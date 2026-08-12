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
  // 媒体页展示下载快照；历史页自动刷新。
  if (page === "media") refreshDownloads();
  if (page === "history") loadHistory();
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

/* ===== 文本工具（引号转换/去除空格，点击切换，共用输入/结果框） ===== */
let currentTextTool = "quote";
const textInput = document.getElementById("text-input");
const textOutput = document.getElementById("text-output");
const textStatus = document.getElementById("text-status");
const textPlaceholders = {
  quote: "输入包含英文或中文双引号的文本…",
  space: "输入包含半角/全角空格的文本…",
};
document.querySelectorAll("#page-home [data-tool]").forEach((tab) => {
  tab.addEventListener("click", () => {
    currentTextTool = tab.dataset.tool;
    document.querySelectorAll("#page-home [data-tool]").forEach((t) => {
      t.classList.toggle("btn-primary", t === tab);
      t.classList.toggle("btn", t !== tab);
    });
    textInput.placeholder = textPlaceholders[currentTextTool];
    convertTextNow();
  });
});

let textTimer = null;
async function convertTextNow() {
  try {
    const { output: text } = await Bridge.invoke("convertText", { tool: currentTextTool, input: textInput.value }, 10000);
    textOutput.textContent = text;
  } catch (e) {
    textOutput.textContent = "";
    textStatus.textContent = "转换失败：" + e.message;
  }
}
textInput.addEventListener("input", () => {
  clearTimeout(textTimer);
  textTimer = setTimeout(convertTextNow, 200);
});
document.getElementById("text-copy").addEventListener("click", async () => {
  const text = textOutput.textContent;
  if (!text) { textStatus.textContent = "没有可复制的内容。"; return; }
  try {
    await Bridge.invoke("copyTextWithHistory", { tool: currentTextTool, input: textInput.value, output: text });
    textStatus.textContent = "已复制并记录历史。";
    toast("已复制结果");
  } catch (e) { textStatus.textContent = e.message; }
});

/* ===== 媒体下载页（Phase 6.2） ===== */
const mediaLink = document.getElementById("media-link");
const mediaStatus = document.getElementById("media-status");
const mediaPost = document.getElementById("media-post");
const mediaGrid = document.getElementById("media-grid");
let currentShareLink = null;
let currentAssets = [];
const mediaSelection = new Set(); // 跨页勾选的媒体索引（分页后 DOM 只含当前页卡片）
const previewCache = new Map();
// 默认全选设置（媒体页解析后按此勾选；初始化时从 getSettings 加载）
let defaultSelectAllSetting = true;

const roleLabels = { livephotostill: "图片 LIVE", livephotomotion: "视频 LIVE" };
function assetTypeLabel(asset) {
  if (asset.role === "livephotostill" || asset.role === "livephotomotion") return roleLabels[asset.role];
  return asset.kind === "image" ? "图片" : "视频";
}

// ===== 分页（解析结果 + 下载队列）：每页条数可改、页码可跳转、条数持久化 =====
function createPager(prefix, sizeKey, pageSizes, defaultSize, renderPage, onSizeChange) {
  let sizes = pageSizes;
  let key = sizeKey;
  const stored = Number(localStorage.getItem(key));
  let pageSize = sizes.some((s) => s.value === stored) ? stored : defaultSize;
  let page = 1;
  let items = [];
  const el = (id) => document.getElementById(prefix + "-page-" + id);
  const els = {
    bar: document.getElementById(prefix + "-pagination"),
    total: el("total"), current: el("current"), count: el("count"),
    prev: el("prev"), next: el("next"), input: el("input"), go: el("go"), size: el("size"),
  };
  function rebuildSizeOptions() {
    els.size.innerHTML = "";
    sizes.forEach((s) => {
      const opt = document.createElement("option");
      opt.value = String(s.value);
      opt.textContent = s.label;
      opt.selected = s.value === pageSize;
      els.size.appendChild(opt);
    });
  }
  function render() {
    const total = items.length;
    const pages = Math.max(1, Math.ceil(total / pageSize));
    page = Math.min(Math.max(1, page), pages);
    const start = (page - 1) * pageSize;
    els.total.textContent = String(total);
    els.current.textContent = String(page);
    els.count.textContent = String(pages);
    els.bar.hidden = total <= pageSize;
    els.prev.disabled = page <= 1;
    els.next.disabled = page >= pages;
    renderPage(items.slice(start, start + pageSize));
  }
  els.prev.addEventListener("click", () => { page -= 1; render(); });
  els.next.addEventListener("click", () => { page += 1; render(); });
  const jump = () => {
    const p = parseInt(els.input.value, 10);
    if (Number.isFinite(p) && p > 0) { page = p; render(); }
  };
  els.go.addEventListener("click", jump);
  els.input.addEventListener("keydown", (e) => { if (e.key === "Enter") { jump(); e.preventDefault(); } });
  els.size.addEventListener("change", () => {
    pageSize = Number(els.size.value);
    page = 1;
    try { localStorage.setItem(key, String(pageSize)); } catch { /* 存储不可用不影响 */ }
    if (onSizeChange) onSizeChange(pageSize);
    render();
  });
  rebuildSizeOptions();
  return {
    update(newItems, resetPage) { items = newItems; if (resetPage) page = 1; render(); },
    setPageSizes(newSizes, newDefault, newKey) {
      sizes = newSizes;
      key = newKey;
      const storedSize = Number(localStorage.getItem(key));
      pageSize = sizes.some((s) => s.value === storedSize) ? storedSize : newDefault;
      page = 1;
      rebuildSizeOptions();
      if (onSizeChange) onSizeChange(pageSize);
      render();
    },
  };
}

