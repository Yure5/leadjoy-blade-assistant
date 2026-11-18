# DS4/HID 模式按键在别的电脑上无反应（V0.1.9 修复记录）

> 现象：本机（开发机）DS4 模式按键全部正常；把同一个 exe 拷到另一台电脑后，
> DS4 模式只有 **LT/RT** 有反应，**LB/RB、ABXY、十字键**全部无反应；同一台机器上
> **厂商模式（G-Touch）** 一切正常。

## 1. 根因

DS4 / Switch / 通用 HID 手柄走的是 `RawGameControllerTransport`
（`src/LeadjoyBlade.Core/Devices/RawGameControllerTransport.cs`）。
旧版按键语义完全依赖 `Windows.Gaming.Input` 的 `RawGameController.GetButtonLabel()`：

```csharp
labels[i] = MapLabel(pad.GetButtonLabel(i));   // 旧代码
...
if (buttons[i]) flags |= labels[i];            // labels[i] 可能是 None
```

Windows 是否给某个按键返回标签，取决于 **Windows 版本 + 该手柄的 HID 驱动**，
不是程序的常量：

- 开发机（Windows 11）能拿到 `Cross / Circle / Left1 / Right1 / Left3 …`，所以按键全对；
- 另一台电脑上 `GetButtonLabel()` 对所有按键都返回 `None`，
  于是每个按键都被映射成 `BladeButtons.None`，`flags` 永远是 0 —— 按键全部被静默丢弃。

**为什么只有 LT/RT 还能用？**
扳机不走标签：代码用模拟量阈值合成按键

```csharp
if (axes[leftTriggerAxis] > 0.5) flags |= BladeButtons.L2;
if (axes[rightTriggerAxis] > 0.5) flags |= BladeButtons.R2;
```

摇杆轴不依赖标签，所以「所有数字键死、只有两个扳机活」正好是这个 bug 的指纹。

**为什么厂商模式正常？**
厂商模式走 BLE G-Touch 通道（`BleBladeTransport`），按键来自 0x20 报文里的
32 位位图，和 `RawGameController` 完全无关，所以不受影响。

## 2. 顺带修掉的另一个 bug

旧 `MapLabel()` 只认 `LeftStick` / `L3`，而 Windows 对 DS4 摇杆按下的标签是
**`Left3` / `Right3`**——`"Left3"` 里既没有 `LeftStick` 也没有 `L3`，
所以即便在开发机上 **L3/R3 也一直无法识别**。V0.1.9 已补上。

## 3. 修复方式

新增 `src/LeadjoyBlade.Core/Devices/GamepadButtonLayout.cs`：

1. 仍然优先使用系统标签（标签可用时行为与旧版一致）；
2. 标签缺失时按**厂商固定物理顺序**回退：
   - `054C`（DualShock 4 / DualSense）：Square→X、Cross→A、Circle→B、Triangle→Y、
     L1、R1、（序号 6/7 的 L2/R2 数字位**不映射**，扳机继续走模拟量阈值）、
     Share/View→Select、Options/Menu→Start、L3、R3、PS→Home、Touchpad→不映射；
     若该固件把十字键声明成 4 个按键，则序号 14–17 回退为 ↑/↓/←/→；
   - `057E`（Switch Pro / 风刃 Switch 模式）：B、A、Y、X、L、R、ZL、ZR、Minus、Plus、
     LStick、RStick、Home、Capture（这一路只有 4 个轴，扳机必须靠按键位，所以保留 L2/R2）；
   - 其它 VID：A、B、X、Y、LB、RB、LT、RT、View、Menu、L3、R3、Guide。
3. 标签「部分」可用时只补空缺，且不会把已确认的按键重复分配给别的序号；
4. 有 Hat（`SwitchCount > 0`）时不启用末尾的方向键兜底，避免把固件多声明的按键误判成方向键；
5. 连接时把「序号 : 系统标签 → 映射结果」写进诊断日志，标签全缺失时给出明确提示。

## 4. 现场可用的兜底：hid-layout.json

如果某个固件的按键顺序和上表都不一样，不用改程序：在 **exe 同目录** 放
`hid-layout.json`，按 `VID:PID`（或只写 `VID`、或 `*`）给出序号到按键的数组：

```json
{
  "054C:09CC": ["X", "A", "B", "Y", "L1", "R1", "L2", "R2",
                "Select", "Start", "L3", "R3", "Home", "-",
                "Up", "Down", "Left", "Right"]
}
```

