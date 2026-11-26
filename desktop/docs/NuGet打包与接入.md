# 底层库 NuGet 打包与接入

脚本 `scripts/Pack-NuGet.ps1` 将九个独立类库构建为 NuGet 包，输出固定在解决方案根目录 `pack_nuget`。不打包桌面、示例、测试或安装程序，不上传包源，也不操作设备。要求 .NET SDK 10 或更新版本；支持 Windows PowerShell 5.1 和 PowerShell 7。输出仍为 `net10.0`。

## 执行方式

```powershell
# 仅解析当前 HEAD 的版本标签；不回溯旧提交或联网拉取标签
.\scripts\Pack-NuGet.ps1

# 手动版本优先，不要求安装 Git 或处于 Git 仓库
.\scripts\Pack-NuGet.ps1 -Version '1.2.3'
.\scripts\Pack-NuGet.ps1 -Version '1.2.3-rc.1+build.5'

# 同包身份已存在时，默认报错；显式覆盖只替换本次对应文件
.\scripts\Pack-NuGet.ps1 -Version '1.2.3' -Overwrite
```

从其他目录调用脚本的绝对路径也会输出到解决方案根目录，不取决于当前工作目录。九库使用统一版本、Release 配置。脚本构建**当前工作区**，包括尚未提交的修改；Git 标签只提供版本，不会切换源码。

## 版本规则

- HEAD 上支持裸 `1.2.3`、`v1.2.3`、`V1.2.3`，含轻量和附注标签；相同完整版本去重。没有有效版本或存在多个不同版本，要求显式 `-Version`，不交互选择。
- 手动版本必须严格符合 SemVer 2，无 v/V 前缀，无首尾空白。允许 `1.2.3-alpha.13`、`1.2.3-rc.1+build.5`；拒绝 `01.2.3`、`1.2.3.4`、`1.0.0-01`、空标识等。
- 主、次、修订号各限制在 0～65534，以兼容 SDK 自动生成的程序集版本。超过此范围即使符合 SemVer 语法也会明确报错，不截断。
- 完整版本统一传入 Version、PackageVersion、InformationalVersion；程序集信息版本不额外附加 Git 提交，Git 元数据单独记录。
- NuGet 包身份不区分构建元数据；`1.2.3+build.1` 与 `1.2.3+build.2` 会冲突。文件名去掉 `+...`，预发布标签转为小写；`.nuspec` 和清单保留完整版本。要并存发布，应使用不同预发布版本，例如 `1.2.3-ci.1`、`1.2.3-ci.2`。

## 包与资源

九个包均以 `EGGtCSPlatform.` 开头：Communication.Core、Communication.Udp、DeviceSdk、DeviceRuntime、Protocol.EggtCs.Gen1、Audio.Core、Audio.Dsp、Audio.Sdl、SpeechTraining。

内部项目引用转换为本次版本的包依赖；NuGet 默认生成最低版本约束，而不是自动锁死所有传递依赖。调用方若要严格固定整组版本，可使用锁定文件或显式固定各包版本。Audio.Sdl 保留 SDL3-CS 及 Windows/Linux/macOS 原生运行时包依赖，无需手工复制这些 DLL。

SpeechTraining 包包含语料、语料元数据和噪声（源码资源约 56 MB，包大小取决于压缩）。包内 `speechResources` 保存资源，`buildTransitive` targets 将其复制到消费项目构建及发布目录的 `SpeechCorpora`、`SpeechNoises`，包括间接包引用。原有源码 ProjectReference 的复制规则保持不变。

不要同时用源码引用和包引用引入同一模块。已有语料路径下的同名文件会按 MSBuild 的 PreserveNewest 规则处理，不要在生成目录手改内置语料；自定义语料应使用单独目录并显式传入提供器。

脚本先在唯一暂存目录完成全部打包，检查版本、依赖及训练资源，再发布九个包及清单。构建失败不会发布半套产物；发布失败会尝试回滚已有输出。`-Overwrite` 不清空目录，也不删除其他版本或无关文件。仅清理自己的暂存目录。`.pack.lock` 防止两个脚本并发构建和发布，不应与其他进程同时构建这些源码项目。

清单 `EGGtCSPlatform.<规范化版本>.manifest.json` 记录完整版本、包身份版本、版本来源、可获得的 Git 提交/工作区状态、SDK、时间及各包 SHA256。退出码非零表示失败；每个构建进程超时 600 秒。进程被外部强制终止或机器断电不保证事务完成，应检查输出与清单后重试。

## 控制台安装

先执行打包，再将实际输出路径添加为本地 NuGet 源：

```powershell
dotnet nuget add source 'D:\work_project\EGGtCSPlatform\pack_nuget' --name EggtCsLocal
dotnet new console -n DeviceConsole -f net10.0
dotnet add DeviceConsole/DeviceConsole.csproj package EGGtCSPlatform.DeviceRuntime --version 1.2.3
dotnet add DeviceConsole/DeviceConsole.csproj package EGGtCSPlatform.Protocol.EggtCs.Gen1 --version 1.2.3

# 可选：言语训练及默认音频播放器
dotnet add DeviceConsole/DeviceConsole.csproj package EGGtCSPlatform.SpeechTraining --version 1.2.3
dotnet add DeviceConsole/DeviceConsole.csproj package EGGtCSPlatform.Audio.Sdl --version 1.2.3
dotnet build DeviceConsole/DeviceConsole.csproj
```

预发布包使用 `--version 1.2.3-rc.1`。资源包不包含桌面应用配置文件；宿主仍需构造 Runtime 和训练设置。设备包用法见[通信复用说明书](通信库跨项目复用说明书.md)，训练接入见[训练复用指南](言语训练模块复用指南.md)。DeviceEnvelopeOutput 仍在主应用，本次打包不会将它抽取到训练包中。

覆盖同版本包后，NuGet 全局缓存可能仍使用旧包；正常升级应增加版本。验证时使用独立缓存，不依赖旧缓存中的同名包。

## 验证

```powershell
# 临时 Git 仓库、版本边界、路径空格、失败及回滚测试
.\scripts\Test-NuGetPack.ps1

# 先生成上文两个版本，再运行包消费验证
.\scripts\Test-NuGetPackages.ps1
```

包消费验证使用隔离缓存和源映射，只从 pack_nuget 获取本项目包；第三方 SDL 包可从现有缓存或 nuget.org 还原。检查直接/间接引用下的构建及发布资源哈希、SDL 运行时资产、完整程序集版本，并运行模拟设备和无声训练准备；不加载真实音频设备或发送刺激。验证命令具有超时，结束后清理自己的临时消费项目。
