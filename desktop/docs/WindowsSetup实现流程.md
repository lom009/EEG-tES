# WindowsSetup 实现与打包流程

本文按当前仓库实现说明 Windows 安装引导程序的生成方式、运行过程，以及它与 Velopack 远程更新和安装包体积的关系。构建入口及多平台参数另见[多平台打包指南](多平台打包指南.md)。

## 1. 三个程序各自负责什么

| 程序 | 来源 | 职责 |
| --- | --- | --- |
| `EGGtCSPlatform.Desktop.exe` | 发布 Desktop 项目 | 实际业务程序，安装后运行，也作为远程更新的目标 |
| 原始 `EGGtCSPlatform.Desktop-win-Setup.exe` | `vpk pack` | Velopack 安装器，负责安装 Desktop 发布内容 |
| `EGGtCSPlatform.WindowsSetup.exe` | 发布 WindowsSetup 项目 | WinForms 安装引导界面，选择目录、释放并启动内嵌的原始安装器 |

最终对外分发的 `EGGtCSPlatform.Desktop-win-Setup.exe` 是第三个程序的复制件。它沿用第二个程序的文件名，但已经包含外层引导界面。

Velopack 打包的对象是 **Desktop 的发布目录**。WindowsSetup 在这之后单独编译发布，不进入 Desktop 的更新包。

## 2. 主要源码入口

| 文件 | 作用 |
| --- | --- |
| `scripts/Build-Publish-WindowsLinuxVelopack.ps1` | Windows 构建入口，处理参数、平台检查和目标 RID |
| `scripts/VelopackBuild.Common.psm1` | 执行发布、Velopack 打包、WindowsSetup 包装和产物发布 |
| `EGGtCSPlatform.WindowsSetup/EGGtCSPlatform.WindowsSetup.csproj` | 配置 WinForms 项目、嵌入安装器资源及构建校验 |
| `EGGtCSPlatform.WindowsSetup/Program.cs` | 安装目录界面、资源释放、启动安装器及错误处理 |
| `EGGtCSPlatform.Desktop/Program.cs` | 调用 `VelopackApp.Build().Run()` |
| `EGGtCSPlatform/Services/ApplicationUpdateServices.cs` | 使用 `UpdateManager` 检查、下载和应用更新 |

## 3. 脚本整体流程

```text
Desktop 源码
  │ 第一次 dotnet publish
  ▼
Desktop 发布目录：业务 EXE、依赖、资源、运行时
  │ 写入更新源配置，然后 vpk pack
  ▼
原始 Velopack Setup.exe + 更新包 + MSI 等产物
  │ 将原始 Setup.exe 路径传给 WindowsSetup 项目
  │ 第二次 dotnet publish
  ▼
WindowsSetup.exe：目录选择界面 + 内嵌原始安装器 + 外层运行时
  │ 复制并覆盖 Releases 中同名分发安装包
  ▼
最终 Setup.exe + 原有更新包、MSI 等产物
  │ vpk upload local --regenerate
  ▼
PublishPath/<平台>/<架构>：最终分发文件及更新索引
```

以下以 `win-x64`、当前 HEAD 唯一有效标签为 `v1.0.0` 为例，脚本自动解析为版本 `1.0.0`。命令展示脚本核心参数；实际执行时由脚本计算路径。

### 3.1 调用入口并准备环境

在仓库根目录运行：

```powershell
.\scripts\Build-Publish-WindowsLinuxVelopack.ps1 `
  -SourceBaseAddress 'https://updates.example.com' `
  -RuntimeIdentifiers 'win-x64'
```

更新地址是示例，需替换为实际服务地址。省略 `PublishPath` 时，输出到 `TestArtifacts/Publish`。省略 `RuntimeIdentifiers` 时，入口默认构建其支持的全部 Windows/Linux 目标，而非只构建 x64。

入口导入公共模块并调用 `Invoke-VelopackBuildAndPublish`。公共模块先解析版本：显式 `Version` 优先，否则读取 HEAD 上以 `v/V` 开头的 SemVer 标签；多个有效标签时输入序号选择，没有有效标签时报错。随后校验更新源地址、目标平台和图标，执行 `dotnet tool restore`，然后逐个 RID 构建。

本例中的中间目录为：

```text
TestArtifacts/Velopack/win-x64/
├─ publish/1.0.0/   Desktop 发布内容
├─ Releases/        Velopack 产物，随后替换其中的 Setup.exe
└─ setup/1.0.0/     WindowsSetup 发布内容
```

### 3.2 第一次 dotnet publish：发布 Desktop

```powershell
dotnet publish EGGtCSPlatform.Desktop/EGGtCSPlatform.Desktop.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:Version=1.0.0 `
  --output <publish目录>
