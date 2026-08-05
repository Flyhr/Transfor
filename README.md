# Transfor

Windows 桌面文本转换工具。基于 .NET 10 + WinForms 构建，常驻系统托盘，通过全局快捷键随时呼出历史记录面板，一键将转换结果粘贴到任意窗口。

## 功能

- **引号转换**：英文双引号 `"` → `'`，中文双引号 `“ ”` → `‘ ’`
- **去除空格**：移除半角空格 ` ` 与全角空格 `　`，保留换行与制表符
- **历史记录**：两种功能独立记录转换历史，自动裁剪，上限可在 1–500 之间配置（默认 100）
- **全局快捷键**：默认 `Alt+Q` 呼出历史面板（可在设置中修改），选中历史项后自动粘贴到呼出前的目标窗口
- **系统托盘**：关闭主窗口时最小化到托盘，双击托盘图标重新打开
- **状态持久化**：设置与历史保存到 `%LOCALAPPDATA%\Transfor\state.json`，文件损坏时自动回退到默认值

## 项目结构

```
Transfor/
├── Transfor.slnx                  # 解决方案（XML 格式）
├── src/Transfor/                  # 主程序（WinForms）
│   ├── Program.cs                 # 入口，启动 ApplicationContext
│   ├── TransforApplicationContext.cs  # 托盘、全局快捷键、窗口生命周期
│   ├── MainForm.cs                # 主窗口：文本转换界面
│   ├── HistoryPanelForm.cs        # 历史记录浮动面板（贴靠光标弹出）
│   ├── SettingsForm.cs            # 设置窗口（快捷键、历史上限）
│   ├── HistoryStore.cs            # 历史与设置的 JSON 持久化
│   ├── QuoteConverter.cs          # 引号转换核心逻辑
│   ├── SpaceRemover.cs            # 去空格核心逻辑
│   ├── PasteCoordinator.cs        # 剪贴板 + SendInput 粘贴流程
│   ├── GlobalHotKeyManager.cs     # RegisterHotKey 全局快捷键
│   └── WindowsNative.cs           # Win32 P/Invoke 封装
└── tests/Transfor.Tests/          # 无第三方依赖的控制台测试程序
```

## 环境要求

- Windows 10/11
- .NET 10 SDK

## 构建与运行

```bash
# 构建
dotnet build Transfor.slnx

# 运行
dotnet run --project src/Transfor

# 运行测试（输出 "All N tests passed."）
dotnet run --project tests/Transfor.Tests
```

## 使用说明

1. 首次启动显示主窗口，输入文本后自动实时转换，点击「复制结果」写入剪贴板并记入历史。
2. 关闭主窗口不会退出程序，进程驻留系统托盘。
3. 在任意程序中按下 `Alt+Q`，历史面板会出现在鼠标附近；单击或回车即可把选中结果粘贴回原窗口。
4. 托盘菜单提供「打开主窗口 / 设置 / 退出」。

## Rider 调试提示

若 Rider 调试启动报 `Unable to detect target platform of file`：

- 升级到 Rider 2025.1+（.NET 10 支持），并在 `File > Invalidate Caches / Restart` 后重新构建；
- 或先 `dotnet build` 确认 `src/Transfor/bin/Debug/net10.0-windows/Transfor.exe` 已生成。
