# 风刃系列手柄协议（逆向记录）

本文记录从官方 Android App 1.1.9 与 Windows App 1.10.3 中还原出的通信协议。
实现代码见 `src/LeadjoyBlade.Core/Protocol` 与 `src/LeadjoyBlade.Core/Devices`。

## 1. 连接方式

### 1.1 BLE（G-Touch 模式，推荐）

| 项目 | 值 |
| --- | --- |
| 广播名 | `Blade Plus` / `Blade SE`（或空名，带 0x8650 服务） |
| Service UUID | `00008650-0000-1000-8000-00805f9b34fb` |
| Write Characteristic | `00008655-0000-1000-8000-00805f9b34fb` |
| Notify Characteristic | `0000865f-0000-1000-8000-00805f9b34fb` |
| CCCD | `00002902-0000-1000-8000-00805f9b34fb` |

进入方式：手柄开机后按住 **Home + Y** 约 3 秒，进入 G-Touch 模式，再在主机系统蓝牙中配对。

普通 Xbox 蓝牙模式只有标准 HID 报告，不包含厂商服务，因此看不到 Fn / M1–M8 / 背键。

### 1.2 USB（Blade SE）

| 项目 | 值 |
| --- | --- |
| VID | `0x4131` (16689) |
| PID | `0x8808` (34824) |
| OUT 端点 | `0x01` |
| IN 端点 | `0x82` (130) |
| MTU | 64 |
| 传输类型 | Bulk |

> Windows 访问该厂商接口需要 WinUSB/libusb 驱动；当前版本默认走 BLE。

---

## 2. 外层帧格式

来源：`com.leadjoy.mapper.model.MsgPackPacket`。

### 2.1 短帧

```
偏移  内容
0     0xA5             HEADER_SHORT
1     LEN              整帧长度（含头、长度、类型、校验）
2     0xCC (或 0x20)   TYPE
3..   PAYLOAD          MessagePack 或手柄状态
末尾  CHECKSUM         前面所有字节之和的低 8 位
```

### 2.2 长帧（分片）

```
0     0xA4             HEADER_LONG
1     LEN
2     0xCC
3     SEQ              低 7 位 = 剩余分片数（含当前），首片最高位置 1
4..   PAYLOAD
末尾  CHECKSUM
```

分片大小：`min(MTU - 5, 200)`，最多 127 片。

### 2.3 校验

```c
sum = 0;
for (i = 0; i < len - 1; i++) sum += frame[i];
checksum = sum & 0xFF;
```

### 2.4 接收重组

BLE/USB 是字节流，需要自行缓存、寻找 `0xA5/0xA4`、校验失败时后移一字节重新同步。
实现见 `FrameReader.Feed()`。

---

## 3. 命令（MessagePack）

外层 TYPE 固定 `0xCC`，内容为 MessagePack map。

### 3.1 开启手柄上报

```json
{"c": 40, "s": 1}
```

- `c = 40`：`CMD_GAMEPAD_REPORT`
- `s = 1` 开启，`0` 关闭

编码后（短帧）：

```
A5 0B CC 82 A1 63 28 A1 73 01 CS
```

连接成功后必须发送一次；部分固件在停止上报后需要重发。

### 3.2 其它命令（板载映射/屏幕映射，暂未使用）

| c | 用途 | 参数 |
| --- | --- | --- |
| 1001 | 屏幕初始化 | `s: {sw, sh, so}` |
| 1002 | 读取设备配置 | 无 |
| 1003 | 下发映射配置 | `s: {d1: [...]}` |
| 1004 | 保存并应用 | 无 |
| 1005 | 开关映射 | `s: 0/1` |

命令 ID 来自 `MsgPackCmd` 中的 `PointerIconCompat` 常量。

---

## 4. 手柄状态上报（TYPE = 0x20）

来源：`MapperManager.parseGamepadReport()`。

外层帧 TYPE 为 `0x20`（也可能出现 `0xE0`），PAYLOAD 15 字节：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| 0 | u8 | 子类型（通常 0/1） |
| 1..4 | u32 LE | 32 位按键位图 |
| 5..6 | i16 LE | 左摇杆 X |
| 7..8 | i16 LE | 左摇杆 Y |
| 9..10 | i16 LE | 右摇杆 X |
| 11..12 | i16 LE | 右摇杆 Y |
| 13 | u8 | 左扳机 0–255 |
| 14 | u8 | 右扳机 0–255 |

整帧长度：`3 + 15 + 1 = 19` 字节。

示例：

