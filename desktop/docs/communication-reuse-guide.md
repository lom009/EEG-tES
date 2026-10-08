# 通信库跨项目复用说明书

本文面向需要在独立项目中连接设备的开发者，介绍源码引用、Runtime 装配、设备操作、事件处理和资源释放。当前 Gen1 实现的协议修订为 **V101（1.0.1）**。

## 1. 环境要求与项目引用

使用 **.NET 10**。通信库不依赖 Avalonia、桌面应用、数据库或依赖注入容器，不读取 AppData、配置文件或环境变量。宿主负责加载自己的配置，并通过代码选项传入。

| 使用场景 | 直接引用 | 说明 |
|---|---|---|
| 模拟设备 | `EGGtCSPlatform.DeviceRuntime` | 调用 `UseSimulation()`，不需要物理协议项目 |
| 当前真实设备 | `DeviceRuntime`、`Protocol.EggtCs.Gen1` | 显式注册 Gen1，Runtime 提供 UDP 与设备生命周期管理 |
| 自定义设备协议 | `DeviceRuntime`、自己的协议项目 | 通过 `AddProtocol` 注册 |
| 只使用传输或 Session | `Communication.Core`，按需引用传输项目 | 自行装配协议、调度和资源释放 |

项目完整名称均以 `EGGtCSPlatform.` 开头。Runtime 传递引用 Core、UDP 和 DeviceSdk，不引用任何具体协议；Gen1 只引用 Core 和 DeviceSdk。

在引用方项目中执行，按检出位置调整路径：

```powershell
dotnet add MyApp/MyApp.csproj reference ../EGGtCSPlatform/EGGtCSPlatform.DeviceRuntime/EGGtCSPlatform.DeviceRuntime.csproj
dotnet add MyApp/MyApp.csproj reference ../EGGtCSPlatform/EGGtCSPlatform.Protocol.EggtCs.Gen1/EGGtCSPlatform.Protocol.EggtCs.Gen1.csproj
dotnet build MyApp/MyApp.csproj
```

当前采用源码项目引用。保留通信项目之间的相对目录，统一固定到同一个 Git tag/commit，不混用不同提交的源码或 DLL。

## 2. 从模拟设备开始

以下为独立控制台的最小示例，无需 UI、DI 或配置文件，只连接并读取状态：

```csharp
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;

await using var runtime = new DeviceRuntimeBuilder().UseSimulation().Build();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

await foreach (var candidate in runtime.Discovery.DiscoverAsync(
    TimeSpan.FromMilliseconds(1), cancellation.Token))
{
    var device = await runtime.ConnectVerifiedAsync(candidate, cancellation.Token);
    var info = ((IDeviceProtocolBinding)device).ProtocolInfo;
    Console.WriteLine($"{device.Identity.DeviceId}: {device.State.BatteryPercent}%");
    Console.WriteLine($"{info.ProtocolId} · {info.ActiveRevision}; {info.RevisionSource}");
}
```

仓库提供可编译的 [Console 示例](../EGGtCSPlatform.Communication.Sample/Program.cs)，默认连接两台模拟设备：

```powershell
dotnet run --project EGGtCSPlatform.Communication.Sample
dotnet run --project EGGtCSPlatform.Communication.Sample -- --help
dotnet run --project EGGtCSPlatform.Communication.Sample -- --reconnect
```

Build 不主动连接。首次发现或连接时才使用网络资源；退出 `await using` 时释放 Runtime 拥有的资源。

## 3. 接入真实 Gen1 设备

### 装配与手动端点连接

```csharp
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

await using var runtime = new DeviceRuntimeBuilder()
    .WithOptions(new DeviceRuntimeOptions
    {
        NetworkSettings = () => new UdpNetworkSettings(
            "255.255.255.255", 30307, "0.0.0.0", 30302),
        UdpMode = UdpConnectionMode.Dedicated,
    })
    .AddProtocol(new EggtCsProtocolModule(new EggtCsModuleOptions
    {
        AllowUnverifiedChecksum = true, // 显式使用下述固定校验兼容方式
        Protocol = new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>()),
    }))
    .Build();

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var candidate = new DeviceCandidate(
    new DeviceIdentity(new DeviceId("known-device"), "EGG/tCS Gen1", null, null),
    new TransportEndpoint("udp", "192.168.1.102", 30307),
    EggtCsRevisions.DefaultRevision,
    IdentityIsProvisional: true)
{
    ProtocolId = EggtCsProtocolModule.Id,
    RevisionSource = ProtocolRevisionSource.SoftwareDefault,
};
var device = await runtime.ConnectVerifiedAsync(candidate, cancellation.Token);
Console.WriteLine($"{device.State.Connection}, battery={device.State.BatteryPercent}");
```

