# “时序与运行”视觉验收宿主

电极页阴阳极显示验收：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=electrode-configuration-role-labels-3d --output-directory=TestArtifacts/ElectrodeRoleLabelsComplete --timeout-seconds=120`。
枚举全部范式、方向、阵列和 Sham 模式，多靶点使用两个独立靶点；检查按钮、列表、通道选项、头模提示和确认摘要的显示文案及通道分配不变。输出 `Roles.*.png`、`RoleLabels.passed.txt`，并使用双向 tACS 验证原生 3D A/C 标签及退出重入。需正常桌面环境和 WebView2。

电极页导航布局验收：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=electrode-configuration --output-directory=TestArtifacts/Navigation --timeout-seconds=60`。
覆盖真实范式页与电极页双向动画、快速连续切换、1440×768 / 1200×760 窗口及关闭动画；检查公共容器和头模尺寸稳定、返回按钮命中，并与原宿主的静止布局比较页面位置和尺寸。输出 `Navigation.*.passed.txt` 尺寸采样及原布局/当前布局截图。`electrode-configuration-3d` 额外检查导航离开时同步释放原生 3D 视图及重入加载。

数字输入框圆角验收：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=numeric-corners --output-directory=TestArtifacts/NumericCorners`。
输出时长与刺激参数输入框在普通、聚焦、悬停、按下、禁用和隐藏增减按钮状态下的 100% / 150% 绘制截图；验证按钮命中、点击、长按重复增减及文本提交。
同时验证手动输入／粘贴在回车及失焦时对齐步长，覆盖 0.04 mA 电流、负数及仿真数值输入。输入过程中允许暂存未对齐值，提交时取范围内最近的零基准步长倍数，中点向远离零的方向取整；时长输入继续沿用原有独立校验。

UI 合并验收新增 `--scenario=login` 和 `--scenario=home`，分别检查登录输入与记住密码状态、首页标准及小窗口布局。刺激配置的 `--scenario=stimulus-configuration-figma-tacs` 检查新版各刺激模式、通道输入尺寸与分配状态。

`--scenario=electrode-configuration-3d` 使用本地模型和模拟设备，检查 34 个电极、点位同步、检测期间禁止编辑、卸载重载和加载失败后的 2D 回退，再继续原有通道映射回归。需要 Windows WebView2 和可用的本机 HTTP 监听器；视觉宿主复用桌面程序的 Windows manifest。成功输出 `head3d.passed.txt`。

言语训练继续使用现有 `envelope` 场景，保留真实语料与可控音频播放器的回归检查。`speech-scoring-150dpi.png` 为整体 1.5 倍绘制的验收图；实际窄视口缩放由 `speech-layout-scale150` 及其滚动后截图验证。

训练设置中，测试模式紧接句表号，载体参数连续排列，刺激延时位于语速／当前模式状态下方；新建配置默认延时为 0 ms。`envelope` 场景中的 `speech-rir-carrier-group` 和 `speech-rir-carrier-group-small` 截图覆盖混响模式下声码器参数的连续排列及窄窗口布局；RIR 参数截图通过定位控件滚动，避免依赖固定滚动偏移。

弹窗 AdornerLayer 迁移验收：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=dialog-adorner --timeout-seconds=30`。
使用正式 `MainView`、弹窗宿主和 `DialogService`，检查视觉层级、遮罩命中、双弹窗顺序、Esc 与禁止关闭、缩放、卸载重挂、DataContext 切换及实际确认弹窗的 ViewLocator 解析。
输出 `TestArtifacts/DialogAdorner.resized.png` 和成功标记 `DialogAdorner.passed.txt`；失败时输出 `DialogAdorner.error.txt` 并以非零退出码结束。