// 解析结果分页：格子按行数（1/2/3 行 = 4/8/12 个），平铺按条数（10/20/50）
const gridPageSizes = [{ value: 4, label: "1 行（4 个）" }, { value: 8, label: "2 行（8 个）" }, { value: 12, label: "3 行（12 个）" }];
const listPageSizes = [{ value: 10, label: "10 条/页" }, { value: 20, label: "20 条/页" }, { value: 50, label: "50 条/页" }];

const mediaPager = createPager("media", "transfor.gridPageSize", gridPageSizes, 8, renderMediaPage, (size) => {
  if (mediaView === "grid") applyGridRowsClass(size);
});
const queuePager = createPager("queue", "transfor.queuePageSize", listPageSizes, 10, renderQueuePage);

// 解析结果当前页渲染（卡片元素按页保留在 pager 内，切页仅换挂载）
function renderMediaPage(cards) {
  mediaGrid.innerHTML = "";
  const loaders = [];
  cards.forEach((card) => {
    mediaGrid.appendChild(card);
    if (card._previewLoader) loaders.push(card._previewLoader);
  });
  loadPreviewsSequential(loaders);
}

// 格子展示容器高度随所选行数自适应（1 行 4 个 / 2 行 8 个 / 3 行 12 个）
function applyGridRowsClass(pageSize) {
  mediaGrid.classList.toggle("rows-1", pageSize === 4);
  mediaGrid.classList.toggle("rows-2", pageSize === 8);
  mediaGrid.classList.toggle("rows-3", pageSize === 12);
}

// 下载队列当前页渲染（pager 持有 taskId 列表，取值始终来自实时任务 Map）
function renderQueuePage(taskIds) {
  downloadsList.style.display = taskIds.length === 0 ? "none" : "flex";
  downloadsList.innerHTML = "";
  taskIds.forEach((id) => {
    const t = downloadTasks.get(id);
    if (t) downloadsList.appendChild(buildTaskRow(t));
  });
}

// 解析结果展示方式：格子（每行最多 4 列）/ 平铺（单列，仅基础信息）；选择持久化
const viewGridBtn = document.getElementById("view-grid");
const viewListBtn = document.getElementById("view-list");
let mediaView = localStorage.getItem("transfor.mediaView") === "list" ? "list" : "grid";
function applyMediaView(view) {
  mediaView = view;
  mediaGrid.classList.toggle("view-list", view === "list");
  if (view === "list") {
    mediaGrid.classList.remove("rows-1", "rows-2", "rows-3");
  }
  viewGridBtn.classList.toggle("active", view === "grid");
  viewListBtn.classList.toggle("active", view === "list");
  viewGridBtn.setAttribute("aria-pressed", String(view === "grid"));
  viewListBtn.setAttribute("aria-pressed", String(view === "list"));
  try { localStorage.setItem("transfor.mediaView", view); } catch { /* 存储不可用不影响 */ }
  // 分页条数与默认值随展示方式切换
  if (view === "list") mediaPager.setPageSizes(listPageSizes, 10, "transfor.listPageSize");
  else mediaPager.setPageSizes(gridPageSizes, 8, "transfor.gridPageSize");
}
viewGridBtn.addEventListener("click", () => applyMediaView("grid"));
viewListBtn.addEventListener("click", () => applyMediaView("list"));
applyMediaView(mediaView);

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
    updateLinkClearVisibility();
  } catch (e) { mediaStatus.textContent = "读取剪贴板失败：" + e.message; }
});

// 链接输入框右侧叉号：清空输入框全部内容
const mediaLinkClear = document.getElementById("media-link-clear");
function updateLinkClearVisibility() {
  mediaLinkClear.hidden = mediaLink.value.length === 0;
}
mediaLink.addEventListener("input", updateLinkClearVisibility);
mediaLinkClear.addEventListener("click", () => {
  mediaLink.value = "";
  updateLinkClearVisibility();
  mediaStatus.textContent = "";
  mediaLink.focus();
});
updateLinkClearVisibility();

const resolveButton = document.getElementById("media-resolve");
const downloadButton = document.getElementById("media-download");
let activeBatch = null; // { batchId, total, done }

