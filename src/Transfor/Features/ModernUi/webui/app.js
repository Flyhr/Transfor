"use strict";
/* ===== App Bridge（Phase 5B/6M1）：JSON 消息协议 + 事件订阅 ===== */
const Bridge = (() => {
  let nextId = 1;
  const pending = new Map();
  const listeners = new Map();
  const webview = window.chrome.webview;

  function invoke(method, params = {}, timeoutMs = 15000) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      webview.postMessage(JSON.stringify({ id, method, params }));
      setTimeout(() => { if (pending.delete(id)) reject(new Error("调用超时")); }, timeoutMs);
    });
  }

  // 订阅 C# 推送事件（如 downloadProgress / taskCompleted / batchCompleted）
  function on(eventName, callback) {
    if (!listeners.has(eventName)) listeners.set(eventName, []);
    listeners.get(eventName).push(callback);
  }

  // 统一消息分发：事件推送 / 请求-响应匹配
  // （C# 响应经 ExecuteScriptAsync 注入 window.__bridgeDeliver——postMessage
  //  事件在本地 UI 页面实测不可达；postMessage 事件监听保留作兼容）
  function deliver(msg) {
    if (msg.event) {
      const cbs = listeners.get(msg.event);
      if (cbs) cbs.forEach((cb) => { try { cb(msg.data); } catch (err) { console.error(err); } });
      return;
    }
    if (msg.id !== undefined && msg.id !== null) {
      const entry = pending.get(msg.id);
      if (!entry) return;
      pending.delete(msg.id);
      if (msg.error !== undefined && msg.error !== null) entry.reject(new Error(msg.error));
      else entry.resolve(msg.result);
    }
  }

  window.__bridgeDeliver = (json) => {
    let msg;
    try { msg = JSON.parse(json); } catch { return; }
    deliver(msg);
  };

  webview.addEventListener("message", (e) => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }
    deliver(msg);
  });

  return { invoke, on };
})();

/* ===== 主题（Phase 5D）：跟随系统 + 手动切换（宿主侧边栏按钮调用 __toggleTheme） ===== */
const THEMES = ["system", "light", "dark"];
const THEME_LABELS = { system: "跟随系统", light: "浅色", dark: "深色" };
let currentTheme = "system";
function applyTheme(theme) {
  currentTheme = theme;
  document.documentElement.dataset.theme = theme;
}
function notifyTheme() {
  // 当前生效明暗（system 时按系统偏好），同步宿主侧边栏配色
  const theme = document.documentElement.dataset.theme;
  const dark = theme === "dark" || (theme === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  Bridge.invoke("setTheme", { dark }).catch(() => {});
}
window.__toggleTheme = () => {
  applyTheme(THEMES[(THEMES.indexOf(currentTheme) + 1) % THEMES.length]);
  notifyTheme();
};
window.__notifyTheme = notifyTheme;

/* ===== 导航（宿主侧边栏与内部跳转共用） ===== */
function navigateTo(page) {
  document.querySelectorAll(".page").forEach((p) => p.classList.remove("active"));
  const target = document.getElementById("page-" + page);
  if (target) target.classList.add("active");
  // 媒体页展示下载快照；历史页自动刷新；工作台仅刷新低权重文本记录。
  if (page === "media") refreshDownloads();
  if (page === "history") loadHistory();
  if (page === "home") loadHomeSummary();
  // 浏览器页激活时显示浏览器控件（其余页面隐藏）；通知宿主同步侧边栏高亮
  Bridge.invoke("setBrowserVisible", { visible: page === "browser" }).catch(() => {});
  Bridge.invoke("setActiveNav", { page }).catch(() => {});
}
window.__navigateTo = navigateTo;

/* ===== Toast ===== */
function toast(message, type = "") {
  const el = document.createElement("div");
  el.className = "toast " + type;
  el.textContent = message;
  document.getElementById("toast-container").appendChild(el);
  setTimeout(() => el.remove(), 3000);
}

/* ===== 文本工具（引号转换/去除空格，点击切换） ===== */
document.querySelectorAll("#page-home [data-panel]").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll("#page-home [data-panel]").forEach((t) => {
      t.classList.toggle("btn-primary", t === tab);
      t.classList.toggle("btn", t !== tab);
    });
    document.getElementById("panel-quote").style.display = tab.dataset.panel === "panel-quote" ? "block" : "none";
    document.getElementById("panel-space").style.display = tab.dataset.panel === "panel-space" ? "block" : "none";
  });
});

function setupTextTool(prefix, tool) {
  const input = document.getElementById(prefix + "-input");
  const output = document.getElementById(prefix + "-output");
  const status = document.getElementById(prefix + "-status");
  let timer = null;
  input.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try {
        const { output: text } = await Bridge.invoke("convertText", { tool, input: input.value }, 10000);
        output.textContent = text;
      } catch (e) {
        output.textContent = "";
        status.textContent = "转换失败：" + e.message;
      }
    }, 200);
  });
  document.getElementById(prefix + "-copy").addEventListener("click", async () => {
    const text = output.textContent;
    if (!text) { status.textContent = "没有可复制的内容。"; return; }
    try {
      await Bridge.invoke("copyTextWithHistory", { tool, input: input.value, output: text });
      status.textContent = "已复制并记录历史。";
      toast("已复制结果");
    } catch (e) { status.textContent = e.message; }
  });
}
setupTextTool("quote", "quote");
setupTextTool("space", "space");

