import assert from "node:assert/strict";
import {
  formatStimulusValue,
  getShamACFrameAtProgress,
  getShamDCFrameAtProgress,
  getTACSFrameAtProgress,
  getTDCSFrameAtProgress,
  getTPCSFrameAtProgress,
  getTRNSFrameAtProgress,
  getTRNSPathPoints,
} from "../src/model.js";

const start = getTDCSFrameAtProgress(0, { targetCurrent: 2 });
assert.deepEqual(start, {
  progress: 0,
  x: 1,
  y: 37,
  value: 0,
}, "the dot center should sit exactly on the lower-left path endpoint");

const rampEndProgress = 20 / 129;
const rampEnd = getTDCSFrameAtProgress(rampEndProgress, { targetCurrent: 2 });
assert.equal(rampEnd.x, 21, "the moving point should reach the Figma ramp endpoint");
assert.equal(rampEnd.y, 1, "the dot center should sit exactly on the plateau path centerline");
assert.equal(rampEnd.value, 2, "the displayed value should reach the target at the ramp endpoint");

const plateau = getTDCSFrameAtProgress(0.5, { targetCurrent: 2 });
assert.equal(plateau.y, 1, "the moving point should stay centered on the Figma plateau");
assert.equal(plateau.value, 2, "the plateau should retain the target current");

const descending = getTDCSFrameAtProgress(119 / 129, { targetCurrent: -2 });
assert.equal(descending.x, 120, "the moving point should use progress as its horizontal position");
assert.equal(descending.value, -1, "a negative target should produce a synchronized negative value");

const negativeStart = getTDCSFrameAtProgress(0, { targetCurrent: -2 });
assert.equal(negativeStart.y, 1, "negative tDCS should start from the mirrored upper baseline");

const negativePlateau = getTDCSFrameAtProgress(0.5, { targetCurrent: -2 });
assert.equal(negativePlateau.y, 37, "negative tDCS should place its plateau on the lower mirrored path");
assert.equal(negativePlateau.value, -2, "the mirrored plateau should stay synchronized with -2 mA");

const negativeEnd = getTDCSFrameAtProgress(1, { targetCurrent: -2 });
assert.equal(negativeEnd.y, 1, "negative tDCS should end on the mirrored upper baseline");

const end = getTDCSFrameAtProgress(1, { targetCurrent: 2 });
assert.deepEqual(end, {
  progress: 1,
  x: 130,
  y: 37,
  value: 0,
}, "the dot center should sit exactly on the lower-right path endpoint");

assert.equal(getTDCSFrameAtProgress(-1, { targetCurrent: 2 }).progress, 0, "progress should clamp below zero");
assert.equal(getTDCSFrameAtProgress(2, { targetCurrent: 2 }).progress, 1, "progress should clamp above one");
assert.equal(getTDCSFrameAtProgress(Number.NaN, { targetCurrent: 2 }).progress, 0, "invalid progress should fall back to zero");

const tacsStart = getTACSFrameAtProgress(0, { peakCurrent: 2 });
assert.deepEqual(tacsStart, {
  progress: 0,
  x: 1,
  y: 41.4332,
  value: 1.968937,
}, "tACS should start with the point centered on the exported Figma path");

const tacsNegativePeak = getTACSFrameAtProgress((32.457 - 1) / 125, { peakCurrent: 2 });
assert.deepEqual(tacsNegativePeak, {
  progress: 0.251656,
  x: 32.457,
  y: 0.751582,
  value: -2,
}, "the upper tACS peak should synchronize the path point with -2 mA");

const tacsPositivePeak = getTACSFrameAtProgress((65.5695 - 1) / 125, { peakCurrent: 2 });
assert.deepEqual(tacsPositivePeak, {
  progress: 0.516556,
  x: 65.5695,
  y: 41.7516,
  value: 2,
}, "the lower tACS peak should synchronize the path point with +2 mA");