document.getElementById("media-resolve").addEventListener("click", async () => {
  const link = extractShareUrl(mediaLink.value);
  mediaLink.value = link;
  if (!link) { mediaStatus.textContent = "请输入有效链接。"; return; }
  // 解析开始时清空旧作品（失败/交互也不保留，防止误下载旧作品）
  resetMediaView();
  // 解析新作品：清空下载队列旧数据（含保留终态任务；下载历史只在历史页查看）
  downloadTasks.clear();
  taskTimestamps.clear();
  renderDownloads([]);
  Bridge.invoke("clearDownloads").then(() => refreshDownloads()).catch(() => {});
  resolveButton.disabled = true;
  mediaStatus.textContent = "解析中…";
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
  mediaSelection.clear();
  mediaPost.style.display = "none";
  mediaGrid.innerHTML = "";
  document.getElementById("media-select-all").checked = false;
  document.getElementById("media-selection-count").textContent = "已选择 0 / 0";
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
  // 跨页勾选状态：以媒体索引为键维护（分页后 DOM 只保留当前页卡片）
  mediaSelection.clear();
  post.assets.forEach((asset) => {
    if (defaultSelectAllSetting && asset.status === "Selected") mediaSelection.add(asset.index);
  });
  const cards = [];
  post.assets.forEach((asset) => {
    const card = document.createElement("article");
    card.className = "media-card";
    card.dataset.assetIndex = String(asset.index);
    const thumb = document.createElement("div");
    thumb.className = "media-thumbnail";
    const preview = document.createElement("div");
    preview.className = "media-preview";
    const canPreview = (asset.kind === "image" || (asset.kind === "video" && asset.coverUrl)) && asset.status === "Selected";
    if (asset.kind === "video" && asset.status === "Selected" && !asset.coverUrl) {
      // 视频卡片（无封面）：清晰播放图标占位（不再是低对比度空白）
      preview.classList.add("video-placeholder");
      preview.innerHTML = '<svg viewBox="0 0 24 24" width="36" height="36" aria-hidden="true"><circle cx="12" cy="12" r="10"/><path d="M10 8.5l6 3.5-6 3.5z"/></svg><span>视频</span>';
    } else {
      preview.textContent = canPreview ? "加载预览…" : (asset.status !== "Selected" ? (asset.message || "不可下载") : (asset.kind === "video" ? "视频" : "图片"));
    }
    thumb.appendChild(preview);
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.className = "media-card-checkbox";
    checkbox.checked = mediaSelection.has(asset.index);
    checkbox.disabled = asset.status !== "Selected";
    checkbox.setAttribute("aria-label", `选择第 ${asset.index + 1} 个媒体`);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) mediaSelection.add(asset.index);
      else mediaSelection.delete(asset.index);
      updateMediaSelectionCount();
    });
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
      card._previewLoader = { preview, assetIndex: asset.index };
    }
    const info = document.createElement("div");
    info.className = "media-card-info";
    // 无标题/名称时不渲染占位名（避免“图片 1”这类无意义名字），类型由角标展示
    if (asset.title || asset.name) {
      const title = document.createElement("strong");
      title.className = "media-card-title";
      title.textContent = asset.title || asset.name;
      info.appendChild(title);
    }
    // 分辨率/时长与大小一行展示，用 - 分隔
    const metaLine = document.createElement("span");
    metaLine.className = "media-card-meta";
    const metaParts = [];
    if (asset.width && asset.height) metaParts.push(`${asset.width}×${asset.height}`);
    if (asset.duration) metaParts.push(formatDuration(asset.duration));
    metaParts.push(asset.contentLength ? formatBytes(asset.contentLength) : "大小 —");
    metaLine.textContent = metaParts.join(" - ");
    info.appendChild(metaLine);
    const cardDownload = document.createElement("button");
    cardDownload.type = "button";
    cardDownload.className = "btn media-card-download";
    cardDownload.textContent = "下载";
    cardDownload.disabled = asset.status !== "Selected";
    cardDownload.addEventListener("click", () => downloadSingleAsset(asset, cardDownload));
    info.appendChild(cardDownload);
    card.appendChild(thumb);
    card.appendChild(info);
    card._asset = asset;
    card._sizeLine = metaLine;
    card._metaPrefix = metaParts.slice(0, -1);
    cards.push(card);
  });
  mediaPost.style.display = "block";

  // 全选控件与勾选状态同步（跨页），分页渲染（默认回到第 1 页）
  updateMediaSelectionCount();
  mediaPager.update(cards, true);

  // 真实大小探测：缺失大小的媒体（图片为主）发 HEAD 取“下载的文件大小”，
  // 并发受限、按资产去重、失败静默保持 大小 —
  probeAssetSizes(cards.filter((c) => !c._asset.contentLength && !sizeProbed.has(c._asset.index)));
}

// 已探测过大小的资产（会话内去重，避免切页/重渲染重复请求）
const sizeProbed = new Set();
async function probeAssetSizes(cards) {
  const concurrency = 2;
  let index = 0;
  const workers = Array.from({ length: Math.min(concurrency, cards.length) }, async () => {
    while (index < cards.length) {
      const card = cards[index++];
      const assetIndex = card._asset.index;
      sizeProbed.add(assetIndex);
      try {
        const { size } = await Bridge.invoke("getAssetSize", { assetIndex }, 8000);
        if (size > 0) {
          card._asset.contentLength = size;
          if (card._sizeLine) {
            // 回填时保留分辨率前缀（分辨率/时长 - 真实大小）
            const prefix = card._metaPrefix && card._metaPrefix.length > 0
              ? card._metaPrefix.join(" - ") + " - "
              : "";
            card._sizeLine.textContent = prefix + formatBytes(size);
          }
        }
      } catch { /* HEAD 失败（TLS 拦截等）保持 大小 — */ }
    }
  });
  await Promise.all(workers);
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
  // 跨页全选/取消（选择状态以媒体索引为键，不受分页影响）
  currentAssets.forEach((asset) => {
    if (asset.status !== "Selected") return;
    if (e.target.checked) mediaSelection.add(asset.index);
    else mediaSelection.delete(asset.index);
  });
  mediaGrid.querySelectorAll(".media-card-checkbox").forEach((cb) => {
    const card = cb.closest(".media-card");
    cb.checked = card ? mediaSelection.has(Number(card.dataset.assetIndex)) : false;
  });
  updateMediaSelectionCount();
});

