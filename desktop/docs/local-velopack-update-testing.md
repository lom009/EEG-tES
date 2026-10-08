# Velopack 多平台 HTTP 更新测试

项目使用 Velopack 1.2.0，并以自包含方式发布。安装目录中的
`appsettings.default.json` 提供完整默认配置，其中更新源 HTTP 基地址为：

```json
"ApplicationUpdate": {
  "AutoCheckOnStartup": true,
  "SourceBaseAddress": "http://127.0.0.1:8080"
}
```

每个自包含发布包运行时取得其实际 RID。程序会在基地址后追加对应路径，例如
`win-x64` 使用 `http://127.0.0.1:8080/win/x64`。构建脚本仍会拒绝不受支持的
RID。不同架构使用独立
Velopack feed，不能在目录之间混用包。

首次启动会依据该默认文件创建 AppData 中的完整 `appsettings.json`。以后更新时，
用户值按当前默认结构同步保留，只有 `ApplicationUpdate.SourceBaseAddress` 始终采用
新版本默认文件中的值。详细规则见 `docs/configuration-lifecycle.md`。

Velopack 1.2.0 不接受 32 位 `linux-arm` RID，因此自动更新矩阵不包含该目标。
Linux ARM 使用受支持的 64 位 `linux-arm64`。如果以后必须支持 32 位 ARM，只能
提供不带 Velopack 自动更新的普通自包含包，或等待 Velopack 增加该 RID。

真实自动更新必须从 Velopack 打包并按目标平台正常安装或启动的程序发起。通过
Visual Studio、`dotnet run`、`dotnet build` 启动程序，或者直接运行
`dotnet publish` 目录中的产物时，Velopack 的 `UpdateManager.IsInstalled` 为
`false`，项目会跳过启动更新检查。

普通编译或发布目录不包含 Velopack 管理的安装结构、版本记录和更新程序，因此不能
用于验证更新包下载后的文件替换、程序重启和版本切换。

## 1. 准备本机 Nginx

下载并解压 Windows 版 Nginx。Nginx 二进制不提交到仓库。启动模拟服务器：

启动脚本不会创建 `PublishPath`。首次使用时先完成第 2 节构建，使发布根目录存在，
再运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-LocalUpdateServer.ps1 `
  -NginxExecutable "C:\Users\Lenovo\Desktop\nginx-1.30.4\nginx.exe" `
  -PublishPath ".\TestArtifacts\Publish"
```

默认监听 `127.0.0.1:8080`，文档根目录是
`TestArtifacts\Publish`。健康检查地址为：

```text
http://127.0.0.1:8080/health
```

默认不限制更新包下载速度。如需模拟慢速网络并测试取消下载，可按 KB/s 传入限速：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-LocalUpdateServer.ps1 `
  -NginxExecutable "C:\Users\Lenovo\Desktop\nginx-1.30.4\nginx.exe" `
  -PublishPath ".\TestArtifacts\Publish" `
  -DownloadRateLimitKBps 128
```

`DownloadRateLimitKBps` 必须是非负整数，`0` 表示不限速。限速只应用于静态更新包，
不会影响 `/health` 和 `releases.<channel>.json`；Nginx 按每个连接限制速度，
并不是所有客户端共享同一个总带宽上限。

如需让局域网中的 macOS/Linux 机器访问，可使用：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-LocalUpdateServer.ps1 `
  -NginxExecutable "C:\Users\Lenovo\Desktop\nginx-1.30.4\nginx.exe" `
  -PublishPath ".\TestArtifacts\Publish" `
  -ListenAddress 0.0.0.0
```

同时应在 Windows 防火墙中仅向可信局域网开放 TCP 8080，并在构建时把
`SourceBaseAddress` 改成该 Windows 主机的局域网地址。

## 2. Windows 上构建并发布 Windows/Linux

不指定 `RuntimeIdentifiers` 时，一次生成并发布 Windows `x86/x64/arm64` 和 Linux
`x64/arm64`：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Publish-WindowsLinuxVelopack.ps1 `
  -Version 1.0.0-alpha.1 `
  -SourceBaseAddress "http://127.0.0.1:8080" `
  -PublishPath ".\TestArtifacts\Publish"
