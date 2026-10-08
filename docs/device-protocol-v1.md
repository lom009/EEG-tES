# EEG-tES 上位机设备协议 v1

> 版本：1.0-draft  
> 日期：2026-07-28  
> 目标：让虚拟设备、历史回放和未来真实设备适配器遵守同一产品契约。

## 1. 协议边界

协议分为三个 domain：

- `SYSTEM`：连接、身份、能力、复位、ACK/NACK、故障。
- `ACQUISITION`：EEG 配置、连续采样、采集阻抗和 marker。
- `STIMULATION`：刺激程序、刺激阻抗、ARM、START、ABORT 和阶段。

同一物理设备可以实现两个 adapter，也可以由独立 EEG 与 tES 设备分别实现；上层通过 `DeviceSession`、`runId` 和 marker 聚合。

## 2. 消息 envelope

```json
{
  "protocolVersion": "1.0",
  "messageType": "ACQUISITION.EEG_FRAME",
  "domain": "ACQUISITION",
  "deviceId": "SIM-001",
  "runId": "RUN-001",
  "sequence": 1204,
  "deviceTimeUs": 8274000,
  "requestId": null,
  "replyToRequestId": null,
  "payload": {}
}
```

- `sequence` 在单个 `deviceId` 内单调递增。
- `deviceTimeUs` 是设备单调时钟，不使用本地格式化日期代替。
- command 必须有 `requestId`；直接事件可以为空。
- ACK/NACK 通过 `replyToRequestId` 对应命令。
- 传输层到达时间由网关另记，不能覆盖设备时间。

## 3. 命令

| 命令 | Domain | 用途 |
|---|---|---|
| `SYSTEM.CONNECT` | System | 建立设备会话 |
| `SYSTEM.DISCONNECT` | System | 主动断开 |
| `SYSTEM.QUERY_CAPABILITY` | System | 查询真实能力 |
| `SYSTEM.RESET` | System | 从故障/中止状态安全复位 |
| `ACQUISITION.CONFIGURE` | Acquisition | 通道、REF/GND、采样率和原始格式 |
| `ACQUISITION.IMPEDANCE_START` | Acquisition | 开始采集电极阻抗检测 |
| `ACQUISITION.IMPEDANCE_STOP` | Acquisition | 停止采集电极阻抗检测 |
| `ACQUISITION.START` | Acquisition | 开始连续采样 |
| `ACQUISITION.STOP` | Acquisition | 停止连续采样 |
| `ACQUISITION.INSERT_MARKER` | Acquisition | 写入实验阶段或操作 marker |
| `STIMULATION.PREPARE` | Stimulation | 下发已校验程序 |
| `STIMULATION.IMPEDANCE_START` | Stimulation | 开始刺激阻抗检测 |
| `STIMULATION.IMPEDANCE_STOP` | Stimulation | 停止刺激阻抗检测 |
| `STIMULATION.ARM` | Stimulation | 满足门禁后进入 armed |
| `STIMULATION.START` | Stimulation | 执行已 armed 程序 |
| `STIMULATION.ABORT` | Stimulation | 紧急安全停止 |

患者耐受测试不是下位机运行阶段命令。它在首页形成患者级业务记录，`STIMULATION.ARM` 前由上位机门禁校验记录存在且匹配。

## 4. 事件

| 事件 | 关键 payload |
|---|---|
| `SYSTEM.CONNECTION_CHANGED` | `state`, `transport` |
| `SYSTEM.CAPABILITY_REPORTED` | capability 全量快照 |
| `SYSTEM.COMMAND_ACKNOWLEDGED` | `accepted`, `commandType` |
| `SYSTEM.COMMAND_REJECTED` | 标准 error |
| `ACQUISITION.EEG_FRAME` | `packetSequence`, `firstSampleIndex`, `sampleCount`, `sampleRateHz`, `channels`, `samples`, `sourceKind`, `droppedSamplesBefore`, `generatedDeviceTimeUs`, `deliveredDeviceTimeUs` |
| `ACQUISITION.PACKET_DROPPED` | `packetSequence`, `firstSampleIndex`, `sampleCount`, `reason` |
| `ACQUISITION.FRAME_REJECTED` | `packetSequence`, `firstSampleIndex`, `sampleCount`, `reason`, `candidateDeviceTimeUs`, `lastAcceptedDeviceTimeUs` |
| `ACQUISITION.CONTACT_CHANGED` | `positionId`, `valueKohm`, `quality`, `state`, `action` |
| `ACQUISITION.IMPEDANCE_FRAME` | `channels[]`, `valueKohm`, `quality` |
| `ACQUISITION.MARKER` | `markerId`, `label`, `sampleIndex` |
| `STIMULATION.IMPEDANCE_FRAME` | `channels[]`, `valueKohm`, `quality` |
| `STIMULATION.STAGE_CHANGED` | `stage`, `programTimeUs` |
| `STIMULATION.COMPLETED` | `protocolHash`, `summary` |
| `STIMULATION.ABORTED` | `reason`, `safeStateConfirmed` |
| `SYSTEM.FAULT` | 标准 error |

