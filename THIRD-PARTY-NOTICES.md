# 第三方组件与来源说明

本项目整体以 **GPL-3.0** 分发（见 [LICENSE](LICENSE)）。下面列出不是本项目原创的部分，以及对应的许可与修改说明。

---

## 1. 柄输 Binput 2.5.0

| 项目 | 内容 |
| --- | --- |
| 名称 | 柄输 Binput 2.5.0 |
| 作者 | 莫古雨 |
| 上游 | <https://github.com/moguyu-binput/Binput> |
| 许可 | **GNU GPL-3.0** |
| 位置 | `src/LeadjoyBlade.Binput/` |
| 原始对照 | `third_party/Binput-2.5.0/`（不参与编译，未包含在公开仓库中） |

### 1.1 修改说明

本项目把 Binput 的源码整体迁入 `src/LeadjoyBlade.Binput/`，作为独立 WPF 类库编译，并与主程序一起发布。相对上游的主要修改：

1. `App.xaml.cs` 改造为可嵌入宿主 `BinputHost.cs`：由主程序控制启动 / 停止 / 显示键盘 / 打开设置，不再创建独立托盘图标与单实例互斥；
2. 新增 `PadBus.Feed()` 手柄直连通道：主程序把风刃解析好的手柄状态喂给输入法，优先级高于 Binput 原生通道；
3. `Config.cs` 的 DPAPI 改为 P/Invoke `crypt32`，移除 `System.Security.Cryptography.ProtectedData` NuGet 依赖；
4. `SkinTex.cs` / `LoadDict()` 增加内嵌资源回退：`dict.txt`、`skin/*.png`、`sponsor.png`、`Binput.ico` 全部作为程序集资源编译；
5. 按系统标签识别 DS4 轴布局、修正第三方 DS4 的 Y 轴与扳机顺序；
6. 九键选格斜角阈值、右摇杆标点替换、死区跟随、唤醒键冲突检查等本地化调整。

以上修改均以 GPL-3.0 发布。上游原始版本可从上表仓库获取。

### 1.2 拼音词库

`dict.txt` 派生自 **rime/rime-essay**（<https://github.com/rime/rime-essay>，**LGPL-3.0**）：

- 把上游 `essay.txt` 的「词 → 拼音 → 词频」转换为「拼音串 → 词频」，同一拼音串只保留最高频的一条；
- 文件中不含汉字，汉字由系统输入法输出；
- 依 LGPL-3.0 第 2 条 b，派生词库按 **GPL-3.0** 分发。

### 1.3 美术资源

`skin/` 下的皮肤贴图、`sponsor.png`、`Binput.ico` 为 Binput 项目的美术资源，随源码按 GPL-3.0 发布，并在本项目中作为内嵌资源分发。

---

## 2. HidSharp 2.1.0

| 项目 | 内容 |
| --- | --- |
| 用途 | HID 直读（Switch / USB HID / DS5 发现） |
| 许可 | **Apache-2.0** |
| 形式 | NuGet 包，仅 `src/LeadjoyBlade.Core/LeadjoyBlade.Core.csproj` 引用 |

Apache-2.0 与 GPL-3.0 单向兼容，可以并入 GPL-3.0 作品。分发时保留其版权与许可声明；完整许可文本见 <http://www.apache.org/licenses/LICENSE-2.0>。

---

## 3. .NET 8 / WPF / Windows SDK

| 项目 | 许可 |
| --- | --- |
| .NET 8 运行时与基础类库 | MIT |
| WPF / Windows SDK 投影 | Microsoft 许可条款 |
| XInput（`xinput1_4.dll`） | Windows 系统组件 |

发布版为 self-contained 单文件，已包含 .NET 运行时；相关第三方声明见 .NET 发行版附带的 `ThirdPartyNotices.txt`。

---

## 4. 协议与商标

- 手柄通信协议来自对官方 Android App `Leadjoy 1.1.9` 与 Windows App `Leadjoy 1.10.3` 的分析，详见 [docs/PROTOCOL.md](docs/PROTOCOL.md)；
- 「Leadjoy」「风刃」「Blade」等名称与商标归其各自权利人所有；
- 本项目与动感指尖 / Leadjoy 官方无任何关联，未获得官方授权或背书。
