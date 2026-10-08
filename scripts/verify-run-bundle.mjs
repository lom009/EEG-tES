import assert from "node:assert/strict";
import { DEVICE_EVENTS } from "../src/deviceProtocolV1.js";
import {
  createRunBundle,
  deserializeRunBundlePortable,
  readRunBundleEvents,
  ReplayDeviceSession,
  RUN_BUNDLE_SOURCE_KINDS,
  serializeRunBundlePortable,
  validateRunBundle,
} from "../src/runBundle.js";
import { VirtualDeviceSession } from "../src/virtualDeviceSession.js";

const runId = "RUN-BUNDLE-001";
const session = new VirtualDeviceSession({
  scenarioId: "bundle-happy-path",
  seed: "bundle-seed",
  packetLatencyMs: 10,
});
await session.connect();
session.acquisition.configureAcquisition({
  channels: [
    { channelId: "ACQ-1", positionId: "F3" },
    { channelId: "ACQ-2", positionId: "C3" },
  ],
  sampleRateHz: 250,
  reference: { positionId: "Cz" },
  ground: { positionId: "Oz" },
});
session.acquisition.startAcquisition({ runId });
session.advanceBy(350);
session.acquisition.stopAcquisition({ runId });

const originalEvents = session.getEventLog().filter((event) => (
  event.runId == null || event.runId === runId
));
assert.ok(originalEvents.some((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME));

const bundle = createRunBundle({
  runId,
  eventLog: originalEvents,
  patient: { patientId: "PAT-BUNDLE-001", name: "模拟患者" },
  operator: { operatorId: "OP-001", name: "实验员 A" },
  capability: session.capability,
  protocol: { paradigmId: "TDCS", currentMa: 1.5, durationMs: 6000 },
  montage: { topologyId: "DUAL_ELECTRODE", anode: "F3", cathode: "F4" },
  toleranceRecord: { recordId: "TOL-001", thresholdMa: 1.5 },
  result: { status: "COMPLETED", finishedAt: "2026-07-28T12:00:00.000Z" },
  sourceKind: "SIMULATED",
  createdAt: "2026-07-28T12:00:00.000Z",
});

assert.deepEqual(RUN_BUNDLE_SOURCE_KINDS, ["SIMULATED", "REPLAY", "HARDWARE"]);
assert.equal(validateRunBundle(bundle).isValid, true);
assert.equal(bundle.manifest.runId, runId);
assert.equal(bundle.manifest.sourceKind, "SIMULATED");
assert.ok(bundle.manifest.eeg.frameCount >= 3);
assert.ok(bundle.files["eeg-raw.bin"] instanceof Uint8Array);
assert.ok(bundle.files["eeg-raw.bin"].byteLength > 0);

const hydratedEvents = readRunBundleEvents(bundle);
assert.equal(hydratedEvents.length, originalEvents.length);
const originalFrame = originalEvents.find((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME);
const hydratedFrame = hydratedEvents.find((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME);
assert.deepEqual(hydratedFrame.payload.samples.channelValues, originalFrame.payload.samples.channelValues);

const portable = serializeRunBundlePortable(bundle);
const restored = deserializeRunBundlePortable(portable);
assert.equal(validateRunBundle(restored).isValid, true);
assert.deepEqual(
  readRunBundleEvents(restored).map((event) => event.messageType),
  hydratedEvents.map((event) => event.messageType),
);

const replay = new ReplayDeviceSession(restored, { mode: "manual", speed: 2 });
const replayedEvents = [];
replay.subscribe((event) => replayedEvents.push(event));
replay.advanceBy(1_000_000);
assert.equal(replay.getSnapshot().state, "COMPLETED");
assert.equal(replayedEvents.length, hydratedEvents.length);
assert.ok(replayedEvents.every((event) => event.sourceKind === "REPLAY"));
assert.deepEqual(
  replayedEvents.map((event) => event.deviceTimeUs),
  hydratedEvents.map((event) => event.deviceTimeUs),
  "replay must preserve the original device clock",
);
assert.ok(replayedEvents.every((event) => event.replay.originalSourceKind === "SIMULATED"));

const midpoint = Math.round(
  (replay.getSnapshot().startDeviceTimeUs + replay.getSnapshot().endDeviceTimeUs) / 2,
);
replay.seek(midpoint);
assert.ok(replay.getSnapshot().cursor > 0);
assert.ok(replay.getSnapshot().cursor < hydratedEvents.length);
replay.destroy();

const realtimeReplay = new ReplayDeviceSession(restored, { mode: "realtime", speed: 1 });
const replayStates = [];
realtimeReplay.subscribeState((snapshot) => replayStates.push(snapshot.state));
realtimeReplay.play();
realtimeReplay.pause();
assert.equal(realtimeReplay.getSnapshot().state, "PAUSED");
realtimeReplay.setSpeed(4);
assert.equal(realtimeReplay.getSnapshot().speed, 4);
realtimeReplay.seek(realtimeReplay.getSnapshot().endDeviceTimeUs);
realtimeReplay.advanceToDeviceTimeUs(realtimeReplay.getSnapshot().endDeviceTimeUs);
assert.equal(realtimeReplay.getSnapshot().state, "COMPLETED");
assert.ok(replayStates.includes("PLAYING"));
assert.ok(replayStates.includes("PAUSED"));
assert.ok(replayStates.includes("COMPLETED"));
realtimeReplay.destroy();

const tampered = deserializeRunBundlePortable(portable);
tampered.files["result.json"] = tampered.files["result.json"].replace("COMPLETED", "ABORTED");
assert.ok(
  validateRunBundle(tampered).errors.includes("RUN_BUNDLE_CHECKSUM_MISMATCH:result.json"),
);

session.destroy();
console.log("run bundle and replay verification passed");