单采集模式场景：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=acquisition-only`。
校验采集阻抗可独立确认、确认弹窗隐藏刺激配置、运行页面隐藏模式区域且仅显示一个采集阶段，自动切换被拦截，以及采集后直接正常结束；同时检查单模式左对齐标题与组合模式切换按钮。输出三种模式的电极配置、单采集时序、运行和结果页截图；校验单采集与组合模式完成页的“结束实验”和“再次运行此次实验”按钮，并输出标准及 1024×640 窗口截图。

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

## 3D 与弹窗叠层回归

`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=head3d-dialog --timeout-seconds=90`

使用正式 MainView、弹窗宿主和真实 NativeWebView，检查双层弹窗、退出动画期间隐藏、网页选点拦截、同实例恢复、最新状态同步、加载期间弹窗、2D 切换及页面重入释放。

原生截图与鼠标点击验收使用 `--scenario=head3d-dialog-interactive --timeout-seconds=600`。自动检查后旋转头模，点击工具栏 Ordinary / Envelope，分别验证关闭和确认；Cancel dialog 验证取消按钮，最后点击 Finish。宿主会验证每次关闭后的浏览器实例、选点及投影位置（视角）一致，输出 `Head3DDialog.native-input.txt`。此场景仅使用模拟数据，不下发设备命令。原生窗口叠层必须用桌面截图复核，不能用 Avalonia 离屏截图替代。

## 包络-tACS 言语训练界面演示

语料评分配置验收同样使用 `envelope` 场景，增加 `speech-corpus-config-*` 截图及 `speech-corpus-config.passed.txt`。使用输出目录中的独立演示语料，验证有效配置、缺失配置、损坏 JSON、句子字数不符、动态禁用自适应选项、保留模式选择、开始按钮状态、窄窗口错误说明及固定模式播放；结果页显示实际评分规则，旧记录显示“未记录”。

自适应语速迁移沿用 `envelope` 场景，增加 `speech-adaptive-*` 截图及 `speech-adaptive.passed.txt`。覆盖开始前模式选择及未实现选项禁用、1.5 倍首句、播放中提前保存只推进一次、播放结束后载体仍锁定、窄窗口自动语速信息、完整 SRT、提前结束、样本不足、尝试修改载体后重播及后续句子仍沿用原载体，以及逐句详情展开。默认固定语速、两图时间对齐及独立缩放继续执行原回归。

刺激预览使用研究算法的幅值，经 PCHIP 插值调制 200 Hz 连续正弦波。检查 `speech-start` 为无波形网格，`speech-playing` 为浅紫细密交流波形、深紫连续上实线/下虚线，无矩形平台或阶梯轮廓；默认、缩放和窄窗口场景同时校验实际音频时间轴、最大电流、延时与缓存绑定。配置页的固定包络示意图保持原有验证。

滚轮缩放验收覆盖绘图区和 Y 轴命中、独立缩放、触控板小数步长、范围上下限以及窄窗口的图内拦截/图外滚动。输出 `speech-zoom`、`speech-zoom-minimum`、`speech-small-zoom` 和 `speech-small-zoom-minimum` 截图；蓝红曲线与刻度共享语音范围，刺激预览使用独立范围。

`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=envelope --output-directory=TestArtifacts/SpeechTraining`

复用配置、阻抗确认入口后，验证默认 0 原速、播放前语速绑定、训练开始后语速与载体持续锁定；以 2 字/秒播放 7 字句，验证时长为 3.5 秒。播放结束后尝试调为 3 字/秒或更换载体，`speech-rate-locked`、`speech-carrier-locked` 截图验证输入仍禁用、设置与当前音频保持不变。下一句及结果快照沿用 2 字/秒和原始语音；同时检查上下图、真实 UI 计时、逐字评分和三句汇总。
生成 `speech-start`、`speech-playing`、`speech-scoring`、`speech-results`、空结果及 1000×650 小窗口截图；小窗口校验无横向溢出且滚动后可访问结束按钮。
此流程仅使用内存演示数据，不播放音频、不下发刺激、不保存实验结果。

言语训练视觉细节验收还检查按钮及输入控件的实际禁用色、禁用悬停状态、下拉箭头、输入框内外圆角和共享时间轴对齐。输出未评分状态、四个下拉展开截图、150% 布局缩放及 144 DPI 渲染截图；无需更改系统缩放设置。

坐标轴验收直接采样渲染像素，检查底部轴线、9 个向下时间刻度及两图各 5 个向右纵轴刻度。评分按钮在 1000×650、1440×900、1920×1080 和 150% 布局缩放下校验正方形、30% 比例（含窄窗口收缩）、图标居中与无溢出；新增 `speech-fullhd.png` 全屏截图。

自适应信噪比沿用 `envelope` 场景，输出 `speech-snr-*` 截图和 `speech-snr.passed.txt`，覆盖初始 SNR、Babble 选择、开始后锁定、窄窗口、完整阈值和噪声快照详情。

固定信噪比同样使用 `envelope` 场景，输出 `speech-fixed-snr-*` 截图和 `speech-fixed-snr.passed.txt`。检查负号键盘输入、多字符粘贴路径、两位小数限制、30 dB 输入、其他参数继续拒绝负号、开始后锁定、窄窗口及仅显示正确率的完整结果。研究原始噪声资源沿用现有发布内容。

固定 RIR 场景验收参数生成/失效、响应曲线与边界、带符号 SNR 输入、开始后锁定、完整逐字结果和正常/小窗口布局；产物为 `speech-rir-*.png` 与 `speech-rir.passed.txt`。

## 共用点位

`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=electrode-configuration-shared --output-directory=TestArtifacts/shared-electrodes`

检查 2D 共用点位未检测、单侧检测、双侧不同阻抗、前后视角和全阻抗色矩阵，包含 REF/GND、最长示例标签及选中外圈。追加 `-3d` 到场景名可在本机 WebView2 可用时验证真实 3D 的双分区标签及各侧颜色。

耐受度测试场景：`dotnet run --project EGGtCSPlatform.VisualTests -- --scenario=tolerance --output-directory=TestArtifacts/Tolerance --timeout-seconds=60`。
使用独立 SQLite 测试库及预览阻抗服务，验证被试弹窗、配置锁定、刺激阻抗检测后路由、模拟调流、多次保存及重置、配置分组历史；输出 1440×900 / 1000×650 的空态、多记录及历史展开截图，以及 `Tolerance.passed.txt`。正式程序的阻抗服务仍按设备配置选择。

耐受度历史复用验收包含：共用只读日期组件、回车搜索、表头列对齐、`AnimatedExpander` 展开收起、长 ID／多点位／长备注、窄窗口横向滚动，以及公共分页控件的 10／20／50 组切换、末页边界、筛选与重置。`start-experiment-history` 场景同时验证普通历史页公共分页的双向绑定及原有选择与展开动画。

耐受度调试页滚动验收对比空记录、3 条与 100 条记录的页面及面板尺寸，检查列表滚轮、首尾滚动、固定标题与表头、追加记录保留滚动位置，以及小窗口左侧独立滚动和保存按钮可达性。100 条记录使用内存预览保存服务，不写入历史验收数据库。被试弹窗同时检查与新建实验共用的输入框尺寸、字体、聚焦边框、键盘输入和 Tab 顺序。输出 `records-100`、`records-100-bottom`、`adjustment-scrolled` 和输入框聚焦截图。

耐受度场景还验证 Logo 返回首页及保存时禁用、方向键单步调流和上下限、长按去重、组合键不调流、子控件焦点、弹窗隔离、窗口失焦及离页清理。`envelope` 场景验证音量滑块左侧的单按钮静音切换、恢复原音量、提示状态同步，并检查训练设置禁用时外部字段说明仍保持启用样式。

`envelope-interactions` 单独验收固定语速＋声码器、固定 SNR、RIR 的输入控件锁定、标签颜色与透明度、播放中静音切换及滑块同步，生成标准／小窗口截图。完整场景使用 `envelope`，包含原有轴线像素断言、缩放及训练期间持续锁定的检查。刺激区按网格、缓存曲线、坐标轴的顺序绘制，防止曲线遮住零线刻度。
