import tdcsDotUrl from "./assets/tdcs-dot.svg";
import tdcsGuideUrl from "./assets/tdcs-guide.svg";
import {
  getTACSFrameAtProgress,
  TACS_GRAPH,
  TACS_PATH_POINTS,
} from "./model.js";

const GUIDE_POSITIONS = [
  { x: 1, y: 3.75158 },
  { x: 31, y: 4.75158 },
  { x: 98, y: 4.75158 },
  { x: 126, y: 3.75158 },
];

const TACS_PATH_DATA = TACS_PATH_POINTS
  .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`)
  .join("");

function WavePath({ className, clipPath }) {
  return (
    <path
      className={className}
      d={TACS_PATH_DATA}
      fill="none"
      stroke="#9536f3"
      strokeWidth="1.5"
      clipPath={clipPath}
    />
  );
}

function TACSRendererDiagram({ frame, diagramId }) {
  const progressClipId = `${diagramId}-tacs-progress`;

  return (
    <svg
      className="stimulus-visualization__diagram stimulus-visualization__diagram--tacs"
      viewBox={`0 0 ${TACS_GRAPH.width} ${TACS_GRAPH.height}`}
      aria-hidden="true"
    >
      <defs>
        <clipPath id={progressClipId}>
          <rect x="0" y="0" width={frame.x} height={TACS_GRAPH.height} />
        </clipPath>
      </defs>

      {GUIDE_POSITIONS.map(({ x, y }) => (
        <image key={x} href={tdcsGuideUrl} x={x - 0.25} y={y} width="0.5" height="37" />
      ))}

      <WavePath
        className="stimulus-visualization__path stimulus-visualization__path--pending"
      />
      <WavePath
        className="stimulus-visualization__path stimulus-visualization__path--complete"
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

export const tacsRenderer = Object.freeze({
  id: "tACS",
  label: "tACS",
  unit: "mA",
  getFrameAtProgress: getTACSFrameAtProgress,
  Diagram: TACSRendererDiagram,
});
