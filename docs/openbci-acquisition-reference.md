# OpenBCI 采集接入参考

> 状态：架构参考，不代表当前原型已连接 OpenBCI 硬件。  
> 参考来源：OpenBCI GUI 与 EEGsynth 的 OpenBCI 接入说明。

## 1. 对当前产品最重要的结论

OpenBCI 资料进一步验证了采集与刺激需要拆成两个设备域：

- **采集域**负责 EEG 通道、采样率、参考电极、地电极、连续数据帧和采集阻抗。
- **刺激域**负责刺激范式、刺激阵列、刺激通道、刺激阻抗、ARM、START 与 ABORT。
- 两个设备域通过同一个 `runId`、设备单调时间和 marker 对齐，而不是假设它们使用同一串口或同一硬件。

OpenBCI 文档中的 `monopolar / bipolar` 描述的是 EEG/EMG/ECG 的采集参考方式，
不等同于本产品里的“双通道刺激阵列”。产品界面必须避免把这两个概念混在一起。

## 2. 可借鉴的接入拓扑

参考实现提供了两种有价值的采集网关形态：

```text
OpenBCI 硬件
  ├── 直接网关 → FieldTrip buffer → AcquisitionAdapter
  └── OpenBCI GUI → LSL → AcquisitionAdapter
```

因此真实硬件接入时，上位机不应直接依赖某个串口实现，而应允许以下采集来源：

| 来源类型 | 用途 | 当前原型对应 |
|---|---|---|
| `VIRTUAL` | 无硬件流程与数据仿真 | `VirtualAcquisitionAdapter` |
| `REPLAY` | 回放真实或模拟实验数据包 | M5 实现 |
| `LSL` | 接入 OpenBCI GUI 或其他 LSL 数据源 | 真实网关候选 |
| `FIELDTRIP` | 接入 EEGsynth/FieldTrip buffer | 真实网关候选 |
| `DIRECT_DEVICE` | 厂商 SDK、USB dongle 或串口直连 | 待硬件协议确认 |

## 3. 能力发现而不是写死型号

OpenBCI 生态包含不同通道数和传输方式的设备。上位机应在连接后读取 capability，
再决定开放哪些采集配置：

- 设备标识、固件版本和数据来源；
- 可用 EEG 通道数与通道标签；
- 支持的采样率；
- 参考电极与地电极配置方式；
- 是否支持阻抗检测；
- 是否支持 marker；
- 时间戳来源和数据分包格式。

具体设备的采样率、增益、量程和数据格式必须以目标硬件与当前固件资料为准，
不能由旧版第三方文档直接固化进生产配置。

## 4. 对虚拟设备的直接要求

为了让无硬件阶段的产品流程可迁移到真实接入，虚拟设备必须：

1. 以连续帧输出 EEG，而不是由页面绘制随机曲线；
2. 每帧包含通道、采样率、样本序号、设备时间和来源标识；
3. 参考电极、地电极和采样率进入采集配置并接受 capability 校验；
4. 阻抗检测由采集适配器返回；
5. 刺激阶段开始、结束和急停写入采集 marker；
6. 页面只消费事件，不根据页面计时器伪造设备完成。

## 5. 当前不直接照搬的内容

- 不把 OpenBCI GUI 当作本产品的交互模板；本产品包含刺激安全门禁和实验闭环。
- 不把 OpenBCI 的采集参考拓扑写成刺激阵列。
- 不在没有目标硬件协议时实现伪串口命令。
- 不把模拟 EEG、阻抗或刺激输出描述为真实患者数据。

## 6. 后续落地顺序

1. 继续完善 `VirtualAcquisitionAdapter` 的动态 EEG、阻抗和故障场景。
2. 定义 `AcquisitionGateway` 契约，保证 `VIRTUAL / REPLAY / LSL / FIELDTRIP` 可互换。
3. M5 实现 Run Bundle 和 ReplayAdapter。
4. 拿到真实采集硬件后，用同一组契约测试验证真实网关。
5. 拿到刺激下位机协议后，单独实现 `RealStimulationAdapter`，不修改采集网关。

## 参考

- [OpenBCI GUI](https://github.com/OpenBCI/OpenBCI_GUI)
- [EEGsynth OpenBCI 接入说明](https://github.com/eegsynth/eegsynth/blob/master/doc/openbci.md)
