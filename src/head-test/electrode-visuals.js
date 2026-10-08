export const ELECTRODE_COLORS = Object.freeze({
  default: "#f8fbff",
  selected: "#286cff",
  good: "#38a169",
  warning: "#fd5b38",
  bad: "#e91919",
  unavailable: "#94a3b8",
});

const DETECTION_RESULT_CYCLE = Object.freeze(["good", "warning", "bad"]);

function createDetectionResults(pointIds, tick) {
  return Object.fromEntries(pointIds.map((id, index) => [
    id,
    DETECTION_RESULT_CYCLE[(index + tick) % DETECTION_RESULT_CYCLE.length],
  ]));
}

export function beginDetectionSession(pointIds) {
  const ids = [...pointIds];
  return {
    running: ids.length > 0,
    pointIds: ids,
    tick: 0,
    results: createDetectionResults(ids, 0),
  };
}

export function advanceDetectionSession(session) {
  if (!session.running) return session;
  const tick = session.tick + 1;
  return {
    ...session,
    tick,
    results: createDetectionResults(session.pointIds, tick),
  };
}

export function stopDetectionSession(session) {
  if (!session.running) return session;
  return { ...session, running: false };
}

export function shouldUpdateLabels({ controlsActive, controlsChanged, labelsDirty }) {
  return controlsActive || controlsChanged || labelsDirty;
}

export function getElectrodeVisualState({ assigned, checking, result }) {
  if (!assigned) return { key: "default", color: ELECTRODE_COLORS.default };
  if (checking && result && ELECTRODE_COLORS[result]) {
    return { key: "checking", color: ELECTRODE_COLORS[result] };
  }
  if (checking) return { key: "checking", color: ELECTRODE_COLORS.selected };
  if (result && ELECTRODE_COLORS[result]) {
    return { key: result, color: ELECTRODE_COLORS[result] };
  }
  return { key: "selected", color: ELECTRODE_COLORS.selected };
}

function overlaps(a, b, gap = 7) {
  return Math.abs(a.x - b.x) < (a.width + b.width) / 2 + gap
    && Math.abs(a.y - b.y) < (a.height + b.height) / 2 + gap;
}

export function placeElectrodeLabel({
  anchor,
  stageCenter,
  viewport,
  occupied,
  labelWidth = 44,
  labelHeight = 34,
}) {
  const edge = 10;
  if (
    anchor.x < edge
    || anchor.x > viewport.width - edge
    || anchor.y < edge
    || anchor.y > viewport.height - edge
  ) return null;

  let outwardX = anchor.x - stageCenter.x;
  let outwardY = anchor.y - stageCenter.y;
  const length = Math.hypot(outwardX, outwardY) || 1;
  outwardX /= length;
  outwardY /= length;
  const tangentX = -outwardY;
  const tangentY = outwardX;
  const radialOffset = 25;
  const baseX = anchor.x + outwardX * radialOffset;
  const baseY = anchor.y + outwardY * radialOffset;
  const offsets = [0, 42, -42, 78, -78];

  for (const tangentOffset of offsets) {
    const candidate = {
      x: baseX + tangentX * tangentOffset,
      y: baseY + tangentY * tangentOffset,
      width: labelWidth,
      height: labelHeight,
    };
    const halfWidth = labelWidth / 2;
    const halfHeight = labelHeight / 2;
    if (
      candidate.x < halfWidth + edge
      || candidate.x > viewport.width - halfWidth - edge
      || candidate.y < halfHeight + edge
      || candidate.y > viewport.height - halfHeight - edge
    ) continue;
    if (!occupied.some((other) => overlaps(candidate, other))) return candidate;
  }

  return null;
}