function updateMediaSelectionCount() {
  const count = document.getElementById("media-selection-count");
  if (!count) return;
  const selectable = currentAssets.filter((a) => a.status === "Selected");
  const selected = selectable.filter((a) => mediaSelection.has(a.index));
  count.textContent = `已选择 ${selected.length} / ${currentAssets.length}`;
  const selectAll = document.getElementById("media-select-all");
  selectAll.checked = selectable.length > 0 && selected.length === selectable.length;
  selectAll.indeterminate = selected.length > 0 && selected.length < selectable.length;
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
    toast(`已加入下载队列：${accepted || 1} 个媒体`);
    await refreshDownloads();
  } catch (e) {
    toast("下载失败：" + e.message, "error");
  } finally {
    button.disabled = false;
    updateMediaSelectionCount();
  }
}

document.getElementById("media-download").addEventListener("click", async () => {
  if (!currentShareLink) { toast("请先解析作品。", "error"); return; }
  // 跨页收集选中媒体（以媒体索引为键，不受分页影响）
  const indexes = currentAssets
    .filter((a) => mediaSelection.has(a.index) && a.status === "Selected")
    .map((a) => a.index);
  if (indexes.length === 0) { toast("请先勾选要下载的媒体。", "error"); return; }
  downloadButton.disabled = true;
  try {
    const { accepted, batchId } = await Bridge.invoke("downloadSelected", { shareLink: currentShareLink, assets: indexes });
    activeBatch = { batchId, total: accepted, done: 0 };
    toast(`已加入下载队列：${accepted} 个媒体`);
    await refreshDownloads();
  } catch (e) { toast("下载失败：" + e.message, "error"); }
  finally { downloadButton.disabled = false; }
});

/* ===== 下载事件（Phase 6 M1 推送） ===== */
Bridge.on("taskCompleted", (data) => {
  if (activeBatch && data.batchId === activeBatch.batchId) {
    activeBatch.done += 1;
    if (activeBatch.done >= activeBatch.total) activeBatch = null;
  }
});
Bridge.on("batchCompleted", () => { activeBatch = null; });

/* ===== 下载队列（Phase 6.3 真实任务队列：活动/排队 + 保留终态任务） ===== */
const downloadsList = document.getElementById("downloads-list");
const downloadsEmpty = document.getElementById("downloads-empty");
const downloadTasks = new Map();   // taskId -> 任务视图对象
const taskTimestamps = new Map();  // taskId -> { bytes, time }（速度计算）

const phaseLabels = { pending: "排队中", downloading: "下载中", completed: "已结束" };
const statusLabels = { succeeded: "已完成", failed: "下载失败", cancelled: "已取消" };

// 只读实时任务快照（活动/排队/保留终态）；历史页才用 getHistory，队列不拿批次历史冒充任务行
function refreshDownloads() {
  return Bridge.invoke("getDownloads")
    .then((tasks) => {
      downloadTasks.clear();
      taskTimestamps.clear();
      for (const task of tasks || []) {
        downloadTasks.set(task.taskId, { ...task, speed: null });
      }
      renderDownloads([...downloadTasks.values()]);
    })
    .catch((error) => {
      console.error("加载下载队列失败", error);
    });
}

function renderDownloads(tasks) {
  // 最新在前：新下载/进行中任务始终在第 1 页，完成后位置不变（不“消失”）
  const ordered = [...(tasks || [])].reverse();
  const has = ordered.length > 0;
  document.getElementById("queue-header").hidden = !has;
  downloadsEmpty.style.display = has ? "none" : "block";
  queuePager.update(ordered.map((t) => t.taskId), false);
}

// 横向任务行：缩略图｜文件名｜状态｜进度｜百分比｜速度｜错误｜操作（一个媒体一行）
function buildTaskRow(t) {
  const row = document.createElement("article");
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

  const name = document.createElement("span");
  name.className = "queue-file-name";
  name.textContent = decodePath(t.targetPath);
  name.title = t.targetPath;
  row.appendChild(name);

  const status = document.createElement("span");
  status.className = "task-status";
  row.appendChild(status);

  const progressCell = document.createElement("div");
  progressCell.className = "queue-progress-cell";
  const bar = document.createElement("div");
  bar.className = "progress";
  const fill = document.createElement("div");
  fill.style.width = "0%";
  bar.appendChild(fill);
  progressCell.appendChild(bar);
  row.appendChild(progressCell);

  const percent = document.createElement("span");
  percent.className = "queue-percent";
  row.appendChild(percent);

  const speed = document.createElement("span");
  speed.className = "queue-speed";
  row.appendChild(speed);

  const size = document.createElement("span");
  size.className = "queue-size";
  size.textContent = taskSizeLabel(t);
  row.appendChild(size);

  const right = document.createElement("div");
  right.className = "queue-right";
  const error = document.createElement("span");
  error.className = "queue-error";
  right.appendChild(error);
  const actions = document.createElement("div");
  actions.className = "queue-actions";
  right.appendChild(actions);
  row.appendChild(right);

  updateTaskRow(row, t);
  return row;
}