```

脚本先检查路径位于预期产物目录下，再清理当前版本的 publish 目录。发布结果包含业务程序、依赖和 .NET 运行时。

随后 `Set-PublishedUpdateSource` 修改发布副本的 `appsettings.default.json`，写入 `ApplicationUpdate.SourceBaseAddress`，并移除旧的 `Source` 字段。这一步发生在 Velopack 打包之前，因此配置会进入安装内容和更新包。

### 3.3 vpk pack：打包 Desktop 发布目录

脚本通过仓库本地工具执行：

```powershell
dotnet tool run vpk -- pack `
  --packId EGGtCSPlatform.Desktop `
  --packTitle EGGtCSPlatform `
  --packVersion 1.0.0 `
  --packDir <publish目录> `
  --mainExe EGGtCSPlatform.Desktop.exe `
  --runtime win-x64 `
  --channel win `
  --icon <icon.ico路径> `
  --outputDir <Releases目录> `
  --msi `
  --instLocation Either
```

`--packDir` 指向第一步的发布目录；`--mainExe` 指定安装后运行的业务程序。Windows 分支额外传入 `--msi` 和 `--instLocation Either`。

此时 `Releases/EGGtCSPlatform.Desktop-win-Setup.exe` 还是原始 Velopack 安装器。更新包及 MSI 等文件也由这一步生成。后面的自定义包装仅替换 EXE 安装包，MSI 不会因此获得 WinForms 目录选择界面。

### 3.4 第二次 dotnet publish：生成安装引导程序

仅 Windows 目标调用 `New-WindowsInstallLocationSetup`。函数先确认原始安装包存在，再准备 `setup/<版本>` 输出目录，然后执行：

```powershell
dotnet publish EGGtCSPlatform.WindowsSetup/EGGtCSPlatform.WindowsSetup.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:RequireEmbeddedVelopackSetup=true `
  -p:VelopackSetupPath=<原始Velopack安装包的路径> `
  -p:Version=1.0.0 `
  --output <setup目录>
```

安装引导界面的 C# 源码已经写在 `WindowsSetup/Program.cs` 中，脚本负责编译它，不会动态生成界面源码。项目目标框架为 `net10.0-windows`，开启 `UseWindowsForms`，输出类型为 `WinExe`。

核心参数含义：

| 参数 | 作用 |
| --- | --- |
| `VelopackSetupPath` | 指定要嵌入的原始 Velopack EXE |
| `RequireEmbeddedVelopackSetup=true` | 要求提供路径且文件存在，否则构建失败 |
| `--self-contained true` | 为外层引导程序携带运行时 |
| `PublishSingleFile=true` | 将外层程序发布为单文件 EXE |
| `IncludeNativeLibrariesForSelfExtract=true` | 将本机库纳入单文件发布，运行时按需释放 |
| `Version` | 为安装窗口显示版本提供程序集元数据 |

### 3.5 原始安装包如何进入另一个 EXE

WindowsSetup 项目声明了条件资源：

```xml
<ItemGroup Condition="'$(VelopackSetupPath)' != ''">
  <EmbeddedResource
      Include="$(VelopackSetupPath)"
      LogicalName="EGGtCSPlatform.EmbeddedVelopackSetup.exe" />
</ItemGroup>
```

编译器将指定 EXE 的字节保存为 WindowsSetup 程序集中的资源，再通过单文件发布形成外层 EXE。这不是合并两个程序的代码，而是把原始安装器作为二进制数据携带。

逻辑包含关系为：

```text
EGGtCSPlatform.WindowsSetup.exe
├─ WinForms 引导程序
├─ 外层 .NET / WinForms 运行时
└─ 程序集资源 EGGtCSPlatform.EmbeddedVelopackSetup.exe
   └─ 原始 Velopack 安装包
      └─ Desktop 应用及其运行时等安装内容
```

普通构建未传入 `VelopackSetupPath` 时，可以生成用于查看界面的程序，但它不包含原始安装器；点击安装会报缺少内置安装程序。正式打包脚本通过 `RequireEmbeddedVelopackSetup=true` 阻止这种不完整产物进入分发流程。

### 3.6 替换分发的 Setup.exe

第二次发布完成后，脚本确认 `EGGtCSPlatform.WindowsSetup.exe` 已生成，然后执行等价操作：

```powershell
Copy-Item `
  -LiteralPath <setup目录>/EGGtCSPlatform.WindowsSetup.exe `
  -Destination <Releases目录>/EGGtCSPlatform.Desktop-win-Setup.exe `
  -Force
```

顺序必须是先嵌入、后覆盖。覆盖发生时，原始安装器已经保存在外层程序的资源中，因此不会丢失，也不会形成自我嵌套。

该函数不修改 Desktop 的更新包。对外文件名保持原样，用户双击后首先看到自定义目录选择窗口。

### 3.7 发布最终产物并检查索引