/* ===== 首页：快捷操作/最近记录/版本与更新状态（Phase 6.1 收尾） ===== */
document.querySelectorAll("#page-home [data-goto]").forEach((btn) => {
  btn.addEventListener("click", () => navigateTo(btn.dataset.goto));
});

function loadHomeSummary() {
  return Promise.all([Bridge.invoke("getRecent"), Bridge.invoke("getAppInfo")])
    .then(([recent, info]) => {
      const textParts = [];
      recent.text.quote.slice().reverse().forEach((h) => textParts.push(`引号：${h.input.slice(0, 30)} → ${h.output.slice(0, 30)}`));
      recent.text.space.slice().reverse().forEach((h) => textParts.push(`空格：${h.input.slice(0, 30)} → ${h.output.slice(0, 30)}`));
      document.getElementById("home-recent-text").textContent = textParts.length > 0
        ? textParts.map((t) => "• " + t).join("\n")
        : "暂无文本转换记录。";
      const channel = info.channel === "beta" ? "Beta" : "Stable";
      const version = document.getElementById("home-version-status");
      if (version) version.textContent = `Transfor v${info.version} · ${channel}`;
    })
    .catch(() => {
      document.getElementById("home-recent-text").textContent = "最近记录加载失败。";
    });
}

/* ===== 媒体下载页（Phase 6.2） ===== */
const mediaLink = document.getElementById("media-link");
const mediaStatus = document.getElementById("media-status");
const mediaPost = document.getElementById("media-post");
const mediaGrid = document.getElementById("media-grid");
let currentShareLink = null;
let currentAssets = [];
const previewCache = new Map();
// 默认全选设置（媒体页解析后按此勾选；初始化时从 getSettings 加载）
let defaultSelectAllSetting = true;

const roleLabels = { livephotostill: "图片 LIVE", livephotomotion: "视频 LIVE" };
function assetTypeLabel(asset) {
  if (asset.role === "livephotostill" || asset.role === "livephotomotion") return roleLabels[asset.role];
  return asset.kind === "image" ? "图片" : "视频";
}

function extractShareUrl(text) {
  const match = String(text || "").match(/https?:\/\/[^\s]+/i);
  return match ? match[0].replace(/[，。、“”]+$/g, "") : "";
}

document.getElementById("media-paste").addEventListener("click", async () => {
  try {
    const { text, error } = await Bridge.invoke("getClipboardText");
    if (error) { mediaStatus.textContent = error; return; }
    const shareUrl = extractShareUrl(text);
    if (shareUrl) { mediaLink.value = shareUrl; mediaStatus.textContent = "已粘贴链接。"; }
    else { mediaLink.value = ""; mediaStatus.textContent = text ? "剪贴板中未找到链接。" : "剪贴板为空。"; }
  } catch (e) { mediaStatus.textContent = "读取剪贴板失败：" + e.message; }
});

const resolveButton = document.getElementById("media-resolve");
const downloadButton = document.getElementById("media-download");
const downloadStatus = document.getElementById("media-download-status");
let activeBatch = null; // { batchId, total, done }

document.getElementById("media-resolve").addEventListener("click", async () => {
  const link = extractShareUrl(mediaLink.value);
  mediaLink.value = link;
  if (!link) { mediaStatus.textContent = "请输入有效链接。"; return; }
  // 解析开始时清空旧作品（失败/交互也不保留，防止误下载旧作品）
  resetMediaView();
  resolveButton.disabled = true;
  mediaStatus.textContent = "解析中…（可能需要 30–60 秒，含浏览器兜底）";
  try {
    const result = await Bridge.invoke("resolveMedia", { link }, 120000);
    if (result.status === "succeeded") {
      renderPost(result.post);
      mediaStatus.textContent = "";
    } else if (result.status === "requiresInteraction") {
      mediaStatus.textContent = result.message || "需要浏览器登录后继续解析。";
    } else {
      mediaStatus.textContent = result.message || "解析失败。";
    }
  } catch (e) { mediaStatus.textContent = "解析失败：" + e.message; }
  finally { resolveButton.disabled = false; }
});

function resetMediaView() {
  currentShareLink = null;
  currentAssets = [];
  previewCache.clear();
  mediaPost.style.display = "none";
  mediaGrid.innerHTML = "";
  document.getElementById("media-select-all").checked = false;
  document.getElementById("media-selection-count").textContent = "已选择 0 / 0";
  downloadStatus.textContent = "";
  activeBatch = null;
}

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return "";
  if (bytes >= 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + " MB";
  return (bytes / 1024).toFixed(0) + " KB";
}

