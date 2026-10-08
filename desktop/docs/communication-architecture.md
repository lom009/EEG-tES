# EGG/tCS 通信架构

## 项目依赖与复用入口

通信相关项目统一为 .NET 10，不依赖 Avalonia 或桌面业务项目。完整使用方式见 [复用说明书](communication-reuse-guide.md)。

| 项目 | 职责 | 项目依赖 |
|---|---|---|
| Communication.Core | 传输契约、Session、协议适配、调度、诊断与失败上下文 | 无 |
| Communication.Udp | Dedicated/Shared UDP、绑定租约、定向发现 | Core |
| DeviceSdk | 设备身份、能力、语义事件、Manager、订阅与模拟设备 | Core |
| Protocol.EggtCs.Gen1 | 当前1.0.1及未来显式兼容修订的编解码、模块、命令表、能力及历史解码 | Core、DeviceSdk |
| DeviceRuntime | 通用协议注册、统一装配、发现协调、心跳协调、混合能力、恢复决策 | Core、UDP、DeviceSdk；不引用具体协议 |
| Avalonia 应用 | 配置加载、UI 线程、设备选择、实验编排、记录文件与导出 | DeviceRuntime、Gen1 与所需契约 |

应用中的 ConfigurableDeviceBackend 和 ConfigurableEggtCsDevice 保留为兼容适配器。桌面在装配入口显式注册 EggtCsProtocolModule，当前真实会话通过 EggtCsDeviceConnector 建立，应用不再自行复制 Session 装配流程。
通用网络发现、按能力选择来源和心跳暂停计数已经位于可复用库；最近设备文件保存、中文提示及单设备选择仍属于桌面。

## 协议注册与选择

DeviceSdk 定义 IDeviceProtocolModule 和 ProtocolConnectionContext。模块提供稳定标识、显式支持修订、候选匹配、物理连接、发现、验证、能力及命令支持信息。Gen1 当前实现并支持 V101（1.0.1）。

调用链：应用 AddProtocol → 模块 ResolveRevision（无 IO，仅返回修订/来源）→ Runtime 按解析结果选择唯一模块 → Manager 按 DeviceId 加锁 → Runtime 创建代次与上下文 → 模块用上下文 TransportFactory/SessionOptions 建立 Session → 通用 MixedSourceDevice → 模块验证（每次连接缓存一次）→ RuntimeDevice 统一暂停/释放。纯模拟无须注册协议；真实连接缺少模块时报错。

ProtocolConnectionContext 携带身份/修订、代次、取消相关 Session 选项、时钟、诊断、失败接收器、真实能力选择及心跳设置/回调。模块不得绕过该工厂自行创建默认 UDP socket，否则会失去 Runtime 的共享绑定及资源管理。模块只依赖 Core 和 DeviceSdk；Runtime 不知道具体命令、校验算法或 V101 能力常量。

明确 ProtocolId 时仅匹配对应模块；未指定时要求唯一匹配。零匹配不支持，多匹配报歧义。同一 DeviceId 已连接的协议/修订不一致时拒绝复用。重连固定所选协议和修订；只有断开后的显式新连接可以切换。Gen1 发现候选使用模块 Protocol.Revision 并标记 SoftwareDefault；旧最近设备的 CompatibilityAssumption 也采用该配置。Explicit 及已有值的 Unspecified 保留原版本；其他模块默认解析方法保持原候选。解析不改变身份/端点，不增加固件探测。

EggtCsRevisions.DefaultRevision 是新 Gen1 选项的统一默认入口，本次仍为1.0.1；构造函数内求值。Runtime 对公开连接请求解析一次，Manager 内部连接及重连消费已解析快照。重连发现只更新端点，保留原修订和来源。

RuntimeDevice 提供不可变 DeviceProtocolInfo，IDeviceProtocolBinding 的旧 ID/修订/支持查询继续保留，新快照成员有默认接口实现。快照表示软件规则，SupportsCommand 仅表示软件定义支持，不能识别未升级固件。设备卡片读取对应实例，模拟显示“模拟通信”、缺少绑定显示“未提供”、断开清除；显示文本不参与连接选择。

原 AddConnector/AddDiscovery/AddTransport 仍保留。模块接入统一能力混合；低层连接器自行装配能力，可通过 IContextualDeviceConnector 接收 SessionOptions。协议项目/类型已一次性源码迁移，不保留 V101 转发程序集。

## 连接与资源所有权

DeviceRuntimeBuilder.Build 只建立运行时对象，不主动连接，不读文件或环境变量。
runtime.Devices 是受管理连接入口；Manager 按 DeviceId 串行连接和移除，不同设备可并行。断开或故障的实例不会作为新连接返回。
Runtime 默认连接验证一次；桌面通过 DeviceConnectionCoordinator 调用 Runtime.ConnectVerifiedAsync，替代原来协调器中的一次状态查询，避免重复发命令。兼容构造方式中未提供 Runtime 时仍使用原验证入口。

