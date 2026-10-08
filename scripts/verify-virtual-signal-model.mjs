import assert from "node:assert/strict";
import {
  createVirtualEegSample,
  normalizeVirtualSignalProfile,
  impedanceQuality,
  interpolateImpedance,
  VIRTUAL_SIGNAL_MODEL_VERSION,
} from "../src/virtualSignalModel.js";

assert.equal(VIRTUAL_SIGNAL_MODEL_VERSION, "EEG-SIM-1.1");

const deterministicInput = {
  seconds: 3.125,
  channelIndex: 2,
  positionId: "P3",
  noiseUv: 0.35,
  stimulationActive: false,
};
assert.deepEqual(
  createVirtualEegSample(deterministicInput),
  createVirtualEegSample(deterministicInput),
  "signal model must be deterministic for identical inputs",
);

const frontalBlink = createVirtualEegSample({
  seconds: 0.24,
  channelIndex: 0,
  positionId: "FP1",
});
const posteriorBlink = createVirtualEegSample({
  seconds: 0.24,
  channelIndex: 0,
  positionId: "O1",
});
assert.ok(frontalBlink.artifactLabels.includes("BLINK"));
assert.ok(posteriorBlink.artifactLabels.includes("BLINK"));
assert.ok(
  frontalBlink.componentsUv.blink > posteriorBlink.componentsUv.blink * 2,
  "blink artifact should be stronger on frontal channels",
);

const muscle = createVirtualEegSample({
  seconds: 4.8,
  channelIndex: 1,
  positionId: "T3",
});
assert.ok(muscle.artifactLabels.includes("MUSCLE"));
assert.notEqual(muscle.componentsUv.muscle, 0);

const stimulation = createVirtualEegSample({
  seconds: 1.237,
  channelIndex: 1,
  positionId: "C3",
  stimulationActive: true,
});
assert.ok(stimulation.artifactLabels.includes("STIMULATION"));
assert.notEqual(stimulation.componentsUv.stimulationArtifact, 0);

const profiledAlpha = createVirtualEegSample({
  seconds: 3.125,
  channelIndex: 2,
  positionId: "P3",
  profile: { profileId: "HIGH-ALPHA", alphaScale: 2 },
});
const defaultAlpha = createVirtualEegSample({
  seconds: 3.125,
  channelIndex: 2,
  positionId: "P3",
});
assert.ok(Math.abs(profiledAlpha.componentsUv.alpha) > Math.abs(defaultAlpha.componentsUv.alpha));
assert.deepEqual(
  normalizeVirtualSignalProfile({ profileId: "P-001", blinkIntervalSec: 0 }),
  normalizeVirtualSignalProfile({ profileId: "P-001", blinkIntervalSec: 0 }),
);

assert.equal(interpolateImpedance(30, 8, -1), 30);
assert.equal(interpolateImpedance(30, 8, 0), 30);
assert.equal(interpolateImpedance(30, 8, 1), 8);
assert.equal(interpolateImpedance(30, 8, 2), 8);
assert.ok(interpolateImpedance(30, 8, 0.6) < interpolateImpedance(30, 8, 0.25));

assert.equal(impedanceQuality(8), "EXCELLENT");
assert.equal(impedanceQuality(15), "GOOD");
assert.equal(impedanceQuality(25), "MEDIUM");
assert.equal(impedanceQuality(35), "POOR");
assert.equal(impedanceQuality(45), "BAD");

console.log("virtual signal model verification passed");
