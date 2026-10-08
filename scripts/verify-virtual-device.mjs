import assert from "node:assert/strict";
import { DEVICE_EVENTS } from "../src/deviceProtocolV1.js";
import {
  applyClockDrift,
  createPatientSignalProfile,
  createSeededRandom,
  createVirtualClock,
  VirtualDeviceSession,
} from "../src/virtualDeviceSession.js";

const randomA = createSeededRandom("same-seed");
const randomB = createSeededRandom("same-seed");
assert.deepEqual(
  Array.from({ length: 8 }, () => randomA.next()),
  Array.from({ length: 8 }, () => randomB.next()),
);
assert.equal(applyClockDrift(1_000_000, 1_000), 1_001_000);
assert.deepEqual(
  createPatientSignalProfile("patient-profile-001"),
  createPatientSignalProfile("patient-profile-001"),
);

const clock = createVirtualClock();
const schedule = [];
clock.schedule(() => schedule.push(["second", clock.nowUs()]), 20);
clock.schedule(() => schedule.push(["first", clock.nowUs()]), 10);
clock.advanceBy(25);
assert.deepEqual(schedule, [["first", 10_000], ["second", 20_000]]);

async function runDeterministicSession(seed) {
  const session = new VirtualDeviceSession({ seed });
  await session.connect();
  session.acquisition.configureAcquisition({
    channels: ["F3", "F4", "C3", "C4"],
    sampleRateHz: 250,
    reference: { positionId: "Cz" },
    ground: { positionId: "Oz" },
  });
  session.acquisition.startAcquisition({ runId: "RUN-001" });
  session.acquisition.startImpedanceCheck({
    channels: [
      { channelId: "ACQ-1", positionId: "C3" },
      { channelId: "ACQ-2", positionId: "C4" },
    ],
  });
  const program = {
    protocolHash: "protocol-demo-001",
    montageHash: "montage-demo-001",
    paradigmId: "TDCS",
    segments: [
      { type: "RAMP_UP", durationMs: 100 },
      { type: "HOLD", durationMs: 300 },
      { type: "RAMP_DOWN", durationMs: 100 },
    ],
  };
  session.stimulation.prepareProgram(program);
  session.stimulation.startImpedanceCheck({
    channels: [
      { channelId: "STIM-1", positionId: "F3" },
      { channelId: "STIM-2", positionId: "F4" },
    ],
  });
  session.advanceBy(500);
  assert.equal(session.acquisition.impedanceResult.status, "PASSED");
  assert.equal(session.stimulation.impedanceResult.status, "PASSED");
  const changedDurationProgram = {
    ...program,
    protocolHash: "protocol-demo-001-duration-2",
    segments: [{ type: "HOLD", durationMs: 700 }],
  };
  const preserved = session.stimulation.prepareProgram(changedDurationProgram);
  assert.equal(preserved.impedancePreserved, true);
  assert.equal(session.stimulation.impedanceResult.status, "PASSED");
  session.stimulation.arm({
    toleranceRecord: {
      recordId: "TOL-001",
      paradigmId: "TDCS",
      status: "COMPLETED",
    },
    stimulationImpedance: session.stimulation.impedanceResult,
    acquisitionImpedance: session.acquisition.impedanceResult,
  });
  session.stimulation.startRun("RUN-001");
  session.advanceBy(800);
  session.acquisition.stopAcquisition({ runId: "RUN-001" });
  const log = session.getEventLog();
  const summary = log.map((event) => ({
    messageType: event.messageType,
    runId: event.runId,
    deviceTimeUs: event.deviceTimeUs,
    payload: event.payload,
  }));
  const snapshot = session.getSnapshot();
  session.destroy();
  return { summary, snapshot };
}

const first = await runDeterministicSession("patient-001");
const second = await runDeterministicSession("patient-001");
assert.deepEqual(first, second, "same seed and commands must reproduce the same event stream");
assert.equal(first.snapshot.stimulation.state, "COMPLETED");
const eegFrames = first.summary.filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME);
assert.ok(eegFrames.length > 0);
assert.ok(eegFrames.every((event) => event.payload.signalModelVersion === "EEG-SIM-1.1"));
assert.ok(eegFrames.every((event) => Array.isArray(event.payload.artifactLabels)));
assert.ok(
  eegFrames.some((event) => (
    event.payload.stimulationActive
    && event.payload.artifactLabels.includes("STIMULATION")
  )),
  "EEG stream should expose stimulation artifacts while stimulation is active",
);
assert.equal(
  first.summary.filter(
    (event) => event.messageType === DEVICE_EVENTS.ACQUISITION_IMPEDANCE_FRAME,
  ).length,
  3,
);
assert.equal(
  first.summary.filter((event) => event.messageType === DEVICE_EVENTS.IMPEDANCE_FRAME).length,
  3,
);
assert.ok(first.summary.some(
  (event) => event.messageType === DEVICE_EVENTS.ACQUISITION_IMPEDANCE_COMPLETED,
));
assert.ok(first.summary.some((event) => event.messageType === DEVICE_EVENTS.MARKER));
assert.ok(first.summary.some((event) => event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED));

