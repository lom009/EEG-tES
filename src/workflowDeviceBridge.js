function stableHash(value) {
  const source = JSON.stringify(value);
  let hash = 2166136261;
  for (let index = 0; index < source.length; index += 1) {
    hash ^= source.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `workflow-${(hash >>> 0).toString(16).padStart(8, "0")}`;
}

function secondsToMs(value) {
  return Math.max(0, Math.round((Number(value) || 0) * 1000));
}

export function createAcquisitionConfiguration({
  acquisitionPoints,
  referencePoint,
  groundPoint,
  sampleRate,
}) {
  return {
    channels: acquisitionPoints.map((positionId, index) => ({
      channelId: `ACQ-${index + 1}`,
      positionId,
      label: positionId,
    })),
    sampleRateHz: Number(sampleRate),
    reference: { positionId: referencePoint },
    ground: { positionId: groundPoint },
  };
}

export function createStimulationChannels({
  stimulationAssignments,
  roles,
}) {
  const roleById = new Map(roles.map((role) => [role.id, role]));
  return Object.entries(stimulationAssignments).map(([positionId, roleId], index) => {
    const role = roleById.get(roleId);
    return {
      channelId: role?.channel || `STIM-${index + 1}`,
      positionId,
      roleId,
      label: role?.label || roleId,
    };
  });
}

export function createStimulationProgram({
  paradigmId,
  modeId,
  parameters,
  stimulationAssignments,
  roles,
  durationOverrideMs,
}) {
  const topologyId = modeId.startsWith("sham-") ? "dual" : modeId;
  const durationMs = durationOverrideMs ?? secondsToMs(
    parameters.durationSec ?? parameters.totalWaitSec ?? parameters.shortStimSec ?? 1,
  );
  const rampUpMs = Math.min(durationMs, secondsToMs(parameters.rampUpSec));
  const rampDownMs = Math.min(
    Math.max(0, durationMs - rampUpMs),
    secondsToMs(parameters.rampDownSec),
  );
  const holdMs = Math.max(0, durationMs - rampUpMs - rampDownMs);
  const channels = createStimulationChannels({ stimulationAssignments, roles });
  const montageHash = stableHash({
    topologyId,
    channels,
  });
  const programSource = {
    paradigmId: paradigmId.toUpperCase(),
    topologyId,
    parameters,
    channels,
    durationMs,
  };
  return {
    programVersion: 1,
    simulated: true,
    protocolHash: stableHash(programSource),
    montageHash,
    paradigmId: paradigmId.toUpperCase(),
    topologyId: topologyId.toUpperCase(),
    parameters: structuredClone(parameters),
    channels,
    segments: [
      { type: "RAMP_UP", durationMs: rampUpMs },
      { type: "HOLD", durationMs: holdMs },
      { type: "RAMP_DOWN", durationMs: rampDownMs },
    ].filter((segment) => segment.durationMs > 0),
  };
}
