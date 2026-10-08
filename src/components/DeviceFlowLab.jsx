import { useEffect, useMemo, useRef, useState } from "react";
import { DemoDeviceAdapter } from "../demoDeviceAdapter.js";
import { assertDeviceAdapter } from "../deviceAdapterContract.js";
import {
  compileProtocol,
  createDemoAssignments,
  createDeviceFlowState,
  createProtocolDraft,
  DEVICE_PROFILES,
  getCompatibleTopologies,
  getDeviceProfile,
  getParadigmAvailability,
  MONTAGE_TOPOLOGIES,
  STIMULATION_PARADIGMS,
  transitionDeviceFlow,
  validateProtocol,
} from "../deviceProtocol.js";
import "./DeviceFlowLab.css";

const PARAMETER_LABELS = {
  currentMa: "直流电流 (mA)",
  amplitudeMa: "交流幅值 (mA)",
  frequencyHz: "频率 (Hz)",
  phaseDeg: "相位 (°)",
  durationMs: "总时长 (ms)",
  rampUpMs: "缓升 (ms)",
  rampDownMs: "缓降 (ms)",
  carrierFrequencyAHz: "A 组载波 (Hz)",
  carrierFrequencyBHz: "B 组载波 (Hz)",
  currentAMa: "A 组电流 (mA)",
  currentBMa: "B 组电流 (mA)",
  phaseADeg: "A 组相位 (°)",
  phaseBDeg: "B 组相位 (°)",
};

const FLOW_LABELS = {
  connection: {
    DISCONNECTED: "未连接",
    CONNECTED: "已连接",
  },
  acquisition: {
    STOPPED: "未采集",
    RUNNING: "采集中",
  },
  impedance: {
    LOCKED: "等待耐受",
    READY: "可检测",
    NOT_RUN: "未检测",
    PASSED: "模拟通过",
  },
  tolerance: {
    NOT_RUN: "未确认",
    READY: "可执行",
    PASSED: "模拟通过",
  },
  run: {
    IDLE: "未开始",
    ARMED: "已就绪",
    RUNNING: "运行中",
    COMPLETED: "已完成",
    ABORTED: "已急停",
  },
};

