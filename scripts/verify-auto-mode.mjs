import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import { getAutoRestartState, getAutoStoppedStageStatus } from "../src/experimentTiming.js";

const source = fs.readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");
const styles = fs.readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");

const transitionsMatch = source.match(/const phaseAutoAdvance = (\{[\s\S]*?\n\});/);
assert.ok(transitionsMatch, "phaseAutoAdvance configuration should exist");

const transitions = vm.runInNewContext(`(${transitionsMatch[1]})`);
assert.match(
  source,
  /phase === "stimReady" && !isAutoMode/,
  "manual mode should continue to pause before stimulation",
);

const visited = ["acquisition"];
let phase = "acquisition";

while (transitions[phase]) {
  phase = transitions[phase].next;
  visited.push(phase);
  assert.ok(visited.length <= 10, "automatic phase flow should not loop");
}

assert.deepEqual(
  visited,
  ["acquisition", "blanking", "stimReady", "stimulation", "recovery", "finished"],
  "automatic mode should run all four visible stages and finish without another user action",
);

const autoStartMatch = source.match(/const canAutoStart = ([^;]+);/);
assert.ok(autoStartMatch, "automatic start condition should exist");

const autoStartAtStandby = vm.runInNewContext(autoStartMatch[1], { isAutoMode: true, phase: "standby" });
const autoStartAtStimReady = vm.runInNewContext(autoStartMatch[1], { isAutoMode: true, phase: "stimReady" });

assert.equal(autoStartAtStandby, true, "automatic mode should offer one start action while on standby");
assert.equal(autoStartAtStimReady, false, "automatic mode should not ask the user to start again between phases");

const emergencyStopMatch = source.match(/const canEmergencyStop = ([^;]+);/);
assert.ok(emergencyStopMatch, "emergency stop availability should be explicit");
assert.equal(
  vm.runInNewContext(emergencyStopMatch[1], {
    isPhasePaused: false,
    isAutoMode: true,
    isActiveRuntimePhase: false,
    canPauseExperimentPhase: () => false,
    runMode: "auto",
    phase: "standby",
  }),
  false,
  "emergency stop should be disabled before either manual or automatic mode starts",
);
assert.equal(
  vm.runInNewContext(emergencyStopMatch[1], {
    isPhasePaused: false,
    isAutoMode: true,
    isActiveRuntimePhase: true,
    canPauseExperimentPhase: () => false,
    runMode: "auto",
    phase: "acquisition",
  }),
  true,
  "emergency stop should be enabled after an experiment starts",
);
assert.match(source, /disabled=\{!canEmergencyStop\}/, "emergency stop button should receive its disabled state");
assert.match(styles, /\.emergency-stop-button:disabled\s*\{[\s\S]*?background:\s*#acb7c8;/, "disabled emergency stop should use Figma node 96:8947 color");

assert.deepEqual(
  [0, 1, 2, 3].map((index) => getAutoStoppedStageStatus("blanking", index)),
  ["已完成", "已停止", "待执行", "待执行"],
  "automatic emergency stop should preserve the completed rows and mark only the interrupted row as stopped",
);
assert.deepEqual(
  getAutoRestartState(12345),
  {
    phase: "acquisition",
    elapsedMs: 0,
    runStartedAt: 12345,
    completedCycles: 0,
    isExportPanelOpen: false,
  },
  "automatic restart should begin a fresh run from acquisition",
);
assert.match(source, />重新开始\s*<img/, "automatic stopped state should expose the Figma restart action");
assert.match(source, /onClick=\{restartAutoExperiment\}/, "the restart action should use the fresh-run handler");
assert.match(source, /phase === "standby" \|\| phase === "stopped"/, "automatic stopped state should allow cycle-count changes before restart");
assert.match(styles, /\.auto-restart-button\s*\{[\s\S]*?background:\s*#38a169;/, "automatic restart should use the green Figma action style");

console.log("automatic-mode phase verification passed");
