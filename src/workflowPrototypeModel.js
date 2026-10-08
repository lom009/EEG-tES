export const WORKFLOW_PAGES = [
  { id: "home", label: "首页" },
  { id: "patient", label: "患者与实验" },
  { id: "plan", label: "刺激方案" },
  { id: "electrodes", label: "电极与检测" },
  { id: "run", label: "时序与运行" },
  { id: "history", label: "历史记录" },
];

export const EEG_POINTS = [
  "FP1", "FP2", "F7", "F3", "Fz", "F4", "F8",
  "T3", "C3", "Cz", "C4", "T4",
  "T5", "P3", "Pz", "P4", "T6",
  "O1", "Oz", "O2",
];

const sharedModes = {
  dual: {
    label: "双通道",
    description: "两个逻辑刺激通道；点位角色由刺激范式决定",
  },
  hd: {
    label: "HD 4×1",
    description: "一个中心槽位和四个外围槽位",
  },
  multi: {
    label: "多靶点（暂不实现）",
    description: "开放多个独立刺激通道并校验总电流平衡",
    pending: true,
  },
};

const role = (id, label, channel, shortLabel = id, detail = "") => ({
  id,
  label,
  channel,
  shortLabel,
  detail,
});

const dualRoleSchemas = {
  tDCS: [
    role("A", "阳极 A", "STIM-1", "A", "固定直流方向"),
    role("C", "阴极 C", "STIM-2", "C", "固定直流方向"),
  ],
  tACS: [
    role("CHA", "交流通道 A", "STIM-1", "Ch A", "交流电流周期性换向"),
    role("CHB", "交流通道 B", "STIM-2", "Ch B", "交流电流周期性换向"),
  ],
  tRNS: [
    role("NOISE_A", "噪声通道 A", "STIM-1", "Noise A", "零均值随机电流"),
    role("NOISE_B", "回流通道 B", "STIM-2", "Noise B", "零均值随机电流"),
  ],
  tPCS: [
    role("PULSE_A", "脉冲通道 A", "STIM-1", "Pulse A", "角色等待下位机声明单相或双相"),
    role("PULSE_B", "脉冲通道 B", "STIM-2", "Pulse B", "角色等待下位机声明单相或双相"),
  ],
};

const hdRoleSchemas = {
  tDCS: [
    role("CENTER", "中心电极", "STIM-1", "CENTER", "中心方向由方案配置，外围自动使用相反方向"),
    role("R1", "环形回流 1", "STIM-2", "R1"),
    role("R2", "环形回流 2", "STIM-3", "R2"),
    role("R3", "环形回流 3", "STIM-4", "R3"),
    role("R4", "环形回流 4", "STIM-5", "R4"),
  ],
  tACS: [
    role("HD_CENTER", "中心交流通道", "STIM-1", "Center", "记录独立振幅与相位"),
    role("HD_CH1", "外围交流通道 1", "STIM-2", "Ch 1"),
    role("HD_CH2", "外围交流通道 2", "STIM-3", "Ch 2"),
    role("HD_CH3", "外围交流通道 3", "STIM-4", "Ch 3"),
    role("HD_CH4", "外围交流通道 4", "STIM-5", "Ch 4"),
  ],
};

const multiRoleSchema = [
  role("CH1", "刺激通道 1", "STIM-1", "Ch 1"),
  role("CH2", "刺激通道 2", "STIM-2", "Ch 2"),
  role("CH3", "刺激通道 3", "STIM-3", "Ch 3"),
  role("CH4", "刺激通道 4", "STIM-4", "Ch 4"),
];

export const SHAM_SOURCE_PRESETS = {
  "sham-dc": {
    sourceId: "SRC-TDCS-DUAL-001",
    sourceLabel: "已批准 tDCS 双通道来源方案",
    sourceParadigmId: "tDCS",
    modeId: "dual",
    assignments: { F3: "A", F4: "C" },
  },
  "sham-ac": {
    sourceId: "SRC-TACS-DUAL-001",
    sourceLabel: "已批准 tACS 双通道来源方案",
    sourceParadigmId: "tACS",
    modeId: "dual",
    assignments: { F3: "CHA", F4: "CHB" },
  },
};

const parameter = (label, unit, min, max, step, value) => ({
  label,
  unit,
  min,
  max,
  step,
  value,
  source: "模拟设备 capability ∩ 演示协议",
});

