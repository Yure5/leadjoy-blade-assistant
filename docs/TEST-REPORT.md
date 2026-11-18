# 测试报告

日期：2026-10-18
环境：Windows 11 22631 / Intel N100 / 16 GB / Realtek 蓝牙 5.x / .NET SDK 8.0.426

## 1. 构建

```
dotnet build src/LeadjoyBlade.App -c Release
已成功生成。0 个警告，0 个错误。
```

发布产物：`dist/风刃手柄助手/LeadjoyBlade.exe`（win-x64，自包含单文件，约 79 MB）。

## 2. 自动化自检

命令：

```
dotnet run --project src/LeadjoyBlade.SelfTest
```

实际输出：

```
=== 风刃手柄助手 自检 ===
  PASS  文本注入（实际：风刃Test123）
  PASS  A 键按下状态
  PASS  A 键抬起状态
  PASS  映射 A→B：B 应按下
  PASS  映射 A→B：B 应抬起
  PASS  0x20 帧解析
  PASS  位图 bit1 → B
  PASS  c=40 命令帧字节
  PASS  Fn 层：Fn+M1 应触发 F2
  PASS  Fn 层释放
  PASS  组合键：Ctrl 应按下
  PASS  组合键：Enter 应按下
  PASS  组合键释放：Enter
  PASS  鼠标左键按下
  PASS  鼠标左键抬起
全部通过。
```

覆盖内容：
- Unicode 文本注入（中文 + 英文 + 数字）；
- `SendInput` 扫描码按下/抬起；
- BladeReport → 映射引擎 → 键盘输出的完整链路；
- **协议帧**：手工构造 `A5 13 20 ...` 状态帧，校验和、type、位图 bit1 → B；
- **命令帧**：`{"c":40,"s":1}` 编码结果逐字节比对 `A5 0B CC 82 A1 63 28 A1 73 01 3F`；
- Fn 层覆盖（同一按键在不同层输出不同键）；
- 组合键（Fn+Start → Ctrl+Enter）与释放；
- 鼠标按键注入。

## 3. 界面验证

使用 `PrintWindow` 截图（窗口在后台也能正确抓取）。

| 页面 | 截图 | 验证点 |
| --- | --- | --- |
| 连接与测试（模拟手柄） | `build/screen_sim.png` | 连接状态、61 Hz 上报、摇杆坐标、按键网格、G-Touch 提示、地址连接 |
| 按键映射 | `build/screen_mapping.png` | 基础层/Fn 层切换、映射列表、编辑/删除 |
| 编辑映射对话框 | `build/screen_dialog_binding.png` | 按键捕获、修饰键、连发/切换、动作类型 |
| 组合键对话框 | `build/screen_dialog_chord.png` | 多键勾选、从手柄捕获、动作编辑 |
| 设置 | `build/screen_settings2.png` | 死区滑块、反转 Y、右摇杆鼠标、配置管理 |
| 发布版启动 | `build/screen_published.png` | 单文件 exe 独立启动 |

## 4. 协议静态验证

从 APK 反编译结果中逐项核对：

1. **位图是 1 基位序号**
   `MappingConfigurationView`：
   ```java
   int iNumberOfTrailingZeros = Integer.numberOfTrailingZeros(i) + 1;
   ```
   与 `GamepadKey` 的 1..32 枚举值对应，说明实际 mask 为 `1 << (value - 1)`；
   本项目的 `BladeButtons` 采用 0 基位，已对齐。

2. **状态帧长度与字段**
   `MapperManager.parseGamepadReport` 读取 `content[1..14]`，共 15 字节，
   与 `BladeReport.Parse` 完全一致。

3. **命令 40**
   `MsgPackCmd.buildGamepadReport` → `{"c":40,"s":1}`。

4. **短帧**
   `MsgPackPacket.encodeShort` → `A5 len CC payload checksum`，
   与 `FrameReader.Encode` 一致。

5. **BLE UUID**
   `00008650 / 00008655 / 0000865F`，与 `BleBladeTransport` 一致。

## 5. 真机状态

- 电脑蓝牙适配器工作正常，可发现并连接 “Xbox Wireless Controller”；
- 官方 App 提示进入 G-Touch 的方式为 **Home + Y**；
- 在普通 Xbox 蓝牙模式下，GATT 枚举返回 `Unreachable`，没有 `0x8650` 服务，
  与“只有厂商模式才开放完整按键”的判断一致；
- 由于测试时未能稳定进入 G-Touch 并产生探测日志，**BLE 实机帧尚未完成最终抓取**。
  独立工具 `dist/探测工具/LeadjoyProbe.exe` 已准备好，进入 G-Touch 后双击即可自动抓取
  原始帧与按键位图，日志写在同目录 `probe-*.log`。

## 6. 结论

- 键鼠注入、映射引擎、组合键、Fn 层、配置管理、界面均通过自动化或截图验证；
- 协议层与官方 App 的实现逐项对齐；
- 剩余风险集中在“手柄 G-Touch 模式下的首帧时序/子类型字节”，需要有真机日志后做最后校准。