- 数组下标 = 按键序号（诊断日志里的 `indexes`）；
- 值可以是枚举名（`A`/`L1`/`Up`）或短名（`LB`/`RB`/`LT`/`RT`/`↑`/`↓`/`←`/`→`）；
- `-` 表示不映射；
- 该文件优先于内置顺序，改完重新连接手柄即可生效（无需重启程序）。

## 5. 如何在现场确认

1. 连上手柄 → 「连接与测试」；
2. 打开「诊断日志」，连接瞬间会看到：
   - `按键映射：0:Cross→A, 1:...`（正常机型）
   - 或 `系统没有为该手柄提供按键标签，已按固定物理顺序映射 14 个按键`（标签缺失机型）；
3. 按键时会出现 `rawgamepad pressed indexes=[1] mapped=[1:None→A]`：
   - 只有 `pressed indexes`、`mapped` 为空 → 说明该序号没有被解析；
   - 两行都有 → 按键已正确识别。

也可直接看 exe 目录下 `logs\hiddle.log`（与 `logs\ble.log` 分开，专门记录 HID 手柄）。

## 6. 验证

- `LeadjoyBlade.SelfTest` 增加 12 项纯逻辑用例：
  标签解析（`Cross/Left1/Right2/Left3/None`）、标签全缺失回退（ABXY、LB/RB/LT/RT、
  Select/Start/L3/R3/Home）、标签正常时不改变语义、`hid-layout.json` 名称解析；
- 全部通过（`标签缺失回退` 相关用例即模拟「另一台电脑」的环境）。

---

## 7. 追加：Switch 模式“连上了但所有按键无反应”（同一轮排查）

现场现象：DS4 模式已正常，但本机 Switch 模式连上后所有按键无反应；同一只手柄在另一台电脑正常。

实测日志（`logs\hiddle.log`，本机 20:05）：

```
rawgamepad HID-compliant game controller vid=057E pid=2009 buttons=16 switches=1 axes=4
  labels=[LetterB|LetterA|LetterY|LetterX|LeftBumper|RightBumper|LeftTrigger|RightTrigger|
          Minus|Plus|LeftStickButton|RightStickButton|None|None|None|None]
  手柄列表：0:057E:2009「HID-compliant game controller」b16s1a4
心跳 时间戳=0(变化0次) 按下=[] 十字=[Center] 轴=[0.00,0.00,0.00,0.00] 系统手柄数=1
```

结论：**不是按键映射问题**。手柄在系统列表里，但
`RawGameController.GetCurrentReading()` 一直返回 **0（没有任何读数）**，
即 Windows 根本没把这只手柄的输入报文交出来，映射表再正确也不可能有反应。

补充验证（`LeadjoyProbe.exe guitrace`，带消息循环，同时读两套 API，按 C/ABXY/方向键期间）：

```
RawGameController=1 raw[0] 057E:2009 ts=0 按下=[] 十字=[Center] 轴=[0.00,0.00,0.00,0.00]
Gamepad=1          gamepad[0] ts=0 buttons=0x0000 LT=0 RT=0 LX=0.00 LY=0.00
```

两套 Windows 手柄 API（RawGameController 与标准 Gamepad）**都收不到任何数据**，
而同一条 API 通路读 DS4（054C:09CC）是正常的 → 问题在蓝牙/HID 链路层，不在本软件。
换言之：同一状态下换回 V0.1.8 也一样读不到（它用的是同一个 `GetCurrentReading`）。

旧版本在这种状态下仍显示“已连接”、上报频率还显示几十 Hz（循环不判断是否真的收到读数），
看起来就是“连上了但全按键失灵”。本轮已修：

- 每秒检查手柄是否还在系统列表里，被移除就按断开处理，不再假装“已连接”；
- 5 秒收不到任何读数时在「诊断日志」明确提示“已连接但没有读数，请检查蓝牙或换 USB 线”；
- 每 10 秒写一行心跳（时间戳/变化次数/按下序号/十字/轴/系统手柄数），现场可直接定位；
- 设备候选名带上 `VID:PID` 与按键数，多只手柄同时连着时不会再选错；
- 连接时按 `VID:PID` 重新定位手柄，避免系统列表顺序变化后连到另一只。

现场处理建议（属于 Windows/蓝牙层，软件无法代劳）：
1. 确认这只手柄没有同时连着另一台电脑：**另一台电脑开着且蓝牙正常时最容易出现“连着但没数据”**
   （手柄的链路在那边，这台只剩一个幽灵 HID 节点）。关掉那台的蓝牙或在那边“删除设备”；