function renderPost(post) {
  currentShareLink = extractShareUrl(mediaLink.value);
  currentAssets = post.assets;
  const mediaCount = document.getElementById("media-count");
  if (mediaCount) mediaCount.textContent = String(post.assets.length);
  mediaGrid.innerHTML = "";
  const previewLoaders = [];
  post.assets.forEach((asset) => {
    const card = document.createElement("article");
    card.className = "media-card";
    card.dataset.assetIndex = String(asset.index);
    const thumb = document.createElement("div");
    thumb.className = "media-thumbnail";
    const preview = document.createElement("div");
    preview.className = "media-preview";
    const canPreview = asset.kind === "image" && asset.status === "Selected";
    preview.textContent = canPreview ? "加载预览…" : (asset.status !== "Selected" ? (asset.message || "不可下载") : (asset.kind === "video" ? "视频" : "图片"));
    thumb.appendChild(preview);
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.className = "media-card-checkbox";
    checkbox.checked = defaultSelectAllSetting && asset.status === "Selected";
    checkbox.disabled = asset.status !== "Selected";
    checkbox.setAttribute("aria-label", `选择第 ${asset.index + 1} 个媒体`);
    checkbox.addEventListener("change", updateMediaSelectionCount);
    thumb.appendChild(checkbox);
    const badge = document.createElement("span");
    badge.className = "media-badge";
    badge.textContent = assetTypeLabel(asset);
    thumb.appendChild(badge);
    if (canPreview) {
      thumb.style.cursor = "pointer";
      thumb.addEventListener("click", (event) => {
        if (event.target === checkbox) return;
        loadPreview(preview, asset.index);
      });
      previewLoaders.push({ preview, assetIndex: asset.index });
    }
    const info = document.createElement("div");
    info.className = "media-card-info";
    const title = document.createElement("strong");
    title.className = "media-card-title";
    title.textContent = asset.title || asset.name || `${assetTypeLabel(asset)} ${asset.index + 1}`;
    const metadata = document.createElement("span");
    metadata.className = "media-card-meta";
    const details = [assetTypeLabel(asset)];
    if (asset.width && asset.height) details.push(`${asset.width}×${asset.height}`);
    if (asset.duration) details.push(formatDuration(asset.duration));
    if (asset.contentLength) details.push(formatBytes(asset.contentLength));
    metadata.textContent = details.join(" · ");
    info.appendChild(title);
    info.appendChild(metadata);
    const cardDownload = document.createElement("button");
    cardDownload.type = "button";
    cardDownload.className = "btn media-card-download";
    cardDownload.textContent = "下载";
    cardDownload.disabled = asset.status !== "Selected";
    cardDownload.addEventListener("click", () => downloadSingleAsset(asset, cardDownload));
    info.appendChild(cardDownload);
    card.appendChild(thumb);
    card.appendChild(info);
    mediaGrid.appendChild(card);
  });
  mediaPost.style.display = "block";

  // 全选控件与卡片勾选状态同步（可选媒体默认全勾选）
  updateMediaSelectionCount();

  // 图片预览默认自动加载（节流：每次并发 2 个，避免多图作品瞬间大 JSON 传输）
  loadPreviewsSequential(previewLoaders);
}

async function loadPreviewsSequential(loaders) {
  const concurrency = 2;
  let index = 0;
  const workers = Array.from({ length: Math.min(concurrency, loaders.length) }, async () => {
    while (index < loaders.length) {
      const item = loaders[index++];
      await loadPreview(item.preview, item.assetIndex);
    }
  });
  await Promise.all(workers);
}

async function loadPreview(preview, assetIndex) {
  if (preview.dataset.loaded) return;
  preview.dataset.loaded = "1";
  preview.textContent = "加载中…";
  try {
    const { dataUrl } = await Bridge.invoke("getPreview", { assetIndex }, 120000);
    previewCache.set(assetIndex, dataUrl);
    const image = document.createElement("img");
    image.src = dataUrl;
    image.alt = "媒体缩略图";
    preview.replaceChildren(image);
  } catch (e) { preview.textContent = "预览失败"; }
}

document.getElementById("media-select-all").addEventListener("change", (e) => {
  mediaGrid.querySelectorAll(".media-card-checkbox").forEach((cb) => { cb.checked = e.target.checked && !cb.disabled; });
  updateMediaSelectionCount();
});

function updateMediaSelectionCount() {
  const count = document.getElementById("media-selection-count");
  if (!count) return;
  const boxes = [...mediaGrid.querySelectorAll(".media-card-checkbox")];
  const selected = boxes.filter((cb) => !cb.disabled && cb.checked).length;
  const selectable = boxes.filter((cb) => !cb.disabled).length;
  count.textContent = `已选择 ${selected} / ${boxes.length}`;
  const selectAll = document.getElementById("media-select-all");
  selectAll.checked = selectable > 0 && selected === selectable;
  selectAll.indeterminate = selected > 0 && selected < selectable;
}

function formatDuration(seconds) {
  const value = Number(seconds);
  if (!Number.isFinite(value) || value <= 0) return "";
  const minutes = Math.floor(value / 60);
  const remainder = Math.floor(value % 60).toString().padStart(2, "0");
  return `${minutes}:${remainder}`;
}

async function downloadSingleAsset(asset, button) {
  if (!currentShareLink || asset.status !== "Selected") return;
  button.disabled = true;
  try {
    const { accepted, batchId } = await Bridge.invoke("downloadSelected", {
      shareLink: currentShareLink,
      assets: [asset.index],
    });
    activeBatch = { batchId, total: accepted, done: 0 };
    downloadStatus.textContent = `已加入下载队列：${accepted} 个媒体（下载中 0/${accepted}）。`;
    toast(`已加入下载队列：${accepted || 1} 个媒体`);
    refreshDownloads();
  } catch (e) {
    toast("下载失败：" + e.message, "error");
  } finally {
    button.disabled = false;
    updateMediaSelectionCount();
  }
}

