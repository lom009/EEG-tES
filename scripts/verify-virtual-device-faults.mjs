import assert from "node:assert/strict";
import {
  createDeviceCapability,
  DEVICE_ERROR_CODES,
  DEVICE_EVENTS,
} from "../src/deviceProtocolV1.js";
import { VirtualDeviceSession } from "../src/virtualDeviceSession.js";

async function startAcquisition(session, runId = "RUN-FAULT-001") {
  await session.connect();
  session.acquisition.configureAcquisition({
    channels: [{ channelId: "ACQ-1", positionId: "F3" }],
    sampleRateHz: 250,
    reference: { positionId: "Cz" },
    ground: { positionId: "Oz" },
  });
  session.acquisition.startAcquisition({ runId });
}

const warningContact = new VirtualDeviceSession({
  scenarioId: "electrode-warning",
  seed: "fault-seed-electrode-warning",
  runtimeElectrodeFaultAtMs: 150,
  runtimeElectrodePositionId: "F3",
  runtimeElectrodePolicy: "WARN",
});
await startAcquisition(warningContact);
warningContact.advanceBy(200);
let faultLog = warningContact.getEventLog();
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.ACQUISITION_CONTACT_CHANGED
    && event.payload.positionId === "F3"
    && event.payload.action === "WARN"
  ),
));
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.FAULT
    && event.payload.code === DEVICE_ERROR_CODES.ELECTRODE_CONTACT_LOST
    && event.payload.severity === "WARNING"
  ),
));
assert.equal(warningContact.getSnapshot().acquisition.state, "RUNNING");
warningContact.destroy();

const abortContact = new VirtualDeviceSession({
  scenarioId: "electrode-abort",
  seed: "fault-seed-electrode-abort",
  runtimeElectrodeFaultAtMs: 150,
  runtimeElectrodePositionId: "P3",
  runtimeElectrodePolicy: "ABORT",
});
await startAcquisition(abortContact, "RUN-FAULT-ABORT");
abortContact.advanceBy(200);
faultLog = abortContact.getEventLog();
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.FAULT
    && event.payload.code === DEVICE_ERROR_CODES.ELECTRODE_CONTACT_LOST
    && event.payload.details.action === "ABORT"
  ),
));
assert.equal(abortContact.getSnapshot().acquisition.state, "STOPPED");
assert.ok(faultLog.some(
  (event) => event.messageType === DEVICE_EVENTS.ACQUISITION_STOPPED,
));
abortContact.destroy();

const disconnected = new VirtualDeviceSession({
  scenarioId: "transport-disconnect",
  seed: "fault-seed-disconnect",
  disconnectAtMs: 120,
});
await startAcquisition(disconnected, "RUN-FAULT-DISCONNECT");
disconnected.advanceBy(150);
const disconnectSnapshot = disconnected.getSnapshot();
faultLog = disconnected.getEventLog();
assert.equal(disconnectSnapshot.acquisition.connectionState, "DISCONNECTED");
assert.equal(disconnectSnapshot.stimulation.connectionState, "DISCONNECTED");
assert.equal(disconnectSnapshot.acquisition.state, "STOPPED");
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.FAULT
    && event.payload.code === DEVICE_ERROR_CODES.TRANSPORT_DISCONNECTED
  ),
));
assert.equal(
  faultLog.filter(
    (event) => (
      event.messageType === DEVICE_EVENTS.CONNECTION_CHANGED
      && event.payload.state === "DISCONNECTED"
    ),
  ).length,
  2,
);
disconnected.destroy();

const rollback = new VirtualDeviceSession({
  scenarioId: "timestamp-rollback",
  seed: "fault-seed-clock",
  timestampRollbackAtPacket: 3,
  timestampRollbackUs: 250_000,
  timestampRollbackPolicy: "ABORT",
});
await startAcquisition(rollback, "RUN-FAULT-CLOCK");
rollback.advanceBy(350);
faultLog = rollback.getEventLog();
assert.deepEqual(
  faultLog
    .filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME)
    .map((event) => event.payload.packetSequence),
  [1, 2],
  "the rollback frame must be rejected instead of entering the EEG stream",
);
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.ACQUISITION_FRAME_REJECTED
    && event.payload.packetSequence === 3
    && event.payload.reason === DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK
  ),
));
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.FAULT
    && event.payload.code === DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK
  ),
));
assert.equal(rollback.getSnapshot().acquisition.state, "STOPPED");
rollback.destroy();

const capabilityMismatch = new VirtualDeviceSession({
  scenarioId: "capability-mismatch",
  seed: "fault-seed-capability",
  capability: createDeviceCapability({
    stimulation: {
      supportedParadigms: ["TDCS"],
      supportedTopologies: ["DUAL_ELECTRODE"],
    },
  }),
});
await capabilityMismatch.connect();
assert.throws(() => capabilityMismatch.stimulation.prepareProgram({
  protocolHash: "protocol-capability-mismatch",
  montageHash: "montage-capability-mismatch",
  paradigmId: "TACS",
  topologyId: "HD_4X1",
  segments: [{ type: "HOLD", durationMs: 100 }],
}), /CAPABILITY_MISMATCH/);
capabilityMismatch.destroy();

const emergencyRestart = new VirtualDeviceSession({
  scenarioId: "operator-abort-and-restart",
  seed: "fault-seed-operator-abort",
});
await emergencyRestart.connect();
const restartProgram = {
  protocolHash: "protocol-restart",
  montageHash: "montage-restart",
  paradigmId: "TDCS",
  topologyId: "DUAL",
  segments: [{ type: "HOLD", durationMs: 100 }],
};
emergencyRestart.stimulation.prepareProgram(restartProgram);
emergencyRestart.stimulation.arm({
  toleranceRecord: {
    recordId: "TOL-RESTART",
    paradigmId: "TDCS",
    status: "COMPLETED",
  },
  stimulationImpedance: { status: "PASSED" },
  acquisitionImpedance: { status: "PASSED" },
});
emergencyRestart.stimulation.startRun("RUN-BEFORE-ABORT");
emergencyRestart.advanceBy(20);
assert.equal(
  emergencyRestart.stimulation.abortRun(
    DEVICE_ERROR_CODES.OPERATOR_ABORT,
    "RUN-BEFORE-ABORT",
  ).aborted,
  true,
);
emergencyRestart.advanceBy(200);
faultLog = emergencyRestart.getEventLog();
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.STIMULATION_ABORTED
    && event.runId === "RUN-BEFORE-ABORT"
    && event.payload.safeStateConfirmed === true
  ),
));
assert.equal(
  faultLog.some(
    (event) => (
      event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED
      && event.runId === "RUN-BEFORE-ABORT"
    ),
  ),
  false,
  "an aborted run must never be recorded as completed",
);
emergencyRestart.stimulation.prepareProgram(restartProgram);
emergencyRestart.stimulation.arm({
  toleranceRecord: {
    recordId: "TOL-RESTART",
    paradigmId: "TDCS",
    status: "COMPLETED",
  },
  stimulationImpedance: { status: "PASSED" },
  acquisitionImpedance: { status: "PASSED" },
});
emergencyRestart.stimulation.startRun("RUN-AFTER-RESTART");
emergencyRestart.advanceBy(100);
faultLog = emergencyRestart.getEventLog();
assert.ok(faultLog.some(
  (event) => (
    event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED
    && event.runId === "RUN-AFTER-RESTART"
  ),
));
assert.notEqual("RUN-BEFORE-ABORT", "RUN-AFTER-RESTART");
emergencyRestart.destroy();

console.log("virtual device fault verification passed");
