import assert from "node:assert/strict";
import fs from "node:fs";
import {
  canOpenSummary,
  cloneParameterDefaults,
  getModesForParadigm,
  getRolesForMode,
  importHistoricalConfiguration,
  STIMULATION_PARADIGMS,
  validateAcquisition,
  validateParameters,
  validateStimAssignments,
  WORKFLOW_PAGES,
} from "../src/workflowPrototypeModel.js";

assert.deepEqual(
  Object.keys(STIMULATION_PARADIGMS),
  ["tDCS", "tACS", "tRNS", "tPCS", "Sham"],
  "HD is a topology, not an independent stimulation paradigm",
);
assert.deepEqual(
  WORKFLOW_PAGES.map((page) => page.id),
  ["home", "patient", "plan", "electrodes", "run", "history"],
);
assert.deepEqual(getModesForParadigm("tDCS").map((mode) => mode.id), ["dual", "hd", "multi"]);
assert.equal(getRolesForMode("dual").length, 2);
assert.equal(getRolesForMode("hd").length, 5);
assert.equal(getRolesForMode("multi").length, 4);

const tdcsDefaults = cloneParameterDefaults("tDCS");
assert.equal(validateParameters("tDCS", tdcsDefaults).length, 0);
assert.ok(validateParameters("tDCS", { ...tdcsDefaults, currentMa: 99 }).length > 0);
assert.ok(validateParameters("tDCS", { ...tdcsDefaults, durationSec: 1, rampUpSec: 1, rampDownSec: 1 }).length > 0);

assert.equal(validateStimAssignments("dual", { F3: "A", F4: "C" }).isValid, true);
assert.equal(validateStimAssignments("dual", { F3: "A" }).isValid, false);
assert.equal(validateStimAssignments("tACS", "dual", { F3: "CHA", F4: "CHB" }).isValid, true);
assert.equal(validateStimAssignments("tACS", "dual", { F3: "A", F4: "C" }).isValid, false);
assert.equal(validateStimAssignments("hd", {
  Cz: "CENTER",
  F3: "R1",
  F4: "R2",
  P3: "R3",
  P4: "R4",
}).isValid, true);

assert.equal(validateAcquisition({
  stimulationAssignments: { F3: "CHA", F4: "CHB" },
  acquisitionPoints: ["C3", "C4"],
  referencePoint: "Pz",
  groundPoint: "Oz",
  sampleRate: 500,
}).isValid, true);
assert.equal(validateAcquisition({
  stimulationAssignments: { F3: "A", F4: "C" },
  acquisitionPoints: ["F3", "C4"],
  referencePoint: "Pz",
  groundPoint: "Oz",
  sampleRate: 500,
}).isValid, false);

assert.equal(canOpenSummary({
  stimImpedance: "pass",
  acqImpedance: "pass",
}), true);
assert.equal(canOpenSummary({
  stimImpedance: "pass",
  acqImpedance: "pending",
}), false);

const imported = importHistoricalConfiguration({
  paradigmId: "tACS",
  modeId: "dual",
  parameters: cloneParameterDefaults("tACS"),
  stimulationAssignments: { F3: "A", F4: "C" },
  acquisitionPoints: ["C3", "C4"],
  referencePoint: "Pz",
  groundPoint: "Oz",
  sampleRate: 500,
  runConfig: { mode: "auto", cycles: 2 },
  checks: {
    stimImpedance: "pass",
    acqImpedance: "pass",
    tolerance: "pass",
    summary: "pass",
  },
});
assert.deepEqual(imported.checks, {
  stimImpedance: "pending",
  acqImpedance: "pending",
  summary: "pending",
}, "historical configuration may be reused but physical PASS results must reset");

const prototypeSource = fs.readFileSync(
  new URL("../src/components/WorkflowPrototype.jsx", import.meta.url),
  "utf8",
);
assert.match(
  prototypeSource,
  /VIRTUAL_FAULT_SCENARIOS[\s\S]*"electrode-warning"[\s\S]*"electrode-abort"[\s\S]*disconnect[\s\S]*"clock-rollback"/,
  "the prototype should expose reproducible URL-selected virtual fault scenarios",
);
assert.match(
  prototypeSource,
  /ACQUISITION_FRAME_REJECTED[\s\S]*EEG 异常帧已拒绝/,
  "rejected clock frames should be visible in the operator event log",
);
assert.match(
  prototypeSource,
  /reasonCode:\s*fault\.code/,
  "critical device faults should be frozen into the aborted result",
);
assert.ok(
  prototypeSource.includes("rows.some((row) => row.id === faultRunId)"),
  "the prototype should avoid creating duplicate aborted results for one device fault",
);
assert.match(
  prototypeSource,
  /createRunBundle[\s\S]*sourceKind:\s*"SIMULATED"[\s\S]*serializeRunBundlePortable/,
  "a completed simulated run should export a source-labelled Run Bundle",
);
assert.match(
  prototypeSource,
  /deserializeRunBundlePortable[\s\S]*new ReplayDeviceSession\(bundle,\s*\{\s*mode:\s*"realtime",\s*speed:\s*4\s*\}\)/,
  "the prototype should validate and open an imported bundle in realtime replay mode",
);
assert.match(
  prototypeSource,
  /导出 Run Bundle[\s\S]*导入 Run Bundle[\s\S]*REPLAY ·/,
  "the run page should expose export/import and visibly label replay events",
);

console.log("workflow prototype model verification passed");