## 5. Capability

```json
{
  "capabilityVersion": "1.0",
  "identity": {
    "deviceId": "SIM-001",
    "deviceModel": "EEG-tES Virtual Device",
    "firmwareVersion": "SIM-1.0.0",
    "simulated": true
  },
  "transport": {
    "type": "IN_PROCESS",
    "protocol": "1.0"
  },
  "acquisition": {
    "channelCount": 8,
    "channelLabels": ["FP1", "FP2", "F3", "F4", "C3", "C4", "P3", "P4"],
    "supportedSampleRatesHz": [250, 500],
    "sampleFormat": "FLOAT32_UV",
    "timestampSource": "DEVICE_MONOTONIC_US",
    "referenceOptions": ["CONFIGURABLE"],
    "groundOptions": ["CONFIGURABLE"],
    "markerSupport": true,
    "impedanceSupport": true
  },
  "stimulation": {
    "channelCount": 5,
    "independentGeneratorCount": 1,
    "supportedParadigms": ["TDCS", "TACS"],
    "supportedTopologies": ["DUAL_ELECTRODE", "HD_4X1"],
    "impedanceSupport": true,
    "emergencyAbortSupport": true
  }
}
```

上位机只能展示 capability 与产品允许范围的交集。采样率、通道数、刺激范式和参数边界不得由页面写死为所有设备的通用能力。

## 6. EEG frame

```json
{
  "firstSampleIndex": 1000,
  "sampleCount": 25,
  "sampleRateHz": 250,
  "channels": ["F3", "F4", "C3", "C4"],
  "samples": {
    "encoding": "FLOAT32_LE_BASE64",
    "data": "..."
  },
  "sourceKind": "SIMULATED",
  "droppedSamplesBefore": 0
}
```

M2 初期可以使用 JSON 数组；M3 连续数据切换为二进制或 Base64 块。无论编码如何，采样点数、首样本索引、采样率、时间戳和通道数必须一致。

## 7. 阻抗 frame

采集阻抗和刺激阻抗属于不同 domain/adapter，但使用一致质量语义：

```json
{
  "measurementId": "IMP-001",
  "status": "RUNNING",
  "channels": [
    {
      "channelId": "STIM-1",
      "positionId": "F3",
      "valueKohm": 8.4,
      "quality": "EXCELLENT"
    }
  ],
  "sourceKind": "SIMULATED"
}
```

允许质量值：`EXCELLENT`、`GOOD`、`MEDIUM`、`POOR`、`BAD`、`DETACHED`、`UNKNOWN`。最终通过阈值由设备 capability 与产品安全策略共同决定。

## 8. 门禁

`STIMULATION.ARM` 前，上位机必须验证：

1. capability 与刺激程序匹配；
2. 患者耐受记录存在、完成且刺激范式匹配；
3. 刺激阻抗通过；
4. 采集阻抗通过；
5. 患者、方案、电极、采集计划完成二次确认。

任何历史配置复用都只能复用参数与点位模板，不能继承阻抗结果和本次实验的 ARM 状态。

## 9. 错误结构

```json
{
  "code": "IMPEDANCE_NOT_PASSED",
  "message": "刺激或采集阻抗尚未通过",
  "severity": "ERROR",
  "recoverable": true,
  "source": "STIMULATION",
  "details": {
    "channels": ["STIM-2"]
  }
}
```

P0 错误码覆盖协议无效、能力不匹配、未连接、采集配置无效、刺激程序无效、耐受记录缺失/不匹配、阻抗未通过、未 ARM、设备忙、序列缺口、设备时间回退、传输断开、电极接触丢失、操作员急停和硬件故障。

## 10. 实现映射

- 常量、工厂和校验：`src/deviceProtocolV1.js`
- adapter 结构断言：`src/deviceAdapterContract.js`
- 旧演示适配器：`src/demoDeviceAdapter.js`，M2 拆分
- 协议契约验证：`scripts/verify-device-protocol.mjs`
