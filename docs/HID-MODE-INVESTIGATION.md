# 标准 HID 模式能否读取背键 / 功能键（C 路线结论）

测试日期：2026-10-19

## Xbox 模式（045E:02E0）

- HidSharp 打开 `\\?\hid#...vid_045e&pid_02e0...` 成功；
- 15 秒内读取 **0 条输入报告**；
- 原因：Windows XInput 驱动独占该 HID 设备，用户态拿不到原始报告。

## DS4 模式（054C:09CC）

- HidSharp 打开成功，`GetMaxInputReportLength() = 547`；
- 只读到 1 帧初始报告（`01 80 80 80 80 08 00 08 ...`），之后无新报告；
- WinRT `HidDevice.FromIdAsync` 返回 **null（无法打开）**；
- 原因：Windows 的 `Wireless Controller` 驱动占用输入通道。

## Switch 模式（Blade Plus，0x1812）

- HID 集合：
  - `COL01` 触摸屏（0x0D/0x04，IN=18）
  - `COL02` 消费者控制（0x0C/0x01，IN=3）
  - `COL03` 键盘（系统拒绝打开，错误 5）
- `COL01/COL02` 可以打开，但读取 20 秒 **无输入报告**；
- 结论：不会把背键/Fn 作为可用输入暴露。

## 结论

在 Windows 上，Xbox / DS4 / Switch 三种标准 HID 模式都**无法**让用户态程序读取
L4/R4、C/Z、Fn 等厂商按键。`Blade Plus_1B`（0x8650 厂商通道）是唯一能拿到
全部 23 个按键的路径。

标准模式下想使用背键，只能依靠手机 App 的**板载映射**把它们映射成标准键，
再由 Windows / 本程序读取标准键。
