import tdcsDotUrl from "./assets/tdcs-dot.svg";
import tdcsGuideUrl from "./assets/tdcs-guide.svg";
import tdcsPathUrl from "./assets/tdcs-path.svg";
import { getTDCSFrameAtProgress, TDCS_GRAPH } from "./model.js";

const GUIDE_X_POSITIONS = [1, 21, 110, 130];

function TDCSRendererDiagram({ frame, config, diagramId }) {
  const clipWidth = frame.progress * TDCS_GRAPH.width;
  const progressClipId = `${diagramId}-tdcs-progress`;
  const pathTransform = Number(config?.targetCurrent) < 0
    ? `translate(0 ${TDCS_GRAPH.height}) scale(1 -1)`
    : undefined;

  return (
    <svg
      className="stimulus-visualization__diagram"
      viewBox={`0 0 ${TDCS_GRAPH.width} ${TDCS_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={clipWidth} height={TDCS_GRAPH.height} />
        </clipPath>
      </defs>

      {GUIDE_X_POSITIONS.map((x) => (
        <image key={x} href={tdcsGuideUrl} x={x - 0.25} y="0" width="0.5" height="37" />
      ))}

      <g transform={pathTransform}>
        <image
          className="stimulus-visualization__path stimulus-visualization__path--pending"
          href={tdcsPathUrl}
          x="0.563"
          y="0.5"
          width="129.874"
          height="36.7428"
        />
        <image
          className="stimulus-visualization__path stimulus-visualization__path--complete"
          href={tdcsPathUrl}
          x="0.563"
          y="0.5"
          width="129.874"
          height="36.7428"
          clipPath={`url(#${progressClipId})`}
        />
      </g>
      <image
        className="stimulus-visualization__point"
        href={tdcsDotUrl}
        x={frame.x - 2}
        y={frame.y - 2}
        width="4"
        height="4"
      />
    </svg>
  );
}

export const tdcsRenderer = Object.freeze({
  id: "tDCS",
  label: "tDCS",
  unit: "mA",
  getFrameAtProgress: getTDCSFrameAtProgress,
  Diagram: TDCSRendererDiagram,
});
