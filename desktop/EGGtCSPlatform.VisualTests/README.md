# “时序与运行”视觉验收宿主

弹窗 AdornerLayer 迁移验收：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=dialog-adorner --timeout-seconds=30`。
使用正式 `MainView`、弹窗宿主和 `DialogService`，检查视觉层级、遮罩命中、双弹窗顺序、Esc 与禁止关闭、缩放、卸载重挂、DataContext 切换及实际确认弹窗的 ViewLocator 解析。
输出 `TestArtifacts/DialogAdorner.resized.png` 和成功标记 `DialogAdorner.passed.txt`；失败时输出 `DialogAdorner.error.txt` 并以非零退出码结束。

单采集模式场景：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=acquisition-only`。
校验采集阻抗可独立确认、确认弹窗隐藏刺激配置、运行页面仅有采集与消隐，以及消隐后正常结束；同时检查单模式左对齐标题与组合模式切换按钮。输出三种模式的电极配置、手动时序、自动两轮时序、完成和结果页截图。

模拟数据生成器场景：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=simulation-generator --timeout-seconds=30`。
该场景实际生成32例模拟数据，校验成功数量，并截图检查参数、电极表格、最小窗口、生成结果和无设备历史回放。
测试数据库与截图保存在 `TestArtifacts`，不会写入日常实验数据库。

该项目直接复用正式项目中的 `BasicView`、`ExperimentRunPageView` 和
`ExperimentRunPageViewModel`，只替换导航、运行时钟与模拟运行服务。启动后自动执行正式页面的手动三次操作流程（采集和消隐、刺激和恢复、末次采集），依次生成六张 1440×900 验收截图，并以退出码 `0` 结束。

```powershell
dotnet run --project EGGtCSPlatform.VisualTests
```

默认截图路径：

```text
TestArtifacts/ExperimentRunPage.timing-setup.png
TestArtifacts/ExperimentRunPage.automatic-setup.png
TestArtifacts/ExperimentRunPage.acquisition-hover.png
TestArtifacts/ExperimentRunPage.stimulation-frozen.png
TestArtifacts/ExperimentRunPage.completed.png
TestArtifacts/ExperimentRunPage.results.png
```

可通过参数覆盖输出目录和等待超时：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --output-directory=C:\temp\run-page --timeout-seconds=20
```

`--output=C:\temp\experiment-run.png` 仍兼容：它指定采集悬浮截图路径，其余四张截图在同目录派生文件名。若页面未在超时前完成场景，进程会以非零退出码结束，并在截图目录写入 `ExperimentRunPage.visual.error.txt`。每次启动都会清理上次同名产物，避免误判陈旧截图。

EEG 采集物理通道映射页可使用以下场景执行“修改映射、保存配置、重建页面并回显”的自动验收：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=eeg-mapping
```

验收截图输出为 `TestArtifacts/PhysicalChannelMappingPage.reloaded.png`。

设备连接配置页可分别使用标准字号和 130% 特大字号场景检查布局：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=device-connection
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=device-connection-extra-large
```

刺激电极分板块点位选择页可使用以下场景生成 1440×768 验收截图：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=electrode-configuration
```

验收截图输出为：

```text
TestArtifacts/ElectrodeConfigurationPage.role-sections.png
TestArtifacts/ElectrodeConfigurationPage.stimulus-detecting.png
TestArtifacts/ElectrodeConfigurationPage.acquisition-detecting.png
```

刺激模式导航的共享指示条与上下内容切换动画可使用以下场景验收：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=stimulus-configuration
```

该场景会校验正向、反向动画中间帧坐标、最终位置、滚动归零、最小窗口尺寸和关闭动画配置，并在 `TestArtifacts` 输出对应截图。

历史实验的搜索、筛选、分页及运行记录展开动画可使用以下场景验收：

```powershell
dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=start-experiment-history
```

该场景构造 23 条历史实验，校验初始 10 条、第二页、20 条页大小、搜索结果、筛选重置、批量导出选择工具栏和 1200×760 最小窗口布局，并继续覆盖已选运行记录的展开与收回动画。
