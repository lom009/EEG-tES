import tdcsDotUrl from "./assets/tdcs-dot.svg";
import {
  getTRNSFrameAtProgress,
  getTRNSPathPoints,
  TRNS_GRAPH,
} from "./model.js";

function toPathData(points) {
  return points
    .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`)
    .join("");
}

function NoisePath({ className, clipPath, pathData }) {
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

function TRNSRendererDiagram({ frame, diagramId }) {
  const progressClipId = `${diagramId}-trns-progress`;
  const pathData = toPathData(getTRNSPathPoints());

  return (
    <svg
      className="stimulus-visualization__diagram"
      viewBox={`0 0 ${TRNS_GRAPH.width} ${TRNS_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={frame.x} height={TRNS_GRAPH.height} />
        </clipPath>
      </defs>
      <NoisePath
        className="stimulus-visualization__path stimulus-visualization__path--pending"
        pathData={pathData}
      />
      <NoisePath
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

export const trnsRenderer = Object.freeze({
  id: "tRNS",
  label: "tRNS",
  unit: "mA",
  getFrameAtProgress: getTRNSFrameAtProgress,
  Diagram: TRNSRendererDiagram,
});
