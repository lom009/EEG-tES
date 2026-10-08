import assert from "node:assert/strict";
import {
  createAcquisitionConfiguration,
  createStimulationProgram,
} from "../src/workflowDeviceBridge.js";

const acquisition = createAcquisitionConfiguration({
  acquisitionPoints: ["F3", "F4", "C3"],
  referencePoint: "Cz",
  groundPoint: "Oz",
  sampleRate: 500,
});

assert.equal(acquisition.sampleRateHz, 500);
assert.equal(acquisition.reference.positionId, "Cz");
assert.equal(acquisition.ground.positionId, "Oz");
assert.deepEqual(
  acquisition.channels.map((channel) => channel.positionId),
  ["F3", "F4", "C3"],
);

const roles = [
  { id: "A", label: "阳极", channel: "STIM-1" },
  { id: "C", label: "阴极", channel: "STIM-2" },
];
const base = {
  paradigmId: "tDCS",
  modeId: "dual",
  parameters: {
    currentMa: 1.5,
    durationSec: 10,
    rampUpSec: 2,
    rampDownSec: 2,
  },
  stimulationAssignments: { F3: "A", F4: "C" },
  roles,
};

const tenSecondProgram = createStimulationProgram({
  ...base,
  durationOverrideMs: 10_000,
});
const twentySecondProgram = createStimulationProgram({
  ...base,
  durationOverrideMs: 20_000,
});

assert.equal(tenSecondProgram.montageHash, twentySecondProgram.montageHash);
assert.notEqual(tenSecondProgram.protocolHash, twentySecondProgram.protocolHash);
assert.equal(
  tenSecondProgram.segments.reduce((sum, segment) => sum + segment.durationMs, 0),
  10_000,
);
assert.equal(
  twentySecondProgram.segments.reduce((sum, segment) => sum + segment.durationMs, 0),
  20_000,
);

const changedMontageProgram = createStimulationProgram({
  ...base,
  stimulationAssignments: { C3: "A", C4: "C" },
  durationOverrideMs: 10_000,
});
assert.notEqual(changedMontageProgram.montageHash, tenSecondProgram.montageHash);

console.log("workflow device bridge verification passed");
