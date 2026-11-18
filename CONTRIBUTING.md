# 贡献指南

感谢你愿意改进风刃手柄助手。这是一个个人维护的开源项目，提交前请先读完本文，能省下很多来回沟通。

---

## 1. 适合提交什么

- **Bug 报告**：连接失败、按键错位、映射不生效、崩溃、界面异常；
- **兼容性反馈**：不同手柄型号 / 模式 / 蓝牙适配器 / Windows 版本下的表现；
- **功能建议**：先开 Issue 说明使用场景，不要直接写一大段代码；
- **文档修正**：错别字、过时截图、步骤不清；
- **代码贡献**：小范围、目标明确的修复或功能。

不适合：

- 要求加虚拟手柄驱动 / 内核驱动；
- 要求绕过反作弊；
- 与风刃 / 手柄无关的通用键鼠工具需求。

---

## 2. 报告问题

请尽量附上：

1. 程序版本（标题栏或设置页「关于」）；
2. Windows 版本与位数；
3. 手柄型号、连接模式（XBOX / DS4 / Switch / USB）、蓝牙适配器型号；
4. 复现步骤（越具体越好）；
5. `logs\ble.log` / `logs\hiddle.log` 中相关片段（可先删掉设备地址）；
6. 截图或录屏（界面问题必须）。

> 日志里可能包含蓝牙地址和设备名，公开前请自行打码。

---

## 3. 开发环境

| 工具 | 版本 |
| --- | --- |
| Visual Studio 2022 或 .NET SDK | .NET 8（`net8.0-windows10.0.19041.0`） |
| 系统 | Windows 10 2004+ / Windows 11 x64 |
| 可选 | 真实风刃手柄（强烈建议，很多问题只在实机出现） |

### 构建

```powershell
# 普通构建
dotnet build src/LeadjoyBlade.sln -c Release

# 单文件发布（与正式发布一致）
dotnet publish src/LeadjoyBlade.App/LeadjoyBlade.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o build/app
```

### 项目结构

| 目录 | 内容 |
| --- | --- |
| `src/LeadjoyBlade.Core` | 协议、设备传输、映射引擎、配置、键鼠注入 |
| `src/LeadjoyBlade.App` | WPF 界面、幻想模式、系统菜单、托盘 |
| `src/LeadjoyBlade.Binput` | 融合的柄输 Binput 2.5.0（GPL-3.0） |
| `src/LeadjoyBlade.Probe` | 独立硬件探测工具 |
| `src/LeadjoyBlade.SelfTest` | 自检 |

### 调试提示

- 程序数据默认写在 **exe 同目录**；调试时建议单独复制一份 exe 到临时目录，避免覆盖自己的配置；
- 环境变量 `LEADJOY_SIM=1` 可启用模拟手柄，方便无手柄时调 UI；
- 环境变量 `LEADJOY_START_PAGE` 可直接打开指定页面（`mapping` / `chords` / `macro` / `fantasy` / `ime` / `settings` / `diagnostics`）；
- 诊断日志在程序目录 `logs\`。

---

## 4. 提交代码

### 4.1 约定

- **一个 PR 只做一件事**，不要顺手重构无关文件；
- 不要改动与你的需求无关的连接方式、设置项和视觉参数；
- 修改 UI 前先读对应 `.xaml` 与 `.xaml.cs`，本项目大量使用 `x:Name` 事件与手写绑定；
- 所有鼠标位置采集走 `MousePointCapture`，保持「有进程 = 窗口百分比，无进程 = 显示器百分比」的一致逻辑；
- 涉及 Binput 的改动，尽量保持与上游 diff 最小，并在 `THIRD-PARTY-NOTICES.md` 的修改说明里补一条。

### 4.2 提交信息

用一句话说明改了什么，可带前缀：

```
fix: 修复 BLE 断联后摇杆鼠标不停止
feat: 按键映射支持固定鼠标位置
docs: 补充系统菜单启用步骤
```

### 4.3 提交前自检

- [ ] `dotnet build src/LeadjoyBlade.sln -c Release` 无新增警告 / 错误；
- [ ] 实际启动程序，走一遍受影响的功能；
- [ ] 如果有手柄，在真机上验证连接与按键；
- [ ] 没有把个人配置、日志、蓝牙地址、构建产物提交进仓库。

---

## 5. 许可

本项目整体以 **GPL-3.0** 分发。提交 PR 即表示你同意以 GPL-3.0 授权你的贡献，并确认你有权这样做。

请勿提交来源不明或许可不兼容的代码、图片、字体或词库。