function StatusItem({ label, value, tone = "" }) {
  return (
    <div className={`device-lab-status ${tone}`}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function JsonPanel({ title, value }) {
  return (
    <details className="device-lab-json">
      <summary>{title}</summary>
      <pre>{JSON.stringify(value, null, 2)}</pre>
    </details>
  );
}

export function DeviceFlowLab({ onHome }) {
  const [profileId, setProfileId] = useState("sim-basic");
  const [protocol, setProtocol] = useState(() => createProtocolDraft("sim-basic", "TDCS"));
  const [flow, setFlow] = useState(createDeviceFlowState);
  const [program, setProgram] = useState(null);
  const [validation, setValidation] = useState(() => validateProtocol(createProtocolDraft("sim-basic", "TDCS")));
  const [impedanceResults, setImpedanceResults] = useState([]);
  const [events, setEvents] = useState([]);
  const [busyAction, setBusyAction] = useState("");
  const [activeSegment, setActiveSegment] = useState("—");
  const [latestPacket, setLatestPacket] = useState(null);
  const [samplePacketCount, setSamplePacketCount] = useState(0);
  const adapterRef = useRef(null);

  const profile = getDeviceProfile(profileId);
  const compatibleTopologies = useMemo(
    () => getCompatibleTopologies(profile, protocol.paradigmId),
    [profile, protocol.paradigmId],
  );

  useEffect(() => {
    const adapter = assertDeviceAdapter(new DemoDeviceAdapter(profileId));
    adapterRef.current = adapter;
    const unsubscribe = adapter.subscribe((event) => {
      if (event.type === "EEG_SAMPLE") {
        setLatestPacket(event.payload);
        setSamplePacketCount((current) => current + 1);
        return;
      }
      setEvents((current) => [event, ...current].slice(0, 80));
      if (event.type === "SEGMENT_STARTED") {
        setActiveSegment(event.payload.segment.type);
      }
      if (event.type === "RUN_COMPLETED") {
        setFlow((current) => transitionDeviceFlow(current, { type: "COMPLETE" }));
        setActiveSegment("COMPLETED");
      }
      if (event.type === "RUN_ABORTED") {
        setFlow((current) => transitionDeviceFlow(current, { type: "ABORT" }));
        setActiveSegment("ABORTED");
      }
    });
    return () => {
      unsubscribe();
      adapter.destroy();
    };
  }, [profileId]);

  function resetDownstream(nextProtocol) {
    setProtocol(nextProtocol);
    setProgram(null);
    setImpedanceResults([]);
    setActiveSegment("—");
    setLatestPacket(null);
    setSamplePacketCount(0);
    setValidation(validateProtocol(nextProtocol, getDeviceProfile(nextProtocol.profileId)));
    setFlow((current) => transitionDeviceFlow(current, { type: "PROTOCOL_CHANGED" }));
  }

  function changeProfile(nextProfileId) {
    if (flow.connection === "CONNECTED") return;
    setProfileId(nextProfileId);
    const nextProtocol = createProtocolDraft(nextProfileId);
    setProtocol(nextProtocol);
    setProgram(null);
    setImpedanceResults([]);
    setEvents([]);
    setValidation(validateProtocol(nextProtocol));
    setFlow(createDeviceFlowState());
  }

  async function connectDevice() {
    setBusyAction("connect");
    await adapterRef.current.connect();
    setFlow((current) => transitionDeviceFlow(current, { type: "CONNECT" }));
    setBusyAction("");
  }

  function disconnectDevice() {
    adapterRef.current.disconnect();
    setFlow(createDeviceFlowState());
    setProgram(null);
    setImpedanceResults([]);
    setActiveSegment("—");
    setLatestPacket(null);
    setSamplePacketCount(0);
  }

  function startAcquisition() {
    const result = adapterRef.current.startAcquisition();
    if (result.started) {
      setFlow((current) => transitionDeviceFlow(current, { type: "ACQUISITION_START" }));
    }
  }

  function stopAcquisition() {
    const result = adapterRef.current.stopAcquisition();
    if (result.stopped) {
      setFlow((current) => transitionDeviceFlow(current, { type: "ACQUISITION_STOP" }));
    }
  }

  function selectParadigm(paradigmId) {
    if (!getParadigmAvailability(profile, paradigmId).isSupported) return;
    resetDownstream(createProtocolDraft(profileId, paradigmId));
  }

  function selectTopology(topologyId) {
    resetDownstream({
      ...protocol,
      topologyId,
      assignments: [],
    });
  }

  function updateParameter(key, rawValue) {
    const value = Number(rawValue);
    const nextProtocol = {
      ...protocol,
      parameters: {
        ...protocol.parameters,
        [key]: Number.isFinite(value) ? value : 0,
      },
    };
    resetDownstream({
      ...nextProtocol,
      assignments: protocol.assignments.length > 0
        ? createDemoAssignments(nextProtocol)
        : [],
    });
  }

  function generateMontage() {
    const nextProtocol = {
      ...protocol,
      assignments: createDemoAssignments(protocol),
    };
    resetDownstream(nextProtocol);
  }

  async function compileAndPrepare() {
    const result = compileProtocol(protocol, profile);
    setValidation(result.validation);
    if (!result.ok) return;
    setBusyAction("prepare");
    const response = await adapterRef.current.prepareProgram(result.program);
    setBusyAction("");
    if (!response.accepted) {
      setValidation((current) => ({
        ...current,
        isValid: false,
        errors: [...current.errors, {
          code: response.reasonCode,
          message: response.message,
        }],
      }));
      return;
    }
    setProgram(result.program);
    setFlow((current) => transitionDeviceFlow(current, { type: "PREPARE" }));
  }

  async function checkImpedance() {
    setBusyAction("impedance");
    const result = await adapterRef.current.runImpedanceCheck();
    setBusyAction("");
    setImpedanceResults(result.results);
    if (result.passed) {
      setFlow((current) => transitionDeviceFlow(current, { type: "IMPEDANCE_PASS" }));
    }
  }

  async function checkTolerance() {
    setBusyAction("tolerance");
    const result = await adapterRef.current.runToleranceCheck();
    setBusyAction("");
    if (result.passed) {
      setFlow((current) => transitionDeviceFlow(current, { type: "TOLERANCE_PASS" }));
    }
  }

  async function armProgram() {
    setBusyAction("arm");
    await adapterRef.current.arm();
    setBusyAction("");
    setFlow((current) => transitionDeviceFlow(current, { type: "ARM" }));
  }

  function startRun() {
    const runId = `RUN-${Date.now()}`;
    adapterRef.current.startRun(runId);
    setFlow((current) => transitionDeviceFlow(current, { type: "START" }));
    setActiveSegment("STARTING");
  }

  function abortRun() {
    adapterRef.current.abortRun("OPERATOR_ABORT");
  }

  function addOperatorMarker() {
    adapterRef.current.pushMarker("OPERATOR_NOTE", {
      note: "人工标记",
      packetIndex: latestPacket?.sampleIndex ?? null,
    });
  }

  const canPrepare = flow.connection === "CONNECTED" && validation.isValid;
  const isBusy = Boolean(busyAction);

  return (
    <main className="device-lab-shell">
      <header className="device-lab-header">
        <button type="button" onClick={onHome}>← 返回首页</button>
        <div>
          <h1>上位机 ↔ 模拟下位机流程实验室</h1>
          <p>仅验证协议与状态流，所有设备结果均为模拟数据</p>
        </div>
        <span className="device-lab-demo-badge">SIMULATOR</span>
      </header>

      <section className="device-lab-summary">
        <StatusItem label="连接" value={FLOW_LABELS.connection[flow.connection]} />
        <StatusItem label="EEG 数据" value={FLOW_LABELS.acquisition[flow.acquisition]} />
        <StatusItem label="协议" value={flow.prepared ? "已接收" : "未下发"} />
        <StatusItem label="阻抗" value={FLOW_LABELS.impedance[flow.impedance]} />
        <StatusItem label="耐受" value={FLOW_LABELS.tolerance[flow.tolerance]} />
        <StatusItem label="运行" value={FLOW_LABELS.run[flow.run]} />
        <StatusItem label="当前段" value={activeSegment} />
      </section>

      <div className="device-lab-grid">
        <section className="device-lab-card">
          <h2>1. 连接模拟下位机</h2>
          <label>
            设备能力配置
            <select value={profileId} disabled={flow.connection === "CONNECTED"} onChange={(event) => changeProfile(event.target.value)}>
              {Object.values(DEVICE_PROFILES).map((item) => (
                <option key={item.id} value={item.id}>{item.label}</option>
              ))}
            </select>
          </label>
          <div className="device-lab-actions">
            <button type="button" disabled={isBusy || flow.connection === "CONNECTED"} onClick={connectDevice}>
              {busyAction === "connect" ? "连接中…" : "连接设备"}
            </button>
            <button type="button" disabled={flow.connection !== "CONNECTED" || flow.run === "RUNNING"} onClick={disconnectDevice}>断开</button>
          </div>
          <ul>
            <li>刺激通道：{profile.stimChannelCount}</li>
            <li>EEG 通道：{profile.eegChannelCount}（{profile.eegSampleRateHz} Hz）</li>
            <li>独立发生器：{profile.independentGeneratorCount}</li>
            <li>固件：{profile.firmwareVersion}</li>
          </ul>
          <div className="device-lab-actions">
            <button type="button" disabled={flow.connection !== "CONNECTED" || flow.acquisition === "RUNNING"} onClick={startAcquisition}>
              开始模拟 EEG
            </button>
            <button type="button" disabled={flow.acquisition !== "RUNNING"} onClick={stopAcquisition}>
              停止 EEG
            </button>
          </div>
        </section>

        <section className="device-lab-card">
          <h2>2. 选择刺激范式</h2>
          <div className="device-lab-option-list">
            {Object.values(STIMULATION_PARADIGMS).map((paradigm) => {
              const availability = getParadigmAvailability(profile, paradigm.id);
              return (
                <button
                  key={paradigm.id}
                  type="button"
                  className={protocol.paradigmId === paradigm.id ? "is-selected" : ""}
                  disabled={flow.connection !== "CONNECTED" || !availability.isSupported}
                  title={availability.reason}
                  onClick={() => selectParadigm(paradigm.id)}
                >
                  <strong>{paradigm.label}</strong>
                  <small>{availability.isSupported ? paradigm.description : availability.reason}</small>
                </button>
              );
            })}
          </div>
        </section>

        <section className="device-lab-card">
          <h2>3. 刺激配置：阵列与波形参数</h2>
          <p className="device-lab-muted">刺激范式决定波形；阵列决定电流通过哪些电极和硬件通道。</p>
          <div className="device-lab-option-list is-compact">
            {compatibleTopologies.map((topologyId) => (
              <button
                key={topologyId}
                type="button"
                className={protocol.topologyId === topologyId ? "is-selected" : ""}
                disabled={flow.connection !== "CONNECTED"}
                onClick={() => selectTopology(topologyId)}
              >
                <strong>{MONTAGE_TOPOLOGIES[topologyId].label}</strong>
                <small>{MONTAGE_TOPOLOGIES[topologyId].description}</small>
              </button>
            ))}
          </div>
          <div className="device-lab-parameters">
            {Object.entries(protocol.parameters).map(([key, value]) => (
              <label key={key}>
                {PARAMETER_LABELS[key] || key}
                <input type="number" value={value} disabled={flow.connection !== "CONNECTED"} onChange={(event) => updateParameter(key, event.target.value)} />
              </label>
            ))}
          </div>
        </section>

        <section className="device-lab-card">
          <h2>4. 电极与硬件通道映射</h2>
          <button type="button" disabled={flow.connection !== "CONNECTED"} onClick={generateMontage}>生成演示阵列</button>
          {protocol.assignments.length ? (
            <table>
              <thead><tr><th>通道</th><th>点位</th><th>角色</th><th>组</th><th>电流</th></tr></thead>
              <tbody>
                {protocol.assignments.map((assignment) => (
                  <tr key={assignment.channelId}>
                    <td>CH{assignment.channelId}</td>
                    <td>{assignment.positionId}</td>
                    <td>{assignment.role}</td>
                    <td>{assignment.groupId}</td>
                    <td>{assignment.currentMa} mA</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <p className="device-lab-muted">尚未生成通道映射。</p>}
        </section>

        <section className="device-lab-card">
          <h2>5. 编译并下发</h2>
          <div className={validation.isValid ? "device-lab-check is-pass" : "device-lab-check is-blocked"}>
            {validation.isValid ? "协议静态校验通过" : `存在 ${validation.errors.length} 个阻断项`}
          </div>
          {validation.errors.map((error) => <p className="device-lab-error" key={error.code}>{error.code}：{error.message}</p>)}
          <button type="button" disabled={!canPrepare || isBusy} onClick={compileAndPrepare}>
            {busyAction === "prepare" ? "下位机校验中…" : "编译并发送 PREPARE"}
          </button>
          <JsonPanel title="查看上位机语义协议" value={protocol} />
          {program && <JsonPanel title="查看下位机可执行程序" value={program} />}
        </section>

        <section className="device-lab-card">
          <h2>6. 预检：耐受确认后检测阻抗</h2>
          <div className="device-lab-actions">
            <button type="button" disabled={flow.tolerance !== "READY" || isBusy} onClick={checkTolerance}>
              {busyAction === "tolerance" ? "确认中…" : "执行模拟耐受"}
            </button>
            <button type="button" disabled={flow.impedance !== "READY" || isBusy} onClick={checkImpedance}>
              {busyAction === "impedance" ? "检测中…" : "检测刺激阻抗"}
            </button>
          </div>
          {impedanceResults.length > 0 && (
            <ul>
              {impedanceResults.map((result) => (
                <li key={result.channelId}>CH{result.channelId} / {result.positionId}：{result.simulatedKohm} kΩ（模拟）</li>
              ))}
            </ul>
          )}
        </section>

        <section className="device-lab-card">
          <h2>7. ARM、运行和急停</h2>
          <div className="device-lab-actions">
            <button type="button" disabled={flow.tolerance !== "PASSED" || flow.run !== "IDLE" || isBusy} onClick={armProgram}>
              {busyAction === "arm" ? "ARM 中…" : "ARM"}
            </button>
            <button type="button" disabled={flow.run !== "ARMED"} onClick={startRun}>START</button>
            <button type="button" className="is-danger" disabled={flow.run !== "RUNNING"} onClick={abortRun}>ABORT</button>
          </div>
          <p>运行完成后应收到 `RUN_COMPLETED`；运行中点击 ABORT 应收到 `RUN_ABORTED`。</p>
        </section>

        <section className="device-lab-card device-lab-events">
          <h2>8. 设备事件与实验标记</h2>
          <button type="button" disabled={flow.connection !== "CONNECTED"} onClick={addOperatorMarker}>插入人工标记</button>
          {events.length === 0 ? <p className="device-lab-muted">暂无事件。</p> : (
            <ol>
              {events.map((event) => (
                <li key={event.eventId}>
                  <time>#{event.sequence} {event.occurredAt.slice(11, 19)}</time>
                  <strong>{event.type}</strong>
                  <span>{event.message}</span>
                </li>
              ))}
            </ol>
          )}
        </section>

        <section className="device-lab-card device-lab-signal">
          <h2>9. 模拟 EEG 数据流</h2>
          <p className="device-lab-muted">
            这是用于验证上位机订阅、时间戳和事件对齐的合成数据，不是 BrainFlow/LSL 实采数据。
          </p>
          <div className="device-lab-signal-meta">
            <StatusItem label="数据包" value={String(samplePacketCount)} />
            <StatusItem label="最近序号" value={latestPacket ? String(latestPacket.sampleIndex) : "—"} />
            <StatusItem label="标称采样率" value={`${profile.eegSampleRateHz} Hz`} />
          </div>
          {latestPacket ? (
            <ul className="device-lab-channel-values">
              {latestPacket.channels.map((channel) => (
                <li key={channel.label}>
                  <strong>{channel.label}</strong>
                  <span>{channel.valueUv} μV</span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="device-lab-muted">连接设备后点击“开始模拟 EEG”。</p>
          )}
        </section>

        <section className="device-lab-card device-lab-boundary">
          <h2>10. 接入真实设备时替换什么</h2>
          <ol>
            <li>保留上位机语义协议和页面状态机。</li>
            <li>将 DemoDeviceAdapter 替换为厂商 SDK / BrainFlow 采集适配器。</li>
            <li>若需要跨设备同步，再把 EEG 与事件发布到 LSL。</li>
            <li>真实硬件必须重新实现阻抗、耐受、ARM 和急停确认，模拟通过不可复用。</li>
          </ol>
        </section>
      </div>
    </main>
  );
}