```
A5 13 20 01 02 00 00 00 00 40 00 00 00 00 00 00 00 00 00 CS
        ^^ subtype
           ^^^^^^^^^^^ mask = 0x00000002 → bit1 = B
```

---

## 5. 按键位图

来源：`GamepadKey` 枚举。枚举值是 **1 基的位序号**：

```java
int bitIndex = Integer.numberOfTrailingZeros(mask) + 1;
```

因此 0 基位图与按键对应如下（`1 << (GamepadKey.value - 1)`）：

| bit | 值 | 按键 | 说明 |
| --- | --- | --- | --- |
| 0 | 0x00000001 | A | |
| 1 | 0x00000002 | B | |
| 2 | 0x00000004 | C | |
| 3 | 0x00000008 | X | |
| 4 | 0x00000010 | Y | |
| 5 | 0x00000020 | Z | |
| 6 | 0x00000040 | L1 / LB | |
| 7 | 0x00000080 | R1 / RB | |
| 8 | 0x00000100 | L2 / LT | 数字量 |
| 9 | 0x00000200 | R2 / RT | 数字量 |
| 10 | 0x00000400 | SELECT | 视图 |
| 11 | 0x00000800 | START | 菜单 |
| 12 | 0x00001000 | HOME | 主键 |
| 13 | 0x00002000 | L3 | 左摇杆按下 |
| 14 | 0x00004000 | R3 | 右摇杆按下 |
| 15 | 0x00008000 | CAPTURE | 截图 |
| 16 | 0x00010000 | UP | 十字键上 |
| 17 | 0x00020000 | DOWN | 十字键下 |
| 18 | 0x00040000 | LEFT | 十字键左 |
| 19 | 0x00080000 | RIGHT | 十字键右 |
| 20 | 0x00100000 | MENU | 菜单键 |
| 21 | 0x00200000 | MODE | 模式键 |
| 22 | 0x00400000 | FN | 功能键 |
| 23 | 0x00800000 | M1 | 背键 / L4 |
| 24 | 0x01000000 | M2 | 背键 / R4 |
| 25 | 0x02000000 | M3 | |
| 26 | 0x04000000 | M4 | |
| 27 | 0x08000000 | M5 | |
| 28 | 0x10000000 | M6 | |
| 29 | 0x20000000 | M7 | |
| 30 | 0x40000000 | M8 | |
| 31 | 0x80000000 | POWER | 电源键 |

注意：`GamepadKey` 中 80–100 的摇杆方向值是 UI 合成值，不在位图中。

---

## 6. 设备识别（Windows 端）

Windows App 的 `device_manager_lib.dll` 采用以下策略：

1. 按 VID/PID 枚举 USB 设备；
2. 优先选择 Usage Page 在 `0xFF00..0xFFFF` 的厂商 HID 接口；
3. 同时通过 USB 描述符寻找厂商 Bulk 接口，用 WinUSB 打开；
4. 读取 HID 报告时兼容 Report ID 0 / 非 0；
5. 内置设备表包含 Saber（光剑）、Xeno、Spider、MagicKey 等，**没有 Blade（风刃）**，这正是官方 Windows 程序无法识别风刃的原因。

本项目的 BLE 路径参考 Android 端 `BleTransport` / `DeviceProfileManager`：

- 服务筛选 `0x8650`；
- 写特征 `0x8655`，通知特征 `0x865F`；
- 连接后发送 `c=40`。

---

## 7. 证据位置

| 内容 | 文件 |
| --- | --- |
| 按键枚举 | `analysis/jadx_out/sources/com/leadjoy/mapper/model/GamepadKey.java` |
| 帧格式 | `analysis/jadx_out/sources/com/leadjoy/mapper/model/MsgPackPacket.java` |
| 命令 | `analysis/jadx_out/sources/com/leadjoy/mapper/util/MsgPackCmd.java` |
| 状态解析 | `analysis/jadx_out/sources/com/leadjoy/mapper/model/MapperManager.java`（515 行） |
| USB 参数 | `DeviceProfileManager.java`（Blade SE） |
| BLE UUID | `BLEManager.java` / `DeviceProfile.java` |
| G-Touch 提示 | Flutter `libapp.so` 中的 UTF-16 字符串（“请按 Home+Y 键进入 G-Touch 模式”） |

---

## 8. 待验证项

- BLE 实机：连接后 `c=40` 的回包与首帧时序；
- 报告子类型 `payload[0]` 在 Blade Plus / SE 上是否一致；
- 扳机是否同时出现在位图与模拟量中；
- Blade SE USB Bulk 在 Windows 下的 WinUSB 绑定方式。

独立探测工具 `LeadjoyProbe.exe` 可输出原始帧，便于继续校准。
