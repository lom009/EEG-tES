import assert from "node:assert/strict";
import {
  compileProtocol,
  createDemoAssignments,
  createDeviceFlowState,
  createProtocolDraft,
  DEVICE_PROFILES,
  getCompatibleTopologies,
  getParadigmAvailability,
  transitionDeviceFlow,
  validateProtocol,
} from "../src/deviceProtocol.js";
import { assertDeviceAdapter, createDeviceEventFactory } from "../src/deviceAdapterContract.js";

const { DemoDeviceAdapter } = await import("../src/demoDeviceAdapter.js");

const basicProfile = DEVICE_PROFILES["sim-basic"];
const researchProfile = DEVICE_PROFILES["sim-research"];

assert.equal(getParadigmAvailability(basicProfile, "TI").isSupported, false);
assert.match(getParadigmAvailability(basicProfile, "TI").reason, /未声明|两组/);
assert.equal(getParadigmAvailability(researchProfile, "TI").isSupported, true);
assert.deepEqual(getCompatibleTopologies(researchProfile, "TI"), ["TI_DUAL_PAIR", "MULTICHANNEL"]);

let tdcs = createProtocolDraft("sim-basic", "TDCS");
tdcs = { ...tdcs, topologyId: "HD_4X1" };
tdcs = { ...tdcs, assignments: createDemoAssignments(tdcs) };
assert.equal(tdcs.assignments.length, 5);
assert.equal(validateProtocol(tdcs).isValid, true);
const compiledTdcs = compileProtocol(tdcs);
assert.equal(compiledTdcs.ok, true);
assert.equal(compiledTdcs.program.generators[0].waveform, "DC");
assert.equal(compiledTdcs.program.channels.length, 5);
assert.equal(compiledTdcs.program.segments.map((segment) => segment.type).join(","), "RAMP_UP,HOLD,RAMP_DOWN");

let ti = createProtocolDraft("sim-research", "TI");
ti = { ...ti, topologyId: "TI_DUAL_PAIR" };
ti = { ...ti, assignments: createDemoAssignments(ti) };
const compiledTi = compileProtocol(ti);
assert.equal(compiledTi.ok, true);
assert.equal(compiledTi.program.generators.length, 2);
assert.equal(compiledTi.program.channels.filter((channel) => channel.generatorId === "GEN-B").length, 2);

const invalidTi = { ...ti, profileId: "sim-basic" };
assert.equal(validateProtocol(invalidTi, basicProfile).isValid, false);

let flow = createDeviceFlowState();
flow = transitionDeviceFlow(flow, { type: "TOLERANCE_PASS" });
assert.equal(flow.tolerance, "NOT_RUN", "tolerance cannot bypass PREPARE");
flow = transitionDeviceFlow(flow, { type: "CONNECT" });
flow = transitionDeviceFlow(flow, { type: "ACQUISITION_START" });
assert.equal(flow.acquisition, "RUNNING");
flow = transitionDeviceFlow(flow, { type: "PREPARE" });
assert.equal(flow.tolerance, "READY");
assert.equal(flow.impedance, "LOCKED");
flow = transitionDeviceFlow(flow, { type: "TOLERANCE_PASS" });
assert.equal(flow.impedance, "READY");
flow = transitionDeviceFlow(flow, { type: "IMPEDANCE_PASS" });
flow = transitionDeviceFlow(flow, { type: "ARM" });
flow = transitionDeviceFlow(flow, { type: "START" });
flow = transitionDeviceFlow(flow, { type: "COMPLETE" });
assert.equal(flow.run, "COMPLETED");
flow = transitionDeviceFlow(flow, { type: "PROTOCOL_CHANGED" });
assert.deepEqual(
  { prepared: flow.prepared, impedance: flow.impedance, tolerance: flow.tolerance, run: flow.run },
  { prepared: false, impedance: "LOCKED", tolerance: "NOT_RUN", run: "IDLE" },
);

const adapterEvents = [];
const adapter = assertDeviceAdapter(new DemoDeviceAdapter("sim-research", {
  latencyMs: 0,
  timeScale: 0,
  packetIntervalMs: 5,
}));
adapter.subscribe((event) => adapterEvents.push(event));
await adapter.connect();
assert.equal(adapter.connectionState, "CONNECTED");
adapter.startAcquisition();
await new Promise((resolve) => setTimeout(resolve, 20));
adapter.pushMarker("TEST_MARKER");
adapter.stopAcquisition();
assert.ok(adapterEvents.some((event) => event.type === "EEG_SAMPLE"));
assert.ok(adapterEvents.some((event) => event.type === "MARKER_RECEIVED"));
assert.deepEqual(
  adapterEvents.map((event) => event.sequence),
  [...adapterEvents].map((_, index) => index + 1),
  "device event sequence must be monotonic",
);
const prepared = await adapter.prepareProgram(compiledTi.program);
assert.equal(prepared.accepted, true);
await assert.rejects(
  () => adapter.runImpedanceCheck(),
  /TOLERANCE_NOT_PASSED/,
  "lower-machine adapter must reject impedance before tolerance",
);
await assert.rejects(
  () => adapter.arm(),
  /IMPEDANCE_NOT_PASSED/,
  "lower-machine adapter must reject ARM before prechecks",
);
const tolerance = await adapter.runToleranceCheck();
assert.equal(tolerance.passed, true);
const impedance = await adapter.runImpedanceCheck();
assert.equal(impedance.passed, true);
assert.equal(impedance.results.length, 4);
await adapter.arm();
adapter.startRun("RUN-DEMO");
await new Promise((resolve) => setTimeout(resolve, 1500));
assert.equal(adapter.runState, "COMPLETED");
assert.ok(adapterEvents.some((event) => event.type === "PROGRAM_ACCEPTED"));
assert.ok(adapterEvents.some((event) => event.type === "RUN_COMPLETED"));
adapter.destroy();

const deterministicEvents = createDeviceEventFactory({
  sourceId: "TEST",
  now: () => new Date("2026-07-23T00:00:00.000Z"),
});
assert.deepEqual(
  [deterministicEvents("A", "a").eventId, deterministicEvents("B", "b").eventId],
  ["TEST-1", "TEST-2"],
);

console.log("device protocol and simulated lower-machine flow verification passed");