默认 `EggtCsUnverifiedFixedChecksum` 写入/检查固定 `FF FF`，未经过实际校验算法验证。物理连接默认拒绝该算法；兼容现有行为时像示例一样显式允许，或通过 `EggtCsModuleOptions.Checksum` 注入已验证的 `IChecksum`。

手工 DeviceId 由调用方管理。状态响应不能用于推断序列号，后续使用发现连接时应统一设备身份，避免重复登记。
`ConnectVerifiedAsync` 对同一次连接只执行一次状态验证；读取返回设备的缓存 State 不增加请求。

### 发现后连接

使用发现替代上述手动候选构造；先结束发现枚举，再建立 Dedicated 连接：

```csharp
DeviceCandidate? found = null;
await foreach (var item in runtime.Discovery.DiscoverAsync(
    TimeSpan.FromSeconds(1), cancellation.Token))
{
    found ??= item;
}
if (found is null) throw new InvalidOperationException("未发现设备");
var discoveredDevice = await runtime.ConnectVerifiedAsync(found, cancellation.Token);
```

普通发现使用已注册模块，默认 UDP 发现包含广播与小子网单播兜底。需要指定模式时：

```csharp
await foreach (var item in runtime.DiscoverModesAsync(
    [new(DiscoveryModeKind.GlobalBroadcast, TimeSpan.FromMilliseconds(500), "255.255.255.255"),
     new(DiscoveryModeKind.LocalBroadcast, TimeSpan.FromMilliseconds(500))],
    cancellation.Token))
{
    Console.WriteLine(item.Identity.DeviceId);
}
```

模式按传入顺序执行，每个模式中的模块按注册顺序执行；同一设备、协议和修订的候选去重。
不注册物理协议就不会发送隐式 Gen1 探针；自定义发现可通过 `AddDiscovery` 接入。

### 当前连接的协议信息

`EggtCsRevisions.DefaultRevision` 是新 Gen1 选项的默认入口，当前为1.0.1；`EggtCsRevisions.V101` 表示固定1.0.1规则。
读取已连接实例的 `IDeviceProtocolBinding.ProtocolInfo`，获取 ProtocolId、ActiveRevision 和 RevisionSource。该快照表示软件采用的规则，设备目前不能上报协议版本。

| 候选来源 | Gen1 解析方式 |
|---|---|
| SoftwareDefault / CompatibilityAssumption | 使用模块配置的修订 |
| Explicit | 保留指定修订，不支持则拒绝 |
| Unspecified 且已有版本值 | 保留调用方原值 |
| Unspecified 且版本为空 | 使用模块配置的修订 |

需要固定 V101 时，将候选版本设为 `EggtCsRevisions.V101`、来源设为 `Explicit`。
指定 ProtocolId 时仅考虑对应模块；未指定则须唯一匹配。零匹配报不支持，多匹配报歧义，同 DeviceId 的协议或修订冲突会拒绝复用。
`SupportsCommand(type)` 只表示当前软件定义支持，不能证明固件已实现；未提供绑定信息的自定义设备不应被当作采用默认协议。

## 4. 设备能力与多设备

设备通过类型化能力提供操作：

| 入口 | 当前用途 |
|---|---|
| `ReadStatusAsync` | 状态和电量 |
| `EegAcquisition` | 采集启动、停止 |
| `Stimulation` | 刺激配置、启动、停止 |
| `Impedance` | EEG 与刺激阻抗检测 |
| `Tolerance` | 电流增减 |

先检查能力是否可用，再调用其方法；处理返回的 `DeviceCommandResult`，不能把 Task 正常返回等同于设备执行成功。
V101 采集接口的通道选择和采样率属于主机处理参数，启停命令不下发采样率或采集通道配置。数据帧仍为32通道。

宿主可通过 Backend 混合真实、模拟和禁用能力：

