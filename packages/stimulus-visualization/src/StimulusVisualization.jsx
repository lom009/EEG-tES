import { useId } from "react";
import { formatStimulusValue } from "./model.js";

function joinClassNames(...names) {
  return names.filter(Boolean).join(" ");
}

export function StimulusVisualization({
  renderer,
  progress = 0,
  config,
  precision = 2,
  className,
  ariaLabel,
}) {
  if (!renderer?.Diagram || typeof renderer.getFrameAtProgress !== "function") {
    throw new TypeError("StimulusVisualization requires a renderer with Diagram and getFrameAtProgress");
  }

  const frame = renderer.getFrameAtProgress(progress, config);
  const diagramId = useId().replaceAll(":", "");
  const displayValue = formatStimulusValue(frame.value, renderer.unit, precision);
  const Diagram = renderer.Diagram;
  const statusText = `${renderer.label}刺激中`;

  return (
    <section
      className={joinClassNames("stimulus-visualization", className)}
      data-stimulus-type={renderer.id}
      aria-label={ariaLabel || `${statusText}，当前数值 ${displayValue}`}
    >
      <div className="stimulus-visualization__graph">
        <Diagram frame={frame} config={config} diagramId={diagramId} />
        <output className="stimulus-visualization__value" aria-live="off">
          {displayValue}
        </output>
      </div>
      <p className="stimulus-visualization__status">
        <strong>{renderer.label}</strong>
        <span>刺激中</span>
      </p>
    </section>
  );
}