document.getElementById("media-download").addEventListener("click", async () => {
  if (!currentShareLink) return;
  const indexes = [];
  mediaGrid.querySelectorAll(".media-card").forEach((card) => {
    const cb = card.querySelector(".media-card-checkbox");
    if (cb && cb.checked && !cb.disabled) indexes.push(Number(card.dataset.assetIndex));
  });
  if (indexes.length === 0) { downloadStatus.textContent = "请先勾选要下载的媒体。"; return; }
  downloadButton.disabled = true;
  try {
    const { accepted, batchId } = await Bridge.invoke("downloadSelected", { shareLink: currentShareLink, assets: indexes });
    activeBatch = { batchId, total: accepted, done: 0 };
    downloadStatus.textContent = `已加入下载队列：${accepted} 个媒体（下载中 0/${accepted}）。`;
    toast(`已加入下载队列：${accepted} 个媒体`);
    await refreshDownloads();
  } catch (e) { downloadStatus.textContent = "下载失败：" + e.message; }
  finally { downloadButton.disabled = false; }
});

/* ===== 下载事件（Phase 6 M1 推送） ===== */
Bridge.on("taskCompleted", (data) => {
  if (activeBatch && data.batchId === activeBatch.batchId) {
    activeBatch.done += 1;
    const statusLabel = data.status === "succeeded" ? "完成" : (data.status === "cancelled" ? "已取消" : "失败");
    downloadStatus.textContent = `${statusLabel} ${activeBatch.done}/${activeBatch.total}。`;
    if (activeBatch.done >= activeBatch.total) activeBatch = null;
  }
});
Bridge.on("batchCompleted", () => {
  if (activeBatch) { downloadStatus.textContent = "本批次下载已全部落定。"; activeBatch = null; }
});

/* ===== 下载管理页（Phase 6.3） ===== */
const downloadsList = document.getElementById("downloads-list");
const downloadsEmpty = document.getElementById("downloads-empty");
const downloadTasks = new Map();   // taskId -> 任务视图对象
const taskTimestamps = new Map();  // taskId -> { bytes, time }（速度计算）

const phaseLabels = { pending: "等待中", downloading: "下载中", completed: "已结束" };
const statusLabels = { succeeded: "已完成", failed: "下载失败", cancelled: "已取消" };

function refreshDownloads() {
  return Bridge.invoke("getDownloads").then((tasks) => {
    downloadTasks.clear();
    taskTimestamps.clear();
    tasks.forEach((t) => downloadTasks.set(t.taskId, { ...t, speed: null }));
    renderDownloads();
  }).catch(() => {});
}

// 下载历史记录（来自持久化下载历史；最近 50 条，倒序）
function renderDownloadHistory(mediaHistory) {
  const container = document.getElementById("downloads-history");
  const visible = (mediaHistory || []).slice(-50).reverse();
  if (visible.length === 0) {
    container.innerHTML = '<div style="color:var(--text-secondary);font-size:12px;padding:6px 0">暂无历史记录。</div>';
    return;
  }
  container.innerHTML = "";
  visible.forEach((h) => {
    const row = document.createElement("div");
    row.style.cssText = "display:flex;align-items:center;gap:8px;padding:6px 0;font-size:13px;border-bottom:1px solid var(--border)";
    const text = document.createElement("span");
    text.style.cssText = "flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap";
    const time = h.time ? new Date(h.time).toLocaleString() : "";
    text.textContent = `${h.title || "未命名作品"}（成功 ${h.successCount} / 失败 ${h.failureCount} / 取消 ${h.cancelledCount}）· ${time}`;
    text.title = h.sourceShareLink;
    const redo = document.createElement("button");
    redo.className = "btn";
    redo.textContent = "重新执行";
    redo.style.cssText = "padding:2px 8px;font-size:12px";
    redo.addEventListener("click", () => reparseShareLink(h.sourceShareLink));
    const open = document.createElement("button");
    open.className = "btn";
    open.textContent = "打开文件夹";
    open.style.cssText = "padding:2px 8px;font-size:12px";
    const firstFile = h.savedFiles && h.savedFiles.length > 0 ? h.savedFiles[0] : null;
    open.addEventListener("click", () => {
      if (!firstFile) { toast("无文件记录", "error"); return; }
      Bridge.invoke("openFolder", { path: firstFile }).catch((e) => toast(e.message, "error"));
    });
    row.appendChild(text);
    row.appendChild(redo);
    row.appendChild(open);
    container.appendChild(row);
  });
}

function renderDownloads() {
  downloadsEmpty.style.display = downloadTasks.size === 0 ? "block" : "none";
  downloadsList.style.display = downloadTasks.size === 0 ? "none" : "flex";
  downloadsList.innerHTML = "";
  downloadTasks.forEach((t) => downloadsList.appendChild(buildTaskRow(t)));
}

