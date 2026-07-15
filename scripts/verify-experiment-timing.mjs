import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  MAX_DURATION_MS,
  MAX_CYCLE_COUNT,
  MIN_DURATION_MS,
  MIN_CYCLE_COUNT,
  canEditPhaseDuration,
  clampDurationMs,
  formatDurationForInput,
  formatDurationLabel,
  formatElapsedTime,
  getPhaseDelayMs,
  getAutoRecoveryTransition,
  getRunStateAfterModeSwitch,
  normalizeDurationInput,
  normalizeCycleCount,
  toMilliseconds,
} from "../src/experimentTiming.js";

const finishedManualRuntime = {
  phase: "finished",
  elapsedMs: 5000,
  runStartedAt: null,
  completedCycles: 1,
  isExportPanelOpen: false,
};

assert.deepEqual(
  getRunStateAfterModeSwitch("manual", "auto", finishedManualRuntime),
  {
    phase: "standby",
    elapsedMs: 0,
    runStartedAt: null,
    completedCycles: 0,
    isExportPanelOpen: false,
  },
  "switching from a completed manual run to automatic mode should open a fresh standby run",
);
assert.equal(
  getRunStateAfterModeSwitch("manual", "manual", finishedManualRuntime),
  finishedManualRuntime,
  "selecting the already active mode should preserve its current runtime state",
);

assert.equal(MIN_DURATION_MS, 10, "duration lower bound should be 10 ms");
assert.equal(MAX_DURATION_MS, 60_000, "duration upper bound should be 60 seconds");
assert.equal(MIN_CYCLE_COUNT, 1, "cycle count lower bound should be one");
assert.equal(MAX_CYCLE_COUNT, 99, "cycle count upper bound should be 99");

assert.equal(normalizeCycleCount("", 3), 3, "empty cycle input should restore the previous value");
assert.equal(normalizeCycleCount("0", 3), 1, "cycle input should clamp to the minimum");
assert.equal(normalizeCycleCount("120", 3), 99, "cycle input should clamp to the maximum");
assert.equal(normalizeCycleCount("4", 1), 4, "valid cycle input should be preserved");

assert.deepEqual(
  getAutoRecoveryTransition(0, 3),
  { nextPhase: "acquisition", completedCycles: 1 },
  "automatic mode should restart acquisition while cycles remain",
);
assert.deepEqual(
  getAutoRecoveryTransition(2, 3),
  { nextPhase: "finished", completedCycles: 3 },
  "automatic mode should finish after the configured cycle count",
);

assert.equal(toMilliseconds("1500", "ms"), 1500, "milliseconds should remain unchanged");
assert.equal(toMilliseconds("1.5", "s"), 1500, "seconds should convert to milliseconds");
assert.ok(Number.isNaN(toMilliseconds("", "ms")), "empty input should be invalid");
assert.ok(Number.isNaN(toMilliseconds("abc", "s")), "non-numeric input should be invalid");

assert.equal(clampDurationMs(1), 10, "duration should clamp to the minimum");
assert.equal(clampDurationMs(90_000), 60_000, "duration should clamp to the maximum");
assert.equal(normalizeDurationInput("", "ms", 1200), 1200, "empty input should restore the previous duration");
assert.equal(normalizeDurationInput("0.001", "s", 1200), 10, "valid input below range should clamp");
assert.equal(normalizeDurationInput("120", "s", 1200), 60_000, "valid input above range should clamp");

assert.equal(formatDurationForInput(1500, "ms"), "1500");
assert.equal(formatDurationForInput(1500, "s"), "1.5");
assert.equal(formatDurationForInput(10, "s"), "0.01");
assert.equal(formatDurationLabel(1500, "ms"), "1500 ms");
assert.equal(formatDurationLabel(1500, "s"), "1.5 s");
assert.equal(formatElapsedTime(0), "00:00:00");
assert.equal(formatElapsedTime(3_661_900), "01:01:01");

assert.equal(canEditPhaseDuration("acquisition", "standby", "manual"), true, "acquisition should be editable before it starts");
assert.equal(canEditPhaseDuration("acquisition", "stimReady", "manual"), false, "completed acquisition should remain locked");
assert.equal(canEditPhaseDuration("stimulation", "standby", "manual"), true, "stimulation should be editable on standby");
assert.equal(canEditPhaseDuration("stimulation", "stimReady", "manual"), true, "manual stimulation should remain editable before its start action");
assert.equal(canEditPhaseDuration("stimulation", "stimReady", "auto"), false, "automatic mode should not expose a transient editable stimulation state");
assert.equal(canEditPhaseDuration("stimulation", "stimulation", "manual"), false, "stimulation should lock once it starts");

const configuredDurations = { acquisition: 2500, stimulation: 4200 };
assert.equal(getPhaseDelayMs("acquisition", configuredDurations), 2500, "acquisition should use configured duration");
assert.equal(getPhaseDelayMs("stimulation", configuredDurations), 4200, "stimulation should use configured duration");
assert.equal(getPhaseDelayMs("blanking", configuredDurations), 900, "blanking should keep its prototype delay");
assert.equal(getPhaseDelayMs("stimReady", configuredDurations), 900, "automatic mode should bridge stim-ready using its prototype delay");
assert.equal(getPhaseDelayMs("recovery", configuredDurations), 1200, "recovery should keep its prototype delay");
assert.equal(getPhaseDelayMs("finished", configuredDurations), null, "terminal phases should not schedule a transition");

assert.ok(
  getPhaseDelayMs("acquisition", { acquisition: 5000, stimulation: 1000 })
    > getPhaseDelayMs("acquisition", { acquisition: 1000, stimulation: 1000 }),
  "a larger configured value should produce a longer phase delay",
);

const appSource = readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");
const styleSource = readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");
assert.match(appSource, /canEditPhaseDuration\(stageId, phase, runMode\)/, "duration controls should use per-stage editability");
assert.match(appSource, /getPhaseDelayMs\(phase, phaseDurationMs\)/, "phase scheduling should use configured duration state");
assert.match(appSource, /aria-expanded=\{isConfigurable \? openUnitMenu === stage\.id/, "unit controls should expose expanded state");
assert.match(appSource, /role="listbox"/, "unit controls should render a custom listbox");
assert.match(appSource, /formatDurationLabel\(phaseDurationMs\[stage\.id\], phaseUnits\[stage\.id\]\)/, "timeline labels should use configured values");
assert.match(
  appSource,
  /<div className="experiment-status-row"[\s\S]*?<button className="experiment-back"[\s\S]*?返回电极配置/,
  "return-to-electrodes action should be the first control in the status row",
);
assert.match(appSource, /value=\{cycleCountDraft\}/, "cycle count should render as an editable controlled input");
assert.match(appSource, /stepCycleCount\(1\)/, "cycle control should expose an increment action");
assert.match(appSource, /stepCycleCount\(-1\)/, "cycle control should expose a decrement action");
assert.match(appSource, /getAutoRecoveryTransition\(completedCycles, cycleCount\)/, "automatic recovery should honor the configured cycle count");
assert.match(styleSource, /\.cycle-input-shell\s*\{[\s\S]*?width:74px;[\s\S]*?height:32px;/, "cycle input should match the 74 by 32 Figma control");

console.log("experiment timing verification passed");
