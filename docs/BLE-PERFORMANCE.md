# Blade Plus_1B BLE 连接与回报率记录

## 2026-10-19 11:58 高回报率样本

连接阶段（`logs/ble.log`）：

```
11:58:55.325  phase open        +107ms
11:58:55.328  phase quick-pair  +111ms  paired=True
11:58:55.425  device reopened after quick pairing
11:58:57.518  services Success
11:58:57.662  connected         总耗时 2446ms
```

回报率（每 2 秒统计）：

```
36.9Hz  frames=74  interval avg=27.5ms  min=17.5ms  max=224.8ms
35.4Hz  frames=71  interval avg=28.7ms  min=0.3ms   max=148.5ms
39.9Hz  frames=80  interval avg=25.4ms  min=17.5ms  max=74.8ms
40.4Hz  frames=81  interval avg=25.1ms  min=0.2ms   max=75.0ms
```

## 对照：低回报率样本（2026-10-19 10:37）

```
10.7Hz  interval avg=98.2ms  min=56.8ms  max=179.6ms
12.4Hz  interval avg=84.0ms  min=59.5ms  max=240.0ms
```

## 观察与假设

1. 高回报率样本中 `quick pair Paired` 成功，且最小间隔 **17.5ms**，符合 BLE 连接间隔
   15ms + 少量调度开销。
2. 低回报率样本最小间隔稳定 **55–60ms**，符合 60ms 连接间隔。
3. 因此回报率上限主要由 **Windows 与手柄协商的 BLE 连接间隔** 决定，而不是程序解析：
   - 绑定成功 / 链路质量好 → 可能协商 15ms → 最高约 40Hz（有丢帧，未到 60Hz）
   - 未绑定 / 旧绑定失效 / 链路差 → 回落 60ms → 约 15–18Hz
4. 有线供电时 `_1B` 秒连且更稳定，说明供电与射频调度也会影响协商结果。
5. 手柄只在状态变化时推流；静止时 Hz 统计会自然降低，需在持续按键/画摇杆时取样。

## 复现建议

1. 保持 BLE 绑定，不要删除 Windows 里的 `Blade Plus_1B` 配对；
2. 手柄充满电或插 USB 供电；
3. 开机后等 `_1B` 出现再连，观察日志是否出现 `quick pair Paired`；
4. 连上后持续画摇杆 10 秒，检查 `gamepad rate` 是否达到 30Hz 以上、min interval 是否约 17ms；
5. 若回落到 15Hz / 60ms，尝试重新配对或重新开机。

## 后续可做

- 在 UI 显示最近 2 秒的连接间隔与丢帧率；
- 记录每次连接的 `quick pair` 结果与速率，统计绑定成功率；
- 若确认绑定是 15ms 的关键，可在连接失败时优先修复绑定，而不是反复 GATT。

## 2026-10-19 13:16 DS4 模式全部按键成功

Windows 设备：Wireless Controller（BTHENUM，VID 054C PID 09CC）+ Blade Plus_1B（BTHLE）。

`
13:15:54.527  phase open        +33ms
13:16:00.689  quick pair Paired（6.2s）
13:16:00.830  device reopened after quick pairing
13:16:01.829  services Success
13:16:01.968  connected         总耗时 7473ms
`

回报率样本：35.6Hz / 19.5Hz / 19.0Hz，min interval 0.3ms（一次通知多帧），max 150–262ms。

结论：Blade Plus_1B 厂商通道与主机模式无关；**DS4 模式下同样可以读取 L4/R4、C/Z、Fn 全部按键**。
之前 13:00 的 AccessDenied 属于旧配对/安全上下文异常，删除旧配对后重连即可恢复。
