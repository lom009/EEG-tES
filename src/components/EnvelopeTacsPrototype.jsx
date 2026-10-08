import { useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  ArrowLeft,
  Check,
  ChevronRight,
  CircleStop,
  Headphones,
  Play,
  RotateCcw,
  Save,
  Settings2,
  Square,
  UserRound,
  Waves,
  Zap,
} from "lucide-react";
import "./EnvelopeTacsPrototype.css";

// PROTOTYPE: validates the envelope-tACS page flow and state gates, not production UI.
const FLOW_STEPS = [
  ["experiment", "新建实验"],
  ["plan", "刺激方案"],
  ["electrodes", "点位配置"],
  ["run", "本句执行"],
  ["result", "回答与结果"],
];

const POINTS = [
  "FP1", "FP2", "F7", "F3", "Fz", "F4", "F8", "T3", "C3", "Cz",
  "C4", "T4", "T5", "P3", "Pz", "P4", "T6", "O1", "Oz", "O2",
];

const SENTENCES = [
  { id: "MSP-001", text: "今天的阳光真好", duration: 8 },
  { id: "MSP-002", text: "窗外的小鸟在唱歌", duration: 10 },
  { id: "MSP-003", text: "我们一起慢慢练习", duration: 9 },
];

const RUN_LABELS = {
  ready: "待开始",
  running: "执行中",
  awaitingAnswer: "待回答",
  scoring: "评分中",
  saved: "已保存",
  aborted: "已中止",
  finished: "实验完成",
};

const CHART_WIDTH = 900;
const CHART_MIDLINE = 54;
const CHART_AMPLITUDE = 39;

function envelopeAt(position) {
  if (position < 0 || position > 1) return 0;
  const phrases = [
    [0.16, 0.055, 0.72],
    [0.29, 0.07, 0.9],
    [0.46, 0.06, 0.56],
    [0.61, 0.07, 0.94],
    [0.76, 0.055, 0.7],
    [0.87, 0.06, 0.82],
  ];
  return Math.min(1, phrases.reduce((level, [center, width, height]) => {
    const distance = (position - center) / width;
    return Math.max(level, height * Math.exp(-(distance * distance) / 2));
  }, 0));
}

function toSvgPath(points, close = false) {
  if (!points.length) return "";
  const path = points.map(([x, y], index) => `${index ? "L" : "M"}${x.toFixed(2)} ${y.toFixed(2)}`).join(" ");
  return close ? `${path} Z` : path;
}

function createWavePaths(audioDuration, delaySec, carrierFrequencyHz) {
  const totalDuration = Math.max(0.1, audioDuration + delaySec);
  const sampleCount = 900;
  const visibleCarrierCycles = Math.min(120, Math.max(24, carrierFrequencyHz * totalDuration));
  const audio = [];
  const upperEnvelope = [];
  const lowerEnvelope = [];
  const stimulationCarrier = [];
  const stimulationUpper = [];
  const stimulationLower = [];

  for (let index = 0; index <= sampleCount; index += 1) {
    const position = index / sampleCount;
    const x = position * CHART_WIDTH;
    const time = position * totalDuration;
    const audioPosition = time / audioDuration;
    const audioEnvelope = envelopeAt(audioPosition);
    const speech = (
      Math.sin(audioPosition * Math.PI * 2 * 37)
      + 0.42 * Math.sin(audioPosition * Math.PI * 2 * 61 + 0.8)
      + 0.18 * Math.sin(audioPosition * Math.PI * 2 * 89 + 0.2)
    ) / 1.6;
    audio.push([x, CHART_MIDLINE - audioEnvelope * CHART_AMPLITUDE * speech]);
    upperEnvelope.push([x, CHART_MIDLINE - audioEnvelope * CHART_AMPLITUDE]);
    lowerEnvelope.push([x, CHART_MIDLINE + audioEnvelope * CHART_AMPLITUDE]);

    const stimulationPosition = (time - delaySec) / audioDuration;
    const stimulationEnvelope = envelopeAt(stimulationPosition);
    const carrier = Math.sin(position * Math.PI * 2 * visibleCarrierCycles);
    stimulationCarrier.push([x, CHART_MIDLINE - stimulationEnvelope * CHART_AMPLITUDE * carrier]);
    stimulationUpper.push([x, CHART_MIDLINE - stimulationEnvelope * CHART_AMPLITUDE]);
    stimulationLower.push([x, CHART_MIDLINE + stimulationEnvelope * CHART_AMPLITUDE]);
  }

  const band = [...stimulationUpper, ...stimulationLower.slice().reverse()];
  return {
    audio: toSvgPath(audio),
    upperEnvelope: toSvgPath(upperEnvelope),
    lowerEnvelope: toSvgPath(lowerEnvelope),
    stimulationCarrier: toSvgPath(stimulationCarrier),
    stimulationUpper: toSvgPath(stimulationUpper),
    stimulationLower: toSvgPath(stimulationLower),
    stimulationBand: toSvgPath(band, true),
    totalDuration,
  };
}