应用通过 DI 管理 ConfigurableDeviceBackend 的运行时生命周期。Runtime 释放设备、共享 socket 和诊断工作队列；其公开 Connector 也经过同一 Manager 管理。
每次连接有新的代次；旧回调在恢复动作执行前核对代次和设备实例。手动断开、移除或外部重新连接会取消该设备正在进行的 Runtime 重连。

Session 正常 Stop 会取消等待发送锁、命令调度和响应的请求，结束旧事件流。再次 Start 创建新的通道与取消源。
Session Fault 是终态，Stop 仅清理资源，不使故障对象重新可用；重连创建新 Session。连接替换后调用方重新订阅新设备。

## 请求、协议与组帧

请求经过协议 Describe → 协议串行键 → 分配关联键 → 编码帧 → Transport → 匹配 Pending。
ISessionProtocol 的 CorrelationId 和 MessageId 不限制为一字节；同命令是否串行、事务键分配与匹配由协议实现决定。
LegacySessionProtocol 兼容原 IFrameCodec、IDeviceProtocolProfile、IResponseMatcher；V101 仍使用一字节 Index 与原有命令和负载。
非幂等命令不经过重试策略，Runtime 重连也不重放命令。

EggtCsFrameCodec.FeedResults 逐帧返回成功结果和解析错误，正常帧不会因相邻坏帧被丢弃。
旧 Feed 签名仍保留并在存在解析错误时抛异常。DatagramRemainderPolicy 默认 Preserve，兼容既有残帧缓存；显式 Discard 才在数据报结束时清理残帧。

当前 V101 默认 RequireMatchingResponseIndex=false，按响应命令相关。同类串行只能避免同时在途歧义，不能消除超时后的迟到响应。
启用 Index 匹配需要固件可靠回显。重建 UDP socket 本身不能证明旧报文不再到达。

## UDP 与发现

Dedicated 保持一连接一 socket，是桌面及 SDK 的默认值。相同本机端口不能用于多个 Dedicated socket。
Shared 在 Runtime 的 registry 内共享兼容绑定，按远端 IP+端口分发，会话与发现获得各自副本；一设备过载不阻塞另一设备。
每个路由有界，路由过载向对应消费者报错；socket 故障向所有受影响租约报错。最后一个租约释放后关闭 socket。

已建立的通配绑定可以服务指定网卡的请求；具体绑定不能在不中断已有连接的情况下升级为通配，冲突时明确拒绝。
临时端口先绑定，再把实际端口写入发现探针。Shared Runtime 通过持有绑定租约，保证临时端口在发现与后续连接之间保持一致。

DeviceDiscoveryCoordinator 支持全局广播、本地定向广播和受限子网单播。Runtime 可以按给定列表顺序发现并去重。
发现只运行已注册模块的发现器，不追加隐式 Gen1 探针；各模式内模块按注册顺序执行，候选按 DeviceId/ProtocolId/修订去重。桌面普通和分模式入口共用同一个 Gen1 模块。默认通用发现继续使用广播加小子网兜底；桌面分模式发现的开关、顺序、等待窗口和失败提示保持原配置行为。

## 事件、状态与心跳

Session 响应直接完成 Pending，设备事件进入独立 Control/Data 队列；Session 本身队列满依旧触发会话故障。
DeviceSdk 的每个订阅者拥有独立 Control/Data 容量，按该订阅接收顺序输出。可靠订阅过载只结束该订阅并报错；显示订阅可以明确选择丢最旧数据。
原 ReadEventsAsync 是可靠订阅兼容入口。SubscribeEvents 返回可释放订阅和 Ready，命令可能立即产生 Push 时先注册，再发送命令。

Runtime 提供心跳周期、失败时限及按设备暂停判断，模块装配其协议的心跳命令，由 Core Session 调度。Gen1 心跳成功会更新 SDK 状态并发布状态事件，不依赖 Avalonia 的电量回调。
HeartbeatPauseService 按设备计数，支持嵌套作用域。Runtime 默认对真实采集、刺激和阻抗操作保持暂停作用域，结束后释放。
桌面关闭此自动作用域，由原业务服务维持原时段，避免改变设备请求序列。心跳和超时使用可注入 TimeProvider，持续时间由单调时钟判断。

## 异常决策和重连

Session 先清理 Pending，再把失败上下文提交给 Runtime，收发循环不等待外部回调。
外部返回 UseDefault、FailCurrentOperation 或 ReconnectDevice，SDK 验证允许动作并执行。原始失败不会被回调改写为成功。
同设备回调串行、设备间隔离。默认等待两秒，超时、抛异常、非法动作降级 UseDefault，迟到结果失效。
故障上下文包含身份、SessionId、代次、命令、发送尝试标记、幂等性和响应歧义。Core 的 SessionOptions、诊断及失败上下文通过可选字符串 ProtocolId/ProtocolRevision 携带软件协议身份；Runtime 注入固定快照，裸 Session 可以省略，Core 不引用 SDK 或 Gen1。迟到回调诊断保持旧代次修订，不读取当前 UI 设备来补齐。
连接/初次验证失败先释放临时设备，再报告回调；此时尚无可恢复的已注册连接，只允许 UseDefault 或 FailCurrentOperation。自定义连接器可实现 DeviceSdk.IContextualDeviceConnector 接收 SessionOptions 中的代次、时钟、诊断与失败接收器，无需引用 Runtime。