```csharp
var backend = new DeviceBackendProfile();
backend.Sources[DeviceCapabilityKind.Stimulation] = DeviceCapabilitySource.Simulated;
backend.Sources[DeviceCapabilityKind.EegAcquisition] = DeviceCapabilitySource.Real;
// 传给 DeviceRuntimeOptions.Backend；其他项保持默认值。
```

全模拟使用 `UseSimulation()`。模拟连接不能配置 Real 能力；状态能力不能禁用。能力来源可通过 `device.CapabilitySource(kind)` 查询。

`runtime.Devices` 按 DeviceId 管理连接，同设备并发连接只建立一个实例，不同设备可以并行。`runtime.Connector` 也经过同一 Manager。
批量连接发现结果时，按需调用 `Task.WhenAll(candidates.Select(c => runtime.ConnectVerifiedAsync(c, token)))`；UDP 端口须符合下表约束。

| UDP 选项 | 适用方式及约束 |
|---|---|
| Dedicated（默认） | 每连接独立 socket，相同本机地址/固定端口不能被多个连接或发现同时占用 |
| Shared | 在同一 Runtime 内共享兼容绑定，按远端 IP+端口路由；设备会话与发现各收副本 |
| LocalPort = 0 | 先绑定取得实际端口，再编码发现回调端口；需确认设备支持临时回调端口 |
| Shared + LocalPort = 0 | Runtime 保留绑定租约，发现与后续连接复用实际端口 |
| 通配地址与具体网卡 | 已有通配绑定可复用；已有具体绑定不能在原端口直接升级为通配绑定 |

Shared 当前支持 IPv4，共享不跨 Runtime 或进程。单设备队列过载只影响对应路由，共享 socket 故障会影响所有使用它的会话。
需要为候选指定本机绑定时，使用 `UdpEndpoint` 并传入 `DeviceCandidate.ConnectionEndpoint`。
实际设备多连接或连接期间发现的行为需要在集成环境验证。

## 5. 事件、心跳与资源释放

先注册订阅并等待 Ready，再发送可能立即产生 Push 的命令：

```csharp
await using var subscription = device.SubscribeEvents(new DeviceEventSubscriptionOptions
{
    ControlCapacity = 256,
    DataCapacity = 512,
    DataPolicy = DeviceDataDeliveryPolicy.Reliable,
});
await subscription.Ready.WaitAsync(cancellation.Token);
// 在业务任务发送启动命令；此处持续消费。
await foreach (var item in subscription.ReadEventsAsync(cancellation.Token))
{
    Console.WriteLine(item.Event.GetType().Name);
}
```

每个订阅者有独立的 Control/Data 队列，每个订阅只允许一个 reader。Reliable 订阅溢出时以 `DeviceSubscriptionOverflowException` 结束，不静默漏记；其他订阅继续运行。
显示用途可选择 `DataPolicy = Latest` 丢弃旧数据并读取 `DroppedDataCount`，控制事件不会按该策略丢弃。Filter 应为轻量同步判断。
原 `ReadEventsAsync` 是可靠订阅入口；自定义设备使用默认流桥接时，Ready 仅表示首次读取已经启动，真正的订阅就绪语义由实现方保证。

事件中的 RawPacketData 是原始传输块，RawFrameData 是完整协议帧；TransportReceiveId 仅在该 Session 内标识接收块。不要把传输块当作完整帧。

Runtime 默认在真实持续操作期间持有心跳暂停作用域，停止、完成或断开后释放。宿主需要额外暂停时：

```csharp
using (runtime.HeartbeatPause.Pause(device.Identity.DeviceId))
{
    // 执行需要避免插入心跳的业务步骤。
}
```

暂停按设备计数，最后一个作用域退出才恢复。心跳成功更新 SDK 状态；宿主自行将事件投递到 UI 线程。

生命周期由宿主明确管理：

- 业务层先停止自己启动的采集/刺激，再结束订阅和释放资源；取消令牌或关闭连接不等同于设备停止命令。
- `Devices.DisconnectAsync(id)` 断开连接；`Devices.RemoveAsync(id)` 移除设备。再次连接会建立新实例。
- `ReconnectAsync(id)` 返回新实例，保留原协议修订；重新建立订阅，不继续使用旧设备对象。
- `await using` 释放 Runtime 创建的设备、Session、共享 UDP 资源及内部诊断队列；外部传入的工厂、发现器和诊断消费者自身仍由宿主管理。
- 裸 Session 正常 Stop 后可重新 Start，并重新订阅；Fault 后需创建新 Session。

