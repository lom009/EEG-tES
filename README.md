# EEG-tES

EEG-tES 是一个用于脑电采集（EEG）与经颅电刺激（tES）实验配置的产品原型项目。

当前仓库包含可运行的 React 19 + Vite 6 前端原型，覆盖新建实验、电极配置、采集/刺激阻抗检查、刺激参数与耐受测试、实验运行和导出流程。

## 文档

- [旧版项目分析](docs/legacy-bci-demo.md)：功能范围、交互流程、技术架构、已知差异和迁移建议。
- [旧版设计验收记录](docs/design-qa.md)：旧项目基于 Figma 的视觉与交互验收结论。
- [产品交互说明](docs/product-interaction-spec.md)：当前原型的完整页面流程、状态规则、时长配置和验收标准。
- [当前设计 QA](design-qa.md)：持续记录当前实现与 Figma 的对齐情况和回归结果。

## 本地运行

```bash
pnpm install
pnpm run dev
```

访问 `http://127.0.0.1:5173/`。可通过 `/?screen=setup`、`/?screen=electrodes`、`/?screen=experiment` 直接打开主要原型页面。

## 验证

```bash
pnpm test
pnpm run build
```

当前测试覆盖 20 点独立状态、新建实验重置、自动模式阶段流转、时长单位换算及边界值。
