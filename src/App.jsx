import { useEffect, useMemo, useRef, useState } from "react";
import { CircleAlert, CircleArrowRight, Minus, Plus, RotateCcw, Settings, Square, Wifi, X } from "lucide-react";
import {
  MAX_DURATION_MS,
  MAX_CYCLE_COUNT,
  MIN_DURATION_MS,
  MIN_CYCLE_COUNT,
  canEditPhaseDuration,
  formatDurationForInput,
  formatDurationLabel,
  formatElapsedTime,
  getAutoRecoveryTransition,
  getPhaseDelayMs,
  getRunStateAfterModeSwitch,
  normalizeCycleCount,
  normalizeDurationInput,
  toMilliseconds,
} from "./experimentTiming.js";
import {
  applyPointClick,
  createNewExperimentPointAssignments,
  getPointDisplayLabel,
  getPointVisualState,
  getRowsForRole,
  getSelectedTagsForRole,
  hasMeasuredForRole,
  markRoleMeasured,
  removePointAssignment,
} from "./pointState.js";

const headPoints = [
  ["FP1", 292, 540], ["FP2", 442, 540],
  ["F7", 207, 404], ["F3", 260, 452], ["Fz", 367, 489], ["F4", 486, 452], ["F8", 537, 404],
  ["T3", 147, 322], ["C3", 260, 322], ["Cz", 367, 322], ["C4", 486, 322], ["T4", 599, 322],
  ["T5", 194, 233], ["P3", 247, 179], ["Pz", 367, 171], ["P4", 486, 187], ["T6", 537, 237],
  ["O1", 291, 102], ["Oz", 367, 62], ["O2", 442, 102],
];

const POINT_RESULTS = {
  FP1: [7.2, "优", "excellent"], FP2: [9.4, "优", "excellent"],
  F7: [12.2, "良", "good"], F3: [34, "差", "poor"], Fz: [8.7, "优", "excellent"], F4: [45, "不良", "bad"], F8: [13, "良", "good"],
  T3: [6.9, "优", "excellent"], C3: [11.4, "良", "good"], Cz: [21, "中", "medium"], C4: [7.6, "优", "excellent"], T4: [8.2, "优", "excellent"],
  T5: [18.1, "良", "good"], P3: [8.1, "优", "excellent"], Pz: [4, "优", "excellent"], P4: [9, "优", "excellent"], T6: [8.5, "优", "excellent"],
  O1: [7.7, "优", "excellent"], Oz: [22, "中", "medium"], O2: [9.6, "优", "excellent"],
};

const legendRows = [
  ["≤110 kΩ", "优", "excellent"],
  ["10～20 kΩ", "良", "good"],
  ["20～30 kΩ", "中", "medium"],
  ["30～40 kΩ", "差", "poor"],
  [">40 kΩ", "不良", "bad"],
];

const roleOptions = [
  {
    id: "acquisition",
    label: "采集电极",
    activeIcon: "/assets/role-acquisition-active.svg",
    inactiveIcon: "/assets/role-acquisition-inactive.svg",
  },
  {
    id: "stimulation",
    label: "刺激电极",
    activeIcon: "/assets/role-stimulation-active.svg",
    inactiveIcon: "/assets/role-stimulation-inactive.svg",
  },
];

const stimModes = [
  { id: "tDCS", label: "tDCs" },
  { id: "tACS", label: "tACS" },
  { id: "TI", label: "TI" },
  { id: "HD", label: "HD" },
];

const experimentSignals = [
  ["Fz", "/assets/experiment-wave-fz.svg"],
  ["Cz", "/assets/experiment-wave-cz.svg"],
  ["Pz", "/assets/experiment-wave-pz.svg"],
  ["Oz", "/assets/experiment-wave-oz.svg"],
  ["F3", "/assets/experiment-wave-f3.svg"],
];

const experimentPhases = {
  standby: { label: "待机中", panelLabel: "待机中", activeIndex: -1, ring: 78 },
  acquisition: { label: "采集进行中", panelLabel: "采集进行中", activeIndex: 0, ring: 18 },
  blanking: { label: "消隐", panelLabel: "消隐中", activeIndex: 1, ring: 38 },
  stimReady: { label: "刺激待开始", panelLabel: "刺激待开始", activeIndex: 2, ring: 52 },
  stimulation: { label: "刺激中", panelLabel: "刺激中", activeIndex: 2, ring: 72 },
  recovery: { label: "恢复进行中", panelLabel: "恢复中", activeIndex: 3, ring: 90 },
  finished: { label: "已完成", panelLabel: "已完成", activeIndex: 4, ring: 100 },
  stopped: { label: "已急停", panelLabel: "已急停", activeIndex: -1, ring: 0 },
};

const phaseOrder = ["acquisition", "blanking", "stimReady", "stimulation", "recovery", "finished"];

const phaseAutoAdvance = {
  acquisition: { next: "blanking" },
  blanking: { next: "stimReady" },
  stimReady: { next: "stimulation" },
  stimulation: { next: "recovery" },
  recovery: { next: "finished" },
};

const timelineStages = [
  { id: "acquisition", label: "采集", duration: "1000", unit: "ms", bar: "10000 ms", unitSelectable: true },
  { id: "blanking", label: "消隐", duration: "10", unit: "ms", bar: "500ms", unitSelectable: false },
  { id: "stimulation", label: "刺激", duration: "10", unit: "ms", bar: "1200s", unitSelectable: true },
  { id: "recovery", label: "恢复", duration: "10", unit: "ms", bar: "300s", unitSelectable: false },
];

const exportSummaryRows = [
  ["实验ID：", "EXP-20260706-001"],
  ["被试ID：", "1.50 mA"],
  ["运行时长：", "00:00:14"],
  ["刺激模式：", "tDCS"],
  ["异常事件数：", "0"],
];

const experimentHistoryRecords = [
  { time: "2026/09/01 12:23:21", id: "AA:1C:B0:A8:84:D7", patient: "李淑萍", type: "tDCs", therapist: "何天添" },
  { time: "2026/09/01 12:23:21", id: "18:38:9B:F1:6E:AE", patient: "王昊", type: "tDCs", therapist: "何咳" },
  { time: "2026/09/01 12:23:21", id: "05:3A:55:B4:45:3A", patient: "周志慧", type: "tDCs", therapist: "何宏伟" },
];

const toleranceHistoryRecords = [
  { id: "tolerance-history-1", time: "2026/09/01 12:23:21", current: 0.03, operator: "何天添" },
  { id: "tolerance-history-2", time: "2026/09/01 12:23:21", current: 0.03, operator: "何咳" },
  { id: "tolerance-history-3", time: "2026/09/01 12:23:21", current: 0.03, operator: "何宏伟" },
];

function WavePreview({ mode }) {
  const isTi = mode === "TI";
  const isTacs = mode === "tACS";
  const isTdcs = mode === "tDCS";
  return (
    <div className={`wave-preview wave-${mode.toLowerCase()}`} aria-label={`${mode} 波形预览`}>
      <div className="wave-grid">
        {[2.5, 2, 1.5, 1, 0.5, 0].map((tick) => <span key={tick}>{tick}</span>)}
      </div>
      {isTdcs ? (
        <img className="wave-source-asset" src="/assets/tdcs-wave-source.svg" alt="" aria-hidden="true" />
      ) : isTi ? (
        <img className="wave-source-asset" src="/assets/ti-wave-source.svg" alt="" aria-hidden="true" />
      ) : isTacs ? (
        <img className="wave-source-asset" src="/assets/tacs-wave-source.svg" alt="" aria-hidden="true" />
      ) : (
        <svg viewBox="0 0 312 120" preserveAspectRatio="none" aria-hidden="true">
        </svg>
      )}
      <div className="wave-x-axis">
        {[0, 1, 2, 3, 4, 5, 6].map((tick) => <span key={tick}>{tick}</span>)}
      </div>
    </div>
  );
}

function ParameterSlider({ id, label, value, min, max, step, unit, onChange, className = "", visualPercent }) {
  const percent = visualPercent ?? ((value - min) / (max - min)) * 100;
  return (
    <div className={`stim-control-row ${className}`}>
      <label htmlFor={id}>{label}</label>
      <div className="slider-track" style={{ "--value-percent": `${percent}%` }}>
        <input
          id={id}
          type="range"
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          aria-label={`${label}滑块`}
        />
      </div>
      <div className="value-input">
        <input
          aria-label={`${label}数值`}
          inputMode="decimal"
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
        <span>{unit}</span>
      </div>
    </div>
  );
}

function makeDemoPassedAssignments(assignments) {
  return Object.fromEntries(Object.entries(assignments).map(([label, assignment]) => [
    label,
    {
      ...assignment,
      value: assignment.value > 10 ? 8.5 : assignment.value,
      statusLabel: "优",
      tone: "excellent",
      measured: true,
    },
  ]));
}

function BrandHomeButton({ onHome }) {
  return (
    <button className="brand-home" type="button" onClick={onHome} aria-label="返回首页" title="返回首页">
      <img className="brand-mark" src="/assets/eeg-tes-logo.svg" alt="EEG+tES" />
    </button>
  );
}

