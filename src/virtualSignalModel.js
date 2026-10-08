export const VIRTUAL_SIGNAL_MODEL_VERSION = "EEG-SIM-1.1";

export const DEFAULT_VIRTUAL_SIGNAL_PROFILE = Object.freeze({
  profileId: "DEFAULT",
  baselineScale: 1,
  thetaScale: 1,
  alphaScale: 1,
  betaScale: 1,
  mainsScale: 1,
  blinkScale: 1,
  blinkIntervalSec: 7,
  muscleScale: 1,
  muscleIntervalSec: 11,
  stimulationArtifactScale: 1,
});

function finiteOr(value, fallback) {
  const normalized = Number(value);
  return Number.isFinite(normalized) ? normalized : fallback;
}

export function normalizeVirtualSignalProfile(profile = {}) {
  return {
    profileId: String(profile.profileId || DEFAULT_VIRTUAL_SIGNAL_PROFILE.profileId),
    baselineScale: finiteOr(profile.baselineScale, 1),
    thetaScale: finiteOr(profile.thetaScale, 1),
    alphaScale: finiteOr(profile.alphaScale, 1),
    betaScale: finiteOr(profile.betaScale, 1),
    mainsScale: finiteOr(profile.mainsScale, 1),
    blinkScale: finiteOr(profile.blinkScale, 1),
    blinkIntervalSec: Math.max(0.5, finiteOr(profile.blinkIntervalSec, 7)),
    muscleScale: finiteOr(profile.muscleScale, 1),
    muscleIntervalSec: Math.max(0.5, finiteOr(profile.muscleIntervalSec, 11)),
    stimulationArtifactScale: finiteOr(profile.stimulationArtifactScale, 1),
  };
}

function gaussian(value, center, width) {
  const distance = (value - center) / width;
  return Math.exp(-0.5 * distance * distance);
}

function pointWeight(positionId, groups) {
  const normalized = String(positionId || "").toUpperCase();
  return groups.some((prefix) => normalized.startsWith(prefix)) ? 1 : 0.35;
}

export function createVirtualEegSample({
  seconds,
  channelIndex,
  positionId,
  noiseUv = 0,
  stimulationActive = false,
  mainsFrequencyHz = 50,
  profile = DEFAULT_VIRTUAL_SIGNAL_PROFILE,
}) {
  const signalProfile = normalizeVirtualSignalProfile(profile);
  const phase = channelIndex * 0.47;
  const posteriorWeight = pointWeight(positionId, ["P", "O"]);
  const frontalWeight = pointWeight(positionId, ["FP", "F"]);
  const temporalWeight = pointWeight(positionId, ["T"]);
  const baseline = Math.sin(2 * Math.PI * 0.22 * seconds + phase) * 3.2 * signalProfile.baselineScale;
  const theta = Math.sin(2 * Math.PI * (5 + channelIndex * 0.04) * seconds + phase) * 3.4 * signalProfile.thetaScale;
  const alpha = Math.sin(2 * Math.PI * (9.5 + channelIndex * 0.08) * seconds + phase)
    * (5 + posteriorWeight * 4)
    * signalProfile.alphaScale;
  const beta = Math.sin(2 * Math.PI * (18 + channelIndex * 0.15) * seconds + phase * 1.7)
    * 1.8
    * signalProfile.betaScale;
  const mains = Math.sin(2 * Math.PI * mainsFrequencyHz * seconds) * 0.9 * signalProfile.mainsScale;

  const blinkCycle = seconds % signalProfile.blinkIntervalSec;
  const blink = gaussian(blinkCycle, 0.24, 0.08) * 42 * frontalWeight * signalProfile.blinkScale;
  const muscleCycle = seconds % signalProfile.muscleIntervalSec;
  const muscleActive = muscleCycle >= 4.5 && muscleCycle <= 5.15;
  const muscle = muscleActive
    ? (
      Math.sin(2 * Math.PI * 35 * seconds + phase)
      + Math.sin(2 * Math.PI * 43 * seconds + phase * 0.5)
    ) * 4.5 * Math.max(frontalWeight, temporalWeight) * signalProfile.muscleScale
    : 0;
  const stimulationArtifact = stimulationActive
    ? (
      Math.sin(2 * Math.PI * 17 * seconds)
      + 0.45 * Math.sin(2 * Math.PI * 34 * seconds + phase)
    ) * 17 * signalProfile.stimulationArtifactScale
    : 0;

  const artifactLabels = [];
  if (blink > 1) artifactLabels.push("BLINK");
  if (muscleActive) artifactLabels.push("MUSCLE");
  if (stimulationActive) artifactLabels.push("STIMULATION");

  return {
    valueUv: baseline + theta + alpha + beta + mains + blink + muscle + stimulationArtifact + noiseUv,
    artifactLabels,
    componentsUv: {
      baseline,
      theta,
      alpha,
      beta,
      mains,
      blink,
      muscle,
      stimulationArtifact,
      noise: noiseUv,
    },
  };
}

export function impedanceQuality(valueKohm) {
  if (valueKohm <= 10) return "EXCELLENT";
  if (valueKohm <= 20) return "GOOD";
  if (valueKohm <= 30) return "MEDIUM";
  if (valueKohm <= 40) return "POOR";
  return "BAD";
}

export function interpolateImpedance(startKohm, finalKohm, progress) {
  const normalizedProgress = Math.max(0, Math.min(1, Number(progress) || 0));
  const eased = 1 - ((1 - normalizedProgress) ** 2);
  return Number((startKohm + (finalKohm - startKohm) * eased).toFixed(2));
}