Runtime 默认不自动重连；明确的重连决策执行一次重连。配置 ReconnectPolicy 后会按旧端点、同身份且同协议的发现端点（沿用原修订）、指数退避进行恢复，直到成功或取消。
主动断开不触发恢复。恢复只建立连接，不恢复设备操作或订阅。桌面自动连接协调器继续负责现有选择、最近端点和用户提示。

日志和通信诊断经 AsyncCommunicationDiagnostics 隔离，慢日志消费者不能阻塞收发；满时丢旧日志并计数。可靠 EEG 记录不走诊断日志队列。

## EEG、记录和兼容性

事件同时保留 RawPacketData（传输块）、RawFrameData（完整帧）、TransportReceiveId（Session 内接收编号）。
Transport 输入内存可以在下一次 MoveNext 时复用；Session 在跨异步边界保存前复制，帧数据也按事件持有。
分片帧的事件时间是最后一块完成组帧的接收时间；多个帧可共享一次接收编号。

桌面 EEGRAW1 格式版本2、版本1/2读取支持、UDP 写入语义及导出格式未变。
历史读取通过 IEegPacketDecoder 注入协议解码器，默认 EggtCsEegPacketDecoder 显式固定1.0.1规则，不随未来最新修订变化；单记录内多个 EEG 帧合并为完整采样批次。
实验标识、受试者、时间轴、滤波、数据库和导出留在应用层。

DeviceSdk/Runtime 已移除 SafeV101 默认装配依赖；Gen1 提供 EggtCsCapabilities 和模块 GetCapabilities。命令、名称、首次修订、默认超时、明确适用修订集中在 EggtCsCommandCatalog，使用通用 ProtocolCommandCatalog 做支持及超时解析。缺失覆盖采用协议默认值，不要求新增命令同步增加旧配置键。
新增命令须同步完成 SDK 类型化能力、Gen1 编解码、Runtime/混合包装及模拟实现，不能只改目录；修改旧命令须按选定修订保留旧行为。当前实现以 V101 为基线，不进行设备版本协商。
V101 采样率和选中通道目前是主机元数据，启停帧没有对应设备配置字段；本次不改变报文或虚构固件支持。

## 后续升级原则

当前六条 V101 请求、发现、心跳、响应匹配及历史解码构成兼容基线。后续工作以正式协议定义为依据，不根据版本号大小推断命令兼容性。

- 兼容修订继续维护同一 Gen1 项目和稳定 ProtocolId。增加明确修订常量与支持集合，逐条声明旧、新命令的适用修订，不因小更新复制协议项目。
- 命令变更同步维护语义请求/响应、目录元数据、Describe/Encode/Decode、类型化能力、Runtime 转发和真实/模拟能力选择。新增命令未配置超时覆盖时采用目录默认值。
- 修改已有负载、状态码或约束时按实际选定修订保留旧分支；已知正确的 V101 报文不随新增功能改写。新操作同时明确状态、事件、心跳暂停和资源清理。
- 验证旧命令、默认/显式修订选择、重连固定规则及宿主业务后，再调整 DefaultRevision 并回归默认装配。软件定义支持不等于设备固件支持；新增功能部署需确认固件前提，不靠超时推断版本或自动降级。
- 只有形成独立的帧格式、事务规则或协议体系时才新增协议项目，通过同一 IDeviceProtocolModule 注册。Runtime 不增加对具体协议的依赖；多个模块共存时明确匹配条件。
- 新传输通过 ITransport/ITransportFactory 注册，当前没有内置 TCP、串口或蓝牙驱动。传输扩展和协议修订分别维护；不能以增加传输为由改变旧协议字节。
- 历史文件继续采用固定 V101 解码规则。协议升级若影响持久化格式，应单独定义读取兼容和迁移；引用方统一固定、更新或回退通信项目源码。

## 配置和验证

桌面 JSON 未新增、删除、移动或重命名字段，schemaVersion 仍为1；环境变量映射不变。
Shared、订阅策略、回调等待上限属于 SDK 代码选项，不是新的桌面配置键。SDK 接入参数见 [代码选项](communication-reuse-guide.md#7-宿主需要配置的代码选项)，桌面字段与环境变量见 [配置生命周期](configuration-lifecycle.md#桌面通信配置与环境变量)。

物理校验仍为未验证固定校验。桌面现在显式通过统一连接器允许兼容算法；新 SDK 调用方默认拒绝。
DeviceRuntime.Tests 只引用 Runtime/Core/DeviceSdk，验证无 Gen1 的替代协议完整生命周期；Communication.Tests 验证 Gen1 与测试协议并存、原 Core/V101 字节行为及回环 UDP，不引用桌面或 Avalonia；Communication.Sample 默认两台模拟设备。
真实设备长时间采集、双设备、连接期间发现、拔网重连及端口释放仍须由集成方执行真机验收。验收之前桌面默认不切换 Shared。
