import assert from "node:assert/strict";
import { processSignalSamples } from "../src/signalProcessing.js";

const unchanged = processSignalSamples([1, 2, 3], 500, {});
assert.deepEqual(unchanged.samplesUv, [1, 2, 3]);

const dcInput = Array.from({ length: 500 }, () => 12);
const highPassed = processSignalSamples(dcInput, 500, { high: true });
assert.ok(Math.abs(highPassed.samplesUv.at(-1)) < 0.001);

const stepInput = [0, 0, 0, 10, 10, 10];
const lowPassed = processSignalSamples(stepInput, 500, { low: true });
assert.ok(lowPassed.samplesUv[3] > 0 && lowPassed.samplesUv[3] < 10);
assert.ok(lowPassed.samplesUv[5] > lowPassed.samplesUv[3]);

const sampleRateHz = 500;
const mains = Array.from(
  { length: 5000 },
  (_, index) => Math.sin(2 * Math.PI * 50 * index / sampleRateHz),
);
const notched = processSignalSamples(mains, sampleRateHz, { notch: true });
const tail = notched.samplesUv.slice(-1000);
const rms = Math.sqrt(tail.reduce((sum, value) => sum + value * value, 0) / tail.length);
assert.ok(rms < 0.15, `50 Hz notch residual should be small, got ${rms}`);

const firstPacket = processSignalSamples(
  mains.slice(0, 250),
  sampleRateHz,
  { notch: true },
);
const secondPacket = processSignalSamples(
  mains.slice(250, 500),
  sampleRateHz,
  { notch: true },
  firstPacket.state,
);
assert.equal(secondPacket.samplesUv.length, 250);
assert.ok(Object.hasOwn(secondPacket.state, "notch"));

console.log("signal processing verification passed");