function AppHeader({ mode = "user", nextLabel = "下一步：耐受测试", canGoNext = false, onNext, onHome, demoAllPassed = false, onDemoAllPassedChange }) {
  return (
    <header className="topbar">
      <BrandHomeButton onHome={onHome} />
      {mode === "plain" ? null : mode === "setup" ? (
        <button className="setup-next" type="button" onClick={onNext}>
          <img src="/assets/next-arrow.svg" alt="" />
          <span>{nextLabel}</span>
        </button>
      ) : mode === "next" ? (
        <div className="topbar-next-group">
          <label className="demo-pass-toggle">
            <input
              type="checkbox"
              checked={demoAllPassed}
              onChange={(event) => onDemoAllPassedChange?.(event.target.checked)}
            />
            <span />
            测试点位全通过
          </label>
          <button className="topbar-next" type="button" disabled={!canGoNext} onClick={onNext}>
            <img src="/assets/next-arrow.svg" alt="" />
            <span>{nextLabel}</span>
          </button>
        </div>
      ) : (
        <div className="avatar" aria-label="当前用户 TA"><span>TA</span><i /></div>
      )}
    </header>
  );
}

function HomeScreen({ onNewExperiment, onExperimentHistory }) {
  const homeCards = [
    {
      id: "new",
      title: "新建实验",
      description: "创建并配置新的脑机实验",
      image: "/assets/home-new-experiment.png",
      onClick: onNewExperiment,
    },
    {
      id: "history",
      title: "实验记录",
      description: "查看历史实验记录",
      image: "/assets/home-records.png",
      onClick: onExperimentHistory,
    },
    {
      id: "placeholder-one",
      title: "占位",
      description: "占位占位占位占位",
      image: "/assets/home-records.png",
      disabled: true,
    },
    {
      id: "placeholder-two",
      title: "占位",
      description: "占位占位占位占位",
      image: "/assets/home-records.png",
      disabled: true,
    },
  ];

  return (
    <main className="home-shell">
      <video
        className="home-background-video"
        poster="/assets/neural-lines.png"
        muted
        loop
        autoPlay
        playsInline
        aria-hidden="true"
        data-video-slot="home-background"
      >
        <source src="/assets/home-background.mp4" type="video/mp4" />
      </video>
      <div className="home-background-wash" aria-hidden="true" />

      <header className="home-header">
        <img src="/assets/home-header-logo.svg" alt="EEG-tES" />
        <div className="home-avatar" aria-label="当前用户 TA"><span>TA</span><i /></div>
      </header>

      <section className="home-welcome" aria-labelledby="home-title">
        <div className="home-title" id="home-title">
          <strong>欢迎使用</strong>
          <img src="/assets/home-title-logo.svg" alt="EEG-tES" />
          <strong>脑机实验系统</strong>
        </div>
        <p>一体化脑电与经颅电刺激科研平台</p>
      </section>

      <section className="home-entry-grid" aria-label="系统功能入口">
        {homeCards.map((card) => {
          const content = (
            <>
              <img className="home-card-image" src={card.image} alt="" aria-hidden="true" />
              <span className="home-card-copy">
                <strong>{card.title}</strong>
                <small>{card.description}</small>
              </span>
            </>
          );

          return card.disabled ? (
            <div className="home-entry-card is-disabled" aria-disabled="true" key={card.id}>{content}</div>
          ) : (
            <button className="home-entry-card" type="button" onClick={card.onClick} key={card.id}>{content}</button>
          );
        })}
      </section>

      <footer className="home-footer">
        <span>杭州南粟科技有限公司</span>
        <span>Brain • Science• Integration</span>
      </footer>
    </main>
  );
}

function ExperimentSetup({ subjectId, setSubjectId, note, setNote, importedFile, setImportedFile, initialTab, onNext, onHome }) {
  const [showError, setShowError] = useState(false);
  const [activeTab, setActiveTab] = useState(initialTab);

  function continueToElectrodes() {
    if (!subjectId.trim()) {
      setShowError(true);
      return;
    }
    setShowError(false);
    onNext();
  }

  return (
    <main className="app-shell setup-shell">
      <AppHeader
        mode={activeTab === "history" ? "plain" : "setup"}
        nextLabel="下一步：电极配置"
        onNext={continueToElectrodes}
        onHome={onHome}
      />
      <section className="setup-workspace">
        <div className="setup-card">
          <nav className="setup-tabs" aria-label="实验页面">
            <button className={activeTab === "new" ? "is-active" : ""} type="button" onClick={() => setActiveTab("new")}>新建实验</button>
            <button className={activeTab === "history" ? "is-active" : ""} type="button" onClick={() => setActiveTab("history")}>历史记录</button>
          </nav>

          {activeTab === "new" ? (
            <div className="setup-content">
              <section className="setup-section summary-section">
                <h2><img src="/assets/icon-article.svg" alt="" />实验摘要</h2>
                <div className="summary-grid">
                  <div><span>实验ID</span><strong>EXP-20260707-001</strong></div>
                  <div><span>日期</span><strong>2026-07-07 14:30</strong></div>
                </div>
              </section>

              <section className="setup-section subject-section">
                <h2><img src="/assets/icon-subject.svg" alt="" />被试信息</h2>
                <div className="form-row">
                  <label htmlFor="subject-id"><b>*</b>被试ID</label>
                  <input
                    id="subject-id"
                    className={showError ? "is-error" : ""}
                    value={subjectId}
                    onChange={(event) => { setSubjectId(event.target.value); setShowError(false); }}
                    placeholder="请输入被试ID，如 SUB001"
                  />
                </div>
                <div className="form-row">
                  <label htmlFor="experiment-note">备注</label>
                  <input id="experiment-note" value={note} onChange={(event) => setNote(event.target.value)} placeholder="可选，如首次采集" />
                </div>
              </section>

              <section className="setup-section import-section">
                <h2><img src="/assets/icon-package.svg" alt="" />导入实验参数包</h2>
                <div className="upload-zone">
                  <img className="upload-illustration" src="/assets/upload-illustration.png" alt="" />
                  <div>
                    <label className="file-button">
                      <img src="/assets/icon-upload.svg" alt="" />
                      <span>{importedFile ? importedFile.name : "选择文件"}</span>
                      <input type="file" accept=".expp" onChange={(event) => setImportedFile(event.target.files?.[0] || null)} />
                    </label>
                    <p>支持导入.expp 文件，快速复用已有实验配置</p>
                  </div>
                </div>
              </section>
            </div>
          ) : (
            <div className="history-table" role="table" aria-label="实验历史记录">
              <div className="history-table-head" role="row">
                {["实验记录时间", "实验ID", "患者姓名", "类型", "康复师姓名"].map((label) => <span role="columnheader" key={label}>{label}</span>)}
                <span className="history-action-cell" aria-hidden="true" />
              </div>
              {experimentHistoryRecords.map((record) => (
                <div className="history-table-row" role="row" key={record.id}>
                  <span role="cell">{record.time}</span>
                  <span role="cell">{record.id}</span>
                  <span role="cell">{record.patient}</span>
                  <span role="cell">{record.type}</span>
                  <span role="cell">{record.therapist}</span>
                  <span className="history-action-cell" role="cell">
                    <button type="button" aria-label={`查看 ${record.patient} 的实验记录`} title="查看实验记录">
                      <img src="/assets/icon-history-file-text.svg" alt="" />
                    </button>
                  </span>
                </div>
              ))}
            </div>
          )}
        </div>
      </section>
    </main>
  );
}

