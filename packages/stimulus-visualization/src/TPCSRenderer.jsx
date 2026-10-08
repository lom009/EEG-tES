import tdcsDotUrl from "./assets/tdcs-dot.svg";
import {
  getTPCSFrameAtProgress,
  getTPCSPathPoints,
  TPCS_GRAPH,
} from "./model.js";

function toPathData(points) {
  return points
    .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`)
    .join("");
}

function PulsePath({ className, clipPath, pathData }) {
  return (
    <path
      className={className}
      d={pathData}
      fill="none"
      stroke="#9536f3"
      strokeWidth="1.5"
      strokeLinejoin="miter"
      clipPath={clipPath}
    />
  );
}

function TPCSRendererDiagram({ frame, config, diagramId }) {
  const progressClipId = `${diagramId}-tpcs-progress`;
  const pathData = toPathData(getTPCSPathPoints(config));

  return (
    <svg
      className="stimulus-visualization__diagram"
      viewBox={`0 0 ${TPCS_GRAPH.width} ${TPCS_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={frame.x} height={TPCS_GRAPH.height} />
        </clipPath>
      </defs>

      <PulsePath
        className="stimulus-visualization__path stimulus-visualization__path--pending"
        pathData={pathData}
      />
      <PulsePath
        className="stimulus-visualization__path stimulus-visualization__path--complete"
        pathData={pathData}
        clipPath={`url(#${progressClipId})`}
      />
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

export const tpcsRenderer = Object.freeze({
  id: "tPCS",
  label: "tPCS",
  unit: "mA",
  getFrameAtProgress: getTPCSFrameAtProgress,
  Diagram: TPCSRendererDiagram,
});
