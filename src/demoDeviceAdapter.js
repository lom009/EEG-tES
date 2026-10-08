import { getDeviceProfile } from "./deviceProtocol.js";
import { createDeviceEventFactory } from "./deviceAdapterContract.js";

function wait(durationMs) {
  return new Promise((resolve) => globalThis.setTimeout(resolve, durationMs));
}

export class DemoDeviceAdapter {
  constructor(profileId, options = {}) {
    const profile = getDeviceProfile(profileId);
    if (!profile) throw new Error(`Unknown demo device profile: ${profileId}`);
    this.profile = profile;
    this.latencyMs = options.latencyMs ?? 350;
    this.timeScale = options.timeScale ?? 0.16;
    this.connectionState = "DISCONNECTED";
    this.program = null;
    this.runState = "IDLE";
    this.acquisitionState = "STOPPED";
    this.impedancePassed = false;
    this.tolerancePassed = false;
    this.listeners = new Set();
    this.runTimers = [];
    this.acquisitionTimer = null;
    this.sampleIndex = 0;
    this.packetIntervalMs = options.packetIntervalMs ?? 100;
    this.createEvent = createDeviceEventFactory({
      sourceId: `DEMO-${profile.id.toUpperCase()}`,
      sourceType: "SIMULATED_DEVICE",
      now: options.now,
    });
  }

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(type, message, payload = {}) {
    const event = this.createEvent(type, message, payload);
    this.listeners.forEach((listener) => listener(event));
    return event;
  }

  async connect() {
    this.connectionState = "CONNECTING";
    this.emit("DEVICE_CONNECTING", "正在建立模拟设备连接");
    await wait(this.latencyMs);
    this.connectionState = "CONNECTED";
    this.emit("DEVICE_CONNECTED", `${this.profile.label} 已连接`, {
      profileId: this.profile.id,
      firmwareVersion: this.profile.firmwareVersion,
      capabilities: this.getCapabilities(),
    });
    return { ...this.profile };
  }

  disconnect() {
    this.clearRunTimers();
    this.stopAcquisition();
    this.connectionState = "DISCONNECTED";
    this.program = null;
    this.runState = "IDLE";
    this.acquisitionState = "STOPPED";
    this.impedancePassed = false;
    this.tolerancePassed = false;
    this.emit("DEVICE_DISCONNECTED", "模拟设备连接已断开");
  }

  getIdentity() {
    return {
      deviceId: `DEMO-${this.profile.id.toUpperCase()}`,
      profileId: this.profile.id,
      firmwareVersion: this.profile.firmwareVersion,
      isSimulated: true,
    };
  }

  getCapabilities() {
    return structuredClone(this.profile);
  }

  getSnapshot() {
    return {
      connectionState: this.connectionState,
      acquisitionState: this.acquisitionState,
      runState: this.runState,
      prepared: Boolean(this.program),
      impedancePassed: this.impedancePassed,
      tolerancePassed: this.tolerancePassed,
    };
  }

  startAcquisition() {
    if (this.connectionState !== "CONNECTED") throw new Error("DEVICE_NOT_CONNECTED");
    if (this.acquisitionState === "RUNNING") return { started: false };
    this.acquisitionState = "RUNNING";
    this.emit("ACQUISITION_STARTED", "模拟 EEG 数据流已开始", {
      channelLabels: this.profile.eegChannelLabels,
      nominalSampleRateHz: this.profile.eegSampleRateHz,
      packetIntervalMs: this.packetIntervalMs,
    });
    this.acquisitionTimer = globalThis.setInterval(() => {
      if (this.acquisitionState !== "RUNNING") return;
      const packet = this.createSyntheticPacket();
      this.emit("EEG_SAMPLE", "模拟 EEG 数据包", packet);
    }, this.packetIntervalMs);
    return { started: true };
  }

  stopAcquisition() {
    if (this.acquisitionTimer) {
      globalThis.clearInterval(this.acquisitionTimer);
      this.acquisitionTimer = null;
    }
    if (this.acquisitionState !== "RUNNING") return { stopped: false };
    this.acquisitionState = "STOPPED";
    this.emit("ACQUISITION_STOPPED", "模拟 EEG 数据流已停止", {
      sampleIndex: this.sampleIndex,
    });
    return { stopped: true };
  }

  createSyntheticPacket() {
    this.sampleIndex += 1;
    const packetTimestampMs = Date.now();
    const channels = this.profile.eegChannelLabels.map((label, channelIndex) => {
      const phase = (this.sampleIndex / 7) + channelIndex * 0.85;
      const harmonic = Math.sin((this.sampleIndex / 2.7) + channelIndex) * 2.2;
      return {
        label,
        valueUv: Number((Math.sin(phase) * 12 + harmonic).toFixed(2)),
      };
    });
    return {
      sampleIndex: this.sampleIndex,
      packetTimestampMs,
      nominalSampleRateHz: this.profile.eegSampleRateHz,
      channels,
      simulated: true,
    };
  }

  pushMarker(label, payload = {}) {
    if (this.connectionState !== "CONNECTED") throw new Error("DEVICE_NOT_CONNECTED");
    return this.emit("MARKER_RECEIVED", `实验标记：${label}`, {
      label,
      ...payload,
    });
  }

