# EEG-tES Run Bundle v1.0

## 1. 目的

Run Bundle 是一次实验的可移植记录。它用于：

- 保存本次实验使用的患者快照、操作者、设备能力、方案、电极和检测结果；
- 保存原始 EEG 帧、设备事件、操作事件与最终结果；
- 在没有下位机时，从本地文件重放一次已完成或中止的实验；
- 明确区分 `SIMULATED`、`REPLAY` 与未来的 `HARDWARE` 数据来源。

它是原型内部格式，不等同于 EDF/BDF，也不代表已经满足医疗数据标准。

## 2. 可移植文件

当前网页导出单个 JSON 文件：

```text
<RUN_ID>.eegtes-run.json
```

文件使用 `EEG_TES_RUN_BUNDLE_PORTABLE_V1` 容器格式。容器内的逻辑文件与未来目录包保持一致：

```text
run-<RUN_ID>/
├── manifest.json
├── patient-snapshot.json
├── operator.json
├── capability.json
├── protocol.json
├── montage.json
├── tolerance-record.json
├── impedance.ndjson
├── eeg-raw.bin
├── events.ndjson
├── result.json
└── checksums.json
```

网页采用单文件封装，是为了便于浏览器下载、导入和演示；逻辑文件边界仍被保留。

## 3. 关键字段

`manifest.json` 至少包含：

| 字段 | 说明 |
|---|---|
| `bundleVersion` | 当前固定为 `1.0` |
| `runId` | 本次运行唯一标识 |
| `createdAt` | 数据包生成时间 |
| `sourceKind` | `SIMULATED / REPLAY / HARDWARE` |
| `sourceMetadata` | seed、模拟器版本、原始来源等追踪信息 |
| `files` | 逻辑文件清单、媒体类型与长度 |

`protocol.json` 保存刺激范式、二级模式、参数与运行时序；`montage.json` 保存刺激角色、采集点位、参考电极、地电极与采样率；`result.json` 保存终态、原因码、运行时长和阶段结果。

## 4. EEG 二进制编码

`eeg-raw.bin` 使用：

- IEEE 754 Float32；
- little-endian；
- channel-major；
- 每帧记录设备时间、采样率、通道顺序、样本数量和二进制偏移；
- 原始值保持不变，界面滤波只用于显示。

导入时先恢复二进制，再根据帧索引还原通道数据。设备时间不会因为回放倍速而重写。

## 5. 完整性校验

每个逻辑文件均写入长度与 FNV-1a 32 位校验值。导入前必须完成：

1. 容器格式检查；
2. Bundle 版本检查；
3. 数据来源检查；
4. 文件存在性检查；
5. 长度与校验值检查；
6. NDJSON 和 EEG 帧结构检查。

FNV-1a 用于发现演示文件的意外损坏，不是密码学签名，也不能证明数据未被恶意修改。真实设备与合规场景需要升级为 SHA-256、签名和受控存储。

## 6. 回放行为

`ReplayDeviceSession` 支持：

- 手动推进到指定设备时间；
- 实时回放；
- `0.5× / 1× / 2× / 4× / 8×` 倍速；
- 暂停与继续；
- 时间轴定位；
- 回放完成状态。

回放输出统一标记：

```json
{
  "sourceKind": "REPLAY",
  "sourceMetadata": {
    "originalSourceKind": "SIMULATED"
  }
}
```

因此，界面可重用原设备事件和 EEG 数据，但不会把回放数据伪装成实时硬件数据。

## 7. 原型操作

在“时序与运行”页面：

1. 完成或中止一次模拟实验；
2. 点击“导出 Run Bundle”；
3. 清空或刷新页面状态；
4. 在“本地回放”区域导入该文件；
5. 使用播放、暂停、倍速和时间轴检查事件与 EEG 回放。

当前回放只验证数据链路、时间语义和来源追踪，不模拟真实电流输出。
