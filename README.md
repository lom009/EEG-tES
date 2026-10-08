# EEG-tES

EEG-tES 是一个用于脑电采集（EEG）与经颅电刺激（tES）实验配置的产品原型项目。

当前仓库包含可运行的 React 19 + Vite 6 前端原型，覆盖新建实验、电极配置、采集/刺激阻抗检查、刺激参数与耐受测试、实验运行和导出流程。

## 文档

- [旧版项目分析](docs/legacy-bci-demo.md)：功能范围、交互流程、技术架构、已知差异和迁移建议。
- [旧版设计验收记录](docs/design-qa.md)：旧项目基于 Figma 的视觉与交互验收结论。
- [产品交互说明](docs/product-interaction-spec.md)：当前原型的完整页面流程、状态规则、时长配置和验收标准。
- [产品说明与项目讲解稿](docs/product-presentation-guide.md)：面向汇报和演示，按项目背景、用户、完整实验流程、产品逻辑与设计原则组织。
- [上位机—下位机流程实验室](docs/device-flow-lab.md)：模拟设备接口、刺激协议、EEG 数据流、预检、运行和急停验证说明。
- [虚拟设备实施路线图](docs/virtual-device-simulator-roadmap.md)：在缺少下位机时，按设备契约、信号、故障、保存与回放逐步提高流程真实性。
- [Run Bundle v1.0](docs/run-bundle-v1.md)：一次实验的数据包结构、EEG 编码、完整性校验和本地回放规则。
- [当前设计 QA](design-qa.md)：持续记录当前实现与 Figma 的对齐情况和回归结果。

## 本地运行

```bash
pnpm install
pnpm run dev
```

访问 `http://127.0.0.1:5173/`。可通过 `/?screen=setup`、`/?screen=electrodes`、`/?screen=experiment` 直接打开主要原型页面；通过 `/?screen=device-lab` 打开无真实硬件的上下位机流程实验室。

## 验证

```bash
pnpm test
pnpm run build
```

当前测试覆盖 20 点独立状态、新建实验重置、自动模式阶段流转、时长单位换算及边界值，以及模拟下位机的协议编译、阻抗门禁、ARM/START/ABORT、EEG 数据与事件序列、确定性故障和 Run Bundle 本地回放。


## 产品演示与桌面源码

- `/login`：登录页；演示时填写非空账号与密码即可进入首页。
- `/home`：产品首页，使用静态背景。
- `/single-stimulation-demo`：单刺激（包络）流程演示，含参数、电极、训练、逐字评分与结果。
- `desktop/`：Avalonia / .NET 10 桌面源码，沿用原项目结构；桌面应用不由 Render 运行。

Render 构建命令：`npm ci && npm run build`，静态发布目录：`dist`。网页使用演示数据供展示，真实设备通讯和原生执行由桌面端承担。