## 6. 异常决策与重连

创建 Runtime 时注册回调：

```csharp
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;

var builder = new DeviceRuntimeBuilder()
    .OnFailure((context, token) =>
    {
        token.ThrowIfCancellationRequested();
        Console.Error.WriteLine(
            $"{context.DeviceId}/{context.ConnectionGeneration} " +
            $"{context.ProtocolId}/{context.ProtocolRevision}: {context.Exception.Message}");
        return ValueTask.FromResult(CommunicationFailureDecision.UseDefault);
    });
// 继续配置传输、协议及选项，然后调用 Build。
```

| 决策 | 行为 |
|---|---|
| UseDefault | 保留原失败结果，默认不重发、不自动重连；显式启用 ReconnectPolicy 后，会话故障可按策略恢复 |
| FailCurrentOperation | 结束关联请求，不重放；不表示设备操作已停止 |
| ReconnectDevice | 关闭旧连接并重建；不重放命令、不恢复采集/刺激或旧订阅 |

先检查 `context.AllowedDecisions` 再选择动作。初次连接或验证失败没有可恢复的已登记实例，只允许 UseDefault 或 FailCurrentOperation。
请求 Task 仍以原异常结束；回调不能改写为成功。用 try/catch 分别处理 `RequestTimeoutException`、`OperationCanceledException` 和通信异常。

回调在 Pending 清理后异步执行，同设备串行、不同设备独立，可以重入 Manager API。默认等待2秒；超时、异常或非法动作退回 UseDefault，迟到返回不执行。
回调应响应取消，不能等待同设备后续失败回调。上下文保留发生故障时的 SessionId、代次和协议修订，旧代次结果不能替换新连接。

默认重连策略关闭，显式 ReconnectAsync 只尝试一次。启用策略后先试原端点，再发现同身份、同协议的端点，按初始/最大延迟退避；软件默认来源的发现结果不会改变已固定的修订。
主动断开、移除或新的连接请求会取消该设备进行中的 Runtime 重连。

V101 默认按响应命令相关，超时请求的迟到响应仍可能匹配后续同类请求。WasSent 表示进入过发送调用，不能证明设备已收到；重建 UDP socket 也不能绝对排除迟到报文。
只有固件可靠回显 Index 时才开启 `RequireMatchingResponseIndex`。

## 7. 宿主需要配置的代码选项

Build 后将选项视为不可变；调整连接策略时创建新 Runtime。配置文件的组织和加载由宿主决定，SDK 不隐式读取任何 `EGGTCS_*` 环境变量。

| DeviceRuntimeOptions 选项 | 默认值 | 影响 |
|---|---|---|
| Backend.ConnectionSource / Sources | Real；耐受 Simulated，其他 Real | 选择各能力来源 |
| NetworkSettings | 广播255.255.255.255，设备30307，本机0.0.0.0:30302 | 建立连接/发现时读取 |
| UdpMode | Dedicated | Shared 需显式启用 |
| ValidateConnection | true | 新连接验证一次；ConnectVerifiedAsync 使用同次验证结果 |
| HeartbeatEnabled / HeartbeatInterval / HeartbeatFailureTimeout | true / 2s / 8s | 心跳周期及连续失败窗口 |
| PauseHeartbeatDuringOperations | true | 持续真实操作自动暂停心跳 |
| FailureHandlerTimeout | 2s，必须大于零 | 回调最大等待时间 |
| SessionOptions | 控制/数据队列各256，NoRetry，Reconnect Disabled | Session 调度、队列及恢复策略 |
| TimeProvider | TimeProvider.System | 请求超时、心跳及重连延迟时钟 |
| DiscoveryModes / DiscoveryNetworkResolver | null / null | 按需指定发现模式及网卡解析 |
| Diagnostics / ByteTrafficLogger | null / null | 异步诊断与原始字节记录 |

| EggtCsModuleOptions 选项 | 默认值 | 影响 |
|---|---|---|
| Checksum | EggtCsUnverifiedFixedChecksum | 固定 FF FF；可替换为已验证算法 |
| AllowUnverifiedChecksum | false | 显式允许固定校验用于物理连接 |
| Protocol.Revision | 构造时读取 DefaultRevision，当前1.0.1 | 选择新会话的协议规则；仅接受支持集合中的修订 |
| Protocol.RequireMatchingResponseIndex | false | true 时按 Index 与响应命令匹配 |
| Protocol 构造参数 timeouts | 空覆盖表；当前六命令默认1500ms | 按请求 Type 覆盖，必须为正时长，未指定则使用目录默认值 |