function buildTaskRow(t) {
  const row = document.createElement("div");
  row.className = "download-task-row";
  row.dataset.taskId = t.taskId;
  const thumbnail = document.createElement("div");
  thumbnail.className = "queue-thumbnail";
  const cachedPreview = previewCache.get(t.assetIndex);
  if (cachedPreview) {
    const image = document.createElement("img");
    image.src = cachedPreview;
    image.alt = "媒体缩略图";
    thumbnail.appendChild(image);
  } else {
    thumbnail.textContent = t.kind === "video" ? "视频" : "图片";
  }
  row.appendChild(thumbnail);

  const main = document.createElement("div");
  main.className = "queue-task-main";
  const head = document.createElement("div");
  head.className = "queue-task-head";
  const name = document.createElement("span");
  name.className = "queue-file-name";
  name.textContent = decodePath(t.targetPath);
  const status = document.createElement("span");
  status.className = "task-status";
  head.appendChild(name);
  head.appendChild(status);
  main.appendChild(head);

  const progressWrap = document.createElement("div");
  progressWrap.className = "queue-task-progress";
  const bar = document.createElement("div");
  bar.className = "progress";
  const fill = document.createElement("div");
  fill.style.width = "0%";
  bar.appendChild(fill);
  const percent = document.createElement("span");
  percent.className = "queue-percent";
  const speed = document.createElement("span");
  speed.className = "queue-speed";
  progressWrap.appendChild(bar);
  progressWrap.appendChild(percent);
  progressWrap.appendChild(speed);
  main.appendChild(progressWrap);

  const error = document.createElement("span");
  error.className = "queue-error";
  main.appendChild(error);
  row.appendChild(main);

  const actions = document.createElement("div");
  actions.className = "queue-actions";
  row.appendChild(actions);

  updateTaskRow(row, t);
  return row;
}

function updateTaskRow(row, t) {
  const taskId = row.dataset.taskId;
  const statusEl = row.querySelector(".task-status");
  const fill = row.querySelector(".progress > div");
  const percentEl = row.querySelector(".queue-percent");
  const speedEl = row.querySelector(".queue-speed");
  const errorEl = row.querySelector(".queue-error");
  const actions = row.querySelector(".queue-actions");
  if (!statusEl || !fill || !percentEl || !speedEl || !errorEl || !actions) return;

  if (t.phase === "completed") {
    statusEl.textContent = statusLabels[t.status] || t.status;
    statusEl.dataset.status = t.status || "completed";
    fill.style.width = t.status === "succeeded" ? "100%" : "0%";
    percentEl.textContent = t.status === "succeeded" ? "100%" : "—";
    speedEl.textContent = t.status === "succeeded" ? "已完成" : "—";
    errorEl.textContent = t.status === "failed" ? (t.error || "网络连接超时") : (t.status === "cancelled" ? "已取消" : "");
  } else {
    statusEl.textContent = phaseLabels[t.phase] || t.phase;
    statusEl.dataset.status = t.phase || "pending";
    const percent = t.percent != null ? Math.min(100, t.percent) : 0;
    fill.style.width = percent + "%";
    percentEl.textContent = t.percent != null ? `${Math.round(percent)}%` : "—";
    speedEl.textContent = t.speed != null ? formatSpeed(t.speed) : "—";
    errorEl.textContent = "";
  }

  actions.innerHTML = "";
  if (t.phase !== "completed") {
    addAction(actions, "取消", () => Bridge.invoke("cancelTask", { taskId }).then(() => toast("已取消任务")).catch((e) => toast(e.message, "error")));
  } else if (t.status === "failed" || t.status === "cancelled") {
    addAction(actions, "重试", () => Bridge.invoke("retryTask", { taskId }).then(() => { toast("已重新加入队列"); return refreshDownloads(); }).catch((e) => toast(e.message, "error")));
  }
  if (t.status === "succeeded" && t.savedPath) {
    addAction(actions, "打开文件", () => Bridge.invoke("openFile", { path: t.savedPath }).catch((e) => toast(e.message, "error")));
    addAction(actions, "打开目录", () => Bridge.invoke("openFolder", { path: t.savedPath }).catch((e) => toast(e.message, "error")));
  }
}

function addAction(container, label, handler) {
  const btn = document.createElement("button");
  btn.className = "btn";
  btn.textContent = label;
  btn.style.cssText = "padding:4px 10px;font-size:12px";
  btn.addEventListener("click", handler);
  container.appendChild(btn);
}

function formatSpeed(bytesPerSecond) {
  if (bytesPerSecond >= 1024 * 1024) return (bytesPerSecond / 1024 / 1024).toFixed(1) + " MB/s";
  if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024).toFixed(0) + " KB/s";
  return bytesPerSecond + " B/s";
}

function decodePath(path) {
  try {
    return decodeURIComponent(path.replace(/\\/g, "/").split("/").pop() || path);
  } catch { return path; }
}

// 下载事件：增量更新（进度节流渲染；完成/批次落定刷新）
let progressThrottle = 0;
Bridge.on("downloadProgress", (data) => {
  const t = downloadTasks.get(data.taskId);
  if (!t) return;
  const now = Date.now();
  const prev = taskTimestamps.get(data.taskId);
  if (prev && now - prev.time >= 1000) {
    t.speed = (data.bytesDownloaded - prev.bytes) / ((now - prev.time) / 1000);
  }
  taskTimestamps.set(data.taskId, { bytes: data.bytesDownloaded, time: now });
  t.bytesDownloaded = data.bytesDownloaded;
  t.totalBytes = data.totalBytes;
  t.percent = data.percent;
  if (now - progressThrottle > 300) {
    progressThrottle = now;
    const row = downloadsList.querySelector(`[data-task-id="${data.taskId}"]`);
    if (row) updateTaskRow(row, t);
  }
});
Bridge.on("taskCompleted", (data) => {
  const t = downloadTasks.get(data.taskId);
  if (t) {
    t.phase = "completed";
    t.status = data.status;
    t.savedPath = data.savedPath;
    t.error = data.error;
    const row = downloadsList.querySelector(`[data-task-id="${data.taskId}"]`);
    if (row) updateTaskRow(row, t);
    toast(statusLabels[data.status] || data.status + "：" + (data.savedPath || ""));
  }
});
Bridge.on("batchCompleted", () => refreshDownloads());

