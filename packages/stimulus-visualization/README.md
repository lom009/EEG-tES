# @eeg-tes/stimulus-visualization

独立的 React 刺激状态动效组件。主组件只负责布局、数值和状态文案；刺激类型通过 renderer 提供示意图以及同一进度下的点位和数值。

当前包含 tDCS、tACS、tPCS、tRNS、Sham 直流和 Sham 交流 renderer。组件不创建计时器，`progress` 必须由调用方传入。

```jsx
import {
  StimulusVisualization,
  shamACRenderer,
  shamDCRenderer,
  tacsRenderer,
  tdcsRenderer,
  tpcsRenderer,
  trnsRenderer,
} from "@eeg-tes/stimulus-visualization";
import "@eeg-tes/stimulus-visualization/style.css";

export function CurrentStimulus({ progress }) {
  return (
    <StimulusVisualization
      renderer={tdcsRenderer}
      progress={progress}
      config={{ targetCurrent: 2 }}
    />
  );
}
```

tPCS 默认按参考图显示两个 `1.4mA` 方波脉冲，支持 `pulseCurrent` 和归一化的 `pulseWindows` 配置。负电流会把波形上下镜像，保证路径方向和数值极性保持关联。

```jsx
<StimulusVisualization
  renderer={tpcsRenderer}
  progress={progress}
  config={{
    pulseCurrent: 1.4,
    pulseWindows: [[0.216, 0.326], [0.704, 0.813]],
  }}
/>
```

`progress` 范围为 `0–1`，越界值会被钳制。`targetCurrent` 支持负数；数值和圆点位置始终来自 renderer 的同一帧计算。

tDCS 的 `targetCurrent` 为负数时，斜坡和平台会沿垂直方向镜像，保证负电流不只改变文字，也同步倒置路径和圆点位置。

tACS 使用 Figma 原始正弦路径，`peakCurrent` 表示峰值电流。圆点沿路径运动时，上方波峰显示负值、下方波谷显示正值；未传 `peakCurrent` 时会回退到 `targetCurrent`，默认值为 `2`。

```jsx
<StimulusVisualization
  renderer={tacsRenderer}
  progress={progress}
  config={{ peakCurrent: 2 }}
/>
```

Sham 直流按参考图在首尾各显示一个三角脉冲，并在每个峰值中心显示与 tDCS 相同的竖向虚线装饰。`shamCurrent` 默认是 `1.8mA`；负值会把波形整体上下镜像。可通过 `triangleWindows` 传入 `[起点, 峰值点, 终点]` 的归一化进度数组，虚线会跟随自定义峰值位置。

```jsx
<StimulusVisualization
  renderer={shamDCRenderer}
  progress={progress}
  config={{ shamCurrent: -1.8 }}
/>
```

Sham 交流按参考图在首尾各显示一段完整的正负交流波，中间保持零值。`shamPeakCurrent` 是峰值幅度，默认 `0.9mA`，始终按绝对值处理，不区分正负极性。

```jsx
<StimulusVisualization
  renderer={shamACRenderer}
  progress={progress}
  config={{ shamPeakCurrent: 0.9 }}
/>
```

tRNS 使用固定的低密度随机折线作为示意图，默认仅保留 7 个内部转折，避免在小尺寸组件中复刻高采样率噪声。`noiseAmplitude` 默认是 `1.8mA`，只作为静态设置幅度显示；它不会跟随圆点位置跳变，也不区分正负极性。

```jsx
<StimulusVisualization
  renderer={trnsRenderer}
  progress={progress}
  config={{ noiseAmplitude: 1.8 }}
/>
```

## 本地命令

```bash
npm test
npm run build
npm run preview:demo
```
