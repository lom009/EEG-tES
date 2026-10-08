import { useEffect, useMemo, useRef, useState } from "react";
import {
  Activity,
  AlertTriangle,
  Check,
  ChevronRight,
  CircleStop,
  ClipboardCheck,
  Database,
  Download,
  FileClock,
  FlaskConical,
  History,
  Home,
  Pause,
  Play,
  RotateCcw,
  Save,
  Settings2,
  Square,
  Upload,
  UserRound,
  X,
  Zap,
} from "lucide-react";
import {
  EEG_POINTS,
  STIMULATION_MODES,
  STIMULATION_PARADIGMS,
  WORKFLOW_PAGES,
  canOpenSummary,
  cloneParameterDefaults,
  getLockedStimAssignments,
  getModesForParadigm,
  getRoleDisplayLabel,
  getRolesForMode,
  importHistoricalConfiguration,
  isStimAssignmentLocked,
  resetPhysicalChecks,
  SHAM_SOURCE_PRESETS,
  validateAcquisition,
  validateParameters,
  validateStimAssignments,
} from "../workflowPrototypeModel";
import { DEVICE_EVENTS } from "../deviceProtocolV1";
import { VirtualDeviceSession } from "../virtualDeviceSession";
import {
  createAcquisitionConfiguration,
  createStimulationChannels,
  createStimulationProgram,
} from "../workflowDeviceBridge";
import { processSignalSamples } from "../signalProcessing";
import {
  createRunBundle,
  deserializeRunBundlePortable,
  ReplayDeviceSession,
  serializeRunBundlePortable,
} from "../runBundle";
import "./WorkflowPrototype.css";

const SEEDED_HISTORY = [{
  id: "EXP-20260718-003",
  patientId: "PAT-00023",
  patientName: "演示患者 A",
  createdAt: "2026-07-18 15:20",
  status: "已完成",
  paradigmId: "tACS",
  modeId: "dual",
  parameters: {
    amplitudeMa: 1,
    frequencyHz: 10,
    phaseDeg: 0,
    durationSec: 10,
    rampUpSec: 2,
    rampDownSec: 2,
  },
  stimulationAssignments: { F3: "CHA", F4: "CHB" },
  acquisitionPoints: ["C3", "Cz", "C4"],
  referencePoint: "Pz",
  groundPoint: "Oz",
  sampleRate: 500,
  operatorName: "实验员 B",
  runConfig: {
    mode: "auto",
    acquisitionDuration: 4,
    acquisitionUnit: "s",
    stimulationDuration: 6,
    stimulationUnit: "s",
    cycles: 2,
  },
  protocolVersion: "PV-20260718-02",
  result: "完成 / 无异常",
  runs: [
    { id: "RUN-HISTORY-001", startedAt: "2026-07-18 15:20:10", finishedAt: "2026-07-18 15:20:30", status: "已完成", duration: "20.0 s", events: 0 },
    { id: "RUN-HISTORY-002", startedAt: "2026-07-18 15:24:02", finishedAt: "2026-07-18 15:24:18", status: "已中止", duration: "16.0 s", events: 1 },
  ],
  dataSummary: "C3、Cz、C4 · 500 Hz · EEG 数据包 2 份",
}];

const SEEDED_TOLERANCE_RECORDS = [
  {
    id: "TOL-20260723-001",
    patientId: "PAT-DEMO-001",
    patientName: "演示患者",
    paradigmId: "tDCS",
    threshold: 1.2,
    unit: "mA",
    testedAt: "2026-07-23 16:20",
    operatorName: "实验员 A",
    feedback: "轻微刺痛，可接受",
    note: "首次基线测试",
    status: "completed",
  },
  {
    id: "TOL-20260718-003",
    patientId: "PAT-00023",
    patientName: "演示患者 A",
    paradigmId: "tACS",
    threshold: 1,
    unit: "mA",
    testedAt: "2026-07-18 14:50",
    operatorName: "实验员 B",
    feedback: "无不适",
    note: "",
    status: "completed",
  },
];

const VIRTUAL_FAULT_SCENARIOS = {
  none: {
    label: "正常流程",
  },
  "electrode-warning": {
    label: "电极脱落（警告）",
    runtimeElectrodeFaultAtMs: 1_500,
    runtimeElectrodePositionId: "F3",
    runtimeElectrodePolicy: "WARN",
  },
  "electrode-abort": {
    label: "电极脱落（中止）",
    runtimeElectrodeFaultAtMs: 1_500,
    runtimeElectrodePositionId: "F3",
    runtimeElectrodePolicy: "ABORT",
  },
  disconnect: {
    label: "设备运行中断开",
    disconnectAtMs: 1_500,
  },
  "clock-rollback": {
    label: "设备时间戳回退",
    timestampRollbackAtPacket: 8,
    timestampRollbackUs: 500_000,
    timestampRollbackPolicy: "ABORT",
  },
};

const DEFAULT_PATIENT = {
  patientId: "PAT-DEMO-001",
  patientName: "演示患者",
  sex: "未说明",
  age: "32",
  experimentId: "EXP-FLOW-001",
  note: "流程验证，不连接真实患者。",
};

const DEFAULT_RUN_CONFIG = {
  mode: "manual",
  acquisitionDuration: 3,
  acquisitionUnit: "s",
  stimulationDuration: 4,
  stimulationUnit: "s",
  cycles: 1,
};

const DEFAULT_ACQUISITION_SETTINGS = {
  referencePoint: "Pz",
  groundPoint: "Oz",
  sampleRate: 500,
};

const DETECTION_DELAY = 900;
const RUN_TICK = 100;
const DEMO_TIME_SCALE = 0.35;

function nowLabel() {
  return new Date().toLocaleTimeString("zh-CN", { hour12: false });
}

