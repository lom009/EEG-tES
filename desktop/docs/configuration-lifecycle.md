# 配置文件生命周期

## 通信架构重构的兼容说明

通信重构本身未修改配置结构。后续日志升级只新增和删除配置字段，`_meta.schemaVersion`
仍为 1，启动时通过结构同步更新日志配置，已有用户配置无需手动修改。能力来源切换备份、最近设备保存、
恢复出厂设置和启动同步继续使用原有流程。

完整配置路径、默认值、取值范围、环境变量优先级与行为影响见
[桌面通信配置与环境变量](#桌面通信配置与环境变量)。其他项目通过代码接入 SDK，见 [通信库复用说明书](communication-reuse-guide.md)。

共享 UDP、订阅交付策略和异常回调等待上限通过 SDK 代码选项传入，不是新的桌面 JSON 配置键。
不要把 `DeviceRuntimeOptions` 直接添加到用户配置；默认模板没有这些字段，启动结构同步会移除它们。
桌面继续使用 Dedicated UDP，并显式沿用当前校验兼容与心跳暂停行为。

通信 SDK 不读取 AppData 或 `EGGTCS_*` 环境变量。桌面适配层按原规则读取后传入 Runtime；
其他项目应自行完成配置加载。标准桌面路径中实际命令超时由 `V101RequestTimeouts` 及对应的
毫秒环境变量决定，`DeviceHeartbeat.ResponseTimeout` 保留校验和旧后备构造语义，不覆盖这些请求超时。

## Gen1 命名迁移不迁移用户配置

### 模拟模式与最近设备

`DeviceBackend.ConnectionSource=Simulated` 时，自动连接跳过最近记录中的真实端点，只连接模拟设备；
普通发现和分模式发现都不会执行真实网络探测。Runtime 同时拒绝真实端点及隐藏在 `ConnectionEndpoint`
中的真实端点，不创建物理通信会话或发送物理心跳。模拟能力与真实连接混合的场景仍须使用 `ConnectionSource=Real`。

模拟连接不会覆盖 `DeviceAutoConnection.LastDevice`，也不会把模拟端点端口写入真实网络设置。
已有真实设备记录原样保留；切回 Real 后继续优先恢复该记录。已有模拟记录可在模拟模式下读取，
真实模式会跳过它并按原顺序发现真实设备。无需删除配置、重填地址或修改 schemaVersion，模式切换备份和恢复流程不变。

### 协议字段兼容

源码协议项目改为 `EGGtCSPlatform.Protocol.EggtCs.Gen1`，当前实际修订仍为1.0.1。
`V101Protocol`、`V101RequestTimeouts`、`EGGTCS_V101_*` 保留原名、默认值和优先级；
应用把它们映射到 `EggtCsModuleOptions.Protocol`（`EggtCsProtocolOptions`），无需用户修改文件。
校验及未验证算法许可属于 `EggtCsModuleOptions`，不再属于 Runtime 通用选项，桌面显式沿用兼容许可。

最近设备保存结构不增加 ProtocolId 或版本来源字段。读取旧物理记录时只在内存补充 Gen1 标识和兼容假设，模拟记录保留 simulator 身份；
读取时 ProtocolVersion 原值保持为候选输入，Gen1 将兼容来源解析为模块的软件默认修订；成功连接后在原字段保存实际采用值。这使未来升级后的软件不被旧最近记录固定到1.0.1，不代表识别到了设备版本。模拟记录保持原身份；未传入 Runtime 的旧连接路径维持原行为。连接仍查询一次状态，不增加探测请求。
SDK 的命令默认超时允许部分覆盖；桌面原有六项校验和映射不变。

源码引用方使用当前 Gen1 项目和公共类型，按 [复用说明书](communication-reuse-guide.md#1-环境要求与项目引用) 配置引用并重新编译。
源码 API 的更新不等同于用户配置或 EEG 历史文件迁移。当前协议修订为 V101（1.0.1）。

## 软件修订选项与命令维护

当前仅支持 V101（1.0.1）。`EggtCsRevisions.DefaultRevision` 在 Gen1 选项构造时求值；这是代码入口，不是新增 JSON、环境变量或 UI 设置。旧 V101 超时键继续覆盖同一批命令；未来新增命令未配置覆盖时取命令目录默认值。需要代码覆盖或显式固定修订时创建新选项/连接，正在运行的会话及重连保持原修订。

设备卡片显示软件采用的修订，不用于反向配置连接。历史解码仍显式采用 V101；无配置或记录迁移。

## 桌面通信配置与环境变量

以下参数属于桌面宿主，SDK 不要求其他项目采用相同文件、字段或环境变量。当前 `appsettings.default.json` 的 `_meta.schemaVersion` 为1；文件同步与备份规则见下文。
下面的配置在应用启动时加载；手动修改文件后重启应用。SDK 本身不读取配置或环境变量。
TimeSpan 使用 `hh:mm:ss.fff` 字符串；布尔值使用 JSON true/false。

| 配置路径 | 当前默认值 / 范围 | 使用方式与效果 |
|---|---|---|
| `DeviceBackend.ConnectionSource` | Real；Real/Simulated | 决定物理或模拟连接 |
| `DeviceBackend.Capabilities.Status` | Real | 状态查询来源，不能 Disabled |
| `DeviceBackend.Capabilities.EegAcquisition` | Real | EEG 采集能力来源 |
| `DeviceBackend.Capabilities.Stimulation` | Real | 刺激能力来源 |
| `DeviceBackend.Capabilities.EegImpedance` | Real | EEG 阻抗能力来源 |
| `DeviceBackend.Capabilities.StimulationImpedance` | Real | 刺激阻抗能力来源 |
| `DeviceBackend.Capabilities.Tolerance` | Simulated | 耐受能力来源；其他能力项均允许 Real/Simulated/Disabled |
| `DeviceHeartbeat.Enabled` | true | 是否运行周期心跳 |
| `DeviceHeartbeat.Interval` | 2s，正值 | 查询间隔，须小于 DisconnectTimeout |
| `DeviceHeartbeat.ResponseTimeout` | 1.5s，正值 | 保留配置校验及旧后备构造语义；标准桌面路径实际请求超时由 V101RequestTimeouts 控制，须小于 DisconnectTimeout |
| `DeviceHeartbeat.DisconnectTimeout` | 8s，正值 | 连续失败窗口；操作暂停结束后重新计时 |
| `DeviceAutoConnection.Enabled` | true | 是否执行桌面自动连接流程 |
| `DeviceAutoConnection.DefaultDevicePort` | 30307，1～65535 | 默认目标端口，可被环境变量覆盖 |
| `DeviceAutoConnection.RetryInterval` | 2s，正值 | 一轮连接全部失败后的等待时间 |
| `DeviceAutoConnection.GlobalBroadcast.Enabled/Order/ResponseTimeout` | true / 10 / 500ms | 全局广播开关、顺序和等待窗口 |
| `DeviceAutoConnection.GlobalBroadcast.Address` | 255.255.255.255 | 必须为 IPv4 地址 |
| `DeviceAutoConnection.LocalBroadcast.Enabled/Order/ResponseTimeout` | true / 20 / 500ms | 定向广播开关、顺序和窗口 |
| `DeviceAutoConnection.LocalBroadcast.Address` | 空字符串 | 空值按网卡掩码计算，否则使用指定 IPv4 |
| `DeviceAutoConnection.SubnetUnicast.Enabled/Order/ResponseTimeout` | false / 30 / 500ms | 子网单播默认关闭 |
| `DeviceAutoConnection.SubnetUnicast.MaximumHostCount` | 254，1～254 | 限制枚举主机数量；扫描限制在本机 /24 |
| `DeviceAutoConnection.LastDevice` | null | 成功连接后保存身份、协议及端点，下一轮优先尝试；由应用管理 |
| `V101Protocol.RequireMatchingResponseIndex` | false | true 要求 Index+响应命令匹配；需先确认固件支持 |
| `SerialLog.Enabled` | true | 通信与操作日志的共同开关 |
| `SerialLog.MaxFileSizeBytes` | 10485760；正数 | 每类文件的大小阈值，默认 10 MiB |
| `SerialLog.RetentionDays` | 30；1～36500 | 保留的自然日数量，包含当天 |
| `SerialLog.MinimumLevel` | Trace | 操作日志最低等级：Trace、Debug、Info、Warning、Error、Fatal；不影响 bytes |

启用的发现模式 Order 必须为正数且不能重复；ResponseTimeout 必须为正值。模拟连接下所有启用能力均须 Simulated，不能使用 Real。
LastDevice 的 `DeviceId/Model/SerialNumber/MacAddress` 描述身份；`Scheme/Address/DevicePort/ProtocolVersion` 用于重建候选；`LocalAddress/LocalPort` 保存连接时本机信息。它不覆盖启动时读取的网络环境变量。桌面将旧物理设备记录转换为候选时只在内存补充 `eggtcs.gen1` 和 CompatibilityAssumption；模拟记录保留 simulator 身份，不增加保存字段。物理记录的原 ProtocolVersion 保留为输入，模块解析时采用软件配置；成功连接后该原字段保存实际采用修订，旧记录不会永久固定未来默认值。未传入 Runtime 的兼容路径保持旧行为。

### 请求超时覆盖

六项 JSON 超时默认均为 `00:00:01.500`，要求正值。对应环境变量为正整数毫秒，优先级高于 JSON；非法超时环境值报错，不静默替换。

| JSON：`V101RequestTimeouts.*` | 环境变量 |
|---|---|
| ReadDeviceStatus | EGGTCS_V101_TIMEOUT_STATUS_MS |
| ConfigureEegImpedance | EGGTCS_V101_TIMEOUT_EEG_IMPEDANCE_MS |
| ControlAcquisition | EGGTCS_V101_TIMEOUT_ACQUISITION_MS |
| ControlStimulation | EGGTCS_V101_TIMEOUT_STIMULATION_MS |
| AdjustCurrent | EGGTCS_V101_TIMEOUT_CURRENT_MS |
| ConfigureStimulationImpedance | EGGTCS_V101_TIMEOUT_STIMULATION_CONFIG_MS |

例如将采集控制响应等待时间设为两秒，可改 `V101RequestTimeouts.ControlAcquisition` 为 `00:00:02`，或在启动进程前设置 `EGGTCS_V101_TIMEOUT_ACQUISITION_MS=2000`。它不改变采集时长，也不启用自动重试。

| 网络环境变量 | 默认值与影响 |
|---|---|
| EGGTCS_UDP_BROADCAST_ADDRESS | 255.255.255.255；通用兼容发现入口使用；桌面分模式发现使用相应模式 Address |
| EGGTCS_UDP_LOCAL_ADDRESS | 0.0.0.0；本机绑定/网卡选择 |
| EGGTCS_UDP_DEVICE_PORT | 优先覆盖 DefaultDevicePort；启动默认30307，发现得到的候选端口优先用于该连接 |
| EGGTCS_UDP_CALLBACK_PORT | 30302；本机接收与发现探针声明的回调端口，桌面要求1～65535 |

### 保留在应用层的数据与操作配置

| 配置路径 | 默认值 / 范围 | 影响 |
|---|---|---|
| `EegAcquisition.SampleRateHz` | 500；250或500 | 主机时间轴及记录采样率；当前采集启停命令不下发采样率 |
| `EegAcquisition.DataPacketTimeout` | 2.5s，正值 | 采集数据看门狗 |
| `EegAcquisition.EnablePacketReordering` | true | 按包序号重排真实 EEG |
| `EegAcquisition.PacketReorderTimeout` | 50ms，正值 | 等待缺失/乱序包的窗口时间 |
| `EegAcquisition.PacketReorderWindowPackets` | 16，1～127 | 重排窗口包数 |
| `StimulationRun.ProgressPacketTimeout` | 2.5s，正值 | 刺激进度 Push 超时 |
| `StimulationRun.CompletionEventGracePeriod` | 2s，正值 | 本地阶段结束后的完成事件宽限 |
| `ImpedanceDetection.AutoStopStimulationWhenPassed/AutoStopEegWhenPassed` | true / true | 阻抗合格后自动停止检测 |
| `ImpedanceDetection.AutoStopWhenNotPassedTimeout` | 5s，正值 | 不合格检测自动停止时限 |
| `ImpedanceDetection.AllowConfirmationWhenFailed` | true | 是否允许界面在检测失败后确认 |

电极名称、物理映射、刺激界面参数约束和实验时序仍属于应用业务配置，不属于通信 Runtime 的必需依赖。

## 日志文件与操作记录

日志位于 `%LocalAppData%\EGGtCSPlatform\logs\yyyy-MM-dd`，以事件发生的本地日期归档，
时间戳包含本地时区偏移。操作日志包含时间、等级、来源、事件名称、关联 ID、消息和可选的完整异常链。
操作、全局异常及 UDP 独立/共享连接的收发日志统一使用 `DateTimeOffset.Now` 采集事件时间，
写入时保留该时刻，以固定文化格式 `yyyy-MM-ddTHH:mm:ss.fffzzz` 输出电脑本地时间及偏移。
电脑设为东八区时例如 `2026-09-21T16:30:45.123+08:00`，设为 UTC 时输出 `+00:00`，不固定东八区。
接收日志与通信包共享一次时间采集；通信包 `ReceivedAtUtc` 转为同一时刻的 UTC 表示，单调计时器保持不变。
六个等级分别写入 `trace.N.log`、`debug.N.log`、`info.N.log`、`warning.N.log`、`error.N.log`、`fatal.N.log`，
同时汇总到 `all.N.log`。`all` 不包含通信原始数据，也不会绕过 `MinimumLevel` 过滤。
UDP 收发数据仅写入 `bytes.N.log`，保留 TX/RX、端点、长度及完整十六进制内容。

每类文件按需创建，序号每天从 1 开始，达到大小阈值后递增；同日重启继续追加最大序号文件，
不重命名历史文件。单条超大记录保持完整，允许该文件超过阈值，下一条另开文件。
操作与通信使用独立队列，文件写入完成后刷新。通信上游仍采用容量 2048 的分发队列，
队列满时丢弃最旧通信记录，因此 bytes 不保证无丢包。

启动读取用户配置后及跨日后的下一次日志写入执行保留清理，默认保留当天和前 29 天。
只清理日期目录中已识别的日志分片，空目录随后删除；旧版 `BytesLog.log`、`error/Error.log`
及其他未知文件不迁移、不自动删除。单个输出失败不影响其他日志文件；失败输出在下一次日期维护时重试，
失败诊断使用 Debug 后备输出，不递归写入文件日志。

操作日志覆盖启动退出、登录结果、设备连接与断开、实验准备/开始/停止/结果、导出、删除、
配置保存与恢复、更新检查/下载/重启请求。批量导出只记录批次开始与结果汇总。
正常操作使用 Info，连接重试细节使用 Debug，拒绝登录及连接异常使用 Warning，
操作失败使用 Error，终止程序的未处理异常使用 Fatal；不记录密码、逐次点击或完整业务对象。

日志在配置加载前使用默认参数启动，成功加载配置后切换为用户参数，因此关闭日志时仍可能存在早期启动记录。
正常退出在业务收尾和服务释放后记录最终退出结果，然后关闭日志，最多等待 5 秒。
全局异常另外保留 `%ProgramData%\EGGtCSPlatform\lastcrash.json`；强制结束进程或断电无法保证退出日志。

## 配置文件与同步规则

应用使用两个配置文件：

- 安装目录的 `appsettings.default.json` 是当前版本的完整默认模板，由 Velopack 更新。
- `%LocalAppData%\EGGtCSPlatform\appsettings.json` 是当前用户的完整配置，程序实际从这里读取。

每次启动时，程序按照默认模板校准用户配置。已有且类型兼容的值会保留；新属性采用
默认值；默认模板已删除的属性会从用户配置删除。数组内容和顺序视为用户值而整体
保留，但对象数组中每个元素的字段会按照当前默认元素结构补充和删除。

`ApplicationUpdate.SourceBaseAddress` 是唯一例外。该字段虽然也存在于用户配置中，
但会在启动同步时强制恢复为当前默认模板的值，更新客户端也会显式采用默认模板的
值。构建脚本只修改发布目录里的默认模板。

设备连接页提供“恢复出厂设置”。确认后，程序会先备份现有用户配置，再将当前版本的
`appsettings.default.json` 原样覆盖到 AppData，并要求立即重启。该操作只恢复应用配置，
不会删除数据库、实验数据、采集记录、导出文件或日志。

两个文件都使用 `_meta.schemaVersion`。配置字段发生重命名、移动或类型变化时，必须
增加 schema 版本并提供顺序迁移；简单新增和删除由结构同步自动处理。用户配置版本
高于程序支持版本时，程序拒绝启动，避免旧程序破坏新格式。

同步需要修改用户配置时，会先在
`%LocalAppData%\EGGtCSPlatform\config-backups` 创建备份，再进行原子替换，只保留最近
5 份。配置损坏、迁移失败或校验失败时，程序显示错误文件路径并退出，不会静默删除
用户数据。