function ExperimentRun({ onBack, onHome }) {
  const [runMode, setRunMode] = useState("manual");
  const [phase, setPhase] = useState("standby");
  const [isExportPanelOpen, setIsExportPanelOpen] = useState(false);
  const [phaseDurationMs, setPhaseDurationMs] = useState({ acquisition: 1000, stimulation: 10 });
  const [phaseUnits, setPhaseUnits] = useState({ acquisition: "ms", stimulation: "ms" });
  const [durationDrafts, setDurationDrafts] = useState({ acquisition: "1000", stimulation: "10" });
  const [openUnitMenu, setOpenUnitMenu] = useState(null);
  const [elapsedMs, setElapsedMs] = useState(0);
  const [runStartedAt, setRunStartedAt] = useState(null);
  const [cycleCount, setCycleCount] = useState(1);
  const [cycleCountDraft, setCycleCountDraft] = useState("1");
  const [completedCycles, setCompletedCycles] = useState(0);
  const durationControlRefs = useRef({});
  const currentPhase = experimentPhases[phase] || experimentPhases.standby;
  const elapsed = formatElapsedTime(elapsedMs);
  const isFinished = phase === "finished";
  const isStopped = phase === "stopped";
  const isRunningIndicator = ["acquisition", "blanking", "stimulation", "recovery"].includes(phase);
  const isAutoMode = runMode === "auto";
  const canAutoStart = isAutoMode && phase === "standby";
  const canEditCycleCount = isAutoMode && phase === "standby";

  useEffect(() => {
    const autoAdvance = phaseAutoAdvance[phase];
    if (phase === "stimReady" && !isAutoMode) return undefined;
    if (!autoAdvance) return undefined;
    const delay = getPhaseDelayMs(phase, phaseDurationMs);
    if (delay === null) return undefined;
    const timer = window.setTimeout(() => {
      if (phase === "recovery" && isAutoMode) {
        const transition = getAutoRecoveryTransition(completedCycles, cycleCount);
        setCompletedCycles(transition.completedCycles);
        setPhase(transition.nextPhase);
        return;
      }
      setPhase(autoAdvance.next);
    }, delay);
    return () => window.clearTimeout(timer);
  }, [phase, isAutoMode, phaseDurationMs, completedCycles, cycleCount]);

  useEffect(() => {
    if (!runStartedAt) return undefined;
    if (isFinished || isStopped) {
      setElapsedMs(Date.now() - runStartedAt);
      setRunStartedAt(null);
      return undefined;
    }

    const updateElapsed = () => setElapsedMs(Date.now() - runStartedAt);
    updateElapsed();
    const timer = window.setInterval(updateElapsed, 250);
    return () => window.clearInterval(timer);
  }, [runStartedAt, isFinished, isStopped]);

  useEffect(() => {
    if (!openUnitMenu) return undefined;

    const closeOnOutsidePointer = (event) => {
      const activeControl = durationControlRefs.current[openUnitMenu];
      if (activeControl && !activeControl.contains(event.target)) setOpenUnitMenu(null);
    };
    const closeOnEscape = (event) => {
      if (event.key === "Escape") setOpenUnitMenu(null);
    };

    document.addEventListener("pointerdown", closeOnOutsidePointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [openUnitMenu]);

  useEffect(() => {
    if (phase !== "finished" && isExportPanelOpen) {
      setIsExportPanelOpen(false);
    }
  }, [phase, isExportPanelOpen]);

  function advancePhase() {
    if (phase === "standby") {
      const now = Date.now();
      setElapsedMs(0);
      setRunStartedAt(now);
      setCompletedCycles(0);
      setOpenUnitMenu(null);
      setPhase("acquisition");
      return;
    }
    const index = phaseOrder.indexOf(phase);
    setPhase(index >= 0 && index < phaseOrder.length - 1 ? phaseOrder[index + 1] : "finished");
  }

  function resetExperiment() {
    setIsExportPanelOpen(false);
    setElapsedMs(0);
    setRunStartedAt(null);
    setCompletedCycles(0);
    setOpenUnitMenu(null);
    setPhase("standby");
  }

  function changeRunMode(nextMode) {
    const nextRuntime = getRunStateAfterModeSwitch(runMode, nextMode, {
      phase,
      elapsedMs,
      runStartedAt,
      completedCycles,
      isExportPanelOpen,
    });
    if (nextRuntime.phase === phase && nextMode === runMode) return;

    setRunMode(nextMode);
    setPhase(nextRuntime.phase);
    setElapsedMs(nextRuntime.elapsedMs);
    setRunStartedAt(nextRuntime.runStartedAt);
    setCompletedCycles(nextRuntime.completedCycles);
    setIsExportPanelOpen(nextRuntime.isExportPanelOpen);
    setOpenUnitMenu(null);
  }

  function updateDurationDraft(stageId, value) {
    if (!canEditPhaseDuration(stageId, phase, runMode) || !/^\d*(?:\.\d{0,3})?$/.test(value)) return;
    setDurationDrafts((current) => ({ ...current, [stageId]: value }));

    const durationMs = toMilliseconds(value, phaseUnits[stageId]);
    if (Number.isFinite(durationMs) && durationMs >= MIN_DURATION_MS && durationMs <= MAX_DURATION_MS) {
      setPhaseDurationMs((current) => ({ ...current, [stageId]: Math.round(durationMs) }));
    }
  }

  function commitDuration(stageId) {
    const durationMs = normalizeDurationInput(durationDrafts[stageId], phaseUnits[stageId], phaseDurationMs[stageId]);
    setPhaseDurationMs((current) => ({ ...current, [stageId]: durationMs }));
    setDurationDrafts((current) => ({
      ...current,
      [stageId]: formatDurationForInput(durationMs, phaseUnits[stageId]),
    }));
  }

  function selectDurationUnit(stageId, unit) {
    if (!canEditPhaseDuration(stageId, phase, runMode)) return;
    const durationMs = phaseDurationMs[stageId];
    setPhaseUnits((current) => ({ ...current, [stageId]: unit }));
    setDurationDrafts((current) => ({ ...current, [stageId]: formatDurationForInput(durationMs, unit) }));
    setOpenUnitMenu(null);
  }

  function updateCycleCountDraft(value) {
    if (!canEditCycleCount || !/^\d{0,3}$/.test(value)) return;
    setCycleCountDraft(value);
    if (value !== "") {
      const nextCycleCount = normalizeCycleCount(value, cycleCount);
      if (Number(value) >= MIN_CYCLE_COUNT && Number(value) <= MAX_CYCLE_COUNT) {
        setCycleCount(nextCycleCount);
      }
    }
  }

  function commitCycleCount() {
    const nextCycleCount = normalizeCycleCount(cycleCountDraft, cycleCount);
    setCycleCount(nextCycleCount);
    setCycleCountDraft(String(nextCycleCount));
  }

  function stepCycleCount(delta) {
    if (!canEditCycleCount) return;
    const nextCycleCount = normalizeCycleCount(cycleCount + delta, cycleCount);
    setCycleCount(nextCycleCount);
    setCycleCountDraft(String(nextCycleCount));
  }

  function timelineDuration(stage) {
    return stage.unitSelectable
      ? formatDurationLabel(phaseDurationMs[stage.id], phaseUnits[stage.id])
      : stage.bar;
  }

  function stageStatus(index) {
    if (isStopped) return "已中断";
    if (isFinished) return "已完成";
    if (currentPhase.activeIndex === index) return "进行中";
    if (currentPhase.activeIndex > index || phase === "stimReady" && index < 2) return "已完成";
    return "待执行";
  }

  return (
    <main className="app-shell experiment-shell">
      <header className="experiment-topbar">
        <BrandHomeButton onHome={onHome} />
        <div className="experiment-mode-switch" role="tablist" aria-label="实验控制模式">
          <button type="button" className={runMode === "manual" ? "is-active" : ""} onClick={() => changeRunMode("manual")}>手动模式</button>
          <button type="button" className={runMode === "auto" ? "is-active" : ""} onClick={() => changeRunMode("auto")}>自动模式</button>
        </div>
      </header>

      <section className="experiment-workspace" aria-label="实验中">
        <div className="experiment-status-row" aria-label="设备状态">
          <button className="experiment-back" type="button" onClick={onBack}>
            <img src="/assets/arrow-left.svg" alt="" aria-hidden="true" />
            <span>返回电极配置</span>
          </button>
          {["通信链路正常", "USB 设备已连接", "电量 84%", "LSL 在线"].map((item) => (
            <span className="experiment-status-pill" key={item}><i />{item}</span>
          ))}
          <span className="experiment-status-pill is-limit">安全上限：0.04 mA</span>
        </div>

        <section className="signal-card" aria-label="实时信号监测">
          <header>
            <h2>实时信号监测</h2>
            <label className="interpolation-toggle">
              <input type="checkbox" defaultChecked />
              <span />
              在线插值预览
            </label>
          </header>
          <div className="signal-chart">
            <div className="signal-cursor"><span>Cz：3.3 μV</span></div>
            {experimentSignals.map(([label, src]) => (
              <div className="signal-row" key={label}>
                <strong>{label}</strong>
                <img src={src} alt="" aria-hidden="true" />
              </div>
            ))}
          </div>
          <div className="timeline-bar" aria-label="时序概览">
            <div className="timeline-label-row">
              {timelineStages.map((stage) => <strong className={`timeline-cell segment-${stage.id}`} key={`${stage.id}-label`}>{stage.label}</strong>)}
            </div>
            <div className="timeline-duration-row">
              {timelineStages.map((stage) => <span className={`timeline-cell duration-${stage.id}`} key={`${stage.id}-duration`}>{timelineDuration(stage)}</span>)}
            </div>
          </div>
        </section>

        <aside className={`run-panel is-${isExportPanelOpen ? "export" : phase} ${isAutoMode ? "is-auto-mode" : ""}`} aria-label={isExportPanelOpen ? "导出数据" : "运行监控"}>
          {isExportPanelOpen ? (
            <>
              <h2 className="export-panel-title">导出数据</h2>
              <div className="export-panel-body">
                <section className="export-summary-card" aria-label="实验摘要">
                  {exportSummaryRows.map(([label, value]) => (
                    <p className="export-summary-row" key={label}><span>{label}</span><strong>{value}</strong></p>
                  ))}
                </section>
                <section className="export-form-card" aria-label="导出设置">
                  <label className="export-filename-row">
                    <span className="export-field-label"><em>*</em>文件名</span>
                    <input value="20260714_111_EXP-20260706-001" readOnly aria-label="导出文件名" />
                  </label>
                  <div className="export-options" aria-label="导出内容">
                    <label className="export-checkbox-row">
                      <input type="checkbox" defaultChecked />
                      <span className="export-checkbox-control"><img src="/assets/icon-export-checkbox-check.svg" alt="" aria-hidden="true" /></span>
                      <span>导出EDF+ 脑电数据</span>
                    </label>
                    <label className="export-checkbox-row">
                      <input type="checkbox" defaultChecked />
                      <span className="export-checkbox-control"><img src="/assets/icon-export-checkbox-check.svg" alt="" aria-hidden="true" /></span>
                      <span>导出CSV故障日志</span>
                    </label>
                  </div>
                </section>
              </div>
              <button className="export-submit-button" type="button"><img src="/assets/icon-export-data-arrow-right-line.svg" alt="" aria-hidden="true" />导出数据</button>
            </>
          ) : (
            <>
              <section className="run-monitor-card">
                <div className="run-panel-heading">
                  <h2>运行监控</h2>
                  <span><Wifi size={12} />通信链路正常</span>
                </div>
                <div className="run-summary">
                  <div className="run-summary-list">
                    <p><img src="/assets/icon-run-stage.svg" alt="" aria-hidden="true" /><span>当前阶段：</span><strong>{currentPhase.label}</strong></p>
                    <p><img src="/assets/icon-run-current.svg" alt="" aria-hidden="true" /><span>刺激电流：</span><strong>1.50 mA</strong></p>
                    <p><img src="/assets/icon-run-impedance.svg" alt="" aria-hidden="true" /><span>阻抗状态：</span><strong>3.9 kΩ 平均</strong></p>
                    <p><img src="/assets/icon-run-duration.svg" alt="" aria-hidden="true" /><span>运行时长：</span><strong>{elapsed}</strong></p>
                    <p><img src="/assets/icon-run-alert.svg" alt="" aria-hidden="true" /><span>异常事件：</span><strong>{isStopped ? "急停触发" : "无"}</strong></p>
                  </div>
                  <div className={`run-ring ${isRunningIndicator ? "is-running-indicator" : ""}`} data-phase={phase} aria-label={currentPhase.panelLabel}>
                    <img className="run-ring-layer run-ring-inner" src="/assets/run-status-inner.svg" alt="" aria-hidden="true" />
                    <img className="run-ring-layer run-ring-outer" src="/assets/run-status-outer.svg" alt="" aria-hidden="true" />
                    <svg className="run-ring-wave" viewBox="0 0 37.5 26.0001" fill="none" aria-hidden="true">
                      <path
                        className="run-ring-wave-path"
                        d="M1.5 15H5.5L7.5 11.5L11 20L16.5 1.50002L20.5 24.5L25 11L28 15H36"
                        pathLength="1"
                      />
                    </svg>
                    <span className="run-ring-label">{currentPhase.panelLabel}</span>
                    <img className="run-ring-layer run-ring-rotor" src={isRunningIndicator ? "/assets/run-status-rotor-active.svg" : "/assets/run-status-rotor.svg"} alt="" aria-hidden="true" />
                  </div>
                </div>
              </section>

              <section className="run-params-card">
                <h2>刺激参数配置</h2>
                <div className="experiment-tags"><span>tDCS</span><span>1.5mA</span><span>30s 缓升</span><span>10min</span></div>
              </section>

              <section className="run-sequence-card">
                <h2>时序控制</h2>
                <div className="sequence-table">
                  <div className="sequence-head"><span>阶段</span><span>时长</span><span>{!isAutoMode && (phase === "standby" || phase === "stimReady") ? "操作" : "状态"}</span></div>
                  {timelineStages.map((stage, index) => {
                    const status = stageStatus(index);
                    const canStart = !isAutoMode && ((phase === "standby" && index === 0) || (phase === "stimReady" && index === 2));
                    const isRunning = currentPhase.activeIndex === index && !["standby", "stimReady", "finished", "stopped"].includes(phase);
                    const isConfigurable = stage.unitSelectable;
                    const canEditDuration = isConfigurable && canEditPhaseDuration(stage.id, phase, runMode);
                    const isLocked = isConfigurable && !canEditDuration;
                    const displayedDuration = isConfigurable ? durationDrafts[stage.id] : stage.duration;
                    const displayedUnit = isConfigurable ? phaseUnits[stage.id] : stage.unit;
                    return (
                      <div className={`sequence-row ${isRunning ? "is-running" : ""} ${status === "已完成" ? "is-done" : ""}`} key={stage.id}>
                        <div className="sequence-stage"><b className={index === 0 ? "is-wide" : ""}>{index + 1}</b><strong>{stage.label}</strong></div>
                        <div
                          className={`sequence-duration ${isConfigurable ? "is-selectable" : "is-disabled"} ${isLocked ? "is-locked" : ""}`}
                          ref={(node) => { durationControlRefs.current[stage.id] = node; }}
                          data-unit-control={stage.id}
                        >
                          <input
                            value={displayedDuration}
                            readOnly={!canEditDuration}
                            disabled={!canEditDuration}
                            inputMode={displayedUnit === "s" ? "decimal" : "numeric"}
                            min={displayedUnit === "s" ? 0.01 : MIN_DURATION_MS}
                            max={displayedUnit === "s" ? 60 : MAX_DURATION_MS}
                            onChange={(event) => updateDurationDraft(stage.id, event.target.value)}
                            onBlur={() => isConfigurable && commitDuration(stage.id)}
                            onKeyDown={(event) => {
                              if (event.key === "Enter") event.currentTarget.blur();
                              if (event.key === "Escape") {
                                setDurationDrafts((current) => ({
                                  ...current,
                                  [stage.id]: formatDurationForInput(phaseDurationMs[stage.id], phaseUnits[stage.id]),
                                }));
                                event.currentTarget.blur();
                              }
                            }}
                            aria-label={`${stage.label}时长，范围 10 毫秒到 60 秒`}
                          />
                          <button
                            type="button"
                            disabled={!canEditDuration}
                            aria-haspopup={isConfigurable ? "listbox" : undefined}
                            aria-expanded={isConfigurable ? openUnitMenu === stage.id : undefined}
                            aria-label={`${stage.label}单位${isConfigurable ? "下拉" : "不可编辑"}`}
                            onClick={() => canEditDuration && setOpenUnitMenu((current) => current === stage.id ? null : stage.id)}
                          >
                            <span>{displayedUnit}</span>
                            {isConfigurable && <img src="/assets/icon-sequence-arrow-down.svg" alt="" aria-hidden="true" />}
                          </button>
                          {isConfigurable && openUnitMenu === stage.id && (
                            <div className="sequence-unit-menu" role="listbox" aria-label={`${stage.label}时长单位`}>
                              {["ms", "s"].map((unit) => (
                                <button
                                  className={displayedUnit === unit ? "is-selected" : ""}
                                  type="button"
                                  role="option"
                                  aria-selected={displayedUnit === unit}
                                  key={unit}
                                  onClick={() => selectDurationUnit(stage.id, unit)}
                                >
                                  <span>{unit}</span>
                                  {displayedUnit === unit && <span className="sequence-unit-check" aria-hidden="true">✓</span>}
                                </button>
                              ))}
                            </div>
                          )}
                        </div>
                        {canStart ? (
                          <button className="sequence-start" type="button" onClick={advancePhase}>开始 <img src="/assets/icon-sequence-play-fill.svg" alt="" aria-hidden="true" /></button>
                        ) : (
                          <span className={`sequence-status status-${status}`}>
                            {status === "进行中" ? (
                              <>
                                <span>{status}</span>
                                <img className="sequence-loader" src="/assets/icon-sequence-loader.svg" alt="" aria-hidden="true" />
                              </>
                            ) : status === "已完成" ? (
                              <>
                                <img className="sequence-done-icon" src="/assets/icon-sequence-done-fill.svg" alt="" aria-hidden="true" />
                                <span>{status}</span>
                              </>
                            ) : (
                              <>
                                <img src="/assets/icon-sequence-time-fill.svg" alt="" aria-hidden="true" />
                                <span>{status}</span>
                              </>
                            )}
                          </span>
                        )}
                      </div>
                    );
                  })}
                </div>
              </section>

              {isAutoMode && (
                <section className="run-cycle-card" aria-label="刺激循环次数">
                  <h2>刺激循环次数</h2>
                  <div className="cycle-control-row">
                    <span>循环次数：</span>
                    <div className="cycle-number-control">
                      <div className={`cycle-input-shell ${canEditCycleCount ? "" : "is-locked"}`}>
                        <input
                          value={cycleCountDraft}
                          type="text"
                          inputMode="numeric"
                          disabled={!canEditCycleCount}
                          onChange={(event) => updateCycleCountDraft(event.target.value)}
                          onBlur={commitCycleCount}
                          onKeyDown={(event) => {
                            if (event.key === "Enter") event.currentTarget.blur();
                            if (event.key === "Escape") {
                              setCycleCountDraft(String(cycleCount));
                              event.currentTarget.blur();
                            }
                          }}
                          aria-label={`刺激循环次数，范围 ${MIN_CYCLE_COUNT} 到 ${MAX_CYCLE_COUNT}`}
                        />
                        <div className="cycle-stepper">
                          <button type="button" disabled={!canEditCycleCount || cycleCount >= MAX_CYCLE_COUNT} onClick={() => stepCycleCount(1)} aria-label="增加刺激循环次数"><img src="/assets/icon-hd-chevron-up.svg" alt="" aria-hidden="true" /></button>
                          <button type="button" disabled={!canEditCycleCount || cycleCount <= MIN_CYCLE_COUNT} onClick={() => stepCycleCount(-1)} aria-label="减少刺激循环次数"><img src="/assets/icon-hd-chevron-down.svg" alt="" aria-hidden="true" /></button>
                        </div>
                      </div>
                      <span>次</span>
                    </div>
                  </div>
                </section>
              )}

              <div className="run-panel-actions">
                {isFinished ? (
                  <button className="export-data-button" type="button" onClick={() => setIsExportPanelOpen(true)}><img src="/assets/icon-finish-experiment-check-fill.svg" alt="" aria-hidden="true" />结束实验</button>
                ) : isStopped ? (
                  <button className="restart-experiment-button" type="button" onClick={resetExperiment}><RotateCcw size={18} />重新进入待机</button>
                ) : (
                  <>
                    <button className="emergency-stop-button" type="button" onClick={() => setPhase("stopped")}><Square size={16} fill="currentColor" />紧急停止刺激</button>
                    {canAutoStart && (
                      <button className="auto-start-button" type="button" onClick={advancePhase}>
                        <span>开始</span>
                        <img src="/assets/icon-sequence-play-fill.svg" alt="" aria-hidden="true" />
                      </button>
                    )}
                  </>
                )}
              </div>
            </>
          )}
        </aside>

      </section>
    </main>
  );
}

export function App() {
  const [screen, setScreen] = useState(() => {
    const requestedScreen = new URLSearchParams(window.location.search).get("screen");
    return ["home", "setup", "electrodes", "experiment"].includes(requestedScreen) ? requestedScreen : "home";
  });
  const [setupInitialTab, setSetupInitialTab] = useState(() => (
    new URLSearchParams(window.location.search).get("tab") === "history" ? "history" : "new"
  ));
  const [subjectId, setSubjectId] = useState("");
  const [note, setNote] = useState("");
  const [importedFile, setImportedFile] = useState(null);
  const [role, setRole] = useState("acquisition");
  const [checking, setChecking] = useState(false);
  const [checkedAt, setCheckedAt] = useState(0);
  const [stimWorkflowStage, setStimWorkflowStage] = useState("closed");
  const [toleranceCurrent, setToleranceCurrent] = useState(0.04);
  const [toleranceHistoryOpen, setToleranceHistoryOpen] = useState(false);
  const [polarity, setPolarity] = useState("C");
  const [pointAssignments, setPointAssignments] = useState({});
  const [showAcquisition, setShowAcquisition] = useState(true);
  const [showStimulation, setShowStimulation] = useState(true);
  const [stimMode, setStimMode] = useState("tDCS");
  const [stimModeOpen, setStimModeOpen] = useState(false);
  const [tiChannel, setTiChannel] = useState("A");
  const [targetCurrent, setTargetCurrent] = useState(1.5);
  const [rampTime, setRampTime] = useState(3);
  const [totalDuration, setTotalDuration] = useState(6);
  const [tacsFrequency, setTacsFrequency] = useState(10);
  const [tacsCurrent, setTacsCurrent] = useState(2);
  const [tacsPhase, setTacsPhase] = useState(3);
  const [carrierFrequencyA, setCarrierFrequencyA] = useState(980);
  const [carrierCurrentA, setCarrierCurrentA] = useState(1.8);
  const [carrierFrequencyB, setCarrierFrequencyB] = useState(980);
  const [carrierCurrentB, setCarrierCurrentB] = useState(1.8);
  const [hdDefaultCurrent, setHdDefaultCurrent] = useState(0.5);
  const [hdCurrents, setHdCurrents] = useState([2, 2, 2, 2, 2, 2, 2]);
  const [demoAllPassed, setDemoAllPassed] = useState(false);
  const stimulusStage = "electrodes";

  const effectivePointAssignments = useMemo(() => {
    return demoAllPassed ? makeDemoPassedAssignments(pointAssignments) : pointAssignments;
  }, [demoAllPassed, pointAssignments]);

  const assignedPoints = Object.values(effectivePointAssignments);
  const allAssignedPointsPassed = assignedPoints.length > 0 && assignedPoints.every((assignment) => assignment.measured && assignment.tone === "excellent");
  const acquisitionResultValid = hasMeasuredForRole(effectivePointAssignments, "acquisition");
  const stimulationResultValid = hasMeasuredForRole(effectivePointAssignments, "stimulation");
  const canEnterExperiment = stimulationResultValid && allAssignedPointsPassed;

  const rows = useMemo(() => {
    return getRowsForRole(headPoints, effectivePointAssignments, role);
  }, [checkedAt, effectivePointAssignments, role]);

  const selectedTags = useMemo(() => {
    return getSelectedTagsForRole(headPoints, effectivePointAssignments, role);
  }, [effectivePointAssignments, role]);

  const distanceConflict = effectivePointAssignments.Pz?.role === "stimulation" && effectivePointAssignments.P4?.role === "acquisition";
  const hasHighImpedance = rows.some((row) => Number.parseFloat(row.value) > 10);
  const roleConfigIssued = role === "acquisition" ? acquisitionResultValid : stimulationResultValid;

  function runImpedanceCheck(targetRole = role) {
    if (checking) return;
    setDemoAllPassed(false);
    setPointAssignments((current) => markRoleMeasured(current, targetRole));
    setChecking(true);
    window.setTimeout(() => {
      setCheckedAt((value) => value + 1);
      setChecking(false);
    }, 700);
  }

  function activateRole(nextRole) {
    setRole(nextRole);
  }

  function changeToleranceCurrent(delta) {
    setToleranceCurrent((value) => Number(clampNumber(value + delta, 0, 2, value).toFixed(2)));
  }

  function completeToleranceTest() {
    setStimWorkflowStage("closed");
    runImpedanceCheck("stimulation");
  }

  function clampNumber(value, min, max, fallback) {
    const next = Number.parseFloat(value);
    if (Number.isNaN(next)) return fallback;
    return Math.min(max, Math.max(min, next));
  }

  function updateHdCurrent(index, value) {
    setHdCurrents((current) => {
      const next = [...current];
      next[index] = clampNumber(value, 0, 2.5, current[index]);
      return next;
    });
  }

  function applyHdDefaultToAll() {
    const nextValue = clampNumber(hdDefaultCurrent, 0, 2.5, 0.5);
    setHdDefaultCurrent(nextValue);
    setHdCurrents(Array.from({ length: 7 }, () => nextValue));
  }

  function startNewExperiment() {
    setRole("acquisition");
    setPolarity("C");
    setPointAssignments(createNewExperimentPointAssignments());
    setDemoAllPassed(false);
    setChecking(false);
    setCheckedAt(0);
    setStimWorkflowStage("closed");
    setToleranceHistoryOpen(false);
    setShowAcquisition(true);
    setShowStimulation(true);
    setScreen("electrodes");
  }

  const tiCarrierFrequency = tiChannel === "A" ? carrierFrequencyA : carrierFrequencyB;
  const tiCarrierCurrent = tiChannel === "A" ? carrierCurrentA : carrierCurrentB;
  const usesTiDesignDefaults = carrierFrequencyA === 980 && carrierFrequencyB === 980 && carrierCurrentA === 1.8 && carrierCurrentB === 1.8;
  const interferenceFrequency = usesTiDesignDefaults ? 40 : Math.abs(carrierFrequencyB - carrierFrequencyA);
  const envelopeCurrent = usesTiDesignDefaults ? 1.5 : Math.min(carrierCurrentA, carrierCurrentB);
  const activeStimMode = stimModes.find((mode) => mode.id === stimMode) || stimModes[0];

  if (screen === "home") {
    return (
      <HomeScreen
        onNewExperiment={() => {
          setSetupInitialTab("new");
          setScreen("setup");
        }}
        onExperimentHistory={() => {
          setSetupInitialTab("history");
          setScreen("setup");
        }}
      />
    );
  }

  if (screen === "setup") {
    return (
      <ExperimentSetup
        subjectId={subjectId}
        setSubjectId={setSubjectId}
        note={note}
        setNote={setNote}
        importedFile={importedFile}
        setImportedFile={setImportedFile}
        initialTab={setupInitialTab}
        onNext={startNewExperiment}
        onHome={() => setScreen("home")}
      />
    );
  }

  if (screen === "experiment") {
    return <ExperimentRun onBack={() => setScreen("electrodes")} onHome={() => setScreen("home")} />;
  }

  return (
    <main className="app-shell electrode-shell">
      <AppHeader
        mode="next"
        nextLabel={canEnterExperiment ? "下一步：进行实验" : "下一步：耐受测试"}
        canGoNext={canEnterExperiment}
        onNext={() => setScreen("experiment")}
        onHome={() => setScreen("home")}
        demoAllPassed={demoAllPassed}
        onDemoAllPassedChange={setDemoAllPassed}
      />

      <section className="workspace">
        <img className="neural-background" src="/assets/neural-lines.png" alt="" aria-hidden="true" />
        <div className="page-meta">
          <button className="back-link" type="button" onClick={() => setScreen("home")}>
            <img src="/assets/arrow-left.svg" alt="" />
            <span>返回项目启动</span>
          </button>
          <div className="project-chip">
            <strong>电极配置与阻抗检查</strong>
            <span>{acquisitionResultValid ? "ID：1232131312414" : "实验ID：EXP-20260707-001"}</span>
          </div>
        </div>
        <div className="electrode-visibility" aria-label="点位显示筛选">
          <label>
            <input type="checkbox" checked={showAcquisition} onChange={(event) => setShowAcquisition(event.target.checked)} />
            <span>查看采集电极</span>
          </label>
          <label>
            <input type="checkbox" checked={showStimulation} onChange={(event) => setShowStimulation(event.target.checked)} />
            <span>查看刺激电极</span>
          </label>
        </div>
        <div id="head-model-stage" className="static-head-stage" aria-label="头模点位配置区域">
          <img className="static-head-image" src="/assets/head-static.png" alt="俯视头部模型" />
          {headPoints.map(([label, x, y]) => {
            const assignment = effectivePointAssignments[label];
            const isRoleMuted = assignment
              && (assignment.role === "acquisition" ? !showAcquisition : !showStimulation);
            const showPolarity = assignment?.role === "stimulation";
            const effectiveState = getPointVisualState(assignment);
            const hasDistanceError = distanceConflict && label === "P4";
            return (
              <button
                className={`electrode-point point-${effectiveState} ${isRoleMuted ? "is-role-muted" : ""} ${hasDistanceError ? "has-distance-error" : ""}`}
                style={{ left: x, top: y }}
                type="button"
                key={label}
                aria-label={`${getPointDisplayLabel(label, assignment)} ${effectiveState}`}
                onClick={() => {
                  setDemoAllPassed(false);
                  setPointAssignments((current) => applyPointClick(current, {
                    label,
                    role,
                    polarity,
                    result: POINT_RESULTS[label],
                  }));
                }}
              >
                <span>{label}</span>
                {showPolarity && <><i /><small>{assignment.polarity}</small></>}
              </button>
            );
          })}
        </div>

        {distanceConflict && (
          <div className="distance-warning" role="alert">
            <img className="distance-caret" src="/assets/tooltip-caret.svg" alt="" />
            <div><img src="/assets/warning.svg" alt="" />刺激点 <strong>PZ•{effectivePointAssignments.Pz.polarity}</strong> 与 采集点<strong>P4</strong> 距离过近，请调整</div>
          </div>
        )}

        <section className={`impedance-legend ${role === "stimulation" ? "is-stimulation" : ""}`} aria-label="阻抗图例">
          <h3>阻抗图例</h3>
          <div>
            {legendRows.map(([range, label, tone]) => (
              <p key={range}><i className={`status-dot ${tone}`} /><span>{range}</span><b>{label}</b></p>
            ))}
          </div>
        </section>

        <aside className={`control-panel ${role === "stimulation" ? "is-stimulation" : ""}`} aria-label="电极配置操作板">
          <section className="role-section">
            <div className="role-switch" role="tablist" aria-label="选择通道角色">
              {roleOptions.map((option) => (
                <button
                  key={option.id}
                  className={`role-button ${role === option.id ? "is-active" : ""}`}
                  type="button"
                  role="tab"
                  aria-selected={role === option.id}
                  onClick={() => activateRole(option.id)}
                >
                  <span className={`role-button-icon is-${option.id}`} aria-hidden="true">
                    <img
                      src={role === option.id ? option.activeIcon : option.inactiveIcon}
                      alt=""
                    />
                  </span>
                  <span>{option.label}</span>
                </button>
              ))}
            </div>
            {role === "stimulation" && (
              <div className="polarity-switch" role="group" aria-label="选择刺激电极极性">
                <button className={polarity === "A" ? "is-active" : ""} type="button" onClick={() => setPolarity("A")}>
                  <span>阳极</span>
                  {polarity === "A" && <span className="polarity-check" aria-hidden="true" />}
                </button>
                <button className={polarity === "C" ? "is-active" : ""} type="button" onClick={() => setPolarity("C")}>
                  <span>阴极</span>
                  {polarity === "C" && <span className="polarity-check" aria-hidden="true" />}
                </button>
              </div>
            )}
            <div className="role-heading">
              <h2>通道角色</h2>
              <p className="helper-text"><img src="/assets/warning.svg" alt="" />点击头模点位赋予所选角色{!roleConfigIssued && <><span>：</span><b>{role === "acquisition" ? "采集电极" : polarity === "A" ? "刺激阳极" : "刺激阴极"}</b></>}</p>
            </div>
            {!!selectedTags.length && (
              <div className="selected-tags" aria-label="已选择点位">
                {selectedTags.map((tag) => (
                  <button
                    type="button"
                    key={tag.label}
                    className="selected-tag"
	                    onClick={() => {
	                      setDemoAllPassed(false);
	                      setPointAssignments((current) => {
	                        return removePointAssignment(current, tag.label);
	                      });
                    }}
                    aria-label={`移除 ${tag.text}`}
                  >
                    <span>{tag.text}</span><i>×</i>
                  </button>
                ))}
              </div>
            )}
            <div className="panel-divider" />
          </section>

          {role === "stimulation" && stimulusStage === "parameters" ? (
            <section className="stim-params-section">
              <div className="section-heading">
                <h3>刺激参数配置</h3>
                <div className={`mode-dropdown ${stimModeOpen ? "is-open" : ""}`}>
                  <button
                    className="mode-select"
                    type="button"
                    aria-haspopup="listbox"
                    aria-expanded={stimModeOpen}
                    aria-label="选择刺激模式"
                    onClick={() => setStimModeOpen((value) => !value)}
                  >
                    <span>{activeStimMode.label}</span>
                    <img src="/assets/arrow-down-s-line.svg" alt="" aria-hidden="true" />
                  </button>
                  {stimModeOpen && (
                    <div className="mode-menu" role="listbox" aria-label="刺激模式列表">
                      {stimModes.map((mode) => (
                        <button
                          key={mode.id}
                          type="button"
                          role="option"
                          aria-selected={stimMode === mode.id}
                          className={stimMode === mode.id ? "is-active" : ""}
                          onClick={() => {
                            setStimMode(mode.id);
                            setStimModeOpen(false);
                          }}
                        >
                          {mode.label}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
              {stimMode !== "HD" && <WavePreview mode={stimMode} />}
              {stimMode === "TI" && (
                <div className="ti-summary-cards" aria-label="TI 关键参数">
                  <div className="ti-summary-card">
                    <img className="ti-card-icon" src="/assets/icon-ti-interference-frequency.svg" alt="" />
                    <div><p>干涉频率</p><strong>{interferenceFrequency} Hz</strong></div>
                  </div>
                  <div className="ti-card-divider" />
                  <div className="ti-summary-card">
                    <img className="ti-card-icon" src="/assets/icon-ti-envelope-current.svg" alt="" />
                    <div><p>包络电流</p><strong>{envelopeCurrent.toFixed(2)} MA</strong></div>
                  </div>
                </div>
              )}
              {stimMode === "TI" && (
                <div className="ti-channel-card">
                  <div className="ti-tabs" role="tablist" aria-label="TI 通道">
                    {["A", "B"].map((channel) => (
                      <button
                        key={channel}
                        type="button"
                        role="tab"
                        aria-selected={tiChannel === channel}
                        className={tiChannel === channel ? "is-active" : ""}
                        onClick={() => setTiChannel(channel)}
                      >
                        通道 {channel}
                      </button>
                    ))}
                  </div>
                  <div className="stim-control-card ti-control-card">
                    <ParameterSlider
                      id="carrier-frequency"
                      label="载波频率"
                      min={900}
                      max={1100}
                      step={1}
                      unit="Hz"
                      value={tiCarrierFrequency}
                      onChange={(value) => {
                        const next = clampNumber(value, 900, 1100, tiCarrierFrequency);
                        tiChannel === "A" ? setCarrierFrequencyA(next) : setCarrierFrequencyB(next);
                      }}
                    />
                    <ParameterSlider
                      id="carrier-current"
                      label="电流"
                      min={0}
                      max={2.5}
                      step={0.1}
                      unit="mA"
                      value={tiCarrierCurrent}
                      onChange={(value) => {
                        const next = clampNumber(value, 0, 2.5, tiCarrierCurrent);
                        tiChannel === "A" ? setCarrierCurrentA(next) : setCarrierCurrentB(next);
                      }}
                    />
                  </div>
                </div>
              )}
              {stimMode === "tDCS" && (
                <div className="stim-control-card">
                  <ParameterSlider
                    id="target-current"
                    label="目标电流"
                    min={0}
                    max={2.5}
                    step={0.1}
                    unit="mA"
                    value={targetCurrent}
                    onChange={(value) => setTargetCurrent(clampNumber(value, 0, 2.5, targetCurrent))}
                  />
                  <ParameterSlider
                    id="ramp-time"
                    label="缓升时间"
                    min={0}
                    max={6}
                    step={0.5}
                    unit="S"
                    value={rampTime}
                    onChange={(value) => setRampTime(clampNumber(value, 0, 6, rampTime))}
                  />
                  <ParameterSlider
                    id="total-duration"
                    label="总时长"
                    min={1}
                    max={30}
                    step={1}
                    unit="min"
                    value={totalDuration}
                    onChange={(value) => setTotalDuration(clampNumber(value, 1, 30, totalDuration))}
                  />
                </div>
              )}
              {stimMode === "tACS" && (
                <div className="stim-control-card">
                  <ParameterSlider
                    id="tacs-frequency"
                    label="频率"
                    min={1}
                    max={80}
                    step={1}
                    unit="Hz"
                    value={tacsFrequency}
                    onChange={(value) => setTacsFrequency(clampNumber(value, 1, 80, tacsFrequency))}
                  />
                  <ParameterSlider
                    id="tacs-current"
                    label="电流"
                    min={0}
                    max={2.5}
                    step={0.1}
                    unit="mA"
                    value={tacsCurrent}
                    onChange={(value) => setTacsCurrent(clampNumber(value, 0, 2.5, tacsCurrent))}
                  />
                  <ParameterSlider
                    id="tacs-phase"
                    label="相位"
                    min={0}
                    max={360}
                    step={5}
                    unit="°"
                    value={tacsPhase}
                    onChange={(value) => setTacsPhase(clampNumber(value, 0, 360, tacsPhase))}
                  />
                </div>
              )}
              {stimMode === "HD" && (
                <div className="hd-config-panel" aria-label="HD 刺激参数配置">
                  <div className="hd-quick-row">
                    <div className="hd-default-input">
                      <input
                        aria-label="HD 默认电流数值"
                        inputMode="decimal"
                        value={hdDefaultCurrent}
                        onChange={(event) => setHdDefaultCurrent(clampNumber(event.target.value, 0, 2.5, hdDefaultCurrent))}
                      />
                      <div className="hd-stepper">
                        <button
                          type="button"
                          aria-label="增加 HD 默认电流"
                          onClick={() => setHdDefaultCurrent((value) => Number(clampNumber(value + 0.1, 0, 2.5, value).toFixed(1)))}
                        >
                          <img src="/assets/icon-hd-chevron-up.svg" alt="" aria-hidden="true" />
                        </button>
                        <button
                          type="button"
                          aria-label="减少 HD 默认电流"
                          onClick={() => setHdDefaultCurrent((value) => Number(clampNumber(value - 0.1, 0, 2.5, value).toFixed(1)))}
                        >
                          <img src="/assets/icon-hd-chevron-down.svg" alt="" aria-hidden="true" />
                        </button>
                      </div>
                    </div>
                    <button className="apply-all-button" type="button" onClick={applyHdDefaultToAll}>
                      <CircleArrowRight className="apply-icon" aria-hidden="true" />
                      <span>应用到全部</span>
                    </button>
                  </div>
                  <div className="hd-channel-list">
                    {hdCurrents.map((value, index) => (
                      <ParameterSlider
                        key={`hd-ch-${index + 1}`}
                        id={`hd-ch-${index + 1}`}
                        label={`Ch${index + 1}`}
                        min={0}
                        max={2.5}
                        step={0.1}
                        unit="mA"
                        value={value}
                        className="hd-control-row"
                        onChange={(nextValue) => updateHdCurrent(index, nextValue)}
                      />
                    ))}
                  </div>
                </div>
              )}
              <button className="confirm-params" type="button"><img src="/assets/check-double-line.svg" alt="" />确认参数</button>
            </section>
          ) : (
            <section className="impedance-section">
              <div className="section-heading">
                <h3>{role === "stimulation" ? "刺激阻抗状态" : acquisitionResultValid ? "采集阻抗状态" : "阻抗状态"}</h3>
                {(role === "stimulation" ? stimulationResultValid : acquisitionResultValid) && (
                  <button className={`impedance-refresh ${checking ? "is-checking" : ""}`} type="button" onClick={() => runImpedanceCheck(role)} aria-label="重新检测阻抗">
                    <img src="/assets/history.svg" alt="" />
                  </button>
                )}
              </div>
              <div className="impedance-list">
                {rows.map((row) => (
                  <div className="impedance-row" key={`${row.channel}-${row.tone}`}>
                    <div className="channel-name">
                      <i className={`status-dot ${row.tone}`} />
                      <strong>{row.channel}</strong>
                    </div>
                    <span className="reading">{row.value}</span>
                    <span className={`status-label ${row.tone}`}>{row.label}</span>
                  </div>
                ))}
                {!rows.length && (
                  <div className="empty-impedance">
                    <img src="/assets/empty-impedance.png" alt="" />
                    <strong>未开始检测</strong>
                    <span className="empty-copy">{role === "stimulation" ? <>请先点击头模点位，并完成参数配置与耐受测试<br />开始检测刺激阻抗状态</> : selectedTags.length ? "请先点击头模点位并下发采集配置，开始检测阻抗状态" : "请选择通道角色，并点击头模点位测试阻抗"}</span>
                  </div>
                )}
              </div>
            </section>
          )}
          {(role === "acquisition" || role === "stimulation") && (
            <button
              className="issue-config"
              type="button"
              disabled={!selectedTags.length}
              onClick={() => {
                if (role === "stimulation") {
                  setStimModeOpen(false);
                  setStimWorkflowStage("parameters");
                  return;
                }
                runImpedanceCheck("acquisition");
              }}
            >
              <img src="/assets/check-double-line.svg" alt="" />确认并下发{role === "stimulation" ? "刺激" : "采集"}配置
            </button>
          )}
        </aside>
      </section>

      {role === "stimulation" && stimWorkflowStage !== "closed" && (
        <div className="stim-workflow-overlay" role="presentation">
          <section
            className={`stim-workflow-modal ${stimWorkflowStage === "tolerance" ? "is-tolerance" : "is-parameters"} ${stimWorkflowStage === "parameters" && stimMode === "TI" ? "is-ti" : ""} ${stimWorkflowStage === "parameters" && stimMode === "HD" ? "is-hd" : ""} ${stimWorkflowStage === "parameters" && stimMode === "tACS" ? "is-tacs" : ""}`}
            role="dialog"
            aria-modal="true"
            aria-labelledby="stim-workflow-title"
          >
            <header className="stim-workflow-header">
              <h2 id="stim-workflow-title">参数配置与耐受测试</h2>
              <button
                type="button"
                aria-label="关闭参数配置与耐受测试"
                onClick={() => {
                  setStimModeOpen(false);
                  setToleranceHistoryOpen(false);
                  setStimWorkflowStage("closed");
                }}
              >
                <X aria-hidden="true" />
              </button>
            </header>

            <div className="stim-workflow-alert"><CircleAlert aria-hidden="true" />阻抗检测需完成参数配置和耐受测试</div>

            <div className={`stim-workflow-steps is-${stimWorkflowStage}`} aria-label="刺激阻抗检测步骤">
              <div className="stim-workflow-step is-parameters">
                <div className="step-title">
                  <i className={stimWorkflowStage === "tolerance" ? "is-complete-icon" : undefined}>
                    {stimWorkflowStage === "tolerance" ? <img src="/assets/icon-step-complete.svg" alt="" aria-hidden="true" /> : "1"}
                  </i>
                  <strong>刺激参数配置</strong>
                </div>
                <span>说明文字</span>
              </div>
              <div className="step-rule" />
              <div className="stim-workflow-step is-tolerance">
                <div className="step-title"><i>2</i><strong>刺激耐受测试</strong></div>
                <span>说明文字</span>
              </div>
            </div>

            {stimWorkflowStage === "parameters" ? (
              <div className={`stim-modal-parameters ${stimMode === "TI" ? "is-ti" : ""} ${stimMode === "HD" ? "is-hd" : ""} ${stimMode === "tACS" ? "is-tacs" : ""}`}>
                <div className="stim-type-select">
                  <span>类型选择：</span>
                  <div className={`stim-modal-mode-dropdown ${stimModeOpen ? "is-open" : ""}`}>
                    <button
                      className="stim-modal-mode-button"
                      type="button"
                      aria-haspopup="listbox"
                      aria-expanded={stimModeOpen}
                      aria-label="选择刺激类型"
                      onClick={() => setStimModeOpen((value) => !value)}
                    >
                      <span>{activeStimMode.label}</span>
                      <img src="/assets/arrow-down-s-line.svg" alt="" aria-hidden="true" />
                    </button>
                    {stimModeOpen && (
                      <div className="stim-modal-mode-menu" role="listbox" aria-label="刺激类型">
                        {stimModes.map((mode) => (
                          <button
                            key={mode.id}
                            type="button"
                            role="option"
                            aria-selected={stimMode === mode.id}
                            className={stimMode === mode.id ? "is-active" : ""}
                            onClick={() => {
                              setStimMode(mode.id);
                              setStimModeOpen(false);
                            }}
                          >
                            {mode.label}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
                {stimMode !== "HD" && stimMode !== "tACS" && <WavePreview mode={stimMode} />}
                {stimMode === "TI" ? (
                  <>
                    <div className="ti-summary-cards stim-modal-ti-summary" aria-label="TI 关键参数">
                      <div className="ti-summary-card">
                        <img className="ti-card-icon" src="/assets/icon-ti-interference-frequency.svg" alt="" />
                        <div><p>干涉频率</p><strong>{interferenceFrequency} Hz</strong></div>
                      </div>
                      <div className="ti-card-divider" />
                      <div className="ti-summary-card">
                        <img className="ti-card-icon" src="/assets/icon-ti-envelope-current.svg" alt="" />
                        <div><p>包络电流</p><strong>{envelopeCurrent.toFixed(2)} MA</strong></div>
                      </div>
                    </div>
                    <div className="stim-modal-ti-channels" aria-label="TI 通道参数">
                      <section className="stim-modal-ti-channel" aria-labelledby="ti-channel-a-title">
                        <h3 id="ti-channel-a-title">通道A</h3>
                        <div className="stim-modal-ti-controls">
                          <ParameterSlider
                            id="modal-carrier-frequency-a"
                            label="载波频率"
                            min={900}
                            max={1100}
                            step={1}
                            unit="Hz"
                            value={carrierFrequencyA}
                            onChange={(value) => setCarrierFrequencyA(clampNumber(value, 900, 1100, carrierFrequencyA))}
                          />
                          <ParameterSlider
                            id="modal-carrier-current-a"
                            label="电流"
                            min={0}
                            max={2.5}
                            step={0.1}
                            unit="mA"
                            value={carrierCurrentA}
                            onChange={(value) => setCarrierCurrentA(clampNumber(value, 0, 2.5, carrierCurrentA))}
                          />
                        </div>
                      </section>
                      <section className="stim-modal-ti-channel" aria-labelledby="ti-channel-b-title">
                        <h3 id="ti-channel-b-title">通道B</h3>
                        <div className="stim-modal-ti-controls">
                          <ParameterSlider
                            id="modal-carrier-frequency-b"
                            label="载波频率"
                            min={900}
                            max={1100}
                            step={1}
                            unit="Hz"
                            value={carrierFrequencyB}
                            onChange={(value) => setCarrierFrequencyB(clampNumber(value, 900, 1100, carrierFrequencyB))}
                          />
                          <ParameterSlider
                            id="modal-carrier-current-b"
                            label="电流"
                            min={0}
                            max={2.5}
                            step={0.1}
                            unit="mA"
                            value={carrierCurrentB}
                            onChange={(value) => setCarrierCurrentB(clampNumber(value, 0, 2.5, carrierCurrentB))}
                          />
                        </div>
                      </section>
                    </div>
                  </>
                ) : stimMode === "HD" ? (
                  <div className="hd-config-panel stim-modal-hd-panel" aria-label="HD 刺激参数配置">
                    <div className="hd-quick-row">
                      <div className="hd-default-input">
                        <input
                          aria-label="HD 默认电流数值"
                          inputMode="decimal"
                          value={hdDefaultCurrent}
                          onChange={(event) => setHdDefaultCurrent(clampNumber(event.target.value, 0, 2.5, hdDefaultCurrent))}
                        />
                        <div className="hd-stepper">
                          <button
                            type="button"
                            aria-label="增加 HD 默认电流"
                            onClick={() => setHdDefaultCurrent((value) => Number(clampNumber(value + 0.1, 0, 2.5, value).toFixed(1)))}
                          >
                            <img src="/assets/icon-hd-chevron-up.svg" alt="" aria-hidden="true" />
                          </button>
                          <button
                            type="button"
                            aria-label="减少 HD 默认电流"
                            onClick={() => setHdDefaultCurrent((value) => Number(clampNumber(value - 0.1, 0, 2.5, value).toFixed(1)))}
                          >
                            <img src="/assets/icon-hd-chevron-down.svg" alt="" aria-hidden="true" />
                          </button>
                        </div>
                      </div>
                      <button className="apply-all-button" type="button" onClick={applyHdDefaultToAll}>
                        <CircleArrowRight className="apply-icon" aria-hidden="true" />
                        <span>应用到全部</span>
                      </button>
                    </div>
                    <div className="hd-channel-list">
                      {hdCurrents.map((value, index) => (
                        <ParameterSlider
                          key={`modal-hd-ch-${index + 1}`}
                          id={`modal-hd-ch-${index + 1}`}
                          label={`Ch${index + 1}`}
                          min={0}
                          max={2.5}
                          step={0.1}
                          unit="mA"
                          value={value}
                          className="hd-control-row"
                          onChange={(nextValue) => updateHdCurrent(index, nextValue)}
                        />
                      ))}
                    </div>
                  </div>
                ) : stimMode === "tACS" ? (
                  <div className="stim-modal-tacs-panel" aria-label="tACS 刺激参数配置">
                    <WavePreview mode={stimMode} />
                    <div className="stim-modal-control-card is-tacs">
                      <ParameterSlider
                        id="modal-tacs-frequency"
                        label="频率"
                        min={1}
                        max={80}
                        step={1}
                        unit="Hz"
                        value={tacsFrequency}
                        visualPercent={30}
                        onChange={(value) => setTacsFrequency(clampNumber(value, 1, 80, tacsFrequency))}
                      />
                      <ParameterSlider
                        id="modal-tacs-current"
                        label="电流"
                        min={0}
                        max={2.5}
                        step={0.1}
                        unit="mA"
                        value={tacsCurrent}
                        visualPercent={30}
                        onChange={(value) => setTacsCurrent(clampNumber(value, 0, 2.5, tacsCurrent))}
                      />
                      <ParameterSlider
                        id="modal-tacs-phase"
                        label="相位"
                        min={0}
                        max={360}
                        step={5}
                        unit="°"
                        value={tacsPhase}
                        visualPercent={30}
                        onChange={(value) => setTacsPhase(clampNumber(value, 0, 360, tacsPhase))}
                      />
                    </div>
                  </div>
                ) : (
                  <div className="stim-modal-control-card is-tdcs">
                    <ParameterSlider
                      id="modal-target-current"
                      label="目标电流"
                      min={0}
                      max={2.5}
                      step={0.1}
                      unit="mA"
                      value={targetCurrent}
                      onChange={(value) => setTargetCurrent(clampNumber(value, 0, 2.5, targetCurrent))}
                    />
                    <ParameterSlider
                      id="modal-ramp-time"
                      label="缓升时间"
                      min={0}
                      max={6}
                      step={0.5}
                      unit="S"
                      value={rampTime}
                      onChange={(value) => setRampTime(clampNumber(value, 0, 6, rampTime))}
                    />
                  </div>
                )}
                <button className="stim-workflow-primary" type="button" onClick={() => {
                  setStimModeOpen(false);
                  setStimWorkflowStage("tolerance");
                }}>
                  <img src="/assets/check-double-line.svg" alt="" />确认参数配置
                </button>
              </div>
            ) : (
              <div className="stim-modal-tolerance">
                <div className="tolerance-card">
                  <div className="tolerance-warning"><CircleAlert aria-hidden="true" />请逐步递增，注意被试反馈</div>
                  <div className="tolerance-value-control">
                    <button type="button" aria-label="降低耐受测试电流" onClick={() => changeToleranceCurrent(-0.04)}><Minus aria-hidden="true" /></button>
                    <strong>{toleranceCurrent.toFixed(2)}<small>mA</small></strong>
                    <button type="button" aria-label="提高耐受测试电流" onClick={() => changeToleranceCurrent(0.04)}><Plus aria-hidden="true" /></button>
                  </div>
                  <p>测试上限 2 mA，当前步长 40 μA</p>
                </div>
                <div className="stim-tolerance-actions">
                  <button className="stim-import-history" type="button" onClick={() => setToleranceHistoryOpen(true)}>
                    <Settings className="gear-icon" aria-hidden="true" />导入历史数据
                  </button>
                  <button className="stim-workflow-primary" type="button" onClick={completeToleranceTest}>
                    <img src="/assets/check-double-line.svg" alt="" />确认阈值并进行阻抗检测
                  </button>
                </div>
              </div>
            )}
          </section>
          {toleranceHistoryOpen && (
            <div className="stim-history-overlay" role="presentation" onMouseDown={(event) => {
              if (event.target === event.currentTarget) setToleranceHistoryOpen(false);
            }}>
              <section className="stim-history-modal" role="dialog" aria-modal="true" aria-labelledby="stim-history-title">
                <header>
                  <h2 id="stim-history-title">导入历史数据</h2>
                  <button type="button" aria-label="关闭导入历史数据" onClick={() => setToleranceHistoryOpen(false)}>
                    <img src="/assets/icon-import-history-close.svg" alt="" aria-hidden="true" />
                  </button>
                </header>
                <div className="stim-history-table" role="table" aria-label="历史耐受测试数据">
                  <div className="stim-history-row is-head" role="row">
                    <span role="columnheader">数据记录时间</span>
                    <span role="columnheader">上次数据</span>
                    <span role="columnheader">记录人</span>
                    <span className="stim-history-action" aria-hidden="true" />
                  </div>
                  {toleranceHistoryRecords.map((record) => (
                    <div className="stim-history-row" role="row" key={record.id}>
                      <span role="cell">{record.time}</span>
                      <span role="cell">{record.current.toFixed(2)} mA</span>
                      <span role="cell">{record.operator}</span>
                      <span className="stim-history-action" role="cell">
                        <button type="button" aria-label={`导入 ${record.time} 的历史数据`} onClick={() => {
                          setToleranceCurrent(record.current);
                          setToleranceHistoryOpen(false);
                        }}>
                          <img src="/assets/icon-import-history.svg" alt="" aria-hidden="true" />
                        </button>
                      </span>
                    </div>
                  ))}
                </div>
              </section>
            </div>
          )}
        </div>
      )}

    </main>
  );
}