const failing = new VirtualDeviceSession({
  scenarioId: "stim-impedance-fail",
  seed: "patient-002",
  stimulationImpedancePass: false,
});
await failing.connect();
failing.stimulation.prepareProgram({
  protocolHash: "protocol-demo-002",
  montageHash: "montage-demo-002",
  paradigmId: "TDCS",
  segments: [{ type: "HOLD", durationMs: 100 }],
});
failing.stimulation.startImpedanceCheck({
  channels: [{ channelId: "STIM-1", positionId: "F3" }],
});
failing.advanceBy(500);
assert.equal(failing.stimulation.impedanceResult.status, "FAILED");
assert.throws(() => failing.stimulation.arm({
  toleranceRecord: { recordId: "TOL-002", paradigmId: "TDCS", status: "COMPLETED" },
  stimulationImpedance: failing.stimulation.impedanceResult,
  acquisitionImpedance: { status: "PASSED" },
}), /IMPEDANCE_NOT_PASSED/);
failing.destroy();

const changedMontage = new VirtualDeviceSession({ seed: "patient-003" });
await changedMontage.connect();
changedMontage.stimulation.prepareProgram({
  protocolHash: "protocol-demo-003",
  montageHash: "montage-demo-003",
  paradigmId: "TDCS",
  segments: [{ type: "HOLD", durationMs: 100 }],
});
changedMontage.stimulation.startImpedanceCheck({
  channels: [{ channelId: "STIM-1", positionId: "F3" }],
});
changedMontage.advanceBy(500);
assert.equal(changedMontage.stimulation.impedanceResult.status, "PASSED");
const invalidated = changedMontage.stimulation.prepareProgram({
  protocolHash: "protocol-demo-004",
  montageHash: "montage-demo-004",
  paradigmId: "TDCS",
  segments: [{ type: "HOLD", durationMs: 100 }],
});
assert.equal(invalidated.impedancePreserved, false);
assert.equal(changedMontage.stimulation.impedanceResult, null);
changedMontage.destroy();

const degradedTransport = new VirtualDeviceSession({
  scenarioId: "transport-degradation",
  seed: "patient-transport-001",
  packetLossEveryN: 3,
  packetLatencyMs: 40,
  clockDriftPpm: 1_000,
  patientSignalProfile: {
    profileId: "PATIENT-CUSTOM",
    alphaScale: 1.8,
  },
});
await degradedTransport.connect();
degradedTransport.acquisition.configureAcquisition({
  channels: [{ channelId: "ACQ-1", positionId: "P3" }],
  sampleRateHz: 250,
  reference: { positionId: "Cz" },
  ground: { positionId: "Oz" },
});
degradedTransport.acquisition.startAcquisition({ runId: "RUN-TRANSPORT-001" });
degradedTransport.advanceBy(139);
assert.equal(
  degradedTransport.getEventLog().filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME).length,
  0,
  "delivery latency must delay the first EEG frame",
);
degradedTransport.advanceBy(1);
let transportFrames = degradedTransport.getEventLog()
  .filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME);
assert.equal(transportFrames.length, 1);
assert.equal(transportFrames[0].payload.packetSequence, 1);
assert.equal(transportFrames[0].payload.generatedDeviceTimeUs, 100_100);
assert.equal(transportFrames[0].payload.deliveredDeviceTimeUs, 140_140);
assert.equal(transportFrames[0].payload.signalProfileId, "PATIENT-CUSTOM");
assert.equal(transportFrames[0].payload.clockDriftPpm, 1_000);
degradedTransport.advanceBy(300);
const transportLog = degradedTransport.getEventLog();
const packetDrop = transportLog.find(
  (event) => event.messageType === DEVICE_EVENTS.ACQUISITION_PACKET_DROPPED,
);
assert.equal(packetDrop.payload.packetSequence, 3);
transportFrames = transportLog.filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME);
assert.deepEqual(transportFrames.map((event) => event.payload.packetSequence), [1, 2, 4]);
assert.equal(transportFrames.at(-1).payload.droppedSamplesBefore, 25);
assert.equal(transportFrames.at(-1).payload.firstSampleIndex, 75);
assert.ok(transportFrames.at(-1).payload.samplePeriodDeviceUs > 4_000);
degradedTransport.destroy();

console.log("deterministic virtual device verification passed");
