import tdcsDotUrl from "./assets/tdcs-dot.svg";
import {
  getShamACFrameAtProgress,
  getShamACPathPoints,
  SHAM_AC_GRAPH,
} from "./model.js";

function toPathData(points) {
  return points
    .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`)
    .join("");
}

function ShamACPath({ className, clipPath, pathData }) {
  return (
    <path
      className={className}
      d={pathData}
      fill="none"
      stroke="#9536f3"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      clipPath={clipPath}
    />
  );
}

function ShamACRendererDiagram({ frame, config, diagramId }) {
  const progressClipId = `${diagramId}-sham-ac-progress`;
  const pathData = toPathData(getShamACPathPoints(config));

  return (
    <svg
      className="stimulus-visualization__diagram"
      viewBox={`0 0 ${SHAM_AC_GRAPH.width} ${SHAM_AC_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={frame.x} height={SHAM_AC_GRAPH.height} />
        </clipPath>
      </defs>
      <ShamACPath
        className="stimulus-visualization__path stimulus-visualization__path--pending"
        pathData={pathData}
      />
      <ShamACPath
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

export const shamACRenderer = Object.freeze({
  id: "shamAC",
  label: "Sham交流",
  unit: "mA",
  getFrameAtProgress: getShamACFrameAtProgress,
  Diagram: ShamACRendererDiagram,
});
