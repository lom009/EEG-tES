import assert from "node:assert/strict";
import {
  createDeviceCapability,
  createDeviceError,
  createMessageSequenceGuard,
  createProtocolMessageFactory,
  DEVICE_COMMANDS,
  DEVICE_ERROR_CODES,
  DEVICE_EVENTS,
  validateAcquisitionConfiguration,
  validateDeviceCapability,
  validateProtocolMessage,
  validateStimulationPrerequisites,
} from "../src/deviceProtocolV1.js";
import {
  assertAcquisitionAdapter,
  assertDeviceSession,
  assertStimulationAdapter,
} from "../src/deviceAdapterContract.js";

const capability = createDeviceCapability();
assert.equal(validateDeviceCapability(capability).isValid, true);
assert.deepEqual(capability.acquisition.supportedSampleRatesHz, [250, 500]);
assert.equal(capability.acquisition.referenceOptions[0], "CONFIGURABLE");

const factory = createProtocolMessageFactory({
  deviceId: "SIM-001",
  nowUs: (() => {
    let value = 1_000_000;
    return () => {
      value += 1_000;
      return value;
    };
  })(),
});
const connect = factory.command(DEVICE_COMMANDS.CONNECT);
const connected = factory.event(DEVICE_EVENTS.CONNECTION_CHANGED, { state: "CONNECTED" }, {
  replyToRequestId: connect.requestId,
});
assert.equal(validateProtocolMessage(connect).isValid, true);
assert.equal(connected.replyToRequestId, connect.requestId);
assert.equal(connect.sequence, 1);
assert.equal(connected.sequence, 2);

const guard = createMessageSequenceGuard();
assert.equal(guard(connect).isValid, true);
assert.equal(guard(connected).isValid, true);
const skipped = { ...factory.event(DEVICE_EVENTS.MARKER, { label: "SKIP" }), sequence: 4 };
assert.deepEqual(guard(skipped).errors, [DEVICE_ERROR_CODES.SEQUENCE_GAP]);

assert.equal(validateAcquisitionConfiguration({
  channels: ["F3", "F4"],
  sampleRateHz: 250,
  reference: { positionId: "Cz" },
  ground: { positionId: "Oz" },
}, capability).isValid, true);
assert.ok(validateAcquisitionConfiguration({
  channels: ["F3"],
  sampleRateHz: 1000,
  reference: { positionId: "Cz" },
  ground: { positionId: "Cz" },
}, capability).errors.length >= 2);

const prerequisites = validateStimulationPrerequisites({
  capability,
  program: { protocolHash: "sim-123", paradigmId: "TDCS" },
  toleranceRecord: { recordId: "TOL-1", paradigmId: "TDCS", status: "COMPLETED" },
  stimulationImpedance: { status: "PASSED" },
  acquisitionImpedance: { status: "PASSED" },
});
assert.equal(prerequisites.isValid, true);
assert.deepEqual(
  validateStimulationPrerequisites({
    capability,
    program: { protocolHash: "sim-123", paradigmId: "TDCS" },
    toleranceRecord: null,
    stimulationImpedance: { status: "PASSED" },
    acquisitionImpedance: { status: "PASSED" },
  }).errors,
  [DEVICE_ERROR_CODES.PATIENT_TOLERANCE_RECORD_MISSING],
);

assert.equal(
  createDeviceError(DEVICE_ERROR_CODES.OPERATOR_ABORT, "急停", {}, { recoverable: true }).recoverable,
  true,
);

const sessionMethods = {
  connect() {},
  disconnect() {},
  getIdentity() {},
  getCapabilities() {},
  subscribe() {},
  destroy() {},
};
assertDeviceSession(sessionMethods);
assertAcquisitionAdapter({
  ...sessionMethods,
  configureAcquisition() {},
  startImpedanceCheck() {},
  stopImpedanceCheck() {},
  startAcquisition() {},
  stopAcquisition() {},
  pushMarker() {},
});
assertStimulationAdapter({
  ...sessionMethods,
  prepareProgram() {},
  startImpedanceCheck() {},
  stopImpedanceCheck() {},
  arm() {},
  startRun() {},
  abortRun() {},
  reset() {},
});

console.log("device protocol v1 contract verification passed");