```

也可以只构建指定 RID。单目标直接传入 RID，多目标可传入数组或逗号分隔值：

```powershell
# 只构建 Windows x64
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Publish-WindowsLinuxVelopack.ps1 `
  -Version 1.0.0-alpha.1 `
  -SourceBaseAddress "http://127.0.0.1:8080" `
  -PublishPath ".\TestArtifacts\Publish" `
  -RuntimeIdentifiers win-x64

# 只构建 Windows x64 和 x86
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Publish-WindowsLinuxVelopack.ps1 `
  -Version 1.0.0-alpha.1 `
  -SourceBaseAddress "http://127.0.0.1:8080" `
  -PublishPath ".\TestArtifacts\Publish" `
  -RuntimeIdentifiers "win-x64,win-x86"
```

脚本先完成本次所选目标的编译和 Velopack 打包，全部成功后才发布。默认目标目录为：

```text
TestArtifacts\Publish\win\x86
TestArtifacts\Publish\win\x64
TestArtifacts\Publish\win\arm64
TestArtifacts\Publish\linux\x64
TestArtifacts\Publish\linux\arm64
```

Velopack 不允许同一个 Releases 历史目录重复打包相同版本。重建时必须增加
`-Version`，不要覆盖已经发布的版本号。

Windows 目录包含 Setup、MSI、Portable 和完整/增量包。发布脚本会将 Velopack 原生
一键 Setup 嵌入仓库提供的图形引导器，并以原来的 `*-Setup.exe` 文件名输出。用户运行
该 Setup 时可以输入路径或点击“浏览”自由选择安装目录；引导器再通过 Velopack 的
`--installto` 参数完成真正安装，因此安装后的目录结构和自动更新能力保持不变。

Velopack 自带的 MSI 中，`--instLocation Either` 只允许选择当前用户或所有用户安装，
并不提供任意目录浏览页。需要静默指定 MSI 路径时可使用
`VELOPACK_INSTALLDIR` 属性。已经安装过同一 `packId` 的用户如需迁移安装位置，应先
卸载旧版本，再运行新的 Setup；直接覆盖安装不会搬迁现有目录。
Linux 目录包含 AppImage 和更新包。原始 publish 目录保存在
`TestArtifacts\Velopack\<RID>`，不会由 Nginx 提供。

如需使用其他发布目录，可传入 `-PublishPath`。脚本会把可供 HTTP 服务直接读取的
Velopack feed 直接输出到该目录下的 `{platform}\{arch}`，不会隐式增加 `updates`
层。该路径也可以是 UNC 路径或已经挂载的共享目录：

```powershell
-PublishPath "D:\update-server\html"
```

`PublishPath` 对应 `SourceBaseAddress`，两边只追加完全相同的平台和架构路径：

```text
磁盘：{PublishPath}\win\x64\
URL： {SourceBaseAddress}/win/x64/
```

启动 Nginx 时必须传入与构建脚本完全相同的 `-PublishPath`。测试 Nginx 将该目录
直接映射到 URL 根 `/`。构建完成时脚本会输出每个平台的完整 release JSON 地址；
以 Windows x64 为例，应能直接打开：

```text
http://127.0.0.1:8080/win/x64/releases.win.json
```

只打开 `/health` 或 `/` 不能证明客户端所需的具体 feed 存在。旧 `/updates/` 路径
不再兼容，并由测试 Nginx 明确返回 404。

## 3. macOS 上构建并发布 macOS

macOS 必须安装 .NET 10 SDK、PowerShell 7、Apple Command Line Tools，并能够执行
仓库固定的 `vpk` 1.2.0。在 macOS 仓库根目录运行：

```powershell
pwsh ./scripts/Build-Publish-MacOSVelopack.ps1 `
  -Version 1.0.0-alpha.1 `
  -SourceBaseAddress "http://192.168.1.10:8080" `
  -PublishPath "/Volumes/update-server/html"
```