/* ===== 历史页（Phase 6.4） ===== */
let historyData = { text: { quote: [], space: [] }, media: [] };
let historyFilter = "";

function loadHistory() {
  return Bridge.invoke("getHistory").then((data) => {
    historyData = data;
    renderHistory();
  }).catch((e) => toast("历史加载失败：" + e.message, "error"));
}

function renderHistory() {
  const filter = historyFilter.toLowerCase();
  const textMatches = (h) => !filter || h.input.toLowerCase().includes(filter) || h.output.toLowerCase().includes(filter);
  renderTextGroup("history-quote", historyData.text.quote, "引号转换", textMatches);
  renderTextGroup("history-space", historyData.text.space, "去除空格", textMatches);
  renderMediaGroup(historyData.media, filter);
}

function renderTextGroup(containerId, entries, label, filterFn) {
  const container = document.getElementById(containerId);
  const visible = entries.filter(filterFn);
  if (visible.length === 0) {
    container.innerHTML = `<div style="color:var(--text-secondary);font-size:12px;padding:6px 0">${label}：暂无记录。</div>`;
    return;
  }
  container.innerHTML = `<div style="color:var(--text-secondary);font-size:12px;margin-bottom:4px">${label}（${visible.length} 条）</div>`;
  visible.forEach((h, index) => {
    const row = document.createElement("div");
    row.style.cssText = "display:flex;align-items:center;gap:8px;padding:4px 0;font-size:13px;border-bottom:1px solid var(--border)";
    const text = document.createElement("span");
    text.style.cssText = "flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap";
    text.textContent = h.input + " → " + h.output;
    text.title = text.textContent;
    const del = document.createElement("button");
    del.className = "btn";
    del.textContent = "删除";
    del.style.cssText = "padding:2px 8px;font-size:12px";
    del.addEventListener("click", () => deleteTextEntry(label === "引号转换" ? "quote" : "space", index));
    row.appendChild(text);
    row.appendChild(del);
    container.appendChild(row);
  });
}

function renderMediaGroup(entries, filter) {
  const container = document.getElementById("history-media");
  const visible = entries.filter((h) => !filter || (h.title && h.title.toLowerCase().includes(filter)) || h.sourceShareLink.toLowerCase().includes(filter));
  if (visible.length === 0) {
    container.innerHTML = `<div style="color:var(--text-secondary);font-size:12px;padding:6px 0">暂无记录。</div>`;
    return;
  }
  container.innerHTML = `<div style="color:var(--text-secondary);font-size:12px;margin-bottom:4px">共 ${visible.length} 条</div>`;
  visible.forEach((h, index) => {
    const row = document.createElement("div");
    row.style.cssText = "display:flex;align-items:center;gap:8px;padding:4px 0;font-size:13px;border-bottom:1px solid var(--border)";
    const text = document.createElement("span");
    text.style.cssText = "flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap";
    text.textContent = `${h.title || "未命名作品"}（成功 ${h.successCount} / 失败 ${h.failureCount} / 取消 ${h.cancelledCount}）`;
    text.title = h.sourceShareLink;
    const redo = document.createElement("button");
    redo.className = "btn";
    redo.textContent = "重新执行";
    redo.style.cssText = "padding:2px 8px;font-size:12px";
    redo.addEventListener("click", () => reparseShareLink(h.sourceShareLink));
    const del = document.createElement("button");
    del.className = "btn";
    del.textContent = "删除";
    del.style.cssText = "padding:2px 8px;font-size:12px";
    del.addEventListener("click", () => deleteMediaEntry(index));
    row.appendChild(text);
    row.appendChild(redo);
    row.appendChild(del);
    container.appendChild(row);
  });
}

function deleteTextEntry(tool, index) {
  Bridge.invoke("deleteHistoryEntry", { type: "text", tool, index })
    .then(() => { toast("已删除"); return loadHistory(); })
    .catch((e) => toast(e.message, "error"));
}

function deleteMediaEntry(index) {
  Bridge.invoke("deleteHistoryEntry", { type: "media", index })
    .then(() => { toast("已删除"); return loadHistory(); })
    .catch((e) => toast(e.message, "error"));
}

// 重新执行：切到媒体页并解析该链接
function reparseShareLink(link) {
  navigateTo("media");
  mediaLink.value = link;
  document.getElementById("media-resolve").click();
}

// 历史在进入页面时自动刷新，不提供刷新按钮。
document.getElementById("history-search").addEventListener("input", (e) => {
  historyFilter = e.target.value.trim();
  renderHistory();
});
document.getElementById("history-clear-text").addEventListener("click", async () => {
  try {
    await Bridge.invoke("clearHistory", { type: "text", tool: "quote" });
    await Bridge.invoke("clearHistory", { type: "text", tool: "space" });
    toast("文本历史已清空");
    loadHistory();
  } catch (e) { toast(e.message, "error"); }
});
document.getElementById("history-clear-media").addEventListener("click", () => {
  Bridge.invoke("clearHistory", { type: "media" })
    .then(() => { toast("媒体历史已清空"); return loadHistory(); })
    .catch((e) => toast(e.message, "error"));
});

