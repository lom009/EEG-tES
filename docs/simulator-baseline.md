# EEG-tES 虚拟设备仿真基线

> 版本：v1.0  
> 日期：2026-07-28  
> 状态：M0 基线冻结  
> 范围：产品流程验证原型，不代表真实医疗器械、真实刺激输出或患者生理反应。

## 1. 架构基线

上位机需要分开处理两类数据面，不能继续由一个 React 组件同时伪造全部设备数据。

```text
React 产品流程
  └── DeviceSession
      ├── AcquisitionAdapter（采集数据面）
      │   ├── VirtualAcquisitionAdapter
      │   ├── ReplayAcquisitionAdapter
      │   └── RealAcquisitionGatewayAdapter
      └── StimulationAdapter（刺激控制面）
          ├── VirtualStimulationAdapter
          └── RealStimulationAdapter
```

采集数据面负责 EEG 连续帧、采集阻抗、REF/GND、采样率、事件标记和数据质量；刺激控制面负责方案编译、刺激通道、刺激阻抗、ARM、START、ABORT 和故障。两者通过统一 `runId`、设备时间和 marker 对齐。

真实设备到位前，React 与本地采集网关之间预留 WebSocket/IPC 边界。未来网关可接 BrainFlow、LSL、FieldTrip 或厂商 SDK；页面不直接依赖某一种板卡协议。

## 2. 当前页面、状态和数据来源

| 页面 | 主要状态 | 用户输入 | 产品派生 | 当前模拟/设备数据 | 目标数据源 |
|---|---|---|---|---|---|
| 首页 | 设备连接、耐受页签 | 串口、患者耐受反馈、测试人、备注 | 耐受记录匹配 | 连接状态由页面布尔值维护 | System adapter + 患者级耐受记录库 |
| 患者与实验 | 患者、实验上下文 | 患者 ID、姓名、实验 ID | 唯一实验上下文 | 无 | 产品数据库 |
| 刺激方案 | 范式、阵列、参数 | tDCS/tACS/tRNS/tPCS/Sham、参数 | 参数合法性、点位需求 | capability 文案为静态 | Stimulation capability |
| 电极与检测 | 刺激/采集点位、REF/GND、阻抗 | 点位、角色、采样率 | 冲突和完整性校验 | 阻抗用页面 `setTimeout` 直接变为通过 | 双 adapter 阻抗事件 |
| 时序与运行 | 模式、时长、阶段、循环、滤波 | 手动/自动、采集/刺激时长、循环 | 阶段编排、显示滤波开关 | EEG 为静态 SVG；计时由页面 interval 驱动 | EEG frame、marker、stage event |
| 结果 | 完成/中止、运行摘要 | 保存、导出、再运行 | 结果汇总 | 事件数量和时长来自页面状态 | Run Bundle |
| 历史记录 | 实验、多次运行 | 查看、复制配置 | 复制后清空物理检测 | 种子历史数据 | 产品数据库 + Run Bundle |
| 设备流程实验室 | 协议、下位机状态 | 方案和操作命令 | 编译与门禁 | `DemoDeviceAdapter` | 协议调试台 |

## 3. 当前设备数据生成位置

| 数据 | 当前实现位置 | 当前问题 | M2/M3 目标 |
|---|---|---|---|
| 连接状态 | `WorkflowPrototype.jsx` 的 `deviceConnected` | 页面直接切换，无握手和 capability | 由 `SYSTEM.CONNECTION_CHANGED` 驱动 |
| 阻抗 | `startDetection()` + `setTimeout` | 没有逐通道帧、单位、失败和停止确认 | 双 adapter 分别输出阻抗帧 |
| EEG | `WorkflowPrototype.jsx` 内固定 SVG path | 不连续、无真实采样点和时间戳 | 连续多通道 `EEG_FRAME` |
| 运行计时 | `runTimerRef` + `setInterval` | UI 时钟即设备时钟 | 虚拟设备时钟 + UI 派生显示 |
| 阶段变化 | `finishCurrentPhase()` | 页面直接决定设备阶段 | 编排器发命令、设备回传 stage |
| 事件 | `addEvent()` | 无序列、设备时间和请求关联 | 协议 envelope |
| 模拟下位机 | `demoDeviceAdapter.js` | 采集和刺激耦合；含旧耐受顺序 | 拆成采集/刺激 adapter |

## 4. 产品门禁基线

```text
设备连接
  → 首页完成患者级耐受记录
  → 患者与实验上下文
  → 刺激方案有效
  → 刺激/采集电极映射完整
  → 刺激阻抗通过 + 采集阻抗通过
  → 实验配置二次确认
  → ARM
  → 手动或自动运行
  → 完成/中止结果
```

耐受度是患者级历史记录，已前置到首页；本次实验只校验记录存在且与患者、刺激范式匹配，不再在阻抗之后重复执行耐受测试。有效期规则保留在 PRD 待办，本阶段不阻断。

## 5. 首批八个确定性场景

| scenarioId | 场景 | 预期结果 |
|---|---|---|
| `manual-happy-path` | 手动采集后手动开始刺激 | 产生一个完成 run |
| `auto-happy-path` | 自动按循环运行 | 完成全部循环 |
| `stim-impedance-fail` | 刺激通道高阻抗 | 禁止 ARM，指出通道 |
| `acq-impedance-fail` | EEG 通道高阻抗 | 禁止 ARM，指出通道 |
| `contact-degrades-during-run` | 运行中接触恶化 | 产生警告或按策略中止 |
| `transport-disconnect` | 运行中连接断开 | 中止并冻结数据 |
| `operator-emergency-abort` | 运行中急停 | 生成中止结果，不得记为完成 |
| `auto-restart-after-abort` | 自动模式中止后重跑 | 新 runId，从采集期开始 |

## 6. 数据语义约束

- tES“双通道”指一个源/阳极和一个回流/阴极的刺激回路，不使用容易与 EEG 双极导联混淆的 `Bipolar` 产品文案。
- EEG 参考电极与地电极是独立配置，不固定写成某块板卡的 SRB/BIAS/AGND。
- 采样率由 capability 提供。OpenBCI Cyton 常见 250 Hz，而不同设备/扩展板存在不同通道与分包方式，因此产品不能把 500 Hz 作为通用事实。
- 原始 EEG 与显示滤波副本分离。高通、低通、陷波开关不得改写原始数据。
- 任何模拟帧、回放帧和真实硬件帧必须显式标注 `SIMULATED`、`REPLAY` 或 `HARDWARE`。

## 7. M1 接口草案

### 命令

- System：连接、断开、能力查询、复位。
- Acquisition：配置采集、开始/停止采集、插入 marker。
- Stimulation：准备方案、开始/停止阻抗、ARM、START、ABORT。

### 事件

- System：连接状态、capability、ACK/NACK、fault。
- Acquisition：配置完成、开始/停止、EEG frame、marker。
- Stimulation：阻抗 frame/完成、准备、ARM、阶段变化、完成、中止。

所有消息至少含 `protocolVersion`、`messageType`、`deviceId`、`runId`、`sequence`、`deviceTimeUs`、`requestId`、`replyToRequestId` 和 `payload`。

## 8. M0 完成判定

- 当前所有设备样数据已找到生成位置。
- 产品输入、产品派生和设备事件已分开。
- 新耐受逻辑与 PRD 一致。
- 八个首批场景已固定。
- M1 命令、事件和 capability 边界具备实现依据。