export const STIMULATION_PARADIGMS = {
  tDCS: {
    label: "tDCS",
    description: "恒定直流刺激",
    modes: ["dual", "hd", "multi"],
    parameters: {
      currentMa: parameter("目标电流", "mA", 0.1, 2, 0.1, 1.5),
      durationSec: parameter("刺激时长", "s", 1, 60, 1, 10),
      rampUpSec: parameter("缓升", "s", 0, 10, 0.5, 2),
      rampDownSec: parameter("缓降", "s", 0, 10, 0.5, 2),
    },
  },
  tACS: {
    label: "tACS",
    description: "正弦交流刺激",
    modes: ["dual", "hd", "multi"],
    parameters: {
      amplitudeMa: parameter("峰值振幅（基线→峰值）", "mA", 0.1, 2, 0.1, 1),
      frequencyHz: parameter("频率", "Hz", 1, 100, 1, 10),
      phaseDeg: parameter("相位", "°", 0, 359, 1, 0),
      durationSec: parameter("刺激时长", "s", 1, 60, 1, 10),
      rampUpSec: parameter("缓升", "s", 0, 10, 0.5, 2),
      rampDownSec: parameter("缓降", "s", 0, 10, 0.5, 2),
    },
  },
  tRNS: {
    label: "tRNS",
    description: "随机噪声刺激",
    modes: ["dual"],
    parameters: {
      amplitudeMa: parameter("噪声幅值", "mA", 0.1, 2, 0.1, 1),
      lowCutHz: parameter("低截止", "Hz", 0.1, 100, 0.1, 0.1),
      highCutHz: parameter("高截止", "Hz", 101, 640, 1, 500),
      durationSec: parameter("刺激时长", "s", 1, 60, 1, 10),
      rampUpSec: parameter("缓升", "s", 0, 10, 0.5, 2),
      rampDownSec: parameter("缓降", "s", 0, 10, 0.5, 2),
    },
  },
  tPCS: {
    label: "tPCS",
    description: "脉冲电流刺激（等待设备声明单相/双相）",
    modes: ["dual"],
    parameters: {
      pulseMa: parameter("脉冲幅值", "mA", 0.1, 2, 0.1, 1),
      frequencyHz: parameter("脉冲频率", "Hz", 1, 100, 1, 20),
      pulseWidthMs: parameter("脉宽", "ms", 0.1, 20, 0.1, 2),
      dutyPercent: parameter("占空比", "%", 1, 90, 1, 50),
      durationSec: parameter("刺激时长", "s", 1, 60, 1, 10),
    },
    pending: true,
  },
  Sham: {
    label: "Sham",
    description: "继承并锁定已批准来源方案",
    modes: ["sham-dc", "sham-ac"],
    parameters: {
      amplitudeMa: parameter("模拟幅值", "mA", 0.1, 2, 0.1, 1),
      shortStimSec: parameter("短刺激时长", "s", 1, 30, 1, 10),
      totalWaitSec: parameter("总等待时长", "s", 10, 120, 1, 60),
      rampUpSec: parameter("缓升", "s", 0, 10, 0.5, 2),
      rampDownSec: parameter("缓降", "s", 0, 10, 0.5, 2),
    },
  },
};

export const STIMULATION_MODES = {
  ...sharedModes,
  "sham-dc": {
    ...sharedModes.dual,
    label: "继承 tDCS 来源方案",
    description: "锁定来源方案的阵列、点位和角色，只改变伪刺激输出时序",
  },
  "sham-ac": {
    ...sharedModes.dual,
    label: "继承 tACS 来源方案",
    description: "锁定来源方案的阵列、点位和角色，只改变伪刺激输出时序",
  },
};

export function cloneParameterDefaults(paradigmId) {
  return Object.fromEntries(
    Object.entries(STIMULATION_PARADIGMS[paradigmId].parameters)
      .map(([key, definition]) => [key, definition.value]),
  );
}

export function getModesForParadigm(paradigmId) {
  return STIMULATION_PARADIGMS[paradigmId].modes.map((id) => ({
    id,
    ...STIMULATION_MODES[id],
  }));
}

export function getRolesForMode(paradigmOrModeId, maybeModeId) {
  const paradigmId = maybeModeId ? paradigmOrModeId : "tDCS";
  const modeId = maybeModeId || paradigmOrModeId;
  if (paradigmId === "Sham") {
    const source = SHAM_SOURCE_PRESETS[modeId];
    return getRolesForMode(source?.sourceParadigmId || "tDCS", source?.modeId || "dual");
  }
  if (modeId === "dual") return dualRoleSchemas[paradigmId] || [];
  if (modeId === "hd") return hdRoleSchemas[paradigmId] || [];
  if (modeId === "multi") return multiRoleSchema;
  return [];
}