脚本生成并发布 `osx-x64`、`osx-arm64` 两个 `.pkg` feed。`PublishPath` 应指向
要存放 HTTP 静态产物的本地路径或已挂载共享路径。本阶段生成的 macOS 包未签名，
只能用于受控测试；正式分发前必须配置签名和公证。

macOS 脚本同样支持 `-RuntimeIdentifiers osx-x64` 或
`-RuntimeIdentifiers "osx-x64,osx-arm64"`；不指定时默认构建两个 RID。

### 版本号大小比对原则

应用版本遵循语义化版本格式：

```text
主版本.次版本.修订版本[-预发布标识][+构建元数据]
```

- 首先依次按数值比较主版本、次版本和修订版本，例如
  `2.0.0 > 1.9.9`、`1.1.0 > 1.0.9`、`1.0.1 > 1.0.0`。
- 主体版本相同时，正式版本高于任何预发布版本，例如
  `1.0.0 > 1.0.0-rc.1 > 1.0.0-beta.1 > 1.0.0-alpha.1`。
- 预发布标识按 `.` 分段从左到右比较。纯数字段按数值比较，因此
  `1.0.0-alpha.10 > 1.0.0-alpha.2`，不能按普通字符串比较。
- 对应段一方为数字、一方为文本时，数字段优先级更低；同为文本时按词法顺序比较。
  前面各段都相同时，段数更多的版本优先级更高，例如
  `1.0.0-alpha.1 > 1.0.0-alpha`。
- `+` 后的构建元数据不参与版本优先级比较，例如 `1.0.0+build.1` 与
  `1.0.0+build.2` 的更新优先级相同，不能依靠它触发升级。

综合排序示例：

```text
1.0.0-alpha.1 < 1.0.0-alpha.2 < 1.0.0-beta.1 < 1.0.0-rc.1 < 1.0.0 < 1.0.1 < 1.1.0 < 2.0.0
```

文档中的 `1.0.0-alpha.1` 用作首次构建示例。验证自动更新时，目标版本必须严格高于
当前已安装版本，例如在安装 `1.0.0-alpha.1` 后发布 `1.0.0-alpha.2`；相同版本不能
作为新更新发布，也不能在同一个 Releases 历史目录中重复打包。

## 4. 检查 HTTP feed

只要至少发布了一个平台，即可运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-LocalUpdateServer.ps1 `
  -SourceBaseAddress "http://127.0.0.1:8080"
```

脚本检查 `/health` 和七个预期 feed，并报告当前已发布的平台。排查客户端错误时，
应检查对应 RID 的 `releases.<channel>.json`；仅打开
`http://127.0.0.1:8080/` 查看目录不足以验证更新源。

## 5. 验证更新

1. 构建较低版本，并使用目标 RID 目录中的 Velopack 基线包完成安装或启动。
2. 用更高的 `-Version` 再运行同一构建脚本，向同一个更新源发布新版本。
3. 从安装目录或系统快捷方式启动已安装程序，确认登录窗口出现更新提示。
4. 点击“立即更新”，确认下载进度为 0–100%；点击“取消下载”后应停止下载并允许重试。
5. 重新下载更新，确认应用安全收尾、自动重启并显示新版本。

Windows 请使用 `*-Setup.exe` 验证自选安装目录。Linux 使用 AppImage，不存在安装目录
向导。macOS 使用 `.pkg`。

开发阶段如只需测试更新弹窗、下载进度、取消和重试等业务状态，可以注入模拟的
`IApplicationUpdateClient`。模拟客户端不具备 Velopack 的真实安装环境，不能替代上述
下载、文件替换、重启和版本切换测试。

## 停止服务器

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Stop-LocalUpdateServer.ps1 `
  -NginxExecutable "C:\Users\Lenovo\Desktop\nginx-1.30.4\nginx.exe"
```

所有构建物和动态 Nginx 配置均位于被 Git 忽略的 `TestArtifacts`。原有业务数据目录
`%LocalAppData%\EGGtCSPlatform` 不属于构建或清理目标。