const tacsSecondNegativePeak = getTACSFrameAtProgress((98.6821 - 1) / 125, { peakCurrent: 2 });
assert.equal(tacsSecondNegativePeak.y, 0.751582, "the point should stay centered on the second exported peak");
assert.equal(tacsSecondNegativePeak.value, -2, "the second upper peak should also display -2 mA");

assert.equal(getTACSFrameAtProgress(-1, { peakCurrent: 2 }).progress, 0, "tACS progress should clamp below zero");
assert.equal(getTACSFrameAtProgress(2, { peakCurrent: 2 }).progress, 1, "tACS progress should clamp above one");

const tpcsStart = getTPCSFrameAtProgress(0, { pulseCurrent: 1.4 });
assert.deepEqual(tpcsStart, {
  progress: 0,
  x: 1,
  y: 37,
  value: 0,
  baselineY: 37,
  pulseY: 11.8,
}, "tPCS should begin on the zero-current baseline from the reference screenshot");

const tpcsFirstPulse = getTPCSFrameAtProgress(0.25, { pulseCurrent: 1.4 });
assert.deepEqual(tpcsFirstPulse, {
  progress: 0.25,
  x: 33.25,
  y: 11.8,
  value: 1.4,
  baselineY: 37,
  pulseY: 11.8,
}, "the point and displayed value should move onto the first 1.4 mA pulse together");

const tpcsBetweenPulses = getTPCSFrameAtProgress(0.5, { pulseCurrent: 1.4 });
assert.equal(tpcsBetweenPulses.y, 37, "the point should return to the baseline between pulses");
assert.equal(tpcsBetweenPulses.value, 0, "the value should return to zero between pulses");

const tpcsSecondPulse = getTPCSFrameAtProgress(0.75, { pulseCurrent: 1.4 });
assert.equal(tpcsSecondPulse.y, 11.8, "the point should sit on the second pulse plateau");
assert.equal(tpcsSecondPulse.value, 1.4, "the second pulse should reuse the configured current");

const tpcsNegativePulse = getTPCSFrameAtProgress(0.75, { pulseCurrent: -1.4 });
assert.equal(tpcsNegativePulse.baselineY, 1, "negative tPCS should mirror the baseline to the top edge");
assert.equal(tpcsNegativePulse.y, 26.2, "negative tPCS should move downward along the mirrored pulse path");
assert.equal(tpcsNegativePulse.value, -1.4, "negative pulse polarity should remain synchronized with the path");

const tpcsCustomWindow = getTPCSFrameAtProgress(0.4, {
  pulseCurrent: 1,
  pulseWindows: [[0.35, 0.45]],
});
assert.equal(tpcsCustomWindow.value, 1, "callers should be able to reuse tPCS with custom pulse windows");

assert.equal(getTPCSFrameAtProgress(-1).progress, 0, "tPCS progress should clamp below zero");
assert.equal(getTPCSFrameAtProgress(2).progress, 1, "tPCS progress should clamp above one");

const shamDCStart = getShamDCFrameAtProgress(0, { shamCurrent: 1.8 });
assert.deepEqual(shamDCStart, {
  progress: 0,
  x: 1,
  y: 37,
  value: 0,
  baselineY: 37,
  peakY: 4.6,
}, "Sham DC should start at zero before the opening triangle rises");

const shamDCFirstPeak = getShamDCFrameAtProgress(0.065, { shamCurrent: 1.8 });
assert.equal(shamDCFirstPeak.y, 4.6, "the opening Sham DC dot should reach the triangle peak");
assert.equal(shamDCFirstPeak.value, 1.8, "the opening triangle should reach the configured current");

const shamDCMiddle = getShamDCFrameAtProgress(0.5, { shamCurrent: 1.8 });
assert.equal(shamDCMiddle.y, 37, "Sham DC should return to its zero baseline between pulses");
assert.equal(shamDCMiddle.value, 0, "Sham DC should report zero between pulses");