function makeId(prefix) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}`;
}

function durationToSeconds(value, unit) {
  const number = Math.max(0.1, Number(value) || 0.1);
  return unit === "min" ? number * 60 : number;
}

function StatusPill({ status }) {
  const labels = {
    standby: "待机中",
    "waiting-stimulation": "刺激待开始",
    pending: "待执行",
    running: "进行中",
    pass: "已通过",
    failed: "未通过",
    stopped: "已停止",
    completed: "已完成",
    paused: "已暂停",
    aborted: "已中止",
  };
  return <span className={`workflow-status is-${status}`}>{labels[status] || status}</span>;
}

function PageHeader({ eyebrow, title, description }) {
  return (
    <header className="workflow-page-heading">
      <p>{eyebrow}</p>
      <h2>{title}</h2>
      {description && <span>{description}</span>}
    </header>
  );
}

function Modal({ title, children, onClose, wide = false }) {
  return (
    <div className="workflow-modal-backdrop" role="presentation">
      <section className={`workflow-modal ${wide ? "is-wide" : ""}`} role="dialog" aria-modal="true" aria-label={title}>
        <header>
          <h3>{title}</h3>
          <button type="button" onClick={onClose} aria-label="关闭"><X size={18} /></button>
        </header>
        {children}
      </section>
    </div>
  );
}

function Waveform({ offset = 0 }) {
  const points = Array.from({ length: 84 }, (_, index) => {
    const x = index * 9;
    const y = 48
      + Math.sin((index + offset) * 0.32) * 15
      + Math.sin((index + offset) * 0.78) * 6
      + Math.cos((index + offset) * 0.11) * 4;
    return `${x},${y}`;
  }).join(" ");
  return (
    <svg viewBox="0 0 750 96" preserveAspectRatio="none" aria-hidden="true">
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  );
}

function DeviceWaveform({ filterState, frame, point, offset = 0 }) {
  const processingRef = useRef({
    cache: null,
    filterKey: "",
    firstSampleIndex: null,
    nextSampleIndex: null,
    state: {},
  });
  const channel = frame?.samples?.channelValues?.find((row) => (
    (row.channel?.positionId || row.channel?.label || row.channel) === point
  ));
  if (!channel?.samplesUv?.length) return <Waveform offset={offset} />;
  const filterKey = `${filterState.high ? 1 : 0}${filterState.low ? 1 : 0}${filterState.notch ? 1 : 0}`;
  const firstSampleIndex = Number(frame.firstSampleIndex || 0);
  const isCachedFrame = (
    processingRef.current.filterKey === filterKey
    && processingRef.current.firstSampleIndex === firstSampleIndex
  );
  if (!isCachedFrame) {
    const isContinuous = (
      processingRef.current.filterKey === filterKey
      && processingRef.current.nextSampleIndex === firstSampleIndex
    );
    const processed = processSignalSamples(
      channel.samplesUv,
      frame.sampleRateHz,
      filterState,
      isContinuous ? processingRef.current.state : {},
    );
    processingRef.current = {
      cache: processed.samplesUv,
      filterKey,
      firstSampleIndex,
      nextSampleIndex: firstSampleIndex + channel.samplesUv.length,
      state: processed.state,
    };
  }
  const samples = processingRef.current.cache || channel.samplesUv;
  const width = 750;
  const points = samples.map((value, index) => {
    const x = samples.length === 1 ? 0 : (index / (samples.length - 1)) * width;
    const y = 48 - Math.max(-24, Math.min(24, Number(value))) * 1.5;
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  }).join(" ");
  return (
    <svg viewBox="0 0 750 96" preserveAspectRatio="none" aria-hidden="true">
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  );
}

function formatWaveValue(value, digits = 2) {
  return Number(value || 0).toLocaleString("zh-CN", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

function buildStimulusPreview(paradigmId, parameters) {
  const sampleCount = 180;
  const durationSec = Number(parameters.durationSec || parameters.totalWaitSec || 10);
  const rampUpSec = Number(parameters.rampUpSec || 0);
  const rampDownSec = Number(parameters.rampDownSec || 0);
  const amplitude = Number(parameters.currentMa || parameters.amplitudeMa || parameters.pulseMa || 1);
  let windowSec = durationSec;
  let currentAt = () => 0;
  let windowLabel = `完整时程 ${formatWaveValue(durationSec)} s`;
  let summary = [];
  let envelopePoints = [];

  if (paradigmId === "tDCS") {
    const plateauEnd = Math.max(rampUpSec, durationSec - rampDownSec);
    currentAt = (time) => {
      if (rampUpSec > 0 && time < rampUpSec) return amplitude * (time / rampUpSec);
      if (rampDownSec > 0 && time > plateauEnd) return amplitude * ((durationSec - time) / rampDownSec);
      return amplitude;
    };
    summary = [
      `目标 ${formatWaveValue(amplitude)} mA`,
      `总时长 ${formatWaveValue(durationSec)} s`,
      `缓升 ${formatWaveValue(rampUpSec)} s`,
      `缓降 ${formatWaveValue(rampDownSec)} s`,
    ];
  } else if (paradigmId === "tACS") {
    const frequency = Number(parameters.frequencyHz || 10);
    const phase = Number(parameters.phaseDeg || 0);
    windowSec = Math.min(durationSec, Math.max(0.05, Math.min(1, 5 / frequency)));
    windowLabel = `代表窗口 ${formatWaveValue(windowSec * 1000, 0)} ms / 总时长 ${formatWaveValue(durationSec)} s`;
    currentAt = (time) => amplitude * Math.sin((Math.PI * 2 * frequency * time) + (phase * Math.PI / 180));
    summary = [
      `峰值 ±${formatWaveValue(amplitude)} mA`,
      `峰峰值 ${formatWaveValue(amplitude * 2)} mA p-p`,
      `频率 ${formatWaveValue(frequency)} Hz`,
      `相位 ${formatWaveValue(phase)}°`,
      `总时长 ${formatWaveValue(durationSec)} s`,
    ];
    envelopePoints = Array.from({ length: sampleCount + 1 }, (_, index) => {
      const ratio = index / sampleCount;
      const time = ratio * durationSec;
      const rampUpFactor = rampUpSec > 0 ? Math.min(1, time / rampUpSec) : 1;
      const rampDownFactor = rampDownSec > 0
        ? Math.min(1, Math.max(0, (durationSec - time) / rampDownSec))
        : 1;
      return {
        ratio,
        current: amplitude * Math.min(rampUpFactor, rampDownFactor),
      };
    });
  } else if (paradigmId === "tRNS") {
    const lowCut = Number(parameters.lowCutHz || 0.1);
    const highCut = Number(parameters.highCutHz || 500);
    windowSec = Math.min(durationSec, 1);
    windowLabel = `代表窗口 ${formatWaveValue(windowSec, 1)} s / 总时长 ${formatWaveValue(durationSec)} s`;
    currentAt = (time) => amplitude * (
      Math.sin(time * 91.3)
      + Math.sin(time * 227.1 + 0.8)
      + Math.sin(time * 413.7 + 1.9)
      + Math.sin(time * 701.9 + 0.4)
    ) / 3.2;
    summary = [
      `幅值 ${formatWaveValue(amplitude)} mA`,
      `频带 ${formatWaveValue(lowCut)}–${formatWaveValue(highCut)} Hz`,
      `总时长 ${formatWaveValue(durationSec)} s`,
      "确定性示意噪声",
    ];
  } else if (paradigmId === "tPCS") {
    const frequency = Number(parameters.frequencyHz || 20);
    const pulseWidthMs = Number(parameters.pulseWidthMs || 2);
    const periodSec = 1 / frequency;
    windowSec = Math.min(durationSec, periodSec * 5);
    windowLabel = `代表窗口 ${formatWaveValue(windowSec * 1000, 0)} ms / 总时长 ${formatWaveValue(durationSec)} s`;
    currentAt = (time) => ((time % periodSec) * 1000 < pulseWidthMs ? amplitude : 0);
    summary = [
      `脉冲 ${formatWaveValue(amplitude)} mA`,
      `频率 ${formatWaveValue(frequency)} Hz`,
      `脉宽 ${formatWaveValue(pulseWidthMs)} ms`,
      `占空比 ${formatWaveValue(parameters.dutyPercent)}%`,
    ];
  } else if (paradigmId === "Sham") {
    const shortStimSec = Number(parameters.shortStimSec || 10);
    windowSec = Number(parameters.totalWaitSec || 60);
    windowLabel = `完整模拟时程 ${formatWaveValue(windowSec)} s`;
    const plateauEnd = Math.max(rampUpSec, shortStimSec - rampDownSec);
    currentAt = (time) => {
      if (time > shortStimSec) return 0;
      if (rampUpSec > 0 && time < rampUpSec) return amplitude * (time / rampUpSec);
      if (rampDownSec > 0 && time > plateauEnd) return amplitude * ((shortStimSec - time) / rampDownSec);
      return amplitude;
    };
    summary = [
      `模拟幅值 ${formatWaveValue(amplitude)} mA`,
      `短刺激 ${formatWaveValue(shortStimSec)} s`,
      `总等待 ${formatWaveValue(windowSec)} s`,
      `缓升/缓降 ${formatWaveValue(rampUpSec)}/${formatWaveValue(rampDownSec)} s`,
    ];
  }

  const points = Array.from({ length: sampleCount + 1 }, (_, index) => {
    const ratio = index / sampleCount;
    const time = ratio * windowSec;
    const current = Math.max(-amplitude, Math.min(amplitude, currentAt(time)));
    return { ratio, current };
  });

  return {
    amplitude: Math.max(amplitude, 0.1),
    durationSec,
    envelopePoints,
    rampDownSec,
    rampUpSec,
    signedAxis: paradigmId === "tACS" || paradigmId === "tRNS",
    points,
    summary,
    windowLabel,
    windowSec,
    xUnit: windowSec < 1 ? "ms" : "s",
    xMax: windowSec < 1 ? windowSec * 1000 : windowSec,
  };
}

function StimulusWaveformPreview({ paradigmId, modeId, parameters }) {
  const preview = buildStimulusPreview(paradigmId, parameters);
  const plot = { left: 58, right: 706, top: 18, bottom: 174 };
  const plotWidth = plot.right - plot.left;
  const plotHeight = plot.bottom - plot.top;
  const zeroY = preview.signedAxis ? plot.top + plotHeight / 2 : plot.bottom;
  const points = preview.points.map(({ ratio, current }) => {
    const x = plot.left + ratio * plotWidth;
    const y = preview.signedAxis
      ? zeroY - (current / preview.amplitude) * (plotHeight * 0.42)
      : plot.bottom - (Math.max(0, current) / preview.amplitude) * (plotHeight * 0.84);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  const xTicks = [0, 0.25, 0.5, 0.75, 1];
  const yTicks = preview.signedAxis
    ? [
      { ratio: 1, label: `+${formatWaveValue(preview.amplitude)} mA` },
      { ratio: 0, label: "0" },
      { ratio: -1, label: `−${formatWaveValue(preview.amplitude)} mA` },
    ]
    : [
      { ratio: 1, label: `${formatWaveValue(preview.amplitude)} mA` },
      { ratio: 0.5, label: `${formatWaveValue(preview.amplitude / 2)} mA` },
      { ratio: 0, label: "0" },
    ];
  const envelopePlot = { left: 58, right: 706, top: 12, bottom: 76 };
  const envelopeWidth = envelopePlot.right - envelopePlot.left;
  const envelopeHeight = envelopePlot.bottom - envelopePlot.top;
  const envelopePolyline = preview.envelopePoints.map(({ ratio, current }) => {
    const x = envelopePlot.left + ratio * envelopeWidth;
    const y = envelopePlot.bottom - (current / preview.amplitude) * (envelopeHeight * 0.82);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  const envelopeTicks = [
    { time: 0, label: "0" },
    ...(preview.rampUpSec > 0 ? [{ time: Math.min(preview.rampUpSec, preview.durationSec), label: `缓升 ${formatWaveValue(preview.rampUpSec)} s` }] : []),
    ...(preview.rampDownSec > 0 ? [{
      time: Math.max(0, preview.durationSec - preview.rampDownSec),
      label: `缓降开始 ${formatWaveValue(Math.max(0, preview.durationSec - preview.rampDownSec))} s`,
    }] : []),
    { time: preview.durationSec, label: `${formatWaveValue(preview.durationSec)} s` },
  ].filter((tick, index, ticks) => ticks.findIndex((item) => item.time === tick.time) === index);

  return (
    <section className="workflow-wave-preview" aria-label={`${paradigmId} 计划波形预览`}>
      <header>
        <div>
          <h4>方案波形预览</h4>
          <p>{preview.windowLabel}</p>
        </div>
        <span>参数示意图 · 根据当前配置实时生成</span>
      </header>
      <div className="workflow-wave-layout">
        <svg viewBox="0 0 740 218" role="img" aria-label={`横轴为时间，纵轴为电流；${preview.windowLabel}`}>
          <defs>
            <linearGradient id={`workflow-wave-fill-${paradigmId}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#4351c6" stopOpacity=".22" />
              <stop offset="100%" stopColor="#4351c6" stopOpacity="0" />
            </linearGradient>
          </defs>
          {xTicks.map((tick) => {
            const x = plot.left + tick * plotWidth;
            return (
              <g key={`x-${tick}`}>
                <line x1={x} y1={plot.top} x2={x} y2={plot.bottom} className="workflow-wave-grid" />
                <text x={x} y="198" textAnchor="middle">{formatWaveValue(preview.xMax * tick, preview.xUnit === "ms" ? 0 : 2)}</text>
              </g>
            );
          })}
          {yTicks.map((tick) => {
            const y = preview.signedAxis
              ? zeroY - tick.ratio * (plotHeight * 0.42)
              : plot.bottom - tick.ratio * (plotHeight * 0.84);
            return (
              <g key={tick.label}>
                <line x1={plot.left} y1={y} x2={plot.right} y2={y} className={tick.ratio === 0 ? "workflow-wave-zero" : "workflow-wave-grid"} />
                <text x="50" y={y + 4} textAnchor="end">{tick.label}</text>
              </g>
            );
          })}
          <polyline points={points} className="workflow-wave-line" />
          <text x={(plot.left + plot.right) / 2} y="214" textAnchor="middle">时间（{preview.xUnit}）</text>
          <text transform="translate(12 104) rotate(-90)" textAnchor="middle">
            {preview.signedAxis ? "电流（mA）" : "电流幅值（mA）"}
          </text>
        </svg>
        <aside>
          <strong>关键参数</strong>
          <div>
            {preview.summary.map((item) => <span key={item}>{item}</span>)}
          </div>
          <p>
            {modeId === "hd"
              ? "当前显示单通道参数示意；HD 各通道电流分配与总电流平衡在点位映射中确认。"
              : paradigmId === "tDCS"
                ? "目标电流是非负幅值；阳极 A 与阴极 C 决定电流方向，并在点位配置中单独设置。"
                : "此图仅用于解释参数关系，在浏览器内计算生成；修改参数后立即变化，不连接或控制下位机。"}
          </p>
        </aside>
      </div>
      {paradigmId === "tACS" && (
        <section className="workflow-wave-envelope" aria-label="tACS 完整时程包络">
          <header>
            <div>
              <strong>完整时程包络</strong>
              <span>显示峰值振幅随缓升、稳定刺激和缓降阶段的变化</span>
            </div>
            <span>{formatWaveValue(preview.rampUpSec)} s 缓升 · {formatWaveValue(preview.rampDownSec)} s 缓降</span>
          </header>
          <svg viewBox="0 0 740 106" role="img" aria-label={`tACS 完整 ${formatWaveValue(preview.durationSec)} 秒峰值振幅包络`}>
            <line x1={envelopePlot.left} y1={envelopePlot.bottom} x2={envelopePlot.right} y2={envelopePlot.bottom} className="workflow-wave-zero" />
            <line x1={envelopePlot.left} y1={envelopePlot.top + envelopeHeight * 0.18} x2={envelopePlot.right} y2={envelopePlot.top + envelopeHeight * 0.18} className="workflow-wave-grid" />
            <text x="50" y={envelopePlot.top + envelopeHeight * 0.18 + 4} textAnchor="end">{formatWaveValue(preview.amplitude)} mA</text>
            <text x="50" y={envelopePlot.bottom + 4} textAnchor="end">0</text>
            {envelopeTicks.map((tick) => {
              const x = envelopePlot.left + (tick.time / preview.durationSec) * envelopeWidth;
              return (
                <g key={`${tick.time}-${tick.label}`}>
                  <line x1={x} y1={envelopePlot.top} x2={x} y2={envelopePlot.bottom} className="workflow-wave-grid" />
                  <text x={x} y="98" textAnchor={tick.time === 0 ? "start" : tick.time === preview.durationSec ? "end" : "middle"}>{tick.label}</text>
                </g>
              );
            })}
            <polyline points={envelopePolyline} className="workflow-wave-envelope-line" />
          </svg>
          <p>代表窗口展示正弦电流的瞬时正负变化；完整包络展示峰值振幅如何从 0 缓升、保持并缓降至 0。</p>
        </section>
      )}
    </section>
  );
}

function Field({ label, children, hint, defaultValue }) {
  return (
    <label className="workflow-field">
      <span>
        {label}
        {defaultValue && <em>默认：{defaultValue}</em>}
      </span>
      {children}
      {hint && <small>{hint}</small>}
    </label>
  );
}