// 任务大小：优先总大小，未知时回退已下载量，都没有显示占位
function taskSizeLabel(t) {
  const bytes = t.totalBytes > 0 ? t.totalBytes : (t.bytesDownloaded > 0 ? t.bytesDownloaded : null);
  return bytes ? formatBytes(bytes) : "—";
}

// 行状态渲染规则：
// pending → 排队中（进度 —）；downloading → 下载中（进度条+百分比+速度）；
// succeeded → 已完成（100%/大小 + 打开文件/打开目录）；failed → 下载失败（真实错误 + 重试）；
// cancelled → 已取消（重试）
function updateTaskRow(row, t) {
  const taskId = row.dataset.taskId;
  const statusEl = row.querySelector(".task-status");
  const fill = row.querySelector(".progress > div");
  const percentEl = row.querySelector(".queue-percent");
  const speedEl = row.querySelector(".queue-speed");
  const sizeEl = row.querySelector(".queue-size");
  const errorEl = row.querySelector(".queue-error");
  const actions = row.querySelector(".queue-actions");
  if (!statusEl || !fill || !percentEl || !speedEl || !errorEl || !actions) return;
  if (sizeEl) sizeEl.textContent = taskSizeLabel(t);

  if (t.phase === "completed") {
    statusEl.textContent = statusLabels[t.status] || t.status || "已结束";
    statusEl.dataset.status = t.status || "completed";
    if (t.status === "succeeded") {
      fill.style.width = "100%";
      percentEl.textContent = "100%";
      speedEl.textContent = "—";
      errorEl.textContent = "";
    } else {
      fill.style.width = "0%";
      percentEl.textContent = "—";
      speedEl.textContent = "—";
      errorEl.textContent = t.status === "failed" ? (t.error || "网络连接超时") : "已取消";
    }
  } else {
    statusEl.textContent = phaseLabels[t.phase] || t.phase;
    statusEl.dataset.status = t.phase || "pending";
    if (t.phase === "downloading" && t.percent != null) {
      const percent = Math.min(100, t.percent);
      fill.style.width = percent + "%";
      percentEl.textContent = `${Math.round(percent)}%`;
      speedEl.textContent = t.speed != null ? formatSpeed(t.speed) : "—";
    } else {
      fill.style.width = "0%";
      percentEl.textContent = "—";
      speedEl.textContent = "—";
    }
    errorEl.textContent = "";
  }

  actions.innerHTML = "";
  if (t.phase !== "completed") {
    addAction(actions, "取消", () => Bridge.invoke("cancelTask", { taskId })
      .then(() => { toast("已请求取消任务"); return refreshDownloads(); })
      .catch((e) => toast(e.message, "error")));
  } else if (t.status === "failed" || t.status === "cancelled") {
    addAction(actions, "重试", () => Bridge.invoke("retryTask", { taskId })
      .then(() => { toast("已重新加入队列"); return refreshDownloads(); })
      .catch((e) => toast(e.message, "error")));
  }
  if (t.status === "succeeded" && t.savedPath) {
    addOpenMenu(actions, t.savedPath);
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

// 成功任务操作：单一图标按钮（文件夹），点击弹出 打开文件/打开目录 选择菜单
let openMenu = null;
document.addEventListener("click", () => { if (openMenu) { openMenu.hidden = true; openMenu = null; } });

function addOpenMenu(container, savedPath) {
  const wrap = document.createElement("div");
  wrap.className = "queue-menu-wrap";
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "btn queue-open-btn";
  btn.title = "打开";
  btn.setAttribute("aria-haspopup", "menu");
  btn.setAttribute("aria-label", "打开文件或目录");
  btn.innerHTML = '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M2 3.5h3.2l1.6 2H13a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V3.5z"/><path d="M2 7.5h12"/></svg>';
  const menu = document.createElement("div");
  menu.className = "queue-open-menu";
  menu.hidden = true;
  menu.setAttribute("role", "menu");
  const hide = () => { menu.hidden = true; if (openMenu === menu) openMenu = null; };
  const item = (label, fn) => {
    const el = document.createElement("button");
    el.type = "button";
    el.textContent = label;
    el.addEventListener("click", () => { hide(); fn(); });
    return el;
  };
  menu.appendChild(item("打开文件", () => Bridge.invoke("openFile", { path: savedPath }).catch((e) => toast(e.message, "error"))));
  menu.appendChild(item("打开目录", () => Bridge.invoke("openFolder", { path: savedPath }).catch((e) => toast(e.message, "error"))));
  wrap.appendChild(btn);
  wrap.appendChild(menu);
  container.appendChild(wrap);
  btn.addEventListener("click", (e) => {
    e.stopPropagation();
    if (openMenu && openMenu !== menu) openMenu.hidden = true;
    openMenu = menu.hidden ? menu : null;
    menu.hidden = !menu.hidden;
  });
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

// 下载事件：增量更新（进度节流渲染；行缺失时刷新兜底，不静默丢失）
let progressThrottle = 0;
let refreshingQueue = false;function refreshQueueOnce() {
  if (refreshingQueue) return;
  refreshingQueue = true;
  refreshDownloads().finally(() => { refreshingQueue = false; });
}

Bridge.on("downloadProgress", (data) => {
  const t = downloadTasks.get(data.taskId);
  if (!t) { refreshQueueOnce(); return; }
  const now = Date.now();
  const prev = taskTimestamps.get(data.taskId);
  if (prev && now - prev.time >= 1000) {
    t.speed = (data.bytesDownloaded - prev.bytes) / ((now - prev.time) / 1000);
  }
  taskTimestamps.set(data.taskId, { bytes: data.bytesDownloaded, time: now });
  t.bytesDownloaded = data.bytesDownloaded;
  t.totalBytes = data.totalBytes;
  t.percent = data.percent;
  if (now - progressThrottle > 150) {
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
    toast(statusLabels[data.status] || (data.status + "：" + (data.savedPath || "")));
  } else {
    refreshQueueOnce();
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

/* ===== 快捷键按键捕获：点击输入框后按下组合键自动识别绑定 ===== */
let currentHotKey = { modifiers: "", key: "Q" };
const hotkeyCapture = document.getElementById("setting-hotkey-capture");
const hotkeyModDisplay = { Control: "Ctrl", Alt: "Alt", Shift: "Shift", LWin: "Win" };

// e.key → Keys 枚举名；无法作为主键的按键返回 null
function normalizeHotKeyKey(e) {
  const key = e.key;
  if (/^[a-zA-Z]$/.test(key)) return key.toUpperCase();
  // 数字键：映射为 Keys 枚举名 D0–D9（"1" 直接解析会变成 LButton，导致非法主键）
  if (/^[0-9]$/.test(key)) return "D" + key;
  if (/^F([1-9]|1[0-2])$/.test(key)) return key;
  const map = { Backspace: "Back", Enter: "Return", " ": "Space", Home: "Home", End: "End", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right" };
  return map[key] || null;
}

function hotkeyDisplayKey(key) {
  if (/^D[0-9]$/.test(key)) return key.slice(1);
  return key === "Return" ? "Enter" : key === "Space" ? "空格" : key;
}

// 清洗主键：组合枚举文本（如 "Alt, Q"）只取最后一个键位，修饰键/空值返回空
function normalizeStoredKey(key) {
  if (!key) return "";
  const parts = String(key).split(",");
  const last = parts[parts.length - 1].trim();
  return ["Control", "Alt", "Shift", "LWin", "RWin", "Meta", "None"].includes(last) ? "" : last;
}

function renderHotkeyCapture() {
  const mods = currentHotKey.modifiers.split(",").filter(Boolean).map((m) => hotkeyModDisplay[m] || m);
  const key = normalizeStoredKey(currentHotKey.key);
  hotkeyCapture.value = mods.length > 0 && key
    ? mods.join(" + ") + " + " + hotkeyDisplayKey(key)
    : "";
  hotkeyCapture.placeholder = mods.length > 0 ? "继续按下主键…" : "点击后按下组合键，如 Ctrl + T";
}

// 捕获按键组合（输入框聚焦时按下）
hotkeyCapture.addEventListener("keydown", (e) => {
  e.preventDefault();
  e.stopPropagation();
  const mods = [];
  if (e.ctrlKey) mods.push("Control");
  if (e.altKey) mods.push("Alt");
  if (e.shiftKey) mods.push("Shift");
  if (e.metaKey) mods.push("LWin");
  const keyName = normalizeHotKeyKey(e);
  // 仅按下修饰键（或修饰键 + 不可用主键）：实时显示当前按住的组合，清除旧的快捷键显示
  if (keyName === null) {
    if (mods.length > 0) {
      hotkeyCapture.value = mods.map((m) => hotkeyModDisplay[m] || m).join(" + ");
      hotkeyCapture.placeholder = "继续按下主键…";
    } else {
      toast("请同时按下 Ctrl/Alt/Shift/Win 中的至少一个修饰键。", "error");
      renderHotkeyCapture();
    }
    return;
  }
  if (mods.length === 0) { toast("请同时按下 Ctrl/Alt/Shift/Win 中的至少一个修饰键。", "error"); renderHotkeyCapture(); return; }
  const storedKey = normalizeStoredKey(keyName);
  if (!storedKey) { toast("请选择普通按键作为主键（如字母、数字或 F 键）。", "error"); renderHotkeyCapture(); return; }
  currentHotKey = { modifiers: mods.join(","), key: storedKey };
  renderHotkeyCapture();
  autoSaveSettings();
});
hotkeyCapture.addEventListener("blur", () => renderHotkeyCapture());

// 网络模式三态分段开关：关闭（蓝，左）→ 系统代理（橙黄，中）→ 指定代理（绿，右）
let networkState = "direct";
const networkToggle = document.getElementById("network-toggle");
const networkOrder = ["direct", "system", "custom"];
function renderNetworkToggle() {
  networkToggle.className = "network-toggle state-" + networkState;
  networkToggle.setAttribute("aria-checked", String(networkState !== "direct"));
  document.getElementById("setting-network").value = networkState === "custom" ? "customproxy" : networkState;
  // 仅指定代理档显示并启用代理地址输入框
  const custom = networkState === "custom";
  document.getElementById("setting-proxy-row").style.display = custom ? "flex" : "none";
}
function applyNetworkState(next) {
  if (next === networkState) return;
  networkState = next;
  renderNetworkToggle();
  // 切换到指定代理且地址为空：先不保存（填写地址后 change 时保存），
  // 避免空地址校验失败回退导致进不去指定代理界面
  if (next === "custom" && !document.getElementById("setting-proxy").value.trim()) return;
  autoSaveSettings();
}
// 点击任一段直接切换到该模式（无需逐档循环）
document.querySelectorAll(".network-toggle .network-toggle-seg").forEach((seg, index) => {
  seg.addEventListener("click", () => applyNetworkState(networkOrder[index]));
});
// 键盘：左右方向键逐段切换
networkToggle.addEventListener("keydown", (e) => {
  const idx = networkOrder.indexOf(networkState);
  if (e.key === "ArrowRight" && idx < networkOrder.length - 1) { e.preventDefault(); applyNetworkState(networkOrder[idx + 1]); }
  else if (e.key === "ArrowLeft" && idx > 0) { e.preventDefault(); applyNetworkState(networkOrder[idx - 1]); }
});

function loadSettingsUi() {
  return Promise.all([Bridge.invoke("getSettings"), Bridge.invoke("getAppInfo")]).then(([settings, info]) => {
    defaultSelectAllSetting = settings.media.defaultSelectAll;
    document.getElementById("setting-channel").value = settings.updateChannel;
    document.getElementById("setting-quote-limit").value = settings.quoteHistoryLimit;
    document.getElementById("setting-space-limit").value = settings.spaceHistoryLimit;
    currentHotKey = { modifiers: settings.hotKey.modifiers, key: normalizeStoredKey(settings.hotKey.key) };
    renderHotkeyCapture();
    const media = settings.media;
    document.getElementById("setting-directory").value = media.downloadDirectory;
    document.getElementById("setting-concurrency").value = media.maxConcurrentDownloads;
    document.getElementById("setting-select-all").checked = media.defaultSelectAll;
    document.getElementById("setting-open-folder").checked = media.openFolderAfterDownload;
    document.getElementById("setting-quality").value = media.qualityPreference;
    networkState = media.networkMode === "customproxy" ? "custom" : media.networkMode === "system" ? "system" : "direct";
    document.getElementById("setting-proxy").value = media.proxyAddress;
    renderNetworkToggle();
    document.getElementById("setting-app-version").textContent = "v" + info.version.split("+")[0];
  }).catch((e) => settingResult.textContent = "设置加载失败：" + e.message);
}

/* ===== 通用对话框（确认/提醒，整体风格统一） ===== */
function showAppDialog(title, message, buttons) {
  return new Promise((resolve) => {
    document.getElementById("app-dialog-title").textContent = title;
    document.getElementById("app-dialog-message").textContent = message;
    const actions = document.getElementById("app-dialog-actions");
    actions.innerHTML = "";
    buttons.forEach((b) => {
      const btn = document.createElement("button");
      btn.className = "btn" + (b.class ? " " + b.class : "");
      btn.textContent = b.label;
      btn.addEventListener("click", () => { closeAppDialog(); resolve(b.value); });
      actions.appendChild(btn);
    });
    document.getElementById("app-dialog").classList.add("open");
  });
}
function closeAppDialog() {
  document.getElementById("app-dialog").classList.remove("open");
}
document.getElementById("app-dialog").addEventListener("click", (e) => {
  if (e.target.id === "app-dialog") closeAppDialog();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") closeAppDialog();
});

/* ===== 自动保存：设置变更直接保存，无需手动点击；需重启的弹窗提醒 ===== */
async function autoSaveSettings() {
  // 保存前本地校验：历史上限 1–10000、并发 1–8，非法值提示并恢复为已保存值
  const quoteLimit = Number(document.getElementById("setting-quote-limit").value);
  const spaceLimit = Number(document.getElementById("setting-space-limit").value);
  const concurrency = Number(document.getElementById("setting-concurrency").value);
  if (!Number.isInteger(quoteLimit) || quoteLimit < 1 || quoteLimit > 10000) { toast("引用历史上限最高10000。", "error"); loadSettingsUi(); return; }
  if (!Number.isInteger(spaceLimit) || spaceLimit < 1 || spaceLimit > 10000) { toast("空格历史上限最高10000。", "error"); loadSettingsUi(); return; }
  if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 8) { toast("同时下载任务数最高8", "error"); loadSettingsUi(); return; }
  if (networkState === "custom" && !document.getElementById("setting-proxy").value.trim()) { toast("指定代理模式下请填写代理地址。", "error"); loadSettingsUi(); return; }

  const hotKey = currentHotKey;
  const hotKeyKey = normalizeStoredKey(hotKey.key);
  if (!hotKeyKey) { toast("快捷键主键无效，请重新按下组合键。", "error"); return; }
  try {
    const result = await Bridge.invoke("saveSettings", {
      updateChannel: document.getElementById("setting-channel").value,
      quoteHistoryLimit: String(document.getElementById("setting-quote-limit").value),
      spaceHistoryLimit: String(document.getElementById("setting-space-limit").value),
      hotKeyModifiers: hotKey.modifiers,
      hotKeyKey,
      downloadDirectory: document.getElementById("setting-directory").value,
      maxConcurrentDownloads: Number(document.getElementById("setting-concurrency").value),
      defaultSelectAll: document.getElementById("setting-select-all").checked,
      openFolderAfterDownload: document.getElementById("setting-open-folder").checked,
      qualityPreference: document.getElementById("setting-quality").value,
      networkMode: document.getElementById("setting-network").value,
      proxyAddress: document.getElementById("setting-proxy").value,
    }, 20000);
    // 网络设置需要重启生效：右上角 toast 提示
    if (result.restartRequired) {
      toast("网络设置将在重启应用后生效。");
    }
  } catch (e) {
    toast(e.message, "error");
    // 保存失败（如快捷键被占用）：重新加载已保存的真实设置，控件恢复修改前的值
    loadSettingsUi();
  }
}
// 常规/下载/快捷键控件变更即自动保存
["setting-channel", "setting-quote-limit", "setting-space-limit", "setting-concurrency", "setting-quality", "setting-select-all", "setting-open-folder", "setting-directory", "setting-proxy"].forEach((id) => {
  document.getElementById(id).addEventListener("change", autoSaveSettings);
});

document.getElementById("setting-browse").addEventListener("click", async () => {
  try {
    const { path } = await Bridge.invoke("browseDirectory");
    if (path) {
      document.getElementById("setting-directory").value = path;
      autoSaveSettings();
    }
  } catch (e) { settingResult.textContent = "选择目录失败：" + e.message; }
});

// 浏览器数据清除（统一对话框二次确认后执行）
function clearBrowserData(scope, confirmText, successText, danger, confirmLabel) {
  showAppDialog("浏览器数据", confirmText, [
    { label: "取消", value: false },
    { label: confirmLabel || "清除", class: danger ? "btn-danger-soft" : "btn-primary", value: true },
  ]).then((ok) => {
    if (!ok) return;
    Bridge.invoke("clearBrowserData", { scope }, 30000)
      .then(() => {
        document.getElementById("setting-browser-result").textContent = successText;
        toast(successText);
      })
      .catch((e) => { document.getElementById("setting-browser-result").textContent = e.message; toast(e.message, "error"); });
  });
}
document.getElementById("setting-clear-cookies").addEventListener("click", () => clearBrowserData("cookies", "确定清除浏览器 Cookie 吗？登录状态将被清除。", "Cookie 已清除。", false, "清除"));
document.getElementById("setting-clear-cache").addEventListener("click", () => clearBrowserData("cache", "确定清除浏览器缓存吗？", "缓存已清除。", false, "清除"));
document.getElementById("setting-clear-all").addEventListener("click", () => clearBrowserData("all", "确定清除全部浏览器数据吗？Cookie、缓存与登录状态将被全部清除。", "全部浏览器数据已清除。", true, "全部清除"));

document.getElementById("setting-check-update").addEventListener("click", async () => {
  settingResult.textContent = "检查中…";
  try {
    const info = await Bridge.invoke("checkUpdate", {}, 120000);
    const labels = { upToDate: "已是最新版本", optionalUpdate: "发现新版本", requiredUpdate: "需要强制更新", checkFailed: "检查失败", disabled: "更新已禁用" };
    settingResult.textContent = labels[info.status] || info.status;
  } catch (e) { settingResult.textContent = "检查失败：" + e.message; }
});

document.querySelectorAll("[data-history-tab]").forEach((button) => {
  button.addEventListener("click", () => {
    const media = button.dataset.historyTab === "media";
    document.getElementById("history-text-panel").hidden = media;
    document.getElementById("history-media-panel").hidden = !media;
    document.querySelectorAll("[data-history-tab]").forEach((item) => item.classList.toggle("btn-primary", item === button));
  });
});
document.getElementById("queue-toggle").addEventListener("click", (event) => {
  const content = document.getElementById("queue-content");
  const expanded = event.currentTarget.getAttribute("aria-expanded") === "true";
  event.currentTarget.setAttribute("aria-expanded", String(!expanded));
  content.hidden = expanded;
});

/* ===== 初始化（版本/通道信息由宿主侧边栏显示；此处回填设置页） ===== */
(async () => {
  try {
    await loadSettingsUi();
  } catch (e) {
    settingResult.textContent = "Bridge 不可用：" + e.message;
  }
})();
