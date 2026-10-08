import assert from "node:assert/strict";
import {
  advanceDetectionSession,
  beginDetectionSession,
  ELECTRODE_COLORS,
  getElectrodeVisualState,
  placeElectrodeLabel,
  shouldUpdateLabels,
  stopDetectionSession,
} from "../src/head-test/electrode-visuals.js";

assert.deepEqual(
  getElectrodeVisualState({ assigned: false, checking: false, result: null }),
  { key: "default", color: ELECTRODE_COLORS.default },
  "unassigned electrodes stay white",
);

assert.deepEqual(
  getElectrodeVisualState({ assigned: true, checking: false, result: null }),
  { key: "selected", color: ELECTRODE_COLORS.selected },
  "assigned electrodes use the blue selected state before detection",
);

assert.deepEqual(
  getElectrodeVisualState({ assigned: true, checking: true, result: null }),
  { key: "checking", color: ELECTRODE_COLORS.selected },
  "checking keeps the selected blue color while animation communicates activity",
);

assert.deepEqual(
  getElectrodeVisualState({ assigned: true, checking: true, result: "good" }),
  { key: "checking", color: ELECTRODE_COLORS.good },
  "checking can show the latest live impedance result while its animation continues",
);

{
  const started = beginDetectionSession(["F3", "Fz", "FP2"]);
  assert.equal(started.running, true, "starting detection creates a running session");
  assert.deepEqual(
    Object.values(started.results),
    ["good", "warning", "bad"],
    "a running detection immediately exposes semantic point colors",
  );

  const advanced = advanceDetectionSession(started);
  assert.equal(advanced.running, true, "detection remains active after a result update");
  assert.notDeepEqual(
    advanced.results,
    started.results,
    "live detection continues updating point results instead of auto-finishing",
  );

  const stopped = stopDetectionSession(advanced);
  assert.equal(stopped.running, false, "only an explicit stop ends detection");
  assert.deepEqual(
    stopped.results,
    advanced.results,
    "stopping detection preserves the latest point result colors",
  );
  assert.deepEqual(
    advanceDetectionSession(stopped),
    stopped,
    "a stopped session no longer changes point results",
  );
}

for (const [result, color] of [
  ["good", "#38a169"],
  ["warning", "#fd5b38"],
  ["bad", "#e91919"],
]) {
  assert.deepEqual(
    getElectrodeVisualState({ assigned: true, checking: false, result }),
    { key: result, color },
    `${result} detection result should override selected blue`,
  );
}

assert.equal(
  getElectrodeVisualState({ assigned: false, checking: false, result: "bad" }).key,
  "default",
  "stale results must never color an unassigned point",
);

{
  const placement = placeElectrodeLabel({
    anchor: { x: 620, y: 320 },
    stageCenter: { x: 500, y: 320 },
    viewport: { width: 1000, height: 700 },
    occupied: [],
  });
  assert.ok(placement, "an on-screen point should receive a label placement");
  assert.ok(placement.x > 620, "the label should sit just outside the electrode from the head center");
}

{
  const first = placeElectrodeLabel({
    anchor: { x: 620, y: 320 },
    stageCenter: { x: 500, y: 320 },
    viewport: { width: 1000, height: 700 },
    occupied: [],
  });
  const second = placeElectrodeLabel({
    anchor: { x: 625, y: 322 },
    stageCenter: { x: 500, y: 320 },
    viewport: { width: 1000, height: 700 },
    occupied: [first],
  });
  assert.ok(second, "nearby labels should be displaced instead of drifting or overlapping");
  assert.ok(Math.abs(second.y - first.y) >= 30, "collision resolution should use a compact tangential offset");
}

assert.equal(
  placeElectrodeLabel({
    anchor: { x: -80, y: 320 },
    stageCenter: { x: 500, y: 320 },
    viewport: { width: 1000, height: 700 },
    occupied: [],
  }),
  null,
  "labels for off-screen electrodes should be hidden instead of clamped to the edge",
);

assert.equal(
  shouldUpdateLabels({ controlsActive: true, controlsChanged: false, labelsDirty: false }),
  true,
  "labels must update every animation frame while the user is actively rotating",
);

assert.equal(
  shouldUpdateLabels({ controlsActive: false, controlsChanged: false, labelsDirty: false }),
  false,
  "labels can stop updating after rotation and damping have both settled",
);

console.log("head-test interaction verification passed");