function createCarrierZoomPath(carrierFrequencyHz) {
  const zoomSeconds = 0.05;
  const cycleCount = Math.max(1, carrierFrequencyHz * zoomSeconds);
  const points = Array.from({ length: 501 }, (_, index) => {
    const position = index / 500;
    const amplitude = 0.72 + 0.16 * position;
    return [position * 500, 34 - Math.sin(position * Math.PI * 2 * cycleCount) * 24 * amplitude];
  });
  return { path: toSvgPath(points), cycleCount, zoomSeconds };
}

function Field({ label, required = false, children, hint }) {
  return (
    <label className="et-field">
      <span>{required && <b>*</b>}{label}</span>
      {children}
      {hint && <small>{hint}</small>}
    </label>
  );
}

function GateRow({ pass, children }) {
  return (
    <div className={`et-gate-row ${pass ? "is-pass" : "is-blocked"}`}>
      {pass ? <Check size={15} /> : <AlertTriangle size={15} />}
      <span>{children}</span>
    </div>
  );
}

function FlowHeader({ page, onNavigate, onHome, onReset }) {
  const currentIndex = Math.max(0, FLOW_STEPS.findIndex(([id]) => id === page));
  return (
    <>
      <header className="et-header">
        <div>
          <button type="button" className="et-link" onClick={onHome}><ArrowLeft size={16} />返回首页</button>
          <h1>包络-tACS 单刺激言语训练</h1>
          <p>流程原型 · 仅验证页面内容、操作顺序与按钮门禁，不用于真实刺激</p>
        </div>
        <button type="button" className="et-secondary" onClick={onReset}><RotateCcw size={15} />重置原型</button>
      </header>
      <nav className="et-stepper" aria-label="实验流程">
        {FLOW_STEPS.map(([id, label], index) => (
          <button
            key={id}
            type="button"
            className={`${index === currentIndex ? "is-active" : ""} ${index < currentIndex ? "is-done" : ""}`}
            onClick={() => index <= currentIndex && onNavigate(id)}
            disabled={index > currentIndex}
          >
            <span>{index < currentIndex ? <Check size={14} /> : index + 1}</span>
            {label}
          </button>
        ))}
      </nav>
    </>
  );
}

