import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

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
  vm.runInNewContext(emergencyStopMatch[1], { phase: "standby" }),
  false,
  "emergency stop should be disabled before either manual or automatic mode starts",
);
assert.equal(
  vm.runInNewContext(emergencyStopMatch[1], { phase: "acquisition" }),
  true,
  "emergency stop should be enabled after an experiment starts",
);
assert.match(source, /disabled=\{!canEmergencyStop\}/, "emergency stop button should receive its disabled state");
assert.match(styles, /\.emergency-stop-button:disabled\s*\{[\s\S]*?background:\s*#acb7c8;/, "disabled emergency stop should use Figma node 96:8947 color");

console.log("automatic-mode phase verification passed");
