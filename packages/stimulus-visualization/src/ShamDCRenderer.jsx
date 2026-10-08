import tdcsDotUrl from "./assets/tdcs-dot.svg";
import tdcsGuideUrl from "./assets/tdcs-guide.svg";
import {
  getShamDCFrameAtProgress,
  getShamDCPathPoints,
  SHAM_DC_GRAPH,
} from "./model.js";

function toPathData(points) {
  return points
    .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`)
    .join("");
}

function ShamDCPath({ className, clipPath, pathData }) {
  return (
    <path
      className={className}
      d={pathData}
      fill="none"
      stroke="#9536f3"
      strokeWidth="1.5"
      strokeLinejoin="round"
      clipPath={clipPath}
    />
  );
}

function ShamDCRendererDiagram({ frame, config, diagramId }) {
  const progressClipId = `${diagramId}-sham-dc-progress`;
  const pathPoints = getShamDCPathPoints(config);
  const pathData = toPathData(pathPoints);
  const peakXPositions = pathPoints
    .filter((_, index) => index >= 2 && (index - 2) % 3 === 0)
    .map(([x]) => x);

  return (
    <svg
      className="stimulus-visualization__diagram"
      viewBox={`0 0 ${SHAM_DC_GRAPH.width} ${SHAM_DC_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={frame.x} height={SHAM_DC_GRAPH.height} />
        </clipPath>
      </defs>
      {peakXPositions.map((x) => (
        <image
          key={x}
          data-sham-dc-guide="peak"
          href={tdcsGuideUrl}
          x={x - 0.25}
          y="0"
          width="0.5"
          height="37"
        />
      ))}
      <ShamDCPath
        className="stimulus-visualization__path stimulus-visualization__path--pending"
        pathData={pathData}
      />
      <ShamDCPath
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

export const shamDCRenderer = Object.freeze({
  id: "shamDC",
  label: "Sham直流",
  unit: "mA",
  getFrameAtProgress: getShamDCFrameAtProgress,
  Diagram: ShamDCRendererDiagram,
});
