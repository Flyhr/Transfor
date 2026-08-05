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

```text
src/Transfor/
├── App/                 # 入口、组合根、应用生命周期与路径
├── Shell/               # 主窗口外壳与页面接口
├── Features/
│   ├── TextTools/       # 文本转换页面、模型与转换器
│   ├── History/         # 文本历史、粘贴协调器与历史面板
│   └── Settings/        # 设置模型与设置窗口
├── Infrastructure/
│   └── Persistence/     # JSON 状态存储与旧状态迁移
└── Platform/Windows/    # 剪贴板、热键、输入与 Win32 封装
```

应用状态位于 `%LOCALAPPDATA%\Transfor\`：`settings.json`、`ui-state.json` 与 `text-history.json` 分别保存设置、界面状态和文本历史。首次启动新版时会迁移旧 `state.json`，成功后备份为 `state.v1.backup.json`；迁移中断会优先使用仍可读取的旧状态恢复。
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
