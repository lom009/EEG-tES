export const DEVICE_PROFILES = {
  "sim-basic": {
    id: "sim-basic",
    label: "基础刺激器模拟器",
    firmwareVersion: "SIM-1.0.0",
    stimChannelCount: 5,
    eegChannelCount: 5,
    eegSampleRateHz: 250,
    eegChannelLabels: ["Fz", "Cz", "Pz", "Oz", "F3"],
    independentGeneratorCount: 1,
    supportedParadigms: ["TDCS", "TACS"],
    supportedTopologies: ["BIPOLAR", "HD_4X1"],
    features: {
      supportsRuntimeImpedance: true,
      supportsPauseResume: false,
      supportsSimultaneousEegTes: true,
    },
  },
  "sim-research": {
    id: "sim-research",
    label: "多通道研究型模拟器",
    firmwareVersion: "SIM-2.0.0",
    stimChannelCount: 8,
    eegChannelCount: 8,
    eegSampleRateHz: 500,
    eegChannelLabels: ["Fp1", "Fp2", "F3", "F4", "C3", "C4", "P3", "P4"],
    independentGeneratorCount: 2,
    supportedParadigms: ["TDCS", "TACS", "TI"],
    supportedTopologies: ["BIPOLAR", "HD_4X1", "MULTICHANNEL", "TI_DUAL_PAIR"],
    features: {
      supportsRuntimeImpedance: true,
      supportsPauseResume: true,
      supportsSimultaneousEegTes: true,
    },
  },
};

export const STIMULATION_PARADIGMS = {
  TDCS: {
    id: "TDCS",
    label: "tDCS",
    description: "恒定直流刺激，包含缓升、平台和缓降阶段",
    compatibleTopologies: ["BIPOLAR", "HD_4X1", "MULTICHANNEL"],
    defaultParameters: {
      currentMa: 1.5,
      durationMs: 6000,
      rampUpMs: 1000,
      rampDownMs: 1000,
    },
  },
  TACS: {
    id: "TACS",
    label: "tACS",
    description: "正弦交流刺激，配置幅值、频率、相位和包络",
    compatibleTopologies: ["BIPOLAR", "MULTICHANNEL"],
    defaultParameters: {
      amplitudeMa: 1,
      frequencyHz: 10,
      phaseDeg: 0,
      durationMs: 6000,
      rampUpMs: 1000,
      rampDownMs: 1000,
    },
  },
  TI: {
    id: "TI",
    label: "TI",
    description: "两组独立载波通道同时输出并形成差频包络",
    compatibleTopologies: ["TI_DUAL_PAIR", "MULTICHANNEL"],
    defaultParameters: {
      carrierFrequencyAHz: 1000,
      carrierFrequencyBHz: 1010,
      currentAMa: 1,
      currentBMa: 1,
      phaseADeg: 0,
      phaseBDeg: 0,
      durationMs: 6000,
      rampUpMs: 1000,
      rampDownMs: 1000,
    },
  },
};

export const MONTAGE_TOPOLOGIES = {
  BIPOLAR: {
    id: "BIPOLAR",
    label: "Bipolar",
    description: "一个源电极和一个回流电极",
    requiredChannels: 2,
    requiredGenerators: 1,
  },
  HD_4X1: {
    id: "HD_4X1",
    label: "4×1 HD",
    description: "一个中心电极和四个环形回流电极",
    requiredChannels: 5,
    requiredGenerators: 1,
  },
  MULTICHANNEL: {
    id: "MULTICHANNEL",
    label: "Multi-channel",
    description: "多个独立刺激通道按协议分配",
    requiredChannels: 4,
    requiredGenerators: 2,
  },
  TI_DUAL_PAIR: {
    id: "TI_DUAL_PAIR",
    label: "TI dual-pair",
    description: "A/B 两组独立载波回路",
    requiredChannels: 4,
    requiredGenerators: 2,
  },
};

const POSITION_SETS = {
  BIPOLAR: ["F3", "F4"],
  HD_4X1: ["C3", "F3", "F4", "P3", "P4"],
  MULTICHANNEL: ["F3", "F4", "P3", "P4"],
  TI_DUAL_PAIR: ["F3", "P4", "F4", "P3"],
};

function roundCurrent(value) {
  return Number(value.toFixed(4));
}