function WavePanel({ elapsed, duration, delaySec, carrierFrequencyHz, maxCurrent, envelopeLowPassHz, running }) {
  const paths = useMemo(
    () => createWavePaths(duration, delaySec, carrierFrequencyHz),
    [carrierFrequencyHz, delaySec, duration],
  );
  const zoom = useMemo(() => createCarrierZoomPath(carrierFrequencyHz), [carrierFrequencyHz]);
  const progress = paths.totalDuration ? (elapsed / paths.totalDuration) * 100 : 0;
  const cursor = `${Math.max(0, Math.min(100, progress))}%`;
  const midpoint = paths.totalDuration / 2;
  return (
    <div className={`et-wave-stack ${running ? "is-running" : ""}`}>
      <section>
        <header><Headphones size={17} /><strong>题目音频与包络</strong><span>Hilbert 包络 · 低通 {envelopeLowPassHz} Hz</span></header>
        <div className="et-wave-canvas">
          <div className="et-wave-legend"><span className="is-audio">原始语音</span><span className="is-envelope">上下包络</span></div>
          <svg viewBox="0 0 900 108" preserveAspectRatio="none" aria-hidden="true">
            <line className="et-wave-zero" x1="0" y1={CHART_MIDLINE} x2="900" y2={CHART_MIDLINE} />
            <path className="et-audio-path" d={paths.audio} />
            <path className="et-envelope-path" d={paths.upperEnvelope} />
            <path className="et-envelope-path" d={paths.lowerEnvelope} />
          </svg>
          <i style={{ left: cursor }} />
          <em className="et-wave-unplayed" style={{ left: cursor }} />
        </div>
        <div className="et-time-axis"><span>0 s</span><span>{midpoint.toFixed(2)} s</span><span>{paths.totalDuration.toFixed(2)} s</span></div>
      </section>
      <section>
        <header><Waves size={17} /><strong>计划刺激输出电流</strong><span>{carrierFrequencyHz} Hz 载波 · 峰值 ±{maxCurrent} mA</span></header>
        <div className="et-wave-canvas is-stimulation">
          <div className="et-wave-legend"><span className="is-carrier">调制载波</span><span className="is-stim-envelope">刺激包络边界</span></div>
          <svg viewBox="0 0 900 108" preserveAspectRatio="none" aria-hidden="true">
            <line className="et-wave-zero" x1="0" y1={CHART_MIDLINE} x2="900" y2={CHART_MIDLINE} />
            {delaySec > 0 && <rect className="et-delay-zone" x="0" y="0" width={(delaySec / paths.totalDuration) * CHART_WIDTH} height="108" />}
            <path className="et-stimulation-band" d={paths.stimulationBand} />
            <path className="et-carrier-path" d={paths.stimulationCarrier} />
            <path className="et-stimulation-envelope" d={paths.stimulationUpper} />
            <path className="et-stimulation-envelope" d={paths.stimulationLower} />
          </svg>
          <i style={{ left: cursor }} />
          <em className="et-wave-unplayed" style={{ left: cursor }} />
        </div>
        <div className="et-time-axis"><span>{delaySec > 0 ? `延迟 ${delaySec.toFixed(2)} s` : "同步开始"}</span><span>{midpoint.toFixed(2)} s</span><span>{paths.totalDuration.toFixed(2)} s</span></div>
        <div className="et-carrier-zoom">
          <div><strong>游标附近 50 ms 放大</strong><span>{zoom.cycleCount.toFixed(0)} 个周期 · {carrierFrequencyHz} Hz</span></div>
          <svg viewBox="0 0 500 68" preserveAspectRatio="none" aria-hidden="true">
            <line x1="0" y1="34" x2="500" y2="34" />
            <path d={zoom.path} />
          </svg>
        </div>
        <div className="et-progress is-purple"><b style={{ width: cursor }} /></div>
      </section>
    </div>
  );
}

function StateInspector({ state }) {
  return (
    <aside className="et-inspector">
      <h3>原型状态</h3>
      <dl>
        <div><dt>当前页面</dt><dd>{state.page}</dd></div>
        <div><dt>实验草稿</dt><dd>{state.draftValid ? "有效" : "未保存"}</dd></div>
        <div><dt>刺激方案</dt><dd>{state.planValid ? "有效" : "未通过"}</dd></div>
        <div><dt>刺激模块</dt><dd>{state.stimModuleInited ? "已初始化（模拟）" : "未初始化"}</dd></div>
        <div><dt>刺激点位</dt><dd>{state.pointLabel || "未完成"}</dd></div>
        <div><dt>阻抗结果</dt><dd>{state.impedance}</dd></div>
        <div><dt>设备连接</dt><dd>{state.deviceConnected ? "在线" : "离线"}</dd></div>
        <div><dt>本句状态</dt><dd>{RUN_LABELS[state.runState]}</dd></div>
        <div><dt>句子进度</dt><dd>{state.sentenceIndex + 1} / {SENTENCES.length}</dd></div>
      </dl>
      <h4>开始本句门禁</h4>
      <GateRow pass={state.deviceConnected}>设备在线</GateRow>
      <GateRow pass={state.planValid}>刺激方案有效</GateRow>
      <GateRow pass={state.impedance === "已通过"}>刺激阻抗有效</GateRow>
      <GateRow pass={state.audioReady}>音频与包络已就绪</GateRow>
      <p className="et-prototype-warning"><AlertTriangle size={15} />模拟流程，不连接真实患者或刺激设备。</p>
    </aside>
  );
}