export function WorkflowPrototype({ onHome }) {
  const [pageId, setPageId] = useState("home");
  const [unlocked, setUnlocked] = useState(["home", "patient", "history"]);
  const [notice, setNotice] = useState("");
  const [deviceConnected, setDeviceConnected] = useState(false);
  const [devicePort, setDevicePort] = useState("COM-DEMO");
  const [patient, setPatient] = useState(DEFAULT_PATIENT);
  const [paradigmId, setParadigmId] = useState("tDCS");
  const [modeId, setModeId] = useState("dual");
  const [parameters, setParameters] = useState(() => cloneParameterDefaults("tDCS"));
  const [hdCenterPolarity, setHdCenterPolarity] = useState("A");
  const [activeElectrodeTab, setActiveElectrodeTab] = useState("stim");
  const [activeRoleId, setActiveRoleId] = useState("A");
  const [stimulationAssignments, setStimulationAssignments] = useState({});
  const [acquisitionPoints, setAcquisitionPoints] = useState([]);
  const [referencePoint, setReferencePoint] = useState(DEFAULT_ACQUISITION_SETTINGS.referencePoint);
  const [groundPoint, setGroundPoint] = useState(DEFAULT_ACQUISITION_SETTINGS.groundPoint);
  const [sampleRate, setSampleRate] = useState(DEFAULT_ACQUISITION_SETTINGS.sampleRate);
  const [checks, setChecks] = useState(resetPhysicalChecks);
  const [detectionTarget, setDetectionTarget] = useState("");
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [toleranceTab, setToleranceTab] = useState("test");
  const [toleranceRecords, setToleranceRecords] = useState(SEEDED_TOLERANCE_RECORDS);
  const [toleranceDraft, setToleranceDraft] = useState({
    patientId: DEFAULT_PATIENT.patientId,
    patientName: DEFAULT_PATIENT.patientName,
    paradigmId: "tDCS",
    threshold: 1,
    unit: "mA",
    operatorName: "实验员 A",
    feedback: "无不适",
    note: "",
  });
  const [runConfig, setRunConfig] = useState(DEFAULT_RUN_CONFIG);
  const [filterState, setFilterState] = useState({ high: true, low: true, notch: true });
  const [runStatus, setRunStatus] = useState("standby");
  const [runPhase, setRunPhase] = useState("standby");
  const [runId, setRunId] = useState("");
  const [runElapsedMs, setRunElapsedMs] = useState(0);
  const [phaseElapsedMs, setPhaseElapsedMs] = useState(0);
  const [cycle, setCycle] = useState(1);
  const [events, setEvents] = useState([]);
  const [result, setResult] = useState(null);
  const [runResults, setRunResults] = useState([]);
  const [runStartedAt, setRunStartedAt] = useState("");
  const [historyRows, setHistoryRows] = useState(SEEDED_HISTORY);
  const [expandedHistoryId, setExpandedHistoryId] = useState("");
  const [deviceProtocolEvents, setDeviceProtocolEvents] = useState([]);
  const [latestEegFrame, setLatestEegFrame] = useState(null);
  const [replayBundleName, setReplayBundleName] = useState("");
  const [replayError, setReplayError] = useState("");
  const [replaySnapshot, setReplaySnapshot] = useState(null);
  const deviceSessionRef = useRef(null);
  const replaySessionRef = useRef(null);
  const runTimerRef = useRef(null);
  const phaseElapsedRef = useRef(0);
  const phaseTransitionRef = useRef(false);
  const stimulationCompletedHandlerRef = useRef(null);
  const deviceFaultHandlerRef = useRef(null);
  const faultScenarioId = useMemo(() => {
    const requested = new URLSearchParams(window.location.search).get("fault") || "none";
    return VIRTUAL_FAULT_SCENARIOS[requested] ? requested : "none";
  }, []);
  const faultScenario = VIRTUAL_FAULT_SCENARIOS[faultScenarioId];

  const paradigm = STIMULATION_PARADIGMS[paradigmId];
  const modes = getModesForParadigm(paradigmId);
  const roles = getRolesForMode(paradigmId, modeId);
  const parameterErrors = validateParameters(paradigmId, parameters);
  const stimulationValidation = validateStimAssignments(paradigmId, modeId, stimulationAssignments);
  const stimulationAssignmentLocked = isStimAssignmentLocked(paradigmId);
  const acquisitionValidation = validateAcquisition({
    stimulationAssignments,
    acquisitionPoints,
    referencePoint,
    groundPoint,
    sampleRate,
  });
  const stimPoints = useMemo(() => new Set(Object.keys(stimulationAssignments)), [stimulationAssignments]);
  const patientToleranceRecord = useMemo(
    () => toleranceRecords.find((record) => (
      record.patientId === patient.patientId
      && record.paradigmId === paradigmId
      && record.status === "completed"
    )),
    [toleranceRecords, patient.patientId, paradigmId],
  );
  const runIsActive = runStatus === "running";
  const currentPageIndex = WORKFLOW_PAGES.findIndex((page) => page.id === pageId);

  useEffect(() => {
    const session = new VirtualDeviceSession({
      scenarioId: "workflow-happy-path",
      seed: patient.patientId,
      clockMode: "realtime",
      timeScale: DEMO_TIME_SCALE,
      impedanceDurationMs: DETECTION_DELAY / DEMO_TIME_SCALE,
      packetLossEveryN: 30,
      packetLatencyMs: 24,
      clockDriftPpm: 18,
      ...faultScenario,
    });
    deviceSessionRef.current = session;
    const unsubscribe = session.subscribe((event) => {
      setDeviceProtocolEvents((rows) => [event, ...rows].slice(0, 80));
      if (event.messageType === DEVICE_EVENTS.CONNECTION_CHANGED) {
        const snapshot = session.getSnapshot();
        const connected = (
          snapshot.acquisition.connectionState === "CONNECTED"
          && snapshot.stimulation.connectionState === "CONNECTED"
        );
        setDeviceConnected(connected);
      }
      if (event.messageType === DEVICE_EVENTS.ACQUISITION_IMPEDANCE_STARTED) {
        setChecks((current) => ({ ...current, acqImpedance: "running" }));
        setDetectionTarget("acq");
        addEvent("采集阻抗检测开始");
      }
      if (event.messageType === DEVICE_EVENTS.ACQUISITION_IMPEDANCE_COMPLETED) {
        const passed = event.payload.status === "PASSED";
        const failedChannels = event.payload.channels
          .filter((channel) => channel.quality === "BAD")
          .map((channel) => `${channel.positionId} ${channel.valueKohm} kΩ`);
        setChecks((current) => ({ ...current, acqImpedance: passed ? "pass" : "failed" }));
        setDetectionTarget("");
        addEvent(`采集阻抗检测${passed ? "通过" : `未通过：${failedChannels.join("、")}`}`);
        if (!passed) setNotice(`采集阻抗过高：${failedChannels.join("、")}，请检查接触后重新检测。`);
      }
      if (event.messageType === DEVICE_EVENTS.IMPEDANCE_STARTED) {
        setChecks((current) => ({ ...current, stimImpedance: "running" }));
        setDetectionTarget("stim");
        addEvent("刺激阻抗检测开始");
      }
      if (event.messageType === DEVICE_EVENTS.IMPEDANCE_COMPLETED) {
        const passed = event.payload.status === "PASSED";
        const failedChannels = event.payload.channels
          .filter((channel) => channel.quality === "BAD")
          .map((channel) => `${channel.positionId || channel.channelId} ${channel.valueKohm} kΩ`);
        setChecks((current) => ({ ...current, stimImpedance: passed ? "pass" : "failed" }));
        setDetectionTarget("");
        addEvent(`刺激阻抗检测${passed ? "通过" : `未通过：${failedChannels.join("、")}`}`);
        if (!passed) setNotice(`刺激阻抗过高：${failedChannels.join("、")}，不允许进入实验。`);
      }
      if (event.messageType === DEVICE_EVENTS.EEG_FRAME) {
        setLatestEegFrame(event.payload);
      }
      if (event.messageType === DEVICE_EVENTS.ACQUISITION_PACKET_DROPPED) {
        addEvent(
          `EEG 数据包缺失：序号 ${event.payload.packetSequence}，缺失 ${event.payload.sampleCount} 个采样点`,
        );
      }
      if (event.messageType === DEVICE_EVENTS.ACQUISITION_FRAME_REJECTED) {
        addEvent(
          `EEG 异常帧已拒绝：序号 ${event.payload.packetSequence}，原因 ${event.payload.reason}`,
        );
      }
      if (event.messageType === DEVICE_EVENTS.ACQUISITION_CONTACT_CHANGED) {
        addEvent(
          `运行中电极异常：${event.payload.positionId} ${event.payload.state}，策略 ${event.payload.action}`,
        );
      }
      if (event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED) {
        stimulationCompletedHandlerRef.current?.(event);
      }
      if (event.messageType === DEVICE_EVENTS.FAULT) {
        setNotice(event.payload.message || "虚拟设备报告故障。");
        addEvent(`设备故障：${event.payload.code}`);
        deviceFaultHandlerRef.current?.(event);
      }
    });
    return () => {
      unsubscribe();
      session.destroy();
      replaySessionRef.current?.destroy();
      replaySessionRef.current = null;
      deviceSessionRef.current = null;
      window.clearInterval(runTimerRef.current);
    };
  // The virtual device session intentionally lives for the prototype lifetime.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!runIsActive) return undefined;
    const durationSec = runPhase === "acquisition"
      ? durationToSeconds(runConfig.acquisitionDuration, runConfig.acquisitionUnit)
      : durationToSeconds(runConfig.stimulationDuration, runConfig.stimulationUnit);
    const targetMs = Math.max(300, durationSec * 1000);
    const virtualTickMs = RUN_TICK / DEMO_TIME_SCALE;
    runTimerRef.current = window.setInterval(() => {
      setRunElapsedMs((value) => value + virtualTickMs);
      phaseElapsedRef.current = Math.min(targetMs, phaseElapsedRef.current + virtualTickMs);
      setPhaseElapsedMs(phaseElapsedRef.current);
      if (
        runPhase === "acquisition"
        && phaseElapsedRef.current >= targetMs
        && !phaseTransitionRef.current
      ) {
        phaseTransitionRef.current = true;
        window.clearInterval(runTimerRef.current);
        window.setTimeout(() => finishCurrentPhase(), 0);
      }
    }, RUN_TICK);
    return () => window.clearInterval(runTimerRef.current);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runStatus, runPhase, cycle]);

  function addEvent(message) {
    setEvents((rows) => [{ time: nowLabel(), message }, ...rows].slice(0, 30));
  }

  function unlock(nextPage) {
    setUnlocked((pages) => pages.includes(nextPage) ? pages : [...pages, nextPage]);
    setPageId(nextPage);
    setNotice("");
  }

  function goPage(nextPage) {
    if (unlocked.includes(nextPage)) {
      setPageId(nextPage);
      setNotice("");
    }
  }

  function resetCheckResults(reason) {
    deviceSessionRef.current?.acquisition.stopImpedanceCheck();
    deviceSessionRef.current?.stimulation.stopImpedanceCheck();
    setChecks(resetPhysicalChecks());
    setDetectionTarget("");
    setResult(null);
    setRunResults([]);
    setRunStartedAt("");
    setRunStatus("standby");
    setRunPhase("standby");
    if (reason) setNotice(`${reason}；物理检测结果已清空，需要重新执行。`);
  }

  async function connectDevice() {
    const session = deviceSessionRef.current;
    if (!session) return;
    try {
      if (deviceConnected) {
        session.disconnect();
        addEvent("模拟下位机已断开");
      } else {
        await session.connect();
        addEvent(`模拟下位机已连接：${devicePort}`);
      }
      setNotice("");
    } catch (error) {
      setNotice(`设备连接失败：${error.message}`);
    }
  }

  function updateToleranceDraft(key, value) {
    setToleranceDraft((current) => ({ ...current, [key]: value }));
  }

  function adjustToleranceDraft(direction) {
    const current = Number(toleranceDraft.threshold) || 0;
    const next = Math.max(0, Math.min(2, current + direction * 0.02));
    updateToleranceDraft("threshold", Number(next.toFixed(2)));
  }

  function saveToleranceRecord() {
    if (!deviceConnected) {
      setNotice("耐受度测试需要先连接模拟下位机。");
      return;
    }
    if (!toleranceDraft.patientId.trim() || !toleranceDraft.patientName.trim() || !toleranceDraft.operatorName.trim()) {
      setNotice("患者 ID、患者姓名和测试人不能为空。");
      return;
    }
    if (Number(toleranceDraft.threshold) <= 0) {
      setNotice("请完成逐步测试并记录大于 0 的耐受阈值。");
      return;
    }
    const record = {
      ...toleranceDraft,
      id: makeId("TOL"),
      threshold: Number(toleranceDraft.threshold),
      testedAt: new Date().toLocaleString("zh-CN", { hour12: false }),
      status: "completed",
    };
    setToleranceRecords((rows) => [record, ...rows]);
    setToleranceTab("history");
    setNotice(`已保存 ${record.patientId} 的耐受度记录；有效期规则暂不校验。`);
  }

  function createExperiment() {
    setPatient(DEFAULT_PATIENT);
    unlock("patient");
  }

  function updatePatient(key, value) {
    setPatient((current) => ({ ...current, [key]: value }));
  }

  function continueFromPatient() {
    if (!patient.patientId.trim() || !patient.patientName.trim() || !patient.experimentId.trim()) {
      setNotice("患者 ID、姓名和实验 ID 均为必填项。");
      return;
    }
    const hasToleranceRecord = toleranceRecords.some((record) => (
      record.patientId === patient.patientId && record.status === "completed"
    ));
    if (!hasToleranceRecord) {
      setToleranceDraft((current) => ({
        ...current,
        patientId: patient.patientId,
        patientName: patient.patientName,
      }));
      setToleranceTab("test");
      setPageId("home");
      setNotice("该患者尚无耐受度记录，请先在首页完成患者级耐受度测试。");
      return;
    }
    unlock("plan");
  }

  function updateParadigm(nextId) {
    if (STIMULATION_PARADIGMS[nextId].pending) {
      setNotice(`${STIMULATION_PARADIGMS[nextId].label} 的具体配置暂未实现，待补充规则后开放。`);
      return;
    }
    const nextMode = STIMULATION_PARADIGMS[nextId].modes[0];
    const nextAssignments = getLockedStimAssignments(nextId, nextMode) || {};
    setParadigmId(nextId);
    setModeId(nextMode);
    setParameters(cloneParameterDefaults(nextId));
    setHdCenterPolarity("A");
    setStimulationAssignments(nextAssignments);
    setActiveRoleId(getRolesForMode(nextId, nextMode)[0]?.id || "");
    resetCheckResults("刺激范式已修改");
  }

  function updateMode(nextId) {
    if (STIMULATION_MODES[nextId]?.pending) {
      setNotice(`${STIMULATION_MODES[nextId].label}，当前原型暂不开放。`);
      return;
    }
    const nextAssignments = getLockedStimAssignments(paradigmId, nextId) || {};
    setModeId(nextId);
    setHdCenterPolarity("A");
    setStimulationAssignments(nextAssignments);
    setActiveRoleId(getRolesForMode(paradigmId, nextId)[0]?.id || "");
    resetCheckResults("刺激阵列已修改");
  }

  function continueFromPlan() {
    if (parameterErrors.length) {
      setNotice(parameterErrors[0]);
      return;
    }
    if (!patientToleranceRecord) {
      setToleranceDraft((current) => ({
        ...current,
        patientId: patient.patientId,
        patientName: patient.patientName,
        paradigmId,
      }));
      setToleranceTab("test");
      setPageId("home");
      setNotice(`当前患者缺少 ${paradigmId} 对应的耐受度记录，请先完成测试。`);
      return;
    }
    unlock("electrodes");
  }

  function assignStimPoint(pointId) {
    if (stimulationAssignmentLocked) {
      setNotice("Sham 的阵列、点位和角色继承已批准来源方案，当前页面只读。");
      return;
    }
    if (acquisitionPoints.includes(pointId) || pointId === referencePoint || pointId === groundPoint) {
      setNotice(`${pointId} 已被采集方案占用，不能静默覆盖。`);
      return;
    }
    setStimulationAssignments((current) => {
      const next = { ...current };
      Object.entries(next).forEach(([point, role]) => {
        if (role === activeRoleId || point === pointId) delete next[point];
      });
      if (current[pointId] !== activeRoleId) next[pointId] = activeRoleId;
      return next;
    });
    resetCheckResults(`刺激点位 ${pointId} 已修改`);
  }

  function toggleAcquisition(pointId) {
    if (stimPoints.has(pointId)) {
      setNotice(`${pointId} 已承担刺激角色，不能同时作为 EEG 采集点。`);
      return;
    }
    if (pointId === referencePoint || pointId === groundPoint) {
      setNotice(`${pointId} 已作为参考或地电极，请先更换特殊电极。`);
      return;
    }
    setAcquisitionPoints((current) => (
      current.includes(pointId)
        ? current.filter((point) => point !== pointId)
        : [...current, pointId]
    ));
    resetCheckResults(`采集点位 ${pointId} 已修改`);
  }

  function startDetection(target) {
    if (!deviceConnected) {
      setNotice("阻抗检测需要先连接模拟下位机。");
      return;
    }
    if (target === "stim" && !stimulationValidation.isValid) {
      setNotice(`刺激阵列尚未完整：已配置 ${stimulationValidation.assignedCount}/${stimulationValidation.requiredCount}。`);
      return;
    }
    if (target === "acq" && !acquisitionValidation.isValid) {
      setNotice(acquisitionValidation.errors[0]);
      return;
    }
    const session = deviceSessionRef.current;
    if (!session) {
      setNotice("虚拟设备会话尚未初始化。");
      return;
    }
    try {
      if (target === "acq") {
        const configuration = createAcquisitionConfiguration({
          acquisitionPoints,
          referencePoint,
          groundPoint,
          sampleRate,
        });
        session.acquisition.configureAcquisition(configuration);
        session.acquisition.startImpedanceCheck({ channels: configuration.channels });
      } else {
        const channels = createStimulationChannels({
          stimulationAssignments,
          roles,
        });
        const program = createStimulationProgram({
          paradigmId,
          modeId,
          parameters: paradigmId === "tDCS" && modeId === "hd"
            ? { ...parameters, centerPolarity: hdCenterPolarity }
            : parameters,
          stimulationAssignments,
          roles,
        });
        session.stimulation.prepareProgram(program);
        session.stimulation.startImpedanceCheck({ channels });
      }
      setNotice("");
    } catch (error) {
      const key = target === "stim" ? "stimImpedance" : "acqImpedance";
      setChecks((current) => ({ ...current, [key]: "failed" }));
      setDetectionTarget("");
      setNotice(`阻抗检测未启动：${error.message}`);
    }
  }

  function stopDetection() {
    if (!detectionTarget) return;
    if (detectionTarget === "stim") {
      deviceSessionRef.current?.stimulation.stopImpedanceCheck();
    } else {
      deviceSessionRef.current?.acquisition.stopImpedanceCheck();
    }
    const key = detectionTarget === "stim" ? "stimImpedance" : "acqImpedance";
    setChecks((current) => ({ ...current, [key]: "stopped" }));
    addEvent(`${detectionTarget === "stim" ? "刺激" : "采集"}阻抗检测被手动停止`);
    setDetectionTarget("");
  }

  function openSummary() {
    if (!canOpenSummary(checks)) {
      setNotice("刺激与采集阻抗均通过后才可进入实验配置确认。");
      return;
    }
    if (!patientToleranceRecord) {
      setNotice("当前患者缺少与刺激范式匹配的耐受度记录。");
      return;
    }
    setSummaryOpen(true);
  }

  function confirmSummary() {
    setChecks((current) => ({ ...current, summary: "pass" }));
    addEvent("患者、方案、电极和采集计划已二次确认");
    setSummaryOpen(false);
    unlock("run");
  }

  function startPhase(phase) {
    if (!deviceConnected) {
      setNotice("实验运行需要连接模拟下位机。");
      return;
    }
    if (checks.summary !== "pass") {
      setNotice("当前实验配置尚未完成二次确认。");
      return;
    }
    const nextRunId = (!runId || runStatus === "aborted" || runStatus === "completed")
      ? makeId("RUN")
      : runId;
    try {
      if (phase === "acquisition") {
        deviceSessionRef.current?.acquisition.startAcquisition({ runId: nextRunId });
      } else {
        startDeviceStimulation(nextRunId);
      }
    } catch (error) {
      setNotice(`设备拒绝启动：${error.message}`);
      return;
    }
    if (nextRunId !== runId) {
      setRunId(nextRunId);
      setRunStartedAt(new Date().toLocaleString("zh-CN", { hour12: false }));
      setRunElapsedMs(0);
      setCycle(1);
      setResult(null);
    }
    setRunPhase(phase);
    phaseElapsedRef.current = 0;
    phaseTransitionRef.current = false;
    setPhaseElapsedMs(0);
    setRunStatus("running");
    addEvent(`${phase === "acquisition" ? "采集期" : "刺激期"}开始`);
  }

  function startDeviceStimulation(activeRunId) {
    const session = deviceSessionRef.current;
    if (!session) throw new Error("VIRTUAL_DEVICE_SESSION_MISSING");
    const durationMs = Math.round(
      durationToSeconds(runConfig.stimulationDuration, runConfig.stimulationUnit) * 1000,
    );
    const program = createStimulationProgram({
      paradigmId,
      modeId,
      parameters: paradigmId === "tDCS" && modeId === "hd"
        ? { ...parameters, centerPolarity: hdCenterPolarity }
        : parameters,
      stimulationAssignments,
      roles,
      durationOverrideMs: durationMs,
    });
    session.stimulation.prepareProgram(program);
    session.stimulation.arm({
      toleranceRecord: {
        recordId: patientToleranceRecord?.id,
        status: "COMPLETED",
        paradigmId: paradigmId.toUpperCase(),
      },
      stimulationImpedance: session.stimulation.impedanceResult,
      acquisitionImpedance: session.acquisition.impedanceResult,
    });
    session.stimulation.startRun(activeRunId);
  }

  function changeRunMode(mode) {
    if (runIsActive) return;
    setRunConfig((current) => ({
      ...current,
      mode,
      cycles: mode === "manual" ? 1 : Math.max(1, current.cycles),
    }));
    if (runStatus === "completed" || runStatus === "aborted") {
      setRunStatus("standby");
      setRunPhase("standby");
      setRunId("");
      setRunElapsedMs(0);
      phaseElapsedRef.current = 0;
      phaseTransitionRef.current = false;
      setPhaseElapsedMs(0);
      setCycle(1);
      setResult(null);
      addEvent(`已切换为${mode === "manual" ? "手动" : "自动"}模式，等待新一轮实验启动`);
    }
  }

  function changeRunUnit(phase, nextUnit) {
    const durationKey = phase === "acquisition" ? "acquisitionDuration" : "stimulationDuration";
    const unitKey = phase === "acquisition" ? "acquisitionUnit" : "stimulationUnit";
    setRunConfig((current) => {
      const seconds = durationToSeconds(current[durationKey], current[unitKey]);
      const nextDuration = nextUnit === "min"
        ? Number((seconds / 60).toFixed(2))
        : Number(seconds.toFixed(2));
      return { ...current, [durationKey]: nextDuration, [unitKey]: nextUnit };
    });
  }

  function startExperiment() {
    startPhase("acquisition");
  }

  function finishCurrentPhase() {
    if (runPhase === "acquisition") {
      addEvent("采集期完成");
      if (runConfig.mode === "manual") {
        setRunStatus("waiting-stimulation");
        setRunPhase("stimulation");
        phaseElapsedRef.current = 0;
        phaseTransitionRef.current = false;
        setPhaseElapsedMs(0);
      } else {
        try {
          startDeviceStimulation(runId);
        } catch (error) {
          setRunStatus("aborted");
          setRunPhase("aborted");
          setNotice(`自动流程无法进入刺激期：${error.message}`);
          addEvent("自动流程被设备门禁阻止");
          return;
        }
        setRunPhase("stimulation");
        phaseElapsedRef.current = 0;
        phaseTransitionRef.current = false;
        setPhaseElapsedMs(0);
        setRunStatus("running");
      }
      return;
    }
    addEvent("刺激期完成");
    if (runConfig.mode === "auto" && cycle < Number(runConfig.cycles)) {
      setCycle((value) => value + 1);
      setRunPhase("acquisition");
      phaseElapsedRef.current = 0;
      phaseTransitionRef.current = false;
      setPhaseElapsedMs(0);
      setRunStatus("running");
      addEvent(`自动模式进入第 ${cycle + 1} 次循环`);
      return;
    }
    completeExperiment();
  }

  stimulationCompletedHandlerRef.current = (event) => {
    if (
      event.runId !== runId
      || runStatus !== "running"
      || runPhase !== "stimulation"
      || phaseTransitionRef.current
    ) return;
    phaseTransitionRef.current = true;
    window.clearInterval(runTimerRef.current);
    finishCurrentPhase();
  };

  deviceFaultHandlerRef.current = (event) => {
    const fault = event.payload || {};
    const shouldAbort = fault.severity === "CRITICAL" || fault.details?.action === "ABORT";
    if (!shouldAbort || runStatus !== "running") return;
    window.clearInterval(runTimerRef.current);
    phaseTransitionRef.current = true;
    const finishedAt = new Date().toLocaleString("zh-CN", { hour12: false });
    const faultRunId = event.runId || runId || makeId("RUN");
    const abortedRecord = {
      id: faultRunId,
      startedAt: runStartedAt || finishedAt,
      finishedAt,
      status: "已中止",
      duration: `${(runElapsedMs / 1000).toFixed(1)} s`,
      events: events.length + 1,
      reasonCode: fault.code,
      reason: fault.message,
    };
    setRunResults((rows) => (
      rows.some((row) => row.id === faultRunId)
        ? rows
        : [...rows, abortedRecord]
    ));
    setRunStatus("aborted");
    setRunPhase("aborted");
    setResult({
      status: "aborted",
      runId: faultRunId,
      finishedAt,
      reasonCode: fault.code,
      reason: fault.message,
      faultDetails: fault.details || {},
      events: [...events],
    });
  };

  function completeExperiment() {
    window.clearInterval(runTimerRef.current);
    deviceSessionRef.current?.acquisition.stopAcquisition({ runId });
    const finishedAt = new Date().toLocaleString("zh-CN", { hour12: false });
    const completedRunId = runId || makeId("RUN");
    const runRecord = {
      id: completedRunId,
      startedAt: runStartedAt || finishedAt,
      finishedAt,
      status: "已完成",
      duration: `${(runElapsedMs / 1000).toFixed(1)} s`,
      events: events.length,
    };
    const completedResult = {
      status: "completed",
      runId: completedRunId,
      finishedAt,
      events: [...events],
    };
    setRunResults((rows) => [...rows, runRecord]);
    setResult(completedResult);
    setRunStatus("completed");
    setRunPhase("completed");
    addEvent("实验流程完成，结果可保存与导出");
  }

  function emergencyStop() {
    if (!runIsActive) return;
    window.clearInterval(runTimerRef.current);
    addEvent("紧急停止已触发");
    if (runConfig.mode === "manual") {
      if (runPhase === "stimulation") {
        deviceSessionRef.current?.stimulation.abortRun("OPERATOR_ABORT", runId);
      }
      setRunStatus("paused");
    } else {
      deviceSessionRef.current?.stimulation.abortRun("OPERATOR_ABORT", runId);
      deviceSessionRef.current?.acquisition.stopAcquisition({ runId });
      const finishedAt = new Date().toLocaleString("zh-CN", { hour12: false });
      setRunResults((rows) => [...rows, {
        id: runId,
        startedAt: runStartedAt || finishedAt,
        finishedAt,
        status: "已中止",
        duration: `${(runElapsedMs / 1000).toFixed(1)} s`,
        events: events.length + 1,
      }]);
      setRunStatus("aborted");
      setRunPhase("aborted");
      setResult({
        status: "aborted",
        runId,
        finishedAt,
        events: [...events],
      });
    }
  }

  function continueManual() {
    if (runConfig.mode !== "manual" || runStatus !== "paused") return;
    if (!deviceConnected) {
      setNotice("设备已断开，不能继续当前流程。请重新连接后开始新的运行。");
      return;
    }
    if (runPhase === "stimulation") {
      try {
        startDeviceStimulation(runId);
      } catch (error) {
        setNotice(`无法继续刺激阶段：${error.message}`);
        return;
      }
    }
    addEvent(`继续当前${runPhase === "acquisition" ? "采集" : "刺激"}阶段`);
    setRunStatus("running");
  }

  function restartAutomatic() {
    if (runConfig.mode !== "auto" || runStatus !== "aborted") return;
    const nextRunId = makeId("RUN");
    try {
      deviceSessionRef.current?.acquisition.startAcquisition({ runId: nextRunId });
    } catch (error) {
      setNotice(`自动流程无法重新开始：${error.message}`);
      return;
    }
    setRunId(nextRunId);
    setRunStartedAt(new Date().toLocaleString("zh-CN", { hour12: false }));
    setRunElapsedMs(0);
    phaseElapsedRef.current = 0;
    phaseTransitionRef.current = false;
    setPhaseElapsedMs(0);
    setCycle(1);
    setResult(null);
    setRunPhase("acquisition");
    setRunStatus("running");
    addEvent(`自动模式从头重跑，新运行记录 ${nextRunId}`);
  }

  function runAgain() {
    if (runStatus !== "completed") return;
    const nextRunId = makeId("RUN");
    try {
      deviceSessionRef.current?.acquisition.startAcquisition({ runId: nextRunId });
    } catch (error) {
      setNotice(`新一轮实验无法开始：${error.message}`);
      return;
    }
    setRunId(nextRunId);
    setRunStartedAt(new Date().toLocaleString("zh-CN", { hour12: false }));
    setRunElapsedMs(0);
    phaseElapsedRef.current = 0;
    phaseTransitionRef.current = false;
    setPhaseElapsedMs(0);
    setCycle(1);
    setResult(null);
    setRunPhase("acquisition");
    setRunStatus("running");
    addEvent(`再进行一次，新运行记录 ${nextRunId}`);
  }

  function saveResult() {
    if (!result) return;
    const row = {
      id: patient.experimentId,
      patientId: patient.patientId,
      patientName: patient.patientName,
      createdAt: result.finishedAt,
      status: result.status === "completed" ? "已完成" : "已中止",
      paradigmId,
      modeId,
      parameters: {
        ...parameters,
        ...(paradigmId === "tDCS" && modeId === "hd" ? { centerPolarity: hdCenterPolarity } : {}),
      },
      stimulationAssignments: { ...stimulationAssignments },
      acquisitionPoints: [...acquisitionPoints],
      referencePoint,
      groundPoint,
      sampleRate,
      operatorName: "实验员 A",
      runConfig: { ...runConfig },
      protocolVersion: makeId("PV"),
      result: result.status === "completed" ? "完成 / 可导出" : "中止 / 含异常事件",
      toleranceRecordId: patientToleranceRecord?.id || "",
      runs: [...runResults],
      dataSummary: `${acquisitionPoints.join("、")} · ${sampleRate} Hz · ${runResults.length} 次运行数据`,
    };
    setHistoryRows((rows) => [row, ...rows.filter((item) => item.id !== row.id)]);
    unlock("history");
    addEvent("实验记录已保存到历史列表");
  }

  function buildCurrentRunBundle() {
    if (!result || !runId) throw new Error("当前没有可导出的运行结果");
    const session = deviceSessionRef.current;
    return createRunBundle({
      runId,
      eventLog: session?.getEventLog?.() || [],
      patient: {
        ...patient,
        snapshotAt: new Date().toISOString(),
      },
      operator: {
        operatorId: "OP-DEMO-001",
        operatorName: "实验员 A",
      },
      capability: session?.capability || {},
      protocol: {
        paradigmId,
        modeId,
        parameters: {
          ...parameters,
          ...(paradigmId === "tDCS" && modeId === "hd" ? { centerPolarity: hdCenterPolarity } : {}),
        },
        runConfig: { ...runConfig },
      },
      montage: {
        stimulationAssignments: { ...stimulationAssignments },
        acquisitionPoints: [...acquisitionPoints],
        referencePoint,
        groundPoint,
        sampleRate,
      },
      toleranceRecord: patientToleranceRecord || null,
      result: {
        ...result,
        runResults: [...runResults],
        operatorEvents: [...events],
        checks: { ...checks },
      },
      sourceKind: "SIMULATED",
    });
  }

  function downloadCurrentRunBundle() {
    try {
      const bundle = buildCurrentRunBundle();
      const content = serializeRunBundlePortable(bundle);
      const url = URL.createObjectURL(new Blob([content], { type: "application/json" }));
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `${runId}.eegtes-run.json`;
      anchor.click();
      URL.revokeObjectURL(url);
      addEvent(`Run Bundle 已导出：${runId}`);
      setReplayError("");
    } catch (error) {
      setReplayError(error.message);
      setNotice(`数据包导出失败：${error.message}`);
    }
  }

  async function importRunBundle(event) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    try {
      const bundle = deserializeRunBundlePortable(await file.text());
      replaySessionRef.current?.destroy();
      const replay = new ReplayDeviceSession(bundle, { mode: "realtime", speed: 4 });
      replaySessionRef.current = replay;
      replay.subscribe((deviceEvent) => {
        setDeviceProtocolEvents((rows) => [deviceEvent, ...rows].slice(0, 80));
        if (deviceEvent.messageType === DEVICE_EVENTS.EEG_FRAME) {
          setLatestEegFrame(deviceEvent.payload);
        }
      });
      replay.subscribeState(setReplaySnapshot);
      setReplayBundleName(file.name);
      setReplayError("");
      addEvent(`开始回放本地 Run Bundle：${bundle.manifest.runId}`);
      replay.play();
    } catch (error) {
      replaySessionRef.current?.destroy();
      replaySessionRef.current = null;
      setReplaySnapshot(null);
      setReplayBundleName("");
      setReplayError(error.message);
      setNotice(`Run Bundle 无法读取：${error.message}`);
    }
  }

  function toggleReplay() {
    const replay = replaySessionRef.current;
    if (!replay) return;
    if (replay.getSnapshot().state === "PLAYING") replay.pause();
    else replay.play();
  }

  function changeReplaySpeed(speed) {
    replaySessionRef.current?.setSpeed(Number(speed));
  }

  function seekReplay(percent) {
    const replay = replaySessionRef.current;
    if (!replay) return;
    const snapshot = replay.getSnapshot();
    const target = snapshot.startDeviceTimeUs
      + ((snapshot.endDeviceTimeUs - snapshot.startDeviceTimeUs) * Number(percent)) / 100;
    replay.seek(target);
  }

  function copyHistory(record) {
    const imported = importHistoricalConfiguration(record);
    setPatient((current) => ({
      ...current,
      patientId: record.patientId,
      patientName: record.patientName,
      experimentId: `${record.id}-COPY`,
    }));
    setParadigmId(imported.paradigmId);
    setModeId(imported.modeId);
    setParameters(imported.parameters);
    setHdCenterPolarity(imported.parameters.centerPolarity || "A");
    setStimulationAssignments(imported.stimulationAssignments);
    setAcquisitionPoints(imported.acquisitionPoints);
    setReferencePoint(imported.referencePoint);
    setGroundPoint(imported.groundPoint);
    setSampleRate(imported.sampleRate);
    setRunConfig(imported.runConfig);
    setChecks(imported.checks);
    setActiveRoleId(getRolesForMode(imported.paradigmId, imported.modeId)[0]?.id || "");
    setUnlocked(["home", "patient", "plan", "electrodes", "history"]);
    setPageId("patient");
    setNotice("已复制历史配置。阻抗和二次确认均已重置；患者耐受记录将按患者与刺激范式重新匹配。");
  }

  function resetPrototype() {
    window.clearInterval(runTimerRef.current);
    replaySessionRef.current?.destroy();
    replaySessionRef.current = null;
    deviceSessionRef.current?.disconnect();
    setPageId("home");
    setUnlocked(["home", "patient", "history"]);
    setNotice("");
    setDeviceConnected(false);
    setPatient(DEFAULT_PATIENT);
    setParadigmId("tDCS");
    setModeId("dual");
    setParameters(cloneParameterDefaults("tDCS"));
    setHdCenterPolarity("A");
    setActiveRoleId("A");
    setStimulationAssignments({});
    setAcquisitionPoints([]);
    setReferencePoint(DEFAULT_ACQUISITION_SETTINGS.referencePoint);
    setGroundPoint(DEFAULT_ACQUISITION_SETTINGS.groundPoint);
    setSampleRate(DEFAULT_ACQUISITION_SETTINGS.sampleRate);
    setChecks(resetPhysicalChecks());
    setRunConfig(DEFAULT_RUN_CONFIG);
    setRunStatus("standby");
    setRunPhase("standby");
    setRunId("");
    setRunElapsedMs(0);
    phaseElapsedRef.current = 0;
    phaseTransitionRef.current = false;
    setEvents([]);
    setDeviceProtocolEvents([]);
    setLatestEegFrame(null);
    setReplayBundleName("");
    setReplayError("");
    setReplaySnapshot(null);
    setResult(null);
    setRunResults([]);
    setRunStartedAt("");
    setToleranceRecords(SEEDED_TOLERANCE_RECORDS);
    setToleranceTab("test");
    setToleranceDraft({
      patientId: DEFAULT_PATIENT.patientId,
      patientName: DEFAULT_PATIENT.patientName,
      paradigmId: "tDCS",
      threshold: 1,
      unit: "mA",
      operatorName: "实验员 A",
      feedback: "无不适",
      note: "",
    });
    setExpandedHistoryId("");
  }

  function renderHomePage() {
    return (
      <div className="workflow-home-layout">
        <section className="workflow-hero-card">
          <PageHeader
            eyebrow="流程入口"
            title="EEG-tES 联合实验"
            description="先建立设备连接和实验上下文，再进入刺激方案、电极检测与实验运行。"
          />
          <div className="workflow-home-actions">
            <button type="button" className="workflow-primary" onClick={createExperiment}>
              <FlaskConical size={18} /> 新建实验 <ChevronRight size={18} />
            </button>
            <button type="button" className="workflow-secondary" onClick={() => goPage("history")}>
              <History size={18} /> 查看历史记录
            </button>
          </div>
          <div className="workflow-principles">
            <span><Check size={15} /> 配置结果可追踪</span>
            <span><Check size={15} /> 物理检测不跨实验复用</span>
            <span><Check size={15} /> 自动与手动运行规则分离</span>
          </div>
        </section>
        <section className="workflow-card workflow-device-card">
          <div className="workflow-card-title">
            <Settings2 size={19} />
            <div><h3>串口与下位机</h3><p>本原型使用模拟设备验证上位机流程。</p></div>
          </div>
          <Field label="设备端口">
            <select value={devicePort} onChange={(event) => setDevicePort(event.target.value)}>
              <option>COM-DEMO</option>
              <option>USB-SIM-01</option>
            </select>
          </Field>
          <dl className="workflow-device-meta">
            <div><dt>刺激通道</dt><dd>5</dd></div>
            <div><dt>EEG 采样率</dt><dd>500 Hz</dd></div>
            <div><dt>支持范式</dt><dd>tDCS / tACS / tRNS / tPCS / Sham</dd></div>
          </dl>
          <button type="button" className={deviceConnected ? "workflow-danger-lite" : "workflow-primary"} onClick={connectDevice}>
            {deviceConnected ? "断开模拟设备" : "连接模拟设备"}
          </button>
          <StatusPill status={deviceConnected ? "pass" : "pending"} />
        </section>
        <section className="workflow-card workflow-tolerance-module">
          <div className="workflow-section-header">
            <div>
              <h3>患者耐受度</h3>
              <p>患者进入实验前独立完成测试并沉淀记录；有效期规则暂不执行。</p>
            </div>
            <StatusPill status={toleranceRecords.length ? "pass" : "pending"} />
          </div>
          <div className="workflow-tab-switch" role="tablist" aria-label="患者耐受度">
            <button type="button" role="tab" aria-selected={toleranceTab === "test"} className={toleranceTab === "test" ? "is-active" : ""} onClick={() => setToleranceTab("test")}>进行耐受度测试</button>
            <button type="button" role="tab" aria-selected={toleranceTab === "history"} className={toleranceTab === "history" ? "is-active" : ""} onClick={() => setToleranceTab("history")}>所有患者的耐受度历史记录</button>
          </div>
          {toleranceTab === "test" ? (
            <div className="workflow-tolerance-page">
              <div className="workflow-form-grid">
                <Field label="患者 ID"><input value={toleranceDraft.patientId} onChange={(event) => updateToleranceDraft("patientId", event.target.value)} /></Field>
                <Field label="患者姓名"><input value={toleranceDraft.patientName} onChange={(event) => updateToleranceDraft("patientName", event.target.value)} /></Field>
                <Field label="刺激范式">
                  <select value={toleranceDraft.paradigmId} onChange={(event) => updateToleranceDraft("paradigmId", event.target.value)}>
                    {Object.entries(STIMULATION_PARADIGMS).filter(([, item]) => !item.pending).map(([id, item]) => <option key={id} value={id}>{item.label}</option>)}
                  </select>
                </Field>
                <Field label="测试人"><input value={toleranceDraft.operatorName} onChange={(event) => updateToleranceDraft("operatorName", event.target.value)} /></Field>
              </div>
              <div className="workflow-tolerance-inline">
                <button type="button" onClick={() => adjustToleranceDraft(-1)}>−2%</button>
                <strong>{Number(toleranceDraft.threshold).toFixed(2)} <small>{toleranceDraft.unit}</small></strong>
                <button type="button" onClick={() => adjustToleranceDraft(1)}>＋2%</button>
              </div>
              <div className="workflow-form-grid">
                <Field label="患者反馈">
                  <select value={toleranceDraft.feedback} onChange={(event) => updateToleranceDraft("feedback", event.target.value)}>
                    <option>无不适</option><option>轻微刺痛，可接受</option><option>明显不适</option>
                  </select>
                </Field>
                <Field label="备注"><input value={toleranceDraft.note} onChange={(event) => updateToleranceDraft("note", event.target.value)} placeholder="可记录皮肤感受、停止原因等" /></Field>
              </div>
              <button type="button" className="workflow-primary" onClick={saveToleranceRecord}><Save size={16} /> 保存患者耐受度记录</button>
            </div>
          ) : (
            <div className="workflow-tolerance-history">
              <div className="is-header"><span>患者</span><span>刺激范式 / 阈值</span><span>测试时间</span><span>测试人</span><span>反馈与备注</span></div>
              {toleranceRecords.map((record) => (
                <div key={record.id}>
                  <span><strong>{record.patientName}</strong><small>{record.patientId}<br />{record.id}</small></span>
                  <span><strong>{record.paradigmId} · {record.threshold} {record.unit}</strong><small>已完成</small></span>
                  <span>{record.testedAt}</span>
                  <span>{record.operatorName}</span>
                  <span><strong>{record.feedback}</strong><small>{record.note || "无备注"}</small></span>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    );
  }

  function renderPatientPage() {
    return (
      <>
        <PageHeader
          eyebrow="页面 1 · 患者与实验信息"
          title="先建立本次实验上下文"
          description="患者可来自已有数据系统，也可以在新建实验时临时录入。"
        />
        <div className="workflow-two-columns">
          <section className="workflow-card">
            <div className="workflow-card-title"><UserRound size={19} /><h3>患者信息</h3></div>
            <div className="workflow-form-grid">
              <Field label="患者 ID"><input value={patient.patientId} onChange={(e) => updatePatient("patientId", e.target.value)} /></Field>
              <Field label="患者姓名"><input value={patient.patientName} onChange={(e) => updatePatient("patientName", e.target.value)} /></Field>
              <Field label="性别"><select value={patient.sex} onChange={(e) => updatePatient("sex", e.target.value)}><option>未说明</option><option>男</option><option>女</option></select></Field>
              <Field label="年龄"><input type="number" min="1" max="120" value={patient.age} onChange={(e) => updatePatient("age", e.target.value)} /></Field>
            </div>
          </section>
          <section className="workflow-card">
            <div className="workflow-card-title"><FileClock size={19} /><h3>实验信息</h3></div>
            <Field label="实验 ID"><input value={patient.experimentId} onChange={(e) => updatePatient("experimentId", e.target.value)} /></Field>
            <Field label="实验备注"><textarea rows="4" value={patient.note} onChange={(e) => updatePatient("note", e.target.value)} /></Field>
            <button type="button" className="workflow-secondary" onClick={() => goPage("history")}><Database size={16} /> 从历史记录复制配置</button>
          </section>
        </div>
        <div className="workflow-page-actions">
          <button type="button" className="workflow-primary" onClick={continueFromPatient}>进入刺激方案配置 <ChevronRight size={17} /></button>
        </div>
      </>
    );
  }

  function renderPlanPage() {
    return (
      <>
        <PageHeader
          eyebrow="页面 2 · 刺激方案配置"
          title="先确定刺激范式与阵列，再开放对应参数"
          description="页面范围来自当前模拟设备 capability；不支持的组合不会进入点位配置。"
        />
        <section className="workflow-card">
          <h3>1. 刺激范式</h3>
          <div className="workflow-choice-grid is-six">
            {Object.entries(STIMULATION_PARADIGMS).map(([id, item]) => (
              <button
                type="button"
                className={paradigmId === id ? "is-active" : ""}
                disabled={item.pending}
                onClick={() => updateParadigm(id)}
                key={id}
              >
                <strong>{item.label}</strong><span>{item.description}</span>
              </button>
            ))}
          </div>
        </section>
        <section className="workflow-card">
          <h3>2. 阵列与二级模式</h3>
          <div className="workflow-choice-grid">
            {modes.map((mode) => (
              <button
                type="button"
                className={modeId === mode.id ? "is-active" : ""}
                disabled={mode.pending}
                onClick={() => updateMode(mode.id)}
                key={mode.id}
              >
                <strong>{mode.label}</strong><span>{mode.description}</span>
              </button>
            ))}
          </div>
          {paradigmId === "Sham" && SHAM_SOURCE_PRESETS[modeId] && (
            <div className="workflow-inline-note">
              来源方案：{SHAM_SOURCE_PRESETS[modeId].sourceId} · {SHAM_SOURCE_PRESETS[modeId].sourceLabel}。
              阵列、点位与角色将在后续页面只读展示。
            </div>
          )}
        </section>
        <section className="workflow-card">
          <h3>3. {paradigm.label} 参数</h3>
          <div className="workflow-form-grid is-parameters">
            {Object.entries(paradigm.parameters).map(([key, definition]) => (
              <Field key={key} label={definition.label} hint={`${definition.min}–${definition.max} ${definition.unit}`}>
                <div className="workflow-number-input">
                  <input
                    type="number"
                    min={definition.min}
                    max={definition.max}
                    step={definition.step}
                    value={parameters[key]}
                    onChange={(event) => {
                      setParameters((current) => ({ ...current, [key]: Number(event.target.value) }));
                      resetCheckResults(`${definition.label}已修改`);
                    }}
                  />
                  <span>{definition.unit}</span>
                </div>
              </Field>
            ))}
          </div>
          {parameterErrors.length > 0 && <div className="workflow-inline-warning"><AlertTriangle size={16} /> {parameterErrors[0]}</div>}
          <StimulusWaveformPreview
            paradigmId={paradigmId}
            modeId={modeId}
            parameters={parameters}
          />
        </section>
        <div className="workflow-page-actions">
          <button type="button" className="workflow-primary" onClick={continueFromPlan}>进入电极配置与检测 <ChevronRight size={17} /></button>
        </div>
      </>
    );
  }

  function renderElectrodePage() {
    return (
      <>
        <PageHeader
          eyebrow="页面 3 · 电极配置与阻抗检测"
          title="先完成点位映射，再执行本次实验的物理校验"
          description="刺激与采集使用同一组硬件点位，系统禁止通道冲突和静默抢占。"
        />
        <div className="workflow-electrode-layout">
          <section className="workflow-card workflow-point-card">
            <div className="workflow-tab-switch" role="tablist">
              <button type="button" className={activeElectrodeTab === "stim" ? "is-active" : ""} onClick={() => setActiveElectrodeTab("stim")}>
                <Zap size={16} /> 刺激电极
              </button>
              <button type="button" className={activeElectrodeTab === "acq" ? "is-active" : ""} onClick={() => setActiveElectrodeTab("acq")}>
                <Activity size={16} /> 采集电极
              </button>
            </div>
            {activeElectrodeTab === "stim" ? (
              <>
                {paradigmId === "tDCS" && modeId === "hd" && (
                  <div className="workflow-inline-note">
                    中心电极方向：
                    <button
                      type="button"
                      className={hdCenterPolarity === "A" ? "is-active" : ""}
                      onClick={() => {
                        setHdCenterPolarity("A");
                        resetCheckResults("HD 中心电极方向已修改");
                      }}
                    >
                      阳极 A
                    </button>
                    <button
                      type="button"
                      className={hdCenterPolarity === "C" ? "is-active" : ""}
                      onClick={() => {
                        setHdCenterPolarity("C");
                        resetCheckResults("HD 中心电极方向已修改");
                      }}
                    >
                      阴极 C
                    </button>
                    ；四个外围槽位自动使用相反方向。
                  </div>
                )}
                <div className="workflow-role-list">
                  {roles.map((role) => {
                    const point = Object.entries(stimulationAssignments).find(([, roleId]) => roleId === role.id)?.[0];
                    return (
                      <button
                        type="button"
                        key={role.id}
                        disabled={stimulationAssignmentLocked}
                        className={activeRoleId === role.id ? "is-active" : ""}
                        onClick={() => setActiveRoleId(role.id)}
                      >
                        <strong>{role.label}</strong><span>{point || "请选择点位"}</span><small>{role.channel}{role.detail ? ` · ${role.detail}` : ""}</small>
                      </button>
                    );
                  })}
                </div>
                <p className="workflow-helper">
                  {stimulationAssignmentLocked
                    ? `Sham 已继承 ${SHAM_SOURCE_PRESETS[modeId]?.sourceId || "来源方案"}，点位与角色不可单独修改。`
                    : "先选择上方角色，再点击一个点位进行绑定。"}
                </p>
              </>
            ) : (
              <div className="workflow-acquisition-settings">
                <p>选择至少 2 个 EEG 通道；刺激点位不可选。</p>
                <div className="workflow-form-grid">
                  <Field label="参考电极" defaultValue={DEFAULT_ACQUISITION_SETTINGS.referencePoint}>
                    <select value={referencePoint} onChange={(e) => { setReferencePoint(e.target.value); resetCheckResults("参考电极已修改"); }}>
                      <option value="">请选择</option>
                      {EEG_POINTS.filter((point) => !stimPoints.has(point) && !acquisitionPoints.includes(point) && point !== groundPoint).map((point) => <option key={point}>{point}</option>)}
                    </select>
                  </Field>
                  <Field label="地电极" defaultValue={DEFAULT_ACQUISITION_SETTINGS.groundPoint}>
                    <select value={groundPoint} onChange={(e) => { setGroundPoint(e.target.value); resetCheckResults("地电极已修改"); }}>
                      <option value="">请选择</option>
                      {EEG_POINTS.filter((point) => !stimPoints.has(point) && !acquisitionPoints.includes(point) && point !== referencePoint).map((point) => <option key={point}>{point}</option>)}
                    </select>
                  </Field>
                  <Field label="采样率" defaultValue={`${DEFAULT_ACQUISITION_SETTINGS.sampleRate} Hz`}><select value={sampleRate} onChange={(e) => setSampleRate(Number(e.target.value))}><option value="500">500 Hz</option></select></Field>
                </div>
              </div>
            )}
            <div className="workflow-point-grid">
              {EEG_POINTS.map((point) => {
                const stimRole = stimulationAssignments[point];
                const acquisition = acquisitionPoints.includes(point);
                const special = point === referencePoint ? "REF" : point === groundPoint ? "GND" : "";
                const blocked = activeElectrodeTab === "stim"
                  ? acquisition || Boolean(special) || stimulationAssignmentLocked
                  : Boolean(stimRole) || Boolean(special);
                return (
                  <button
                    type="button"
                    key={point}
                    disabled={blocked}
                    className={[
                      stimRole ? "is-stim" : "",
                      acquisition ? "is-acquisition" : "",
                      special ? "is-special" : "",
                    ].join(" ")}
                    onClick={() => activeElectrodeTab === "stim" ? assignStimPoint(point) : toggleAcquisition(point)}
                  >
                    <strong>{point}</strong>
                    <span>{stimRole ? getRoleDisplayLabel(paradigmId, modeId, stimRole) : (acquisition ? "EEG" : special)}</span>
                  </button>
                );
              })}
            </div>
          </section>
          <aside className="workflow-card workflow-check-panel">
            <div className="workflow-check-group">
              <header>
                <div>
                  <h3>刺激配置</h3>
                  <p>
                    {paradigm.label} · {STIMULATION_MODES[modeId].label}
                    {stimulationAssignmentLocked ? " · 来源锁定" : ""}
                  </p>
                </div>
                <span>{stimulationValidation.assignedCount}/{stimulationValidation.requiredCount}</span>
              </header>
              <ul>{roles.map((role) => <li key={role.id}><span>{role.label}</span><strong>{Object.entries(stimulationAssignments).find(([, id]) => id === role.id)?.[0] || "—"}</strong></li>)}</ul>
              <div className="workflow-check-actions">
                <StatusPill status={checks.stimImpedance} />
                {checks.stimImpedance === "running"
                  ? <button type="button" className="workflow-danger-lite" onClick={stopDetection}><Square size={15} /> 停止检测</button>
                  : <button type="button" className="workflow-secondary" onClick={() => startDetection("stim")}>检测刺激阻抗</button>}
              </div>
            </div>
            <div className="workflow-check-group">
              <header><div><h3>采集配置</h3><p>{acquisitionPoints.length} 个 EEG 通道 · {sampleRate} Hz</p></div><span>{acquisitionValidation.isValid ? "完整" : "待完善"}</span></header>
              <ul>
                <li><span>采集通道</span><strong>{acquisitionPoints.join("、") || "—"}</strong></li>
                <li><span>REF / GND</span><strong>{referencePoint || "—"} / {groundPoint || "—"}</strong></li>
              </ul>
              <div className="workflow-check-actions">
                <StatusPill status={checks.acqImpedance} />
                {checks.acqImpedance === "running"
                  ? <button type="button" className="workflow-danger-lite" onClick={stopDetection}><Square size={15} /> 停止检测</button>
                  : <button type="button" className="workflow-secondary" onClick={() => startDetection("acq")}>检测采集阻抗</button>}
              </div>
            </div>
            <div className="workflow-gate-list">
              <div><span>患者耐受记录</span><StatusPill status={patientToleranceRecord ? "pass" : "pending"} /></div>
              <div><span>刺激阻抗</span><StatusPill status={checks.stimImpedance} /></div>
              <div><span>采集阻抗</span><StatusPill status={checks.acqImpedance} /></div>
              <div><span>配置确认</span><StatusPill status={checks.summary} /></div>
            </div>
            <button type="button" className="workflow-primary" onClick={openSummary} disabled={!canOpenSummary(checks) || !patientToleranceRecord}>
              查看并确认实验配置
            </button>
          </aside>
        </div>
      </>
    );
  }

  function renderRunPage() {
    const duration = runPhase === "acquisition"
      ? durationToSeconds(runConfig.acquisitionDuration, runConfig.acquisitionUnit)
      : durationToSeconds(runConfig.stimulationDuration, runConfig.stimulationUnit);
    const progress = runIsActive || runStatus === "paused"
      ? Math.min(100, (phaseElapsedMs / Math.max(300, Number(duration) * 1000)) * 100)
      : runStatus === "completed" ? 100 : 0;
    const replayDurationUs = replaySnapshot
      ? replaySnapshot.endDeviceTimeUs - replaySnapshot.startDeviceTimeUs
      : 0;
    const replayProgress = replaySnapshot && replayDurationUs > 0
      ? Math.max(0, Math.min(100, (
        (replaySnapshot.positionDeviceTimeUs - replaySnapshot.startDeviceTimeUs)
        / replayDurationUs
      ) * 100))
      : 0;
    const artifactLabels = latestEegFrame?.artifactLabels || [];
    const artifactNames = {
      BLINK: "眨眼伪迹",
      MUSCLE: "肌电伪迹",
      STIMULATION: "刺激伪迹",
    };
    return (
      <>
        <PageHeader
          eyebrow="页面 4 · 时序、运行与结果"
          title="同一页完成实验编排、运行监测和结果沉淀"
          description="手动模式分阶段启动；自动模式一次启动后按循环完整运行。"
        />
        <div className="workflow-run-layout">
          <section className="workflow-card workflow-signal-card">
            <header className="workflow-section-header">
              <div>
                <h3>EEG 实时信号</h3>
                <p>
                  {acquisitionPoints.join("、") || "未配置采集通道"}
                  {latestEegFrame && ` · 模拟源 ${latestEegFrame.signalModelVersion}`}
                  {latestEegFrame && ` · ${latestEegFrame.signalProfileId}`}
                  {latestEegFrame && ` · 传输延迟 ${latestEegFrame.simulatedLatencyMs} ms`}
                  {latestEegFrame && ` · 时钟漂移 ${latestEegFrame.clockDriftPpm} ppm`}
                  {latestEegFrame?.droppedSamplesBefore > 0
                    && ` · 前序缺失 ${latestEegFrame.droppedSamplesBefore} 点`}
                </p>
              </div>
              <div className="workflow-signal-tools">
                {artifactLabels.length > 0 && (
                  <div className="workflow-artifact-list" aria-label="当前数据伪迹">
                    {artifactLabels.map((label) => (
                      <span key={label}>{artifactNames[label] || label}</span>
                    ))}
                  </div>
                )}
                <div className="workflow-filter-list">
                  {Object.entries({ high: "高通", low: "低通", notch: "陷波" }).map(([key, label]) => (
                    <button
                      type="button"
                      key={key}
                      role="switch"
                      aria-checked={filterState[key]}
                      className={filterState[key] ? "is-active" : ""}
                      onClick={() => setFilterState((current) => ({ ...current, [key]: !current[key] }))}
                    >
                      {label}<span>{filterState[key] ? "开" : "关"}</span>
                    </button>
                  ))}
                </div>
              </div>
            </header>
            <div className="workflow-signal-list">
              {(acquisitionPoints.length ? acquisitionPoints : ["C3", "Cz", "C4"]).slice(0, 5).map((point, index) => (
                <div key={point}>
                  <strong>{point}</strong>
                  <DeviceWaveform
                    filterState={filterState}
                    frame={latestEegFrame}
                    point={point}
                    offset={index * 11 + Math.round(runElapsedMs / 180)}
                  />
                </div>
              ))}
            </div>
          </section>
          <aside className="workflow-run-side">
            <section className="workflow-card">
              <div className="workflow-tab-switch">
                <button type="button" className={runConfig.mode === "manual" ? "is-active" : ""} disabled={runIsActive} onClick={() => changeRunMode("manual")}>手动模式</button>
                <button type="button" className={runConfig.mode === "auto" ? "is-active" : ""} disabled={runIsActive} onClick={() => changeRunMode("auto")}>自动模式</button>
              </div>
              <div className="workflow-form-grid">
                <Field label="采集期">
                  <div className="workflow-number-input">
                    <input type="number" min="0.1" max={runConfig.acquisitionUnit === "min" ? 60 : 3600} step="0.1" disabled={runStatus !== "standby"} value={runConfig.acquisitionDuration} onChange={(e) => setRunConfig((current) => ({ ...current, acquisitionDuration: Number(e.target.value) }))} />
                    <select aria-label="采集期时间单位" disabled={runStatus !== "standby"} value={runConfig.acquisitionUnit} onChange={(event) => changeRunUnit("acquisition", event.target.value)}><option value="s">s</option><option value="min">min</option></select>
                  </div>
                </Field>
                <Field label="刺激期">
                  <div className="workflow-number-input">
                    <input type="number" min="0.1" max={runConfig.stimulationUnit === "min" ? 60 : 3600} step="0.1" disabled={runStatus !== "standby"} value={runConfig.stimulationDuration} onChange={(e) => setRunConfig((current) => ({ ...current, stimulationDuration: Number(e.target.value) }))} />
                    <select aria-label="刺激期时间单位" disabled={runStatus !== "standby"} value={runConfig.stimulationUnit} onChange={(event) => changeRunUnit("stimulation", event.target.value)}><option value="s">s</option><option value="min">min</option></select>
                  </div>
                </Field>
                {runConfig.mode === "auto" && <Field label="循环次数"><input type="number" min="1" max="10" disabled={runStatus !== "standby"} value={runConfig.cycles} onChange={(e) => setRunConfig((current) => ({ ...current, cycles: Number(e.target.value) }))} /></Field>}
              </div>
            </section>
            <section className="workflow-card workflow-monitor-card">
              <header><div><h3>运行监控</h3><p>{runId || "尚未生成运行记录"}</p></div><StatusPill status={runStatus === "waiting-stimulation" ? "pending" : runStatus} /></header>
              <dl>
                <div><dt>设备</dt><dd>{deviceConnected ? "已连接" : "未连接"}</dd></div>
                <div><dt>当前阶段</dt><dd>{({ standby: "待机", acquisition: "采集期", stimulation: "刺激期", completed: "已完成", aborted: "已中止" })[runPhase] || runPhase}</dd></div>
                <div><dt>阻抗</dt><dd>刺激 / 采集均通过</dd></div>
                <div><dt>运行时长</dt><dd>{(runElapsedMs / 1000).toFixed(1)} s</dd></div>
                <div><dt>循环</dt><dd>{cycle} / {runConfig.mode === "auto" ? runConfig.cycles : 1}</dd></div>
              </dl>
              <div className="workflow-progress"><span style={{ width: `${progress}%` }} /></div>
              <div className="workflow-run-actions">
                {runStatus === "standby" && <button type="button" className="workflow-primary" onClick={startExperiment}><Play size={16} /> 开始采集</button>}
                {runStatus === "waiting-stimulation" && <button type="button" className="workflow-primary" onClick={() => startPhase("stimulation")}><Zap size={16} /> 开始刺激</button>}
                {runStatus === "paused" && <button type="button" className="workflow-primary" onClick={continueManual}><Play size={16} /> 继续当前流程</button>}
                {runStatus === "aborted" && runConfig.mode === "auto" && <button type="button" className="workflow-primary" onClick={restartAutomatic}><RotateCcw size={16} /> 从头重新运行</button>}
                {runStatus === "completed" && <button type="button" className="workflow-primary" onClick={runAgain}><RotateCcw size={16} /> 再进行一次</button>}
                <button type="button" className="workflow-emergency" disabled={!runIsActive} onClick={emergencyStop}><CircleStop size={16} /> 紧急停止</button>
              </div>
            </section>
            {result && (
              <section className="workflow-card workflow-result-card">
              <div className="workflow-card-title"><ClipboardCheck size={19} /><div><h3>实验结果</h3><p>{result.status === "completed" ? "流程已完成" : "流程已中止"}</p></div></div>
                <p>
                  {result.status === "completed"
                    ? "运行记录、异常事件和采集数据已经形成可追踪结果。"
                    : `中止原因：${result.reasonCode || "OPERATOR_ABORT"}。异常数据与安全动作已冻结到本次运行记录。`}
                </p>
                <div className="workflow-result-actions">
                  <button type="button" className="workflow-primary" onClick={saveResult}><Save size={16} /> 保存记录</button>
                  <button type="button" className="workflow-secondary" onClick={downloadCurrentRunBundle}><Download size={16} /> 导出 Run Bundle</button>
                </div>
              </section>
            )}
            <section className="workflow-card workflow-replay-card">
              <div className="workflow-card-title">
                <FileClock size={19} />
                <div>
                  <h3>本地数据回放</h3>
                  <p>{replayBundleName || "导入 .eegtes-run.json 后按原设备时间回放"}</p>
                </div>
              </div>
              <label className="workflow-secondary workflow-file-button">
                <Upload size={16} /> 导入 Run Bundle
                <input type="file" accept=".json,.eegtes-run.json,application/json" onChange={importRunBundle} />
              </label>
              {replayError && <p className="workflow-replay-error">{replayError}</p>}
              {replaySnapshot && (
                <div className="workflow-replay-controls">
                  <div>
                    <StatusPill status={({
                      IDLE: "standby",
                      PLAYING: "running",
                      PAUSED: "paused",
                      COMPLETED: "completed",
                    })[replaySnapshot.state] || "standby"} />
                    <span>{replaySnapshot.cursor} / {replaySnapshot.eventCount} 条设备事件</span>
                  </div>
                  <input
                    type="range"
                    min="0"
                    max="100"
                    step="0.1"
                    aria-label="回放位置"
                    value={replayProgress}
                    onChange={(event) => seekReplay(event.target.value)}
                  />
                  <div>
                    <button type="button" className="workflow-secondary" onClick={toggleReplay}>
                      {replaySnapshot.state === "PLAYING" ? <Pause size={15} /> : <Play size={15} />}
                      {replaySnapshot.state === "PLAYING" ? "暂停" : replaySnapshot.state === "COMPLETED" ? "重新播放" : "继续"}
                    </button>
                    <select
                      aria-label="回放倍速"
                      value={replaySnapshot.speed}
                      onChange={(event) => changeReplaySpeed(event.target.value)}
                    >
                      <option value="0.5">0.5×</option>
                      <option value="1">1×</option>
                      <option value="2">2×</option>
                      <option value="4">4×</option>
                      <option value="8">8×</option>
                    </select>
                  </div>
                </div>
              )}
            </section>
          </aside>
        </div>
        <section className="workflow-card workflow-event-card">
          <h3>运行与异常事件</h3>
          <div>{events.length ? events.map((event, index) => <p key={`${event.time}-${index}`}><time>{event.time}</time><span>{event.message}</span></p>) : <p className="is-empty">尚无事件</p>}</div>
          <details>
            <summary>设备协议日志（{deviceProtocolEvents.length}）</summary>
            <div>
              {deviceProtocolEvents.slice(0, 12).map((event, index) => (
                <p key={`${event.sourceKind || "LIVE"}-${event.deviceId}-${event.sequence}-${index}`}>
                  <time>{(event.deviceTimeUs / 1_000_000).toFixed(3)}s</time>
                  <span>{event.sourceKind === "REPLAY" ? "REPLAY · " : ""}{event.messageType}</span>
                </p>
              ))}
            </div>
          </details>
        </section>
      </>
    );
  }

  function renderHistoryPage() {
    return (
      <>
        <PageHeader
          eyebrow="页面 5 · 历史记录"
          title="查看实验人员、患者、实验数据与多次运行结果"
          description="可查看同一实验下的多次运行；复制时只复用参数和点位模板，阻抗与准入结果必须重新执行。"
        />
        <section className="workflow-card workflow-history-card">
          <div className="workflow-history-table">
            <div className="is-header"><span>实验 / 患者 / 实验人员</span><span>方案</span><span>实验数据</span><span>多次结果</span><span>操作</span></div>
            {historyRows.map((record) => (
              <div className="workflow-history-entry" key={record.id}>
                <div className="workflow-history-row">
                  <span><strong>{record.id}</strong><small>{record.patientName} · {record.patientId}<br />实验人员：{record.operatorName || "未记录"} · {record.createdAt}</small></span>
                  <span><strong>{record.paradigmId}</strong><small>{STIMULATION_MODES[record.modeId]?.label}<br />{record.protocolVersion}</small></span>
                  <span><strong>{record.dataSummary || `${record.acquisitionPoints?.join("、")} · ${record.sampleRate} Hz`}</strong><small>耐受记录：{record.toleranceRecordId || "独立匹配"}</small></span>
                  <span><strong>{record.runs?.length || 0} 次</strong><small>{record.status} · {record.result}</small></span>
                  <span className="workflow-history-actions">
                    <button type="button" className="workflow-secondary" onClick={() => setExpandedHistoryId((current) => current === record.id ? "" : record.id)}>{expandedHistoryId === record.id ? "收起结果" : "查看结果"}</button>
                    <button type="button" className="workflow-secondary" onClick={() => copyHistory(record)}><RotateCcw size={15} /> 复制配置</button>
                  </span>
                </div>
                {expandedHistoryId === record.id && (
                  <div className="workflow-history-detail">
                    <article><h4>实验人员</h4><p>{record.operatorName || "未记录"}</p><small>负责配置、检测、运行与异常处理</small></article>
                    <article><h4>患者</h4><p>{record.patientName} · {record.patientId}</p><small>实验 ID：{record.id}</small></article>
                    <article><h4>实验数据</h4><p>{record.dataSummary || "尚无数据摘要"}</p><small>刺激：{record.paradigmId} / {STIMULATION_MODES[record.modeId]?.label}</small></article>
                    <section>
                      <h4>多次运行结果</h4>
                      {(record.runs?.length ? record.runs : []).map((run) => (
                        <div className="workflow-run-result-row" key={run.id}>
                          <strong>{run.id}</strong><span>{run.status}</span><span>{run.duration}</span><span>{run.startedAt} → {run.finishedAt}</span><span>异常/事件 {run.events}</span>
                        </div>
                      ))}
                      {!record.runs?.length && <p>该实验尚未保存独立运行结果。</p>}
                    </section>
                  </div>
                )}
              </div>
            ))}
          </div>
        </section>
      </>
    );
  }

  return (
    <main className="workflow-prototype-shell">
      <header className="workflow-topbar">
        <div>
          <span>THROWAWAY PROTOTYPE</span>
          <h1>EEG-tES 产品流程验证原型</h1>
          <p>验证 PRD 页面顺序、状态门禁、检测链路与手动/自动运行规则，不代表最终 UI。</p>
        </div>
        <nav>
          <button type="button" onClick={resetPrototype}><RotateCcw size={16} /> 重置原型</button>
          <button type="button" onClick={onHome}><Home size={16} /> 返回首页</button>
        </nav>
      </header>
      <div className="workflow-context-bar">
        <span className={deviceConnected ? "is-pass" : ""}>{deviceConnected ? "模拟设备已连接" : "设备未连接"}</span>
        <span>患者：{patient.patientId}</span>
        <span>实验：{patient.experimentId}</span>
        <span>方案：{paradigmId} / {STIMULATION_MODES[modeId].label}</span>
        {faultScenarioId !== "none" && <span className="is-fault">故障场景：{faultScenario.label}</span>}
      </div>
      <nav className="workflow-stepper" aria-label="产品流程页面">
        {WORKFLOW_PAGES.map((page, index) => {
          const isUnlocked = unlocked.includes(page.id);
          return (
            <button
              type="button"
              key={page.id}
              disabled={!isUnlocked}
              className={`${pageId === page.id ? "is-active" : ""} ${isUnlocked ? "is-unlocked" : ""}`}
              onClick={() => goPage(page.id)}
            >
              <span>{index + 1}</span>
              <strong>{page.label}</strong>
            </button>
          );
        })}
      </nav>
      {notice && <div className="workflow-notice"><AlertTriangle size={17} /><span>{notice}</span><button type="button" onClick={() => setNotice("")}><X size={15} /></button></div>}
      <section className="workflow-page" data-page-index={currentPageIndex}>
        {pageId === "home" && renderHomePage()}
        {pageId === "patient" && renderPatientPage()}
        {pageId === "plan" && renderPlanPage()}
        {pageId === "electrodes" && renderElectrodePage()}
        {pageId === "run" && renderRunPage()}
        {pageId === "history" && renderHistoryPage()}
      </section>
      <footer className="workflow-prototype-footer">
        <span>核心门禁：设备连接 → 患者耐受记录 → 患者上下文 → 方案有效 → 电极映射 → 双阻抗 → 二次确认 → 运行</span>
        <span>物理检测状态不会从历史配置继承</span>
      </footer>

      {summaryOpen && (
        <Modal title="实验配置二次确认" onClose={() => setSummaryOpen(false)} wide>
          <div className="workflow-summary-grid">
            <article><UserRound size={18} /><h4>患者与实验</h4><p>{patient.patientName} · {patient.patientId}</p><p>{patient.experimentId}</p></article>
            <article><Zap size={18} /><h4>刺激方案</h4><p>{paradigmId} / {STIMULATION_MODES[modeId].label}</p><p>{Object.entries(parameters).map(([key, value]) => `${paradigm.parameters[key].label} ${value}${paradigm.parameters[key].unit}`).join("；")}</p></article>
            <article><Activity size={18} /><h4>电极与采集</h4><p>{Object.entries(stimulationAssignments).map(([point, roleId]) => `${point}·${getRoleDisplayLabel(paradigmId, modeId, roleId)}`).join("、")}</p><p>EEG：{acquisitionPoints.join("、")}；REF {referencePoint}；GND {groundPoint}</p></article>
            <article><ClipboardCheck size={18} /><h4>安全校验</h4><p>患者耐受记录：{patientToleranceRecord?.id || "未匹配"}</p><p>阈值 {patientToleranceRecord?.threshold ?? "—"} {patientToleranceRecord?.unit || ""}；刺激与采集阻抗均已通过。</p></article>
          </div>
          <button type="button" className="workflow-primary workflow-full-button" onClick={confirmSummary}><Check size={17} /> 确认本次实验配置</button>
        </Modal>
      )}
    </main>
  );
}