const shamDCSecondPeak = getShamDCFrameAtProgress(0.946, { shamCurrent: 1.8 });
assert.equal(shamDCSecondPeak.y, 4.6, "the closing Sham DC triangle should reuse the same peak");
assert.equal(shamDCSecondPeak.value, 1.8, "the closing Sham DC triangle should reuse the current");

const shamDCNegativePeak = getShamDCFrameAtProgress(0.065, { shamCurrent: -1.8 });
assert.equal(shamDCNegativePeak.baselineY, 1, "negative Sham DC should mirror the baseline to the top");
assert.equal(shamDCNegativePeak.y, 33.4, "negative Sham DC should mirror the whole triangle downward");
assert.equal(shamDCNegativePeak.value, -1.8, "negative Sham DC should keep path and value polarity synchronized");

const shamACStart = getShamACFrameAtProgress(0, { shamPeakCurrent: 0.9 });
assert.deepEqual(shamACStart, {
  progress: 0,
  x: 1,
  y: 19,
  value: 0,
  centerY: 19,
}, "Sham AC should start on the centered zero baseline");

const shamACPositivePeak = getShamACFrameAtProgress(0.0375, { shamPeakCurrent: 0.9 });
assert.equal(shamACPositivePeak.y, 1, "the first Sham AC quarter-cycle should reach the upper peak");
assert.equal(shamACPositivePeak.value, 0.9, "the upper peak should display the positive instantaneous value");

const shamACNegativePeak = getShamACFrameAtProgress(0.1125, { shamPeakCurrent: 0.9 });
assert.equal(shamACNegativePeak.y, 37, "the first Sham AC three-quarter-cycle should reach the lower peak");
assert.equal(shamACNegativePeak.value, -0.9, "the lower peak should display the negative instantaneous value");

const shamACMiddle = getShamACFrameAtProgress(0.5, { shamPeakCurrent: 0.9 });
assert.equal(shamACMiddle.y, 19, "Sham AC should stay centered between its two bursts");
assert.equal(shamACMiddle.value, 0, "Sham AC should report zero between bursts");

const shamACIgnoresPolarity = getShamACFrameAtProgress(0.0375, { shamPeakCurrent: -0.9 });
assert.equal(shamACIgnoresPolarity.value, 0.9, "Sham AC peak current is a magnitude and must not create a polarity variant");

const trnsPath = getTRNSPathPoints();
assert.equal(trnsPath.length, 9, "the compact tRNS illustration should use only seven interior turns");

const trnsStart = getTRNSFrameAtProgress(0, { noiseAmplitude: 1.8 });
assert.deepEqual(trnsStart, {
  progress: 0,
  x: 1,
  y: 15,
  value: 1.8,
}, "tRNS should start on the simplified path while displaying the configured amplitude");

const trnsPeak = getTRNSFrameAtProgress(0.24, { noiseAmplitude: 1.8 });
assert.equal(trnsPeak.y, 7.8, "the tRNS point should stay centered on the compact path vertex");
assert.equal(trnsPeak.value, 1.8, "the tRNS value should stay static instead of exposing noisy instantaneous samples");

const trnsNoPolarity = getTRNSFrameAtProgress(0.24, { noiseAmplitude: -1.8 });
assert.equal(trnsNoPolarity.y, 7.8, "negative input must not mirror the tRNS illustration");
assert.equal(trnsNoPolarity.value, 1.8, "tRNS amplitude should always be treated as a positive magnitude");

assert.equal(getTRNSFrameAtProgress(-1).progress, 0, "tRNS progress should clamp below zero");
assert.equal(getTRNSFrameAtProgress(2).progress, 1, "tRNS progress should clamp above one");

assert.equal(formatStimulusValue(2, "mA"), "2.00 mA");
assert.equal(formatStimulusValue(-1.256, "mA"), "-1.26 mA");
assert.equal(formatStimulusValue(-0.0001, "mA"), "0.00 mA", "rounded zero should not render a negative sign");

console.log("stimulus visualization verification passed");