2. 本机「蓝牙和其他设备」里删除 `Pro Controller` / `Wireless Controller` / `Blade Plus_1B` 后重新配对；
3. 用 Windows 自带测试确认：`Win+R` → `joy.cpl` → 选中 `Pro Controller` → 属性 → 按按键。
   系统自带测试也没有反应，就说明是驱动/配对问题，与软件无关；
4. 换 USB 线连接（Switch 模式有线时走另一套报文，通常更稳）。

### 7.2 最终方案：绕开 Windows.Gaming.Input，直接读 HID 报文

上面的结论是“Windows 不给数据”，但那台机器上**用户态直接读 HID 接口却能拿到完整报文**：

```
[in] 30 8C 41 00 80 00 01 18 7F 01 18 7F 09 ED FF 23 00 ...
      ^^ 报文 0x30，摇杆居中（2049/2033），之后按 700+ 帧/秒持续到达
```

于是新增 `src\LeadjoyBlade.Core\Devices\SwitchHidTransport.cs`（`BladeTransportKind.SwitchHid`）：

- 用 `HidSharp` 直接打开 057E 手柄的 HID 集合（不依赖系统手柄列表）；
- 解析 Switch 的 `0x30 / 0x21`（全量）与 `0x3F`（简易）报文：
  - 按键在报文第 3/4/5 字节：`Y X B A SR SL R ZR` / `Minus Plus RStick LStick Home Capture` / `↓ ↑ → ← SR SL L ZL`；
  - 摇杆是两组 12 位打包值，中心 2048；
- 连接时发一次 `0x03/0x40/0x48` 握手，之后每秒发一帧空振动做保活；
- **独占一个 AboveNormal 线程连续读取，不做任何 sleep**：该手柄实际约 700~760 报文/秒，
  早期版本每帧 `await Task.Delay(1)`（Windows 上实际 12~15ms）导致驱动缓冲不断排队，
  表现就是“按键有明显延迟”，实测修好后延迟与 CPU（约 3% 单核）都正常；
- 摇杆中心加 96（12 位）死区，静止时显示 0,0 不再抖动；
- Y 轴符号按实机校正（推右上应为 X>0、Y>0）；
- 设备候选独立于 WinRT 枚举，名称形如 `SWITCH 模式（HID 直读 057E:2009）`，
  与旧的 `SWITCH 模式（系统接口 …）` 并列，后者保留作备用。

实测（V0.1.9 第 3 轮，本机）：约 760 报文/秒，A/B/X/Y、LB/RB、LT/RT、十字键、C、
HOME/CAPTURE 全部识别，方向正确，静止无抖动。

### 7.3 已知限制：Switch 模式的 LT/RT 是数字量

Switch Pro 协议里只有数字 `ZL/ZR`，没有模拟扳机（DS4 报文才有 0~255 的扳机轴）。
所以 Switch 模式下 LT/RT 只能是 0/255 —— 旧版走 Windows.Gaming.Input 时同样如此
（`axes=4`，扳机来自按键位）。需要线性扳机请用 DS4 模式或 Xbox 模式。

### 7.4 旧路径下“按 C 变成 HOME-↓-↓-→-A”的说明

改用 HID 直读后，按键直接来自报文的 24 个物理位，不再经过“标签/位置猜测”，
C 键不在这 24 位里，因此不会再被当成 HOME。

下面是走 Windows.Gaming.Input 旧路径时的历史记录，仅作参考：

Switch 模式下 `buttons=16`，其中序号 **12–15 没有系统标签**（上表 `None|None|None|None`）。
这类序号只能按固定顺序猜测：12→HOME、13→CAPTURE、14/15→不映射。
如果该固件把 C 键放在序号 12，按 C 就会显示成 HOME。

确认方法：按 C 时看 `logs\hiddle.log`：

- 出现 `pressed indexes=[12] mapped=[12:None→HOME]` 与
  `提示：序号 12 没有系统标签，按固定顺序当成 HOME` → 说明 C 落在序号 12；
- 如果同一秒内连续出现多行不同的 `pressed indexes=…`（12、17、19、1…），
  说明是手柄固件在回放宏/组合键（板载映射），不是软件拼出来的。

修正方法：在 exe 同目录放 `hid-layout.json`，把该序号改成想要的键，或写成 `-` 取消：

```json
{
  "057E:2009": ["B", "A", "Y", "X", "L1", "R1", "L2", "R2",
                "Select", "Start", "L3", "R3", "Home", "Capture",
                "-", "-", "Up", "Down", "Left", "Right"]
}
```

改完重新连接手柄即生效，无需重启程序。