export function EnvelopeTacsPrototype({ onHome }) {
  const [page, setPage] = useState("experiment");
  const [patientId, setPatientId] = useState("");
  const [note, setNote] = useState("");
  const [draftValid, setDraftValid] = useState(false);
  const [plan, setPlan] = useState({
    waveformType: "正弦波",
    stimulationModeType: "定流型",
    maxCurrent: "0.5",
    envelopeLowPassHz: "100",
    relativeDelaySec: "0.16",
    currentLimitPercent: "80",
    carrierFrequencyHz: "500",
    dutyCyclePercent: "100",
  });
  const [stimModuleInited, setStimModuleInited] = useState(false);
  const [planValid, setPlanValid] = useState(false);
  const [activeRole, setActiveRole] = useState("A");
  const [assignments, setAssignments] = useState({});
  const [impedance, setImpedance] = useState("未检测");
  const [deviceConnected, setDeviceConnected] = useState(true);
  const [training, setTraining] = useState({ corpus: "MSP", mode: "固定语速测试（安静）", voice: "原始语音", speed: "0" });
  const [sentenceIndex, setSentenceIndex] = useState(0);
  const [runState, setRunState] = useState("ready");
  const [elapsed, setElapsed] = useState(0);
  const [startedAt, setStartedAt] = useState(0);
  const [scores, setScores] = useState([]);
  const [records, setRecords] = useState([]);
  const [events, setEvents] = useState(["原型已加载"]);

  const sentence = SENTENCES[sentenceIndex];
  const characters = useMemo(() => [...sentence.text], [sentence.text]);
  const assignedA = Object.keys(assignments).find((point) => assignments[point] === "A");
  const assignedC = Object.keys(assignments).find((point) => assignments[point] === "C");
  const pointLabel = assignedA && assignedC ? `${assignedA}·A / ${assignedC}·C` : "";
  const audioReady = Boolean(sentence?.text && sentence.duration);
  const planNumbersValid = Object.entries(plan)
    .filter(([key]) => !["waveformType", "stimulationModeType"].includes(key))
    .every(([, value]) => Number.isFinite(Number(value)) && Number(value) >= 0);
  const startEnabled = deviceConnected && planValid && impedance === "已通过" && audioReady && runState === "ready";
  const allScored = scores.length === characters.length && scores.every(Boolean);
  const relativeDelaySec = Math.max(0, Number(plan.relativeDelaySec) || 0);
  const executionDuration = sentence.duration + relativeDelaySec;

  useEffect(() => {
    if (runState !== "running") return undefined;
    const timer = window.setInterval(() => {
      const nextElapsed = Math.min(executionDuration, (Date.now() - startedAt) / 1000);
      setElapsed(nextElapsed);
      if (nextElapsed >= executionDuration) {
        window.clearInterval(timer);
        setRunState("awaitingAnswer");
        setEvents((rows) => [`${sentence.id} 音频与刺激完成`, ...rows]);
      }
    }, 80);
    return () => window.clearInterval(timer);
  }, [executionDuration, runState, sentence.id, startedAt]);

  function resetPrototype() {
    setPage("experiment");
    setPatientId("");
    setNote("");
    setDraftValid(false);
    setPlanValid(false);
    setStimModuleInited(false);
    setAssignments({});
    setImpedance("未检测");
    setSentenceIndex(0);
    setRunState("ready");
    setElapsed(0);
    setScores([]);
    setRecords([]);
    setEvents(["原型已重置"]);
  }

  function saveDraft() {
    if (!patientId.trim()) return;
    setDraftValid(true);
    setEvents((rows) => ["Experiment Draft 已保存", ...rows]);
    setPage("plan");
  }

  function updatePlan(key, value) {
    setPlan((current) => ({ ...current, [key]: value }));
    setPlanValid(false);
    setImpedance("未检测");
  }

  function initializeStimModule() {
    setStimModuleInited(true);
    setEvents((rows) => ["刺激模块已初始化（原型模拟）", ...rows]);
  }

  function savePlan() {
    if (!planNumbersValid) return;
    setPlanValid(true);
    setEvents((rows) => ["包络-tACS 方案已校验并保存", ...rows]);
    setPage("electrodes");
  }

  function assignPoint(point) {
    setAssignments((current) => {
      const next = Object.fromEntries(Object.entries(current).filter(([, role]) => role !== activeRole));
      if (current[point] === activeRole) return next;
      delete next[point];
      next[point] = activeRole;
      return next;
    });
    setImpedance("未检测");
  }

  function fillDemoPoints() {
    setAssignments({ F3: "A", F4: "C" });
    setImpedance("未检测");
  }

  function detectImpedance() {
    if (!assignedA || !assignedC || impedance === "检测中") return;
    setImpedance("检测中");
    window.setTimeout(() => {
      setImpedance("已通过");
      setEvents((rows) => [`刺激阻抗通过：${assignedA}·A / ${assignedC}·C`, ...rows]);
    }, 700);
  }

  function openExperiment() {
    if (impedance !== "已通过") return;
    setPage("run");
    setRunState("ready");
    setEvents((rows) => ["进入实验页：待开始", ...rows]);
  }

  function startTrial() {
    if (!startEnabled) return;
    setScores(Array(characters.length).fill(null));
    setElapsed(0);
    setStartedAt(Date.now());
    setRunState("running");
    setEvents((rows) => [`${sentence.id} 音频与刺激联合启动`, ...rows]);
  }

  function emergencyStop() {
    if (runState !== "running") return;
    setRunState("aborted");
    setEvents((rows) => [`紧急停止：已执行 ${elapsed.toFixed(1)} 秒`, ...rows]);
  }

  function confirmAnswer() {
    if (runState !== "awaitingAnswer") return;
    setRunState("scoring");
    setPage("result");
    setEvents((rows) => [`${sentence.id} 患者回答结束，开放人工评分`, ...rows]);
  }

  function scoreAll(value) {
    setScores(Array(characters.length).fill(value));
  }

  function setCharacterScore(index, value) {
    setScores((current) => current.map((score, scoreIndex) => scoreIndex === index ? value : score));
  }

  function saveTrial() {
    if (!allScored) return;
    const correct = scores.filter((score) => score === "correct").length;
    const record = {
      id: `TRIAL-${String(records.length + 1).padStart(2, "0")}`,
      sentenceId: sentence.id,
      sentence: sentence.text,
      correct,
      total: characters.length,
      planVersion: "ENVELOPE-TACS-DRAFT-01",
      points: pointLabel,
      impedance: "已通过",
      waveformType: plan.waveformType,
      stimulationModeType: plan.stimulationModeType,
      carrierFrequencyHz: plan.carrierFrequencyHz,
      status: "已保存",
    };
    setRecords((rows) => [...rows, record]);
    setRunState("saved");
    setEvents((rows) => [`${record.id} 已保存：${correct}/${characters.length}`, ...rows]);
  }

  function nextSentence() {
    const nextIndex = (sentenceIndex + 1) % SENTENCES.length;
    setSentenceIndex(nextIndex);
    setScores([]);
    setElapsed(0);
    setRunState("ready");
    setPage("run");
    setEvents((rows) => [`进入 ${SENTENCES[nextIndex].id} 待开始；未自动播放或刺激`, ...rows]);
  }

  function redoSentence() {
    setScores([]);
    setElapsed(0);
    setRunState("ready");
    setPage("run");
    setEvents((rows) => [`${sentence.id} 已重置为待开始`, ...rows]);
  }

  function finishExperiment() {
    setRunState("finished");
    setEvents((rows) => [`实验结束，共保存 ${records.length} 个试次`, ...rows]);
  }

  const inspectorState = {
    page: FLOW_STEPS.find(([id]) => id === page)?.[1] || page,
    draftValid,
    planValid,
    pointLabel,
    impedance,
    deviceConnected,
    runState,
    sentenceIndex,
    audioReady,
    stimModuleInited,
  };

  return (
    <main className="et-shell">
      <FlowHeader page={page} onNavigate={setPage} onHome={onHome} onReset={resetPrototype} />
      <div className="et-layout">
        <section className="et-page">
          {page === "experiment" && (
            <>
              <div className="et-page-title"><UserRound /><div><h2>新建实验</h2><p>先形成有效 Experiment Draft，再进入刺激方案配置。</p></div></div>
              <div className="et-grid two">
                <Field label="被试ID" required><input value={patientId} onChange={(event) => { setPatientId(event.target.value); setDraftValid(false); }} placeholder="例如 SUB001" /></Field>
                <Field label="实验ID" hint="系统自动生成"><input value="EXP-20260918-001" disabled /></Field>
                <Field label="刺激流程"><select value="single" disabled><option value="single">单刺激模式</option></select></Field>
                <Field label="刺激范式"><select value="envelope" disabled><option value="envelope">包络-tACS</option></select></Field>
              </div>
              <Field label="备注"><textarea rows="3" value={note} onChange={(event) => setNote(event.target.value)} placeholder="可选，例如首次言语听辨训练" /></Field>
              <div className="et-actions"><button className="et-primary" type="button" disabled={!patientId.trim()} onClick={saveDraft}>保存草稿并配置刺激方案<ChevronRight size={16} /></button></div>
            </>
          )}

          {page === "plan" && (
            <>
              <div className="et-page-title"><Settings2 /><div><h2>包络-tACS 刺激方案</h2><p>这里完成全部刺激参数；刺激时长由每句话最终音频决定。</p></div></div>
              <div className="et-grid three">
                <Field label="波形类型" required><select value={plan.waveformType} onChange={(e) => updatePlan("waveformType", e.target.value)}><option>正弦波</option><option>方波</option><option>三角波</option><option>自定义</option></select></Field>
                <Field label="刺激模式类型" required><select value={plan.stimulationModeType} onChange={(e) => updatePlan("stimulationModeType", e.target.value)}><option>定流型</option></select></Field>
                <Field label="最大电流" required><div className="et-unit"><input type="number" min="0" step="0.05" value={plan.maxCurrent} onChange={(e) => updatePlan("maxCurrent", e.target.value)} /><span>mA</span></div></Field>
                <Field label="包络低通截止频率" required><div className="et-unit"><input type="number" min="0" value={plan.envelopeLowPassHz} onChange={(e) => updatePlan("envelopeLowPassHz", e.target.value)} /><span>Hz</span></div></Field>
                <Field label="相对延迟" required hint="沿用截图示例值"><div className="et-unit"><input type="number" min="0" step="0.01" value={plan.relativeDelaySec} onChange={(e) => updatePlan("relativeDelaySec", e.target.value)} /><span>s</span></div></Field>
                <Field label="限制电流比例" required hint="沿用截图示例值"><div className="et-unit"><input type="number" min="0" max="100" value={plan.currentLimitPercent} onChange={(e) => updatePlan("currentLimitPercent", e.target.value)} /><span>%</span></div></Field>
                <Field label="载波频率" required hint="用于生成包络调制正弦电流"><div className="et-unit"><input type="number" min="1" value={plan.carrierFrequencyHz} onChange={(e) => updatePlan("carrierFrequencyHz", e.target.value)} /><span>Hz</span></div></Field>
                <Field label="占空比" required><div className="et-unit"><input type="number" min="0" max="100" value={plan.dutyCyclePercent} onChange={(e) => updatePlan("dutyCyclePercent", e.target.value)} /><span>%</span></div></Field>
              </div>
              <div className="et-plan-preview">
                <div><strong>计划刺激包络预览</strong><span>固定包络由当前句音频生成；时长不可独立输入</span></div>
                <svg viewBox="0 0 800 130" preserveAspectRatio="none" aria-hidden="true"><path d="M0 92 C50 92 50 30 100 30 S150 92 200 92 250 42 300 42 350 92 400 92 450 24 500 24 550 92 600 92 650 38 700 38 750 92 800 92" /></svg>
              </div>
              {!planNumbersValid && <p className="et-error">所有数值字段必须为非负数字。</p>}
              <div className="et-actions spread"><button type="button" className="et-secondary" onClick={() => setPage("experiment")}>返回</button><div><button type="button" className="et-secondary" onClick={initializeStimModule}>{stimModuleInited ? "刺激模块已初始化" : "芯片初始化（模拟）"}</button><button type="button" className="et-primary" disabled={!planNumbersValid} onClick={savePlan}>校验并保存方案<ChevronRight size={16} /></button></div></div>
            </>
          )}

          {page === "electrodes" && (
            <>
              <div className="et-page-title"><Zap /><div><h2>选择刺激点位并检测阻抗</h2><p>先选择角色，再点击点位；每个角色只能分配一个位置。</p></div></div>
              <div className="et-electrode-toolbar">
                <div className="et-role-tabs"><button type="button" className={activeRole === "A" ? "is-active" : ""} onClick={() => setActiveRole("A")}>阳极 ·A / STIM-1</button><button type="button" className={activeRole === "C" ? "is-active" : ""} onClick={() => setActiveRole("C")}>阴极 ·C / STIM-2</button></div>
                <button type="button" className="et-secondary" onClick={fillDemoPoints}>填充演示点位</button>
              </div>
              <div className="et-point-grid">
                {POINTS.map((point) => <button key={point} type="button" className={assignments[point] ? `is-${assignments[point].toLowerCase()}` : ""} onClick={() => assignPoint(point)}><strong>{point}</strong>{assignments[point] && <span>·{assignments[point]}</span>}</button>)}
              </div>
              <div className="et-assignment-summary"><span>阳极：<b>{assignedA || "未分配"}</b></span><span>阴极：<b>{assignedC || "未分配"}</b></span><span>阻抗：<b>{impedance}</b></span></div>
              <div className="et-actions spread"><button type="button" className="et-secondary" onClick={() => setPage("plan")}>返回方案</button><div><button type="button" className="et-secondary" disabled={!assignedA || !assignedC || impedance === "检测中"} onClick={detectImpedance}>{impedance === "检测中" ? "检测中…" : "开始刺激阻抗检测"}</button><button type="button" className="et-primary" disabled={impedance !== "已通过"} onClick={openExperiment}>进入实验页<ChevronRight size={16} /></button></div></div>
            </>
          )}

          {(page === "run" || page === "result") && (
            <>
              <div className="et-page-title"><Play /><div><h2>实验页 · {RUN_LABELS[runState]}</h2><p>{sentence.id} · 第 {sentenceIndex + 1}/{SENTENCES.length} 句 · 音频 {sentence.duration.toFixed(2)} 秒 · 执行窗口 {executionDuration.toFixed(2)} 秒</p></div></div>
              <div className="et-run-grid">
                <div>
                  <section className="et-question-panel">
                    <header><strong>题目预览</strong><span>执行时仅查看，不能提前判分</span></header>
                    <div className="et-character-preview">{characters.map((character, index) => <b key={`${character}-${index}`}>{character}</b>)}</div>
                  </section>
                  <WavePanel
                    elapsed={elapsed}
                    duration={sentence.duration}
                    delaySec={relativeDelaySec}
                    carrierFrequencyHz={Math.max(1, Number(plan.carrierFrequencyHz) || 500)}
                    maxCurrent={plan.maxCurrent}
                    envelopeLowPassHz={plan.envelopeLowPassHz}
                    running={runState === "running"}
                  />
                  {runState === "awaitingAnswer" && <div className="et-answer-prompt"><UserRound /><div><strong>等待患者口头回答</strong><span>回答完成后，由操作人员确认进入逐字评分。</span></div><button type="button" className="et-primary" onClick={confirmAnswer}>患者已回答，开始判分</button></div>}
                  {runState === "aborted" && <div className="et-answer-prompt is-danger"><CircleStop /><div><strong>本句已中止</strong><span>本次不会进入判分，可重新执行本句。</span></div><button type="button" className="et-primary" onClick={redoSentence}>重新执行本句</button></div>}
                  {(runState === "scoring" || runState === "saved" || runState === "finished") && (
                    <section className="et-scoring-panel">
                      <header><div><strong>逐字人工判分</strong><span>所有字必须完成判定后才能保存。</span></div><div><button type="button" onClick={() => scoreAll("correct")}>全对</button><button type="button" onClick={() => scoreAll("wrong")}>全错</button><button type="button" onClick={() => setScores(Array(characters.length).fill(null))}>重置</button></div></header>
                      <div className="et-score-grid">{characters.map((character, index) => <article key={`${character}-score-${index}`} className={scores[index] ? `is-${scores[index]}` : ""}><b>{character}</b><div><button type="button" className={scores[index] === "correct" ? "is-active" : ""} onClick={() => setCharacterScore(index, "correct")}>正确</button><button type="button" className={scores[index] === "wrong" ? "is-active" : ""} onClick={() => setCharacterScore(index, "wrong")}>错误</button></div></article>)}</div>
                    </section>
                  )}
                </div>
                <aside className="et-control-panel">
                  <h3>训练设置</h3>
                  <Field label="语料库"><select disabled={runState !== "ready"} value={training.corpus} onChange={(e) => setTraining((value) => ({ ...value, corpus: e.target.value }))}><option>MSP</option><option>自定义语料</option></select></Field>
                  <Field label="句表号"><select disabled={runState !== "ready"} value={sentenceIndex} onChange={(e) => setSentenceIndex(Number(e.target.value))}>{SENTENCES.map((item, index) => <option key={item.id} value={index}>{index + 1} · {item.id}</option>)}</select></Field>
                  <Field label="测试模式"><select disabled={runState !== "ready"} value={training.mode} onChange={(e) => setTraining((value) => ({ ...value, mode: e.target.value }))}><option>固定语速测试（安静）</option><option>自适应语速测试</option><option>固定信噪比测试</option></select></Field>
                  <Field label="语音形式"><select disabled={runState !== "ready"} value={training.voice} onChange={(e) => setTraining((value) => ({ ...value, voice: e.target.value }))}><option>原始语音</option><option>声码器语音</option></select></Field>
                  <Field label="语速"><div className="et-unit"><input disabled={runState !== "ready"} type="number" value={training.speed} onChange={(e) => setTraining((value) => ({ ...value, speed: e.target.value }))} /><span>字/s</span></div></Field>
                  <h3>执行控制</h3>
                  <label className="et-toggle"><input type="checkbox" checked={deviceConnected} onChange={(e) => setDeviceConnected(e.target.checked)} /><span>模拟设备在线</span></label>
                  {runState === "ready" && <button type="button" className="et-primary et-full" disabled={!startEnabled} onClick={startTrial}><Play size={16} />开始本句</button>}
                  {runState === "running" && <button type="button" className="et-emergency" onClick={emergencyStop}><Square size={15} fill="currentColor" />紧急停止</button>}
                  {runState === "scoring" && <button type="button" className="et-primary et-full" disabled={!allScored} onClick={saveTrial}><Save size={16} />保存本句结果</button>}
                  {runState === "saved" && <div className="et-next-actions"><button type="button" className="et-primary" onClick={nextSentence}>下一句</button><button type="button" className="et-secondary" onClick={redoSentence}>重做本句</button><button type="button" className="et-secondary" onClick={finishExperiment}>结束实验</button></div>}
                  {runState === "finished" && <div className="et-finished"><Check /><strong>实验已结束</strong><span>已保存 {records.length} 个试次</span></div>}
                </aside>
              </div>
            </>
          )}
        </section>
        <StateInspector state={inspectorState} />
      </div>

      <section className="et-debug-panel">
        <div><h3>已保存试次</h3>{records.length ? records.map((record) => <p key={record.id}><b>{record.id}</b> · {record.sentenceId} · {record.correct}/{record.total} 正确 · {record.points}</p>) : <p>暂无记录</p>}</div>
        <div><h3>事件日志</h3>{events.slice(0, 6).map((event, index) => <p key={`${event}-${index}`}>{event}</p>)}</div>
      </section>
    </main>
  );
}