所有请求的目标构建完成后，脚本逐个发布：

```powershell
dotnet tool run vpk -- upload local `
  --outputDir <Releases目录> `
  --path <PublishPath>/win/x64 `
  --channel win `
  --regenerate
```

发布时 EXE 已经替换成外层安装器。脚本随后检查目标目录是否存在 `releases.win.json`；不存在则报错。

`upload local` 的目标是磁盘目录，并不自动部署 HTTP 服务。需要让实际更新服务将该目录映射为：

```text
磁盘：<PublishPath>/win/x64/releases.win.json
地址：<SourceBaseAddress>/win/x64/releases.win.json
```

外部命令均经 `Invoke-CheckedCommand` 调用，非零退出码会中止脚本。脚本确认索引存在，不代表已经验证远端 HTTP 可访问或完成跨版本更新测试。

## 4. 用户运行安装包时发生什么

1. `Main()` 初始化 WinForms 并打开 `InstallLocationForm`。界面显示程序集版本，去掉版本信息中的 `+` 元数据后缀。
2. 默认安装目录为 `%LocalAppData%/EGGtCSPlatform.Desktop`；用户可输入路径或通过文件夹对话框选择。
3. 点击“立即安装”后，程序展开环境变量并通过 `Path.GetFullPath` 规范化路径。当前实现会将相对路径转换为绝对路径，并非严格拒绝相对输入。
4. 创建 `%TEMP%/EGGtCSPlatform/Setup/<GUID>/` 临时目录。
5. 调用 `GetManifestResourceStream("EGGtCSPlatform.EmbeddedVelopackSetup.exe")` 读取资源，写成临时 EXE，并刷新到磁盘。
6. 用 `ProcessStartInfo.ArgumentList` 传入 `--installto` 和用户选择的路径，启动原始安装器。
7. 若路径位于 Program Files、Program Files (x86) 或 Windows 目录内，设置 `Verb = "runas"` 请求提权。该判断只覆盖这些目录，不是通用的目录写权限检测。
8. 安装期间禁用输入、按钮和关闭控件，通过 `WaitForExitAsync()` 等待子进程。
9. 退出码为零时关闭引导窗口；非零退出码或异常会显示错误并恢复操作。`finally` 中尝试删除临时目录，删除失败会被忽略，因此可能留下临时文件。

真正安装 Desktop 的始终是释放出来的 Velopack 安装器。安装目录选择不会改变应用已有的 AppData 配置和数据存储设计。

## 5. 对远程更新的影响

按代码结构，安装外壳不改变正常的远程更新链路：

```text
首次安装：WindowsSetup → 原始 Velopack 安装器 → 安装 Desktop
远程更新：Desktop → UpdateManager → 更新索引和更新包 → 应用更新并重启
```

Desktop 入口保留 `VelopackApp.Build().Run()`。更新服务按当前 RID 解析更新源目录，创建 `UpdateManager`，使用 `CheckForUpdatesAsync()`、`DownloadUpdatesAsync()` 和 `ApplyUpdatesAndRestart()`。

后续更新使用 Desktop 更新包，不重新运行 WindowsSetup，也不下载外层 WinForms 运行时。包装步骤只替换安装 EXE；包标识、业务发布内容和更新包仍由 Velopack 流程管理。

需要单独验证受保护目录下的更新权限：首次安装通过 `runas` 提权，不代表普通启动的业务程序以后自动拥有管理员权限。本文结论基于源码流程分析，不代表已完成不同目录下的端到端升级测试。

## 6. 为什么会包含两套运行时

两次 `dotnet publish` 都指定了 `--self-contained true`：一次用于 Desktop，一次用于 WindowsSetup。因此最终安装 EXE 携带两套分别服务于两个程序的运行时内容，当前流程没有跨层去重。

可以将体积理解为：

```text
最终安装包体积 ≈ 原始 Velopack 安装包体积
               + 外层 WinForms 程序及其运行时
               + 打包附加开销
```

`PublishSingleFile=true` 表示单文件发布，不等同于压缩。当前脚本未显式开启单文件压缩。

此前本地检查发现，WindowsSetup 的 x64 Release 构建目录中 214 个 DLL 合计约 274.5 MiB。这个数字只是当时构建目录的观测值，不能作为最终安装包大小或精确重复运行时大小；准确增量应比较同一 RID、同一版本的原始 Velopack EXE 与包装后的 EXE。

这一成本主要影响首次分发大小，以及运行外层引导程序时的空间使用；后续更新包不包含外层引导程序。Desktop 自身的运行时仍属于其发布内容。

若后续需要优化，可以评估用原生 Windows 小程序承担目录选择、资源释放和启动安装器，以去掉外层 .NET 运行时。改为依赖目标机运行时的发布方式也会改变安装前提，需要另外处理运行时缺失。上述方案均未在当前代码中实现。