  async prepareProgram(program) {
    if (this.connectionState !== "CONNECTED") {
      return {
        accepted: false,
        reasonCode: "DEVICE_NOT_CONNECTED",
        message: "设备未连接，无法接收协议",
      };
    }
    if (!program || program.profileId !== this.profile.id) {
      return {
        accepted: false,
        reasonCode: "DEVICE_PROFILE_MISMATCH",
        message: "协议设备能力版本与当前设备不一致",
      };
    }
    await wait(this.latencyMs);
    this.program = structuredClone(program);
    this.runState = "PREPARED";
    this.impedancePassed = false;
    this.tolerancePassed = false;
    this.emit("PROGRAM_ACCEPTED", "下位机已接收并校验可执行程序", {
      protocolHash: program.protocolHash,
    });
    return {
      accepted: true,
      programId: `PROGRAM-${program.protocolHash}`,
      protocolHash: program.protocolHash,
    };
  }

  async runImpedanceCheck() {
    if (!this.program) throw new Error("PROGRAM_NOT_PREPARED");
    if (!this.tolerancePassed) throw new Error("TOLERANCE_NOT_PASSED");
    this.emit("IMPEDANCE_STARTED", "开始模拟刺激阻抗检测");
    await wait(this.latencyMs * 2);
    const results = this.program.channels.map((channel, index) => ({
      channelId: channel.channelId,
      positionId: channel.positionId,
      simulatedKohm: Number((5.2 + index * 0.8).toFixed(1)),
      status: "SIM_PASS",
    }));
    this.impedancePassed = true;
    this.emit("IMPEDANCE_COMPLETED", "模拟刺激阻抗检测完成", { results });
    return {
      passed: true,
      simulated: true,
      measuredAt: new Date().toISOString(),
      results,
    };
  }

  async runToleranceCheck() {
    if (!this.program) throw new Error("PROGRAM_NOT_PREPARED");
    this.emit("TOLERANCE_STARTED", "开始模拟耐受确认");
    await wait(this.latencyMs * 2);
    const result = {
      passed: true,
      simulated: true,
      confirmedAt: new Date().toISOString(),
      feedback: "DEMO_NO_DISCOMFORT",
    };
    this.tolerancePassed = true;
    this.emit("TOLERANCE_COMPLETED", "模拟耐受确认完成", result);
    return result;
  }

  async arm() {
    if (!this.program) throw new Error("PROGRAM_NOT_PREPARED");
    if (!this.impedancePassed) throw new Error("IMPEDANCE_NOT_PASSED");
    if (!this.tolerancePassed) throw new Error("TOLERANCE_NOT_PASSED");
    await wait(this.latencyMs);
    this.runState = "ARMED";
    this.emit("PROGRAM_ARMED", "模拟下位机已进入 ARMED 状态", {
      protocolHash: this.program.protocolHash,
    });
    return { armed: true, protocolHash: this.program.protocolHash };
  }

  startRun(runId) {
    if (this.runState !== "ARMED") throw new Error("DEVICE_NOT_ARMED");
    this.clearRunTimers();
    this.runState = "RUNNING";
    this.emit("RUN_STARTED", "模拟下位机开始执行刺激程序", { runId });
    this.pushMarker("STIM_RUN_START", { runId, protocolHash: this.program.protocolHash });

    let delayMs = 0;
    this.program.segments.forEach((segment, index) => {
      const scaledDuration = Math.max(350, Math.min(1600, segment.durationMs * this.timeScale));
      const startTimer = globalThis.setTimeout(() => {
        if (this.runState !== "RUNNING") return;
        this.emit("SEGMENT_STARTED", `${segment.type} 阶段开始`, {
          runId,
          segmentIndex: index,
          segment,
        });
        this.pushMarker(`STIM_${segment.type}`, { runId, segmentIndex: index });
      }, delayMs);
      this.runTimers.push(startTimer);
      delayMs += scaledDuration;
    });

    const completeTimer = globalThis.setTimeout(() => {
      if (this.runState !== "RUNNING") return;
      this.runState = "COMPLETED";
      this.emit("RUN_COMPLETED", "模拟刺激程序执行完成", {
        runId,
        protocolHash: this.program.protocolHash,
      });
      this.pushMarker("STIM_RUN_COMPLETE", { runId });
    }, delayMs);
    this.runTimers.push(completeTimer);
  }

  abortRun(reason = "OPERATOR_ABORT") {
    if (this.runState !== "RUNNING") return { aborted: false };
    this.clearRunTimers();
    this.runState = "ABORTED";
    this.emit("RUN_ABORTED", "模拟下位机已执行安全停止", { reason });
    this.pushMarker("STIM_RUN_ABORT", { reason });
    return { aborted: true, reason };
  }

  clearRunTimers() {
    this.runTimers.forEach((timer) => globalThis.clearTimeout(timer));
    this.runTimers = [];
  }

  destroy() {
    this.clearRunTimers();
    this.stopAcquisition();
    this.listeners.clear();
  }
}