/* ===== 浏览器页（Phase 6.5） ===== */
const browserAddress = document.getElementById("browser-address");
const browserStatus = document.getElementById("browser-status");
const browserMediaHint = document.getElementById("browser-media-hint");
let browserCurrentUrl = null;

function browserGo() {
  const address = browserAddress.value.trim();
  if (!address) { browserStatus.textContent = "请输入网址。"; return; }
  Bridge.invoke("browserNavigate", { address }).catch((e) => { browserStatus.textContent = e.message; });
}
document.getElementById("browser-go").addEventListener("click", browserGo);
browserAddress.addEventListener("keydown", (e) => { if (e.key === "Enter") { browserGo(); e.preventDefault(); } });
document.getElementById("browser-back").addEventListener("click", () => Bridge.invoke("browserBack").catch((e) => browserStatus.textContent = e.message));
document.getElementById("browser-forward").addEventListener("click", () => Bridge.invoke("browserForward").catch((e) => browserStatus.textContent = e.message));
document.getElementById("browser-refresh").addEventListener("click", () => Bridge.invoke("browserRefresh").catch((e) => browserStatus.textContent = e.message));
document.getElementById("browser-stop").addEventListener("click", () => Bridge.invoke("browserStop").catch((e) => browserStatus.textContent = e.message));

Bridge.on("browserNavigated", (data) => {
  browserCurrentUrl = data.url || browserCurrentUrl;
  browserAddress.value = data.url || browserAddress.value;
  document.getElementById("browser-back").disabled = !data.canGoBack;
  document.getElementById("browser-forward").disabled = !data.canGoForward;
  browserStatus.textContent = data.success === false ? "导航失败：" + (data.error || "未知原因") : (data.url || "");
});

// 浏览器不可用（初始化失败）：显示提示并禁用导航
Bridge.on("browserUnavailable", (data) => {
  browserStatus.textContent = "浏览器不可用：" + (data.message || "初始化失败");
  document.getElementById("browser-address").disabled = true;
  document.getElementById("browser-go").disabled = true;
});

Bridge.on("pageMediaDetected", (data) => {
  if (data.count > 0) {
    document.getElementById("browser-media-count").textContent = `当前页面检测到 ${data.count} 个可能的媒体`;
    browserMediaHint.style.display = "block";
    browserMediaHint.dataset.url = data.url;
  } else {
    browserMediaHint.style.display = "none";
  }
});

// 查看媒体：切到媒体页并解析当前页面地址
document.getElementById("browser-view-media").addEventListener("click", () => {
  const url = browserMediaHint.dataset.url || browserCurrentUrl;
  if (url) reparseShareLink(url);
});

/* ===== 设置页（Phase 6.6：常规/下载/网络/快捷键/浏览器/更新/外观） ===== */
const settingResult = document.getElementById("setting-result");

// 主键候选（字母/数字/F 键/常用键）
function buildKeyOptions() {
  const keys = [];
  for (let c = 65; c <= 90; c++) keys.push(String.fromCharCode(c));
  for (let d = 48; d <= 57; d++) keys.push(String.fromCharCode(d));
  for (let f = 1; f <= 12; f++) keys.push("F" + f);
  keys.push("Back", "Tab", "Return", "Space", "Home", "End", "Up", "Down", "Left", "Right");
  const select = document.getElementById("setting-key");
  keys.forEach((k) => {
    const opt = document.createElement("option");
    opt.value = k;
    opt.textContent = k === "Return" ? "Enter" : k === "Space" ? "空格" : k;
    select.appendChild(opt);
  });
}

// Keys 名称 → 修饰键勾选（含 Win=LWin）
function loadHotKey(modifiers, key) {
  document.getElementById("setting-mod-ctrl").checked = modifiers.includes("Control");
  document.getElementById("setting-mod-alt").checked = modifiers.includes("Alt");
  document.getElementById("setting-mod-shift").checked = modifiers.includes("Shift");
  document.getElementById("setting-mod-win").checked = modifiers.includes("LWin");
  const keySelect = document.getElementById("setting-key");
  if (key) keySelect.value = key;
  if (keySelect.selectedIndex < 0) keySelect.value = "Q";
}

function collectHotKey() {
  const mods = [];
  if (document.getElementById("setting-mod-ctrl").checked) mods.push("Control");
  if (document.getElementById("setting-mod-alt").checked) mods.push("Alt");
  if (document.getElementById("setting-mod-shift").checked) mods.push("Shift");
  if (document.getElementById("setting-mod-win").checked) mods.push("LWin");
  return { modifiers: mods.join(","), key: document.getElementById("setting-key").value };
}

// 网络模式切换时显示/隐藏代理地址
document.getElementById("setting-network").addEventListener("change", (e) => {
  document.getElementById("setting-proxy-row").style.display = e.target.value === "customproxy" ? "flex" : "none";
});

