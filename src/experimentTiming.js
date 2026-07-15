export const MIN_DURATION_MS = 10;
export const MAX_DURATION_MS = 60_000;
export const MIN_CYCLE_COUNT = 1;
export const MAX_CYCLE_COUNT = 99;

const UNIT_FACTORS = {
  ms: 1,
  s: 1000,
};

const PROTOTYPE_PHASE_DELAYS = {
  blanking: 900,
  stimReady: 900,
  recovery: 1200,
};

export function toMilliseconds(value, unit) {
  if (!Object.hasOwn(UNIT_FACTORS, unit) || value === "" || value === null || value === undefined) {
    return Number.NaN;
  }

  const numericValue = Number(value);
  if (!Number.isFinite(numericValue)) return Number.NaN;
  return numericValue * UNIT_FACTORS[unit];
}

export function clampDurationMs(value) {
  return Math.min(MAX_DURATION_MS, Math.max(MIN_DURATION_MS, Math.round(value)));
}

export function normalizeDurationInput(value, unit, fallbackMs) {
  const durationMs = toMilliseconds(value, unit);
  return Number.isFinite(durationMs) ? clampDurationMs(durationMs) : fallbackMs;
}

export function normalizeCycleCount(value, fallbackCount) {
  if (value === "" || value === null || value === undefined) return fallbackCount;
  const numericValue = Number(value);
  if (!Number.isFinite(numericValue)) return fallbackCount;
  return Math.min(MAX_CYCLE_COUNT, Math.max(MIN_CYCLE_COUNT, Math.round(numericValue)));
}

export function getAutoRecoveryTransition(completedCycles, cycleCount) {
  const nextCompletedCycles = completedCycles + 1;
  return {
    nextPhase: nextCompletedCycles < cycleCount ? "acquisition" : "finished",
    completedCycles: nextCompletedCycles,
  };
}

export function getRunStateAfterModeSwitch(currentMode, nextMode, currentRuntime) {
  if (currentMode === nextMode) return currentRuntime;
  return {
    phase: "standby",
    elapsedMs: 0,
    runStartedAt: null,
    completedCycles: 0,
    isExportPanelOpen: false,
  };
}

export function formatDurationForInput(durationMs, unit) {
  if (unit === "ms") return String(Math.round(durationMs));
  const seconds = durationMs / UNIT_FACTORS.s;
  return seconds.toFixed(3).replace(/\.?0+$/, "");
}

export function formatDurationLabel(durationMs, unit) {
  return `${formatDurationForInput(durationMs, unit)} ${unit}`;
}

export function getPhaseDelayMs(phase, configuredDurations) {
  if (phase === "acquisition" || phase === "stimulation") {
    return configuredDurations[phase];
  }
  return PROTOTYPE_PHASE_DELAYS[phase] ?? null;
}

export function canEditPhaseDuration(stageId, phase, runMode) {
  if (!['acquisition', 'stimulation'].includes(stageId)) return false;
  if (phase === 'standby') return true;
  return stageId === 'stimulation' && phase === 'stimReady' && runMode === 'manual';
}

export function formatElapsedTime(elapsedMs) {
  const totalSeconds = Math.max(0, Math.floor(elapsedMs / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor(totalSeconds % 3600 / 60);
  const seconds = totalSeconds % 60;
  return [hours, minutes, seconds].map((value) => String(value).padStart(2, "0")).join(":");
}