function stableHash(value) {
  const source = JSON.stringify(value);
  let hash = 2166136261;
  for (let index = 0; index < source.length; index += 1) {
    hash ^= source.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `sim-${(hash >>> 0).toString(16).padStart(8, "0")}`;
}

export function getDeviceProfile(profileId) {
  return DEVICE_PROFILES[profileId] || null;
}

export function getParadigmAvailability(profile, paradigmId) {
  const paradigm = STIMULATION_PARADIGMS[paradigmId];
  if (!profile || !paradigm) {
    return { isSupported: false, reason: "设备或刺激范式不存在" };
  }
  if (!profile.supportedParadigms.includes(paradigmId)) {
    return {
      isSupported: false,
      reason: `当前设备固件未声明 ${paradigm.label} 能力`,
    };
  }
  if (paradigmId === "TI" && profile.independentGeneratorCount < 2) {
    return {
      isSupported: false,
      reason: "TI 至少需要两组独立波形发生器",
    };
  }
  return { isSupported: true, reason: "" };
}

export function getCompatibleTopologies(profile, paradigmId) {
  const paradigm = STIMULATION_PARADIGMS[paradigmId];
  if (!profile || !paradigm) return [];
  return paradigm.compatibleTopologies
    .filter((topologyId) => profile.supportedTopologies.includes(topologyId))
    .filter((topologyId) => {
      const topology = MONTAGE_TOPOLOGIES[topologyId];
      return topology.requiredChannels <= profile.stimChannelCount
        && topology.requiredGenerators <= profile.independentGeneratorCount;
    });
}

export function createProtocolDraft(profileId = "sim-basic", paradigmId = "TDCS") {
  const profile = getDeviceProfile(profileId) || DEVICE_PROFILES["sim-basic"];
  const supportedParadigmId = getParadigmAvailability(profile, paradigmId).isSupported
    ? paradigmId
    : profile.supportedParadigms[0];
  const topologyId = getCompatibleTopologies(profile, supportedParadigmId)[0];
  return {
    schemaVersion: 1,
    profileId: profile.id,
    paradigmId: supportedParadigmId,
    topologyId,
    parameters: { ...STIMULATION_PARADIGMS[supportedParadigmId].defaultParameters },
    assignments: [],
  };
}

export function createDemoAssignments(protocol) {
  const positions = POSITION_SETS[protocol.topologyId] || [];
  const paradigmId = protocol.paradigmId;
  if (protocol.topologyId === "BIPOLAR") {
    const current = paradigmId === "TACS"
      ? Number(protocol.parameters.amplitudeMa)
      : Number(protocol.parameters.currentMa);
    return [
      { channelId: 1, positionId: positions[0], role: "SOURCE", groupId: "A", currentMa: current },
      { channelId: 2, positionId: positions[1], role: "RETURN", groupId: "A", currentMa: -current },
    ];
  }
  if (protocol.topologyId === "HD_4X1") {
    const current = Number(protocol.parameters.currentMa);
    const returnCurrent = roundCurrent(-current / 4);
    return [
      { channelId: 1, positionId: positions[0], role: "CENTER", groupId: "A", currentMa: current },
      ...positions.slice(1).map((positionId, index) => ({
        channelId: index + 2,
        positionId,
        role: "RETURN",
        groupId: "A",
        currentMa: returnCurrent,
      })),
    ];
  }
  if (protocol.topologyId === "TI_DUAL_PAIR") {
    return [
      { channelId: 1, positionId: positions[0], role: "SOURCE", groupId: "A", currentMa: Number(protocol.parameters.currentAMa) },
      { channelId: 2, positionId: positions[1], role: "RETURN", groupId: "A", currentMa: -Number(protocol.parameters.currentAMa) },
      { channelId: 3, positionId: positions[2], role: "SOURCE", groupId: "B", currentMa: Number(protocol.parameters.currentBMa) },
      { channelId: 4, positionId: positions[3], role: "RETURN", groupId: "B", currentMa: -Number(protocol.parameters.currentBMa) },
    ];
  }
  const current = paradigmId === "TI"
    ? Number(protocol.parameters.currentAMa)
    : Number(protocol.parameters.amplitudeMa ?? protocol.parameters.currentMa);
  return positions.map((positionId, index) => ({
    channelId: index + 1,
    positionId,
    role: index % 2 === 0 ? "SOURCE" : "RETURN",
    groupId: index < 2 ? "A" : "B",
    currentMa: index % 2 === 0 ? current : -current,
  }));
}

export function validateProtocol(protocol, profile = getDeviceProfile(protocol.profileId)) {
  const errors = [];
  const warnings = [];
  const paradigm = STIMULATION_PARADIGMS[protocol.paradigmId];
  const topology = MONTAGE_TOPOLOGIES[protocol.topologyId];
  if (!profile) errors.push({ code: "DEVICE_PROFILE_MISSING", message: "找不到当前设备能力清单" });
  if (!paradigm) errors.push({ code: "PARADIGM_UNKNOWN", message: "刺激范式不存在" });
  if (!topology) errors.push({ code: "TOPOLOGY_UNKNOWN", message: "阵列拓扑不存在" });
  if (profile && paradigm) {
    const availability = getParadigmAvailability(profile, paradigm.id);
    if (!availability.isSupported) {
      errors.push({ code: "PARADIGM_UNSUPPORTED", message: availability.reason });
    }
  }
  if (profile && topology) {
    if (!profile.supportedTopologies.includes(topology.id)) {
      errors.push({ code: "TOPOLOGY_UNSUPPORTED", message: "当前设备不支持所选阵列" });
    }
    if (topology.requiredChannels > profile.stimChannelCount) {
      errors.push({ code: "CHANNELS_INSUFFICIENT", message: "设备刺激通道数量不足" });
    }
    if (topology.requiredGenerators > profile.independentGeneratorCount) {
      errors.push({ code: "GENERATORS_INSUFFICIENT", message: "设备独立波形发生器数量不足" });
    }
  }
  if (paradigm && topology && !paradigm.compatibleTopologies.includes(topology.id)) {
    errors.push({ code: "PARADIGM_TOPOLOGY_CONFLICT", message: "刺激范式与阵列拓扑不兼容" });
  }
  if (!protocol.assignments.length) {
    errors.push({ code: "MONTAGE_EMPTY", message: "尚未生成刺激电极与通道映射" });
  } else if (topology && protocol.assignments.length < topology.requiredChannels) {
    errors.push({ code: "MONTAGE_INCOMPLETE", message: "刺激电极与通道映射不完整" });
  }
  const channels = protocol.assignments.map((assignment) => assignment.channelId);
  if (new Set(channels).size !== channels.length) {
    errors.push({ code: "CHANNEL_DUPLICATED", message: "同一硬件通道被重复分配" });
  }
  if (profile && channels.some((channelId) => channelId < 1 || channelId > profile.stimChannelCount)) {
    errors.push({ code: "CHANNEL_OUT_OF_RANGE", message: "存在超出设备能力的通道" });
  }
  const positions = protocol.assignments.map((assignment) => assignment.positionId);
  if (new Set(positions).size !== positions.length) {
    errors.push({ code: "POSITION_DUPLICATED", message: "同一头模点位被重复占用" });
  }
  const groupTotals = Object.values(protocol.assignments.reduce((totals, assignment) => {
    const groupId = assignment.groupId || "A";
    totals[groupId] = (totals[groupId] || 0) + Number(assignment.currentMa || 0);
    return totals;
  }, {}));
  if (groupTotals.some((total) => Math.abs(total) > 0.001)) {
    errors.push({ code: "CURRENT_NOT_BALANCED", message: "刺激通道组电流未保持平衡" });
  }
  if (protocol.paradigmId === "TI") {
    const difference = Math.abs(
      Number(protocol.parameters.carrierFrequencyAHz)
      - Number(protocol.parameters.carrierFrequencyBHz),
    );
    if (difference === 0) {
      errors.push({ code: "TI_FREQUENCY_EQUAL", message: "TI 两组载波频率不能相同" });
    }
    if (
      Number(protocol.parameters.carrierFrequencyAHz) < 100
      || Number(protocol.parameters.carrierFrequencyAHz) > 5000
      || Number(protocol.parameters.carrierFrequencyBHz) < 100
      || Number(protocol.parameters.carrierFrequencyBHz) > 5000
    ) {
      errors.push({ code: "TI_CARRIER_OUT_OF_RANGE", message: "演示载波频率范围为 100–5000 Hz" });
    }
  }
  const durationMs = Number(protocol.parameters.durationMs);
  const rampUpMs = Number(protocol.parameters.rampUpMs || 0);
  const rampDownMs = Number(protocol.parameters.rampDownMs || 0);
  if (!Number.isFinite(durationMs) || durationMs < 10 || durationMs > 60000) {
    errors.push({ code: "DURATION_INVALID", message: "演示时长范围为 10–60000 ms" });
  }
  if (rampUpMs < 0 || rampDownMs < 0 || rampUpMs + rampDownMs > durationMs) {
    errors.push({ code: "RAMP_INVALID", message: "缓升与缓降必须非负，且总和不能超过总时长" });
  }
  const currentValues = protocol.paradigmId === "TI"
    ? [protocol.parameters.currentAMa, protocol.parameters.currentBMa]
    : [protocol.parameters.currentMa ?? protocol.parameters.amplitudeMa];
  if (currentValues.some((value) => !Number.isFinite(Number(value)) || Number(value) <= 0 || Number(value) > 2)) {
    errors.push({ code: "CURRENT_OUT_OF_DEMO_RANGE", message: "演示电流范围为 0–2 mA（不含 0）" });
  }
  if (
    protocol.paradigmId === "TACS"
    && (
      Number(protocol.parameters.frequencyHz) < 1
      || Number(protocol.parameters.frequencyHz) > 200
    )
  ) {
    errors.push({ code: "TACS_FREQUENCY_OUT_OF_RANGE", message: "演示 tACS 频率范围为 1–200 Hz" });
  }
  warnings.push({
    code: "SIMULATED_DEVICE",
    message: "当前结果来自模拟下位机，不代表真实设备或医疗安全结论",
  });
  return { isValid: errors.length === 0, errors, warnings };
}

export function compileProtocol(protocol, profile = getDeviceProfile(protocol.profileId)) {
  const validation = validateProtocol(protocol, profile);
  if (!validation.isValid) {
    return { ok: false, validation, program: null };
  }
  const durationMs = Number(protocol.parameters.durationMs);
  const rampUpMs = Number(protocol.parameters.rampUpMs || 0);
  const rampDownMs = Number(protocol.parameters.rampDownMs || 0);
  const holdMs = Math.max(0, durationMs - rampUpMs - rampDownMs);
  const generators = protocol.paradigmId === "TI"
    ? [
      {
        id: "GEN-A",
        waveform: "SINE",
        frequencyHz: Number(protocol.parameters.carrierFrequencyAHz),
        phaseDeg: Number(protocol.parameters.phaseADeg),
      },
      {
        id: "GEN-B",
        waveform: "SINE",
        frequencyHz: Number(protocol.parameters.carrierFrequencyBHz),
        phaseDeg: Number(protocol.parameters.phaseBDeg),
      },
    ]
    : [{
      id: "GEN-A",
      waveform: protocol.paradigmId === "TDCS" ? "DC" : "SINE",
      frequencyHz: protocol.paradigmId === "TACS" ? Number(protocol.parameters.frequencyHz) : null,
      phaseDeg: protocol.paradigmId === "TACS" ? Number(protocol.parameters.phaseDeg) : null,
    }];
  const protocolHash = stableHash(protocol);
  return {
    ok: true,
    validation,
    program: {
      programVersion: 1,
      simulated: true,
      profileId: profile.id,
      firmwareVersion: profile.firmwareVersion,
      protocolHash,
      generators,
      channels: protocol.assignments.map((assignment) => ({
        ...assignment,
        generatorId: protocol.paradigmId === "TI" && assignment.groupId === "B" ? "GEN-B" : "GEN-A",
      })),
      segments: [
        { type: "RAMP_UP", durationMs: rampUpMs },
        { type: "HOLD", durationMs: holdMs },
        { type: "RAMP_DOWN", durationMs: rampDownMs },
      ],
    },
  };
}

export function createDeviceFlowState() {
  return {
    connection: "DISCONNECTED",
    acquisition: "STOPPED",
    prepared: false,
    impedance: "LOCKED",
    tolerance: "NOT_RUN",
    run: "IDLE",
  };
}

export function transitionDeviceFlow(state, event) {
  switch (event.type) {
    case "CONNECT":
      return { ...createDeviceFlowState(), connection: "CONNECTED" };
    case "DISCONNECT":
      return createDeviceFlowState();
    case "PROTOCOL_CHANGED":
      return {
        ...state,
        prepared: false,
        impedance: "LOCKED",
        tolerance: "NOT_RUN",
        run: "IDLE",
      };
    case "PREPARE":
      if (state.connection !== "CONNECTED") return state;
      return { ...state, prepared: true, impedance: "LOCKED", tolerance: "READY", run: "IDLE" };
    case "TOLERANCE_PASS":
      if (!state.prepared) return state;
      return { ...state, tolerance: "PASSED", impedance: "READY" };
    case "IMPEDANCE_PASS":
      if (state.tolerance !== "PASSED") return state;
      return { ...state, impedance: "PASSED" };
    case "ARM":
      if (state.tolerance !== "PASSED" || state.impedance !== "PASSED") return state;
      return { ...state, run: "ARMED" };
    case "START":
      if (state.run !== "ARMED") return state;
      return { ...state, run: "RUNNING" };
    case "COMPLETE":
      if (state.run !== "RUNNING") return state;
      return { ...state, run: "COMPLETED" };
    case "ABORT":
      if (state.run !== "RUNNING") return state;
      return { ...state, run: "ABORTED" };
    case "ACQUISITION_START":
      if (state.connection !== "CONNECTED") return state;
      return { ...state, acquisition: "RUNNING" };
    case "ACQUISITION_STOP":
      return { ...state, acquisition: "STOPPED" };
    default:
      return state;
  }
}