function loadSettingsUi() {
  return Bridge.invoke("getSettings").then((settings) => {
    defaultSelectAllSetting = settings.media.defaultSelectAll;
    document.getElementById("setting-channel").value = settings.updateChannel;
    document.getElementById("setting-quote-limit").value = settings.quoteHistoryLimit;
    document.getElementById("setting-space-limit").value = settings.spaceHistoryLimit;
    loadHotKey(settings.hotKey.modifiers, settings.hotKey.key);
    const media = settings.media;
    document.getElementById("setting-directory").value = media.downloadDirectory;
    document.getElementById("setting-concurrency").value = media.maxConcurrentDownloads;
    document.getElementById("setting-select-all").checked = media.defaultSelectAll;
    document.getElementById("setting-open-folder").checked = media.openFolderAfterDownload;
    document.getElementById("setting-quality").value = media.qualityPreference;
    document.getElementById("setting-network").value = media.networkMode;
    document.getElementById("setting-proxy").value = media.proxyAddress;
    document.getElementById("setting-proxy-row").style.display = media.networkMode === "customproxy" ? "flex" : "none";
  }).catch((e) => settingResult.textContent = "设置加载失败：" + e.message);
}

document.getElementById("setting-save").addEventListener("click", async () => {
  const hotKey = collectHotKey();
  try {
    const result = await Bridge.invoke("saveSettings", {
      updateChannel: document.getElementById("setting-channel").value,
      quoteHistoryLimit: String(document.getElementById("setting-quote-limit").value),
      spaceHistoryLimit: String(document.getElementById("setting-space-limit").value),
      hotKeyModifiers: hotKey.modifiers,
      hotKeyKey: hotKey.key,
      downloadDirectory: document.getElementById("setting-directory").value,
      maxConcurrentDownloads: Number(document.getElementById("setting-concurrency").value),
      defaultSelectAll: document.getElementById("setting-select-all").checked,
      openFolderAfterDownload: document.getElementById("setting-open-folder").checked,
      qualityPreference: document.getElementById("setting-quality").value,
      networkMode: document.getElementById("setting-network").value,
      proxyAddress: document.getElementById("setting-proxy").value,
    }, 20000);
    const notice = result.restartRequired ? "网络设置将在重启应用后生效。" : "设置已保存。";
    settingResult.textContent = notice;
    toast(notice);
    loadSettingsUi();
  } catch (e) { settingResult.textContent = e.message; toast(e.message, "error"); }
});

document.getElementById("setting-browse").addEventListener("click", async () => {
  try {
    const { path } = await Bridge.invoke("browseDirectory");
    if (path) document.getElementById("setting-directory").value = path;
  } catch (e) { settingResult.textContent = "选择目录失败：" + e.message; }
});

// 浏览器数据清除（确认后执行）
function clearBrowserData(scope, confirmText, successText) {
  if (!confirm(confirmText)) return;
  Bridge.invoke("clearBrowserData", { scope }, 30000)
    .then(() => {
      document.getElementById("setting-browser-result").textContent = successText;
      toast(successText);
    })
    .catch((e) => { document.getElementById("setting-browser-result").textContent = e.message; toast(e.message, "error"); });
}
document.getElementById("setting-clear-cookies").addEventListener("click", () => clearBrowserData("cookies", "确定清除浏览器 Cookie 吗？登录状态将被清除。", "Cookie 已清除。"));
document.getElementById("setting-clear-cache").addEventListener("click", () => clearBrowserData("cache", "确定清除浏览器缓存吗？", "缓存已清除。"));
document.getElementById("setting-clear-all").addEventListener("click", () => clearBrowserData("all", "确定清除全部浏览器数据吗？Cookie、缓存与登录状态将被全部清除。", "全部浏览器数据已清除。"));

document.getElementById("setting-check-update").addEventListener("click", async () => {
  settingResult.textContent = "检查中…";
  try {
    const info = await Bridge.invoke("checkUpdate", {}, 120000);
    const labels = { upToDate: "已是最新版本", optionalUpdate: "发现新版本", requiredUpdate: "需要强制更新", checkFailed: "检查失败", disabled: "更新已禁用" };
    settingResult.textContent = labels[info.status] || info.status;
  } catch (e) { settingResult.textContent = "检查失败：" + e.message; }
});

buildKeyOptions();

document.querySelectorAll("[data-history-tab]").forEach((button) => {
  button.addEventListener("click", () => {
    const media = button.dataset.historyTab === "media";
    document.getElementById("history-text-panel").hidden = media;
    document.getElementById("history-media-panel").hidden = !media;
    document.querySelectorAll("[data-history-tab]").forEach((item) => item.classList.toggle("btn-primary", item === button));
  });
});
document.querySelectorAll("[data-setting-panel]").forEach((button) => {
  button.addEventListener("click", () => {
    const panel = button.dataset.settingPanel;
    document.querySelectorAll("[data-setting-panel]").forEach((item) => item.classList.toggle("active", item === button));
    document.querySelectorAll("[data-setting-content]").forEach((item) => { item.hidden = item.dataset.settingContent !== panel; });
  });
});
document.getElementById("queue-toggle").addEventListener("click", (event) => {
  const content = document.getElementById("queue-content");
  const expanded = event.currentTarget.getAttribute("aria-expanded") === "true";
  event.currentTarget.setAttribute("aria-expanded", String(!expanded));
  content.hidden = expanded;
});

/* ===== 初始化（版本/通道信息由宿主侧边栏显示；此处回填设置页与首页） ===== */
(async () => {
  try {
    await loadSettingsUi();
    loadHomeSummary();
  } catch (e) {
    settingResult.textContent = "Bridge 不可用：" + e.message;
  }
})();