例如只覆盖状态查询超时：

```csharp
var protocolOptions = new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>
{
    [typeof(ReadDeviceStatusRequest)] = TimeSpan.FromSeconds(2),
});
// 传给 EggtCsModuleOptions.Protocol。
```

`EggtCsCommandCatalog.Commands` 提供命令类型、名称、首次修订、支持集合和默认超时。覆盖超时不改变采集/刺激时长，也不启用重试。

Runtime 通过有界异步队列隔离诊断消费者。`AsyncCommunicationDiagnostics` 默认容量2048，提供 DroppedCount 和 ConsumerFailureCount；日志不能作为可靠采集记录。

## 8. 扩展与统一升级

### 替换传输或协议

自定义传输实现 `ITransport` 与 `ITransportFactory`，通过 `AddTransport` 注册；专用端点实现 `ITransportEndpoint`，通过 Candidate.ConnectionEndpoint 传入。
收发和断开必须支持取消，接收缓冲区至少在下一次 MoveNext 前有效，Session 会复制跨异步边界保留的数据。TCP 等驱动需要另行实现，当前没有内置驱动。

自定义协议实现 `IDeviceProtocolModule`，通过 `AddProtocol` 注册。模块提供稳定 ID、显式修订集合、无副作用的修订解析、连接、发现、验证和能力信息。
使用 ProtocolConnectionContext 提供的传输工厂、SessionOptions、诊断、失败接收器与心跳回调，不绕过 Runtime 的资源管理。
`ISessionProtocol` 的关联键和消息标识不限制为一字节；旧 Codec/Profile 可由 LegacySessionProtocol 适配。

已有底层集成可继续使用 AddConnector/AddDiscovery。自定义连接器需要 Runtime 失败回调时，实现 `IContextualDeviceConnector` 并沿用收到的 SessionOptions。
项目依赖和协议内部维护原则见 [通信架构](communication-architecture.md)。

### 引用方升级步骤

1. 固定已验证的通信源码 commit/tag，记录宿主使用的修订和代码选项。
2. 整体更新所引用的通信项目，重新编译宿主，不单独替换某个 DLL。
3. 保留明确固定的旧修订；采用软件默认值的宿主在新建选项/连接时使用新默认。已有连接及正常重连不切换规则。
4. 运行通信回归及宿主业务测试，验证发现、验证一次、取消、重连、多设备隔离和资源释放。新增设备功能投入使用前确认固件实现。
5. 回退时统一恢复已验证源码版本并重新编译，检查宿主配置和持久化格式的兼容性。

在通信源码根目录执行：

```powershell
dotnet test EGGtCSPlatform.DeviceRuntime.Tests -c Release
dotnet test EGGtCSPlatform.Communication.Tests -c Release
dotnet build EGGtCSPlatform.Communication.Sample -c Release
dotnet run --project EGGtCSPlatform.Communication.Sample -c Release --no-build -- --reconnect
```

这些测试覆盖协议接入与通信行为，不能代替引用方设备上的真机验证。

## 9. 接入问题排查

| 现象 | 检查项 |
|---|---|
| 无支持协议 / 匹配歧义 | 是否 AddProtocol、ProtocolId 是否正确、解析后的修订是否支持 |
| 端口占用 | 多个 Dedicated 连接、并行发现、不同 Runtime 或其他进程是否占用相同绑定 |
| 发现成功但连接无响应 | 候选设备端口、本机回调端口、网卡、固件端口规则；UDP Connect 不代表设备已就绪 |
| 拒绝未验证校验 | 注入已验证 IChecksum，或显式使用兼容选项 |
| 命令超时 | 有效超时、响应相关规则、固件是否实现、是否存在迟到响应 |
| 订阅溢出 | 消费速度、过滤与容量；可靠记录不能靠丢弃数据掩盖问题 |
| 回调未触发重连 | 允许动作、回调时限、连接代次和 Runtime 生命周期 |
| 重连后无数据 | 重新获取设备和订阅，按业务需要重新启动操作 |