export function getRoleDisplayLabel(paradigmId, modeId, roleId) {
  return getRolesForMode(paradigmId, modeId)
    .find((item) => item.id === roleId)?.shortLabel || roleId;
}

export function getLockedStimAssignments(paradigmId, modeId) {
  if (paradigmId !== "Sham") return null;
  return { ...(SHAM_SOURCE_PRESETS[modeId]?.assignments || {}) };
}

export function isStimAssignmentLocked(paradigmId) {
  return paradigmId === "Sham";
}

export function validateParameters(paradigmId, values) {
  const errors = [];
  const definitions = STIMULATION_PARADIGMS[paradigmId].parameters;
  Object.entries(definitions).forEach(([key, definition]) => {
    const value = Number(values[key]);
    if (!Number.isFinite(value) || value < definition.min || value > definition.max) {
      errors.push(`${definition.label}需在 ${definition.min}–${definition.max} ${definition.unit} 内`);
    }
  });
  if (
    values.durationSec !== undefined
    && Number(values.rampUpSec || 0) + Number(values.rampDownSec || 0) > Number(values.durationSec)
  ) {
    errors.push("缓升与缓降之和不能超过刺激时长");
  }
  if (
    paradigmId === "tRNS"
    && Number(values.lowCutHz) >= Number(values.highCutHz)
  ) {
    errors.push("tRNS 高截止必须大于低截止");
  }
  return errors;
}

export function validateStimAssignments(paradigmOrModeId, modeOrAssignments, maybeAssignments) {
  const hasExplicitParadigm = maybeAssignments !== undefined;
  const paradigmId = hasExplicitParadigm ? paradigmOrModeId : "tDCS";
  const modeId = hasExplicitParadigm ? modeOrAssignments : paradigmOrModeId;
  const assignments = hasExplicitParadigm ? maybeAssignments : modeOrAssignments;
  const roles = getRolesForMode(paradigmId, modeId);
  const assignedRoles = Object.values(assignments);
  const missing = roles.filter((role) => !assignedRoles.includes(role.id));
  const duplicates = assignedRoles.filter((role, index) => assignedRoles.indexOf(role) !== index);
  return {
    isValid: missing.length === 0 && duplicates.length === 0,
    missing,
    duplicates,
    assignedCount: assignedRoles.length,
    requiredCount: roles.length,
  };
}

export function validateAcquisition({
  stimulationAssignments,
  acquisitionPoints,
  referencePoint,
  groundPoint,
  sampleRate,
}) {
  const stimulationPoints = new Set(Object.keys(stimulationAssignments));
  const collisions = acquisitionPoints.filter((point) => stimulationPoints.has(point));
  const specialCollision = [referencePoint, groundPoint].filter((point) => point && stimulationPoints.has(point));
  const errors = [];
  if (acquisitionPoints.length < 2) errors.push("至少选择 2 个 EEG 采集点位");
  if (!referencePoint) errors.push("请选择参考电极");
  if (!groundPoint) errors.push("请选择地电极");
  if (referencePoint && groundPoint && referencePoint === groundPoint) errors.push("参考电极和地电极不能相同");
  if (collisions.length || specialCollision.length) errors.push("采集计划不能占用刺激点位");
  if (Number(sampleRate) !== 500) errors.push("当前模拟采集设备仅开放 500 Hz");
  return { isValid: errors.length === 0, errors };
}

export function resetPhysicalChecks() {
  return {
    stimImpedance: "pending",
    acqImpedance: "pending",
    summary: "pending",
  };
}

export function importHistoricalConfiguration(history) {
  return {
    paradigmId: history.paradigmId,
    modeId: history.modeId,
    parameters: { ...history.parameters },
    stimulationAssignments: { ...history.stimulationAssignments },
    acquisitionPoints: [...history.acquisitionPoints],
    referencePoint: history.referencePoint,
    groundPoint: history.groundPoint,
    sampleRate: history.sampleRate,
    runConfig: { ...history.runConfig },
    checks: resetPhysicalChecks(),
  };
}

export function canOpenSummary(checks) {
  return checks.stimImpedance === "pass" && checks.acqImpedance === "pass";
}
