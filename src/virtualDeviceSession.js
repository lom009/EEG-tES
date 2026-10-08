import {
  createDeviceCapability,
  createDeviceError,
  createProtocolMessageFactory,
  DEVICE_ERROR_CODES,
  DEVICE_EVENTS,
  validateAcquisitionConfiguration,
  validateStimulationPrerequisites,
} from "./deviceProtocolV1.js";
import {
  createVirtualEegSample,
  impedanceQuality,
  interpolateImpedance,
  normalizeVirtualSignalProfile,
  VIRTUAL_SIGNAL_MODEL_VERSION,
} from "./virtualSignalModel.js";

const SIMULATOR_VERSION = "0.5.0";
const SIMULATION_FAULT_MESSAGES = {
  [DEVICE_ERROR_CODES.ELECTRODE_CONTACT_LOST]: "运行中检测到电极接触异常",
  [DEVICE_ERROR_CODES.TRANSPORT_DISCONNECTED]: "模拟下位机连接已中断",
  [DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK]: "设备时间戳回退，异常 EEG 帧已拒绝",
  [DEVICE_ERROR_CODES.HARDWARE_FAULT]: "模拟硬件故障",
};

function hashSeed(seed) {
  const source = String(seed);
  let hash = 2166136261;
  for (let index = 0; index < source.length; index += 1) {
    hash ^= source.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0 || 1;
}

export function createSeededRandom(seed = "eeg-tes-default") {
  let state = hashSeed(seed);
  return {
    next() {
      state ^= state << 13;
      state ^= state >>> 17;
      state ^= state << 5;
      return (state >>> 0) / 4294967296;
    },
    between(min, max) {
      return min + (max - min) * this.next();
    },
    getState() {
      return state >>> 0;
    },
  };
}

export function applyClockDrift(timeUs, clockDriftPpm = 0) {
  const driftRatio = 1 + (Number(clockDriftPpm) || 0) / 1_000_000;
  return Math.max(0, Math.round(Number(timeUs) * driftRatio));
}

export function createPatientSignalProfile(seed = "patient-demo-001", overrides = {}) {
  const random = createSeededRandom(`${seed}:patient-signal-profile`);
  return normalizeVirtualSignalProfile({
    profileId: `PATIENT-${hashSeed(seed).toString(16).toUpperCase()}`,
    baselineScale: random.between(0.8, 1.25),
    thetaScale: random.between(0.82, 1.18),
    alphaScale: random.between(0.78, 1.3),
    betaScale: random.between(0.82, 1.2),
    mainsScale: random.between(0.7, 1.35),
    blinkScale: random.between(0.8, 1.25),
    blinkIntervalSec: random.between(5.8, 8.4),
    muscleScale: random.between(0.75, 1.3),
    muscleIntervalSec: random.between(9.5, 13),
    stimulationArtifactScale: random.between(0.8, 1.25),
    ...overrides,
  });
}

export function createVirtualClock({
  mode = "manual",
  timeScale = 1,
  startTimeUs = 0,
} = {}) {
  let nowUs = startTimeUs;
  const wallClockStartedAtMs = Date.now();
  let scheduleSequence = 0;
  const queue = [];
  const nativeTimers = new Set();

  function readNowUs() {
    if (mode !== "realtime") return nowUs;
    const elapsedRealMs = Date.now() - wallClockStartedAtMs;
    return startTimeUs + Math.round((elapsedRealMs / Math.max(timeScale, 0.0001)) * 1000);
  }

  function sortQueue() {
    queue.sort((left, right) => (
      left.dueUs - right.dueUs || left.sequence - right.sequence
    ));
  }

  function schedule(callback, delayMs = 0) {
    if (mode === "realtime") {
      const timer = globalThis.setTimeout(() => {
        nativeTimers.delete(timer);
        callback();
      }, Math.max(0, delayMs * timeScale));
      nativeTimers.add(timer);
      return () => {
        globalThis.clearTimeout(timer);
        nativeTimers.delete(timer);
      };
    }
    scheduleSequence += 1;
    const task = {
      sequence: scheduleSequence,
      dueUs: nowUs + Math.round(delayMs * 1000),
      callback,
      cancelled: false,
    };
    queue.push(task);
    sortQueue();
    return () => {
      task.cancelled = true;
    };
  }

  function advanceBy(durationMs) {
    if (mode !== "manual") throw new Error("VIRTUAL_CLOCK_NOT_MANUAL");
    const targetUs = nowUs + Math.round(durationMs * 1000);
    while (queue.length && queue[0].dueUs <= targetUs) {
      const task = queue.shift();
      nowUs = task.dueUs;
      if (!task.cancelled) task.callback();
      sortQueue();
    }
    nowUs = targetUs;
  }

  function clear() {
    queue.splice(0, queue.length);
    nativeTimers.forEach((timer) => globalThis.clearTimeout(timer));
    nativeTimers.clear();
  }

  return {
    mode,
    nowUs: readNowUs,
    schedule,
    advanceBy,
    clear,
    pendingCount: () => queue.filter((task) => !task.cancelled).length + nativeTimers.size,
  };
}

class ProtocolEventSource {
  constructor({ deviceId, clock, eventSink, clockDriftPpm = 0 }) {
    this.deviceId = deviceId;
    this.clock = clock;
    this.eventSink = eventSink;
    this.listeners = new Set();
    this.messageFactory = createProtocolMessageFactory({
      deviceId,
      nowUs: () => applyClockDrift(clock.nowUs(), clockDriftPpm),
    });
  }

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(type, payload = {}, context = {}) {
    const event = this.messageFactory.event(type, payload, context);
    this.eventSink?.(event);
    this.listeners.forEach((listener) => listener(event));
    return event;
  }

  destroySource() {
    this.listeners.clear();
  }
}

export class VirtualAcquisitionAdapter extends ProtocolEventSource {
  constructor({ capability, clock, random, eventSink, scenario }) {
    super({
      deviceId: `${capability.identity.deviceId}-ACQ`,
      clock,
      eventSink,
      clockDriftPpm: scenario.clockDriftPpm,
    });
    this.capability = capability;
    this.clock = clock;
    this.random = random;
    this.scenario = scenario;
    this.connectionState = "DISCONNECTED";
    this.acquisitionState = "STOPPED";
    this.configuration = null;
    this.sampleIndex = 0;
    this.packetSequence = 0;
    this.pendingDroppedSamples = 0;
    this.lastAcceptedDeviceTimeUs = null;
    this.cancelNextFrame = null;
    this.deliveryCancellations = new Set();
    this.impedanceCancellations = [];
    this.impedanceResult = null;
    this.stimulationActive = false;
  }

  connect() {
    this.connectionState = "CONNECTED";
    this.emit(DEVICE_EVENTS.CONNECTION_CHANGED, {
      state: "CONNECTED",
      sourceKind: "SIMULATED",
    });
    return Promise.resolve(this.getIdentity());
  }

  disconnect() {
    this.stopAcquisition();
    this.stimulationActive = false;
    this.connectionState = "DISCONNECTED";
    this.emit(DEVICE_EVENTS.CONNECTION_CHANGED, { state: "DISCONNECTED" });
  }

  getIdentity() {
    return { ...this.capability.identity, deviceId: this.deviceId };
  }

  getCapabilities() {
    return structuredClone(this.capability.acquisition);
  }

  configureAcquisition(configuration) {
    const validation = validateAcquisitionConfiguration(configuration, this.capability);
    if (!validation.isValid) {
      throw new Error(`${DEVICE_ERROR_CODES.ACQUISITION_CONFIG_INVALID}:${validation.errors.join(",")}`);
    }
    this.configuration = structuredClone(configuration);
    this.sampleIndex = 0;
    this.packetSequence = 0;
    this.pendingDroppedSamples = 0;
    this.lastAcceptedDeviceTimeUs = null;
    this.emit(DEVICE_EVENTS.ACQUISITION_CONFIGURED, {
      ...this.configuration,
      sourceKind: "SIMULATED",
    });
    return { configured: true, configuration: structuredClone(this.configuration) };
  }

  startImpedanceCheck({ channels = this.configuration?.channels || [], runId = null } = {}) {
    if (this.connectionState !== "CONNECTED") throw new Error(DEVICE_ERROR_CODES.DEVICE_NOT_CONNECTED);
    if (!this.configuration) throw new Error(DEVICE_ERROR_CODES.ACQUISITION_CONFIG_INVALID);
    this.stopImpedanceCheck();
    const measurementId = `ACQ-IMP-${this.scenario.seed}-${Math.round(this.clock.nowUs() / 1000)}`;
    this.emit(DEVICE_EVENTS.ACQUISITION_IMPEDANCE_STARTED, { measurementId }, { runId });
    const failedPosition = this.scenario.acquisitionImpedancePass === false
      ? (channels[0]?.positionId || channels[0] || this.configuration.channels[0])
      : null;
    const channelModels = channels.map((channel, index) => {
      const positionId = channel?.positionId || channel;
      const failed = positionId === failedPosition;
      const finalKohm = failed
        ? 46
        : Number((6.2 + index * 0.75 + this.random.between(-0.25, 0.25)).toFixed(2));
      return {
        channel,
        positionId,
        finalKohm,
        startKohm: Number((finalKohm + 8 + this.random.between(0.5, 2)).toFixed(2)),
      };
    });
    const emitFrame = (progress, completed = false) => {
      const results = channelModels.map((model, index) => {
        const valueKohm = interpolateImpedance(model.startKohm, model.finalKohm, progress);
        return {
          channelId: model.channel?.channelId || `ACQ-${index + 1}`,
          positionId: model.positionId,
          valueKohm,
          quality: impedanceQuality(valueKohm),
        };
      });
      const frame = {
        measurementId,
        status: completed ? (failedPosition ? "FAILED" : "PASSED") : "MEASURING",
        progress,
        stable: completed,
        channels: results,
        sourceKind: "SIMULATED",
      };
      this.emit(DEVICE_EVENTS.ACQUISITION_IMPEDANCE_FRAME, frame, { runId });
      if (completed) {
        this.impedanceResult = frame;
        this.impedanceCancellations = [];
        this.emit(DEVICE_EVENTS.ACQUISITION_IMPEDANCE_COMPLETED, frame, { runId });
      }
    };
    [0.25, 0.6, 1].forEach((progress) => {
      this.impedanceCancellations.push(this.clock.schedule(
        () => emitFrame(progress, progress === 1),
        this.scenario.impedanceDurationMs * progress,
      ));
    });
    return { measurementId };
  }

  stopImpedanceCheck() {
    this.impedanceCancellations.forEach((cancel) => cancel());
    this.impedanceCancellations = [];
    return { stopped: true };
  }

  setStimulationActive(active) {
    this.stimulationActive = Boolean(active);
  }

  startAcquisition({ runId = null } = {}) {
    if (this.connectionState !== "CONNECTED") throw new Error(DEVICE_ERROR_CODES.DEVICE_NOT_CONNECTED);
    if (!this.configuration) throw new Error(DEVICE_ERROR_CODES.ACQUISITION_CONFIG_INVALID);
    if (this.acquisitionState === "RUNNING") return { started: false };
    this.sampleIndex = 0;
    this.packetSequence = 0;
    this.pendingDroppedSamples = 0;
    this.lastAcceptedDeviceTimeUs = null;
    this.acquisitionState = "RUNNING";
    this.emit(DEVICE_EVENTS.ACQUISITION_STARTED, {
      sampleRateHz: this.configuration.sampleRateHz,
      channels: this.configuration.channels,
    }, { runId });
    this.scheduleFrame(runId);
    return { started: true };
  }

  scheduleFrame(runId) {
    if (this.acquisitionState !== "RUNNING") return;
    const sampleRateHz = Number(this.configuration.sampleRateHz);
    const sampleCount = Math.max(1, Math.round(sampleRateHz / 10));
    const packetDurationMs = sampleCount / sampleRateHz * 1000;
    this.cancelNextFrame = this.clock.schedule(() => {
      if (this.acquisitionState !== "RUNNING") return;
      this.packetSequence += 1;
      const frame = this.createFrame(sampleCount);
      frame.packetSequence = this.packetSequence;
      frame.generatedDeviceTimeUs = applyClockDrift(
        this.clock.nowUs(),
        this.scenario.clockDriftPpm,
      );
      const shouldDrop = (
        this.scenario.packetLossEveryN > 0
        && this.packetSequence % this.scenario.packetLossEveryN === 0
      );
      if (shouldDrop) {
        this.pendingDroppedSamples += sampleCount;
        this.emit(DEVICE_EVENTS.ACQUISITION_PACKET_DROPPED, {
          packetSequence: this.packetSequence,
          firstSampleIndex: frame.firstSampleIndex,
          sampleCount,
          reason: "SIMULATED_PACKET_LOSS",
        }, { runId });
      } else {
        frame.droppedSamplesBefore = this.pendingDroppedSamples;
        this.pendingDroppedSamples = 0;
        let cancelDelivery = null;
        cancelDelivery = this.clock.schedule(() => {
          this.deliveryCancellations.delete(cancelDelivery);
          if (this.acquisitionState !== "RUNNING") return;
          const deliveredDeviceTimeUs = applyClockDrift(
            this.clock.nowUs(),
            this.scenario.clockDriftPpm,
          );
          const shouldRollback = (
            this.scenario.timestampRollbackAtPacket > 0
            && frame.packetSequence === this.scenario.timestampRollbackAtPacket
          );
          const candidateDeviceTimeUs = shouldRollback
            ? Math.max(0, deliveredDeviceTimeUs - this.scenario.timestampRollbackUs)
            : deliveredDeviceTimeUs;
          if (
            this.lastAcceptedDeviceTimeUs !== null
            && candidateDeviceTimeUs < this.lastAcceptedDeviceTimeUs
          ) {
            this.pendingDroppedSamples += frame.droppedSamplesBefore + frame.sampleCount;
            this.emit(DEVICE_EVENTS.ACQUISITION_FRAME_REJECTED, {
              packetSequence: frame.packetSequence,
              firstSampleIndex: frame.firstSampleIndex,
              sampleCount: frame.sampleCount,
              reason: DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK,
              candidateDeviceTimeUs,
              lastAcceptedDeviceTimeUs: this.lastAcceptedDeviceTimeUs,
            }, { runId });
            this.emit(DEVICE_EVENTS.FAULT, createSimulationFault(
              DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK,
              {
                runId,
                packetSequence: frame.packetSequence,
                candidateDeviceTimeUs,
                lastAcceptedDeviceTimeUs: this.lastAcceptedDeviceTimeUs,
                action: this.scenario.timestampRollbackPolicy,
              },
              {
                severity: this.scenario.timestampRollbackPolicy === "ABORT" ? "CRITICAL" : "WARNING",
                recoverable: this.scenario.timestampRollbackPolicy !== "ABORT",
              },
            ), { runId });
            return;
          }
          frame.deliveredDeviceTimeUs = candidateDeviceTimeUs;
          this.lastAcceptedDeviceTimeUs = candidateDeviceTimeUs;
          this.emit(DEVICE_EVENTS.EEG_FRAME, frame, {
            runId,
            deviceTimeUs: candidateDeviceTimeUs,
          });
        }, this.scenario.packetLatencyMs);
        this.deliveryCancellations.add(cancelDelivery);
      }
      this.scheduleFrame(runId);
    }, packetDurationMs);
  }

  createFrame(sampleCount) {
    const firstSampleIndex = this.sampleIndex;
    const sampleRateHz = Number(this.configuration.sampleRateHz);
    const channelValues = this.configuration.channels.map((channel, channelIndex) => {
      const samples = [];
      const artifactLabels = new Set();
      for (let offset = 0; offset < sampleCount; offset += 1) {
        const absoluteIndex = firstSampleIndex + offset;
        const seconds = absoluteIndex / sampleRateHz;
        const sample = createVirtualEegSample({
          seconds,
          channelIndex,
          positionId: channel.positionId || channel.label,
          noiseUv: this.random.between(-1.2, 1.2),
          stimulationActive: this.stimulationActive,
          profile: this.scenario.patientSignalProfile,
        });
        sample.artifactLabels.forEach((label) => artifactLabels.add(label));
        samples.push(Number(sample.valueUv.toFixed(4)));
      }
      return { channel, samplesUv: samples, artifactLabels: [...artifactLabels] };
    });
    this.sampleIndex += sampleCount;
    return {
      firstSampleIndex,
      sampleCount,
      sampleRateHz,
      channels: this.configuration.channels,
      samples: {
        encoding: "JSON_FLOAT_UV",
        channelValues,
      },
      sourceKind: "SIMULATED",
      signalModelVersion: VIRTUAL_SIGNAL_MODEL_VERSION,
      signalProfileId: this.scenario.patientSignalProfile.profileId,
      stimulationActive: this.stimulationActive,
      artifactLabels: [...new Set(channelValues.flatMap((channel) => channel.artifactLabels))],
      scenarioId: this.scenario.scenarioId,
      seed: this.scenario.seed,
      droppedSamplesBefore: 0,
      samplePeriodDeviceUs: (
        1_000_000
        / sampleRateHz
        * (1 + this.scenario.clockDriftPpm / 1_000_000)
      ),
      firstSampleDeviceTimeUs: applyClockDrift(
        firstSampleIndex / sampleRateHz * 1_000_000,
        this.scenario.clockDriftPpm,
      ),
      simulatedLatencyMs: this.scenario.packetLatencyMs,
      clockDriftPpm: this.scenario.clockDriftPpm,
    };
  }

  stopAcquisition({ runId = null } = {}) {
    this.cancelNextFrame?.();
    this.cancelNextFrame = null;
    this.deliveryCancellations.forEach((cancel) => cancel());
    this.deliveryCancellations.clear();
    this.pendingDroppedSamples = 0;
    if (this.acquisitionState !== "RUNNING") return { stopped: false };
    this.acquisitionState = "STOPPED";
    this.emit(DEVICE_EVENTS.ACQUISITION_STOPPED, {
      finalSampleIndex: this.sampleIndex,
    }, { runId });
    return { stopped: true };
  }

  pushMarker(label, payload = {}, { runId = null } = {}) {
    return this.emit(DEVICE_EVENTS.MARKER, {
      markerId: `${label}-${this.sampleIndex}`,
      label,
      sampleIndex: this.sampleIndex,
      ...payload,
    }, { runId });
  }

  destroy() {
    this.stopImpedanceCheck();
    this.stopAcquisition();
    this.destroySource();
  }
}

export class VirtualStimulationAdapter extends ProtocolEventSource {
  constructor({ capability, clock, random, eventSink, scenario }) {
    super({
      deviceId: `${capability.identity.deviceId}-STIM`,
      clock,
      eventSink,
      clockDriftPpm: scenario.clockDriftPpm,
    });
    this.capability = capability;
    this.clock = clock;
    this.random = random;
    this.scenario = scenario;
    this.connectionState = "DISCONNECTED";
    this.runState = "IDLE";
    this.program = null;
    this.impedanceResult = null;
    this.impedanceCancellations = [];
    this.runCancellations = [];
  }

  connect() {
    this.connectionState = "CONNECTED";
    this.emit(DEVICE_EVENTS.CONNECTION_CHANGED, {
      state: "CONNECTED",
      sourceKind: "SIMULATED",
    });
    return Promise.resolve(this.getIdentity());
  }

  disconnect() {
    this.reset();
    this.connectionState = "DISCONNECTED";
    this.emit(DEVICE_EVENTS.CONNECTION_CHANGED, { state: "DISCONNECTED" });
  }

  simulateDisconnect({ runId = null, reason = DEVICE_ERROR_CODES.TRANSPORT_DISCONNECTED } = {}) {
    if (this.runState === "RUNNING") this.abortRun(reason, runId);
    this.connectionState = "DISCONNECTED";
    this.emit(DEVICE_EVENTS.CONNECTION_CHANGED, {
      state: "DISCONNECTED",
      reason,
      sourceKind: "SIMULATED",
    }, { runId });
  }

  getIdentity() {
    return { ...this.capability.identity, deviceId: this.deviceId };
  }

  getCapabilities() {
    return structuredClone(this.capability.stimulation);
  }

  prepareProgram(program) {
    if (this.connectionState !== "CONNECTED") throw new Error(DEVICE_ERROR_CODES.DEVICE_NOT_CONNECTED);
    if (!program?.protocolHash || !program?.paradigmId || !Array.isArray(program?.segments)) {
      throw new Error(DEVICE_ERROR_CODES.STIMULATION_PROGRAM_INVALID);
    }
    const topologyAliases = {
      DUAL: "DUAL_ELECTRODE",
      HD: "HD_4X1",
      MULTI: "MULTICHANNEL",
    };
    const normalizedTopologyId = topologyAliases[program.topologyId] || program.topologyId;
    const paradigmSupported = this.capability.stimulation.supportedParadigms.includes(
      program.paradigmId,
    );
    const topologySupported = (
      !normalizedTopologyId
      || this.capability.stimulation.supportedTopologies.includes(normalizedTopologyId)
    );
    if (!paradigmSupported || !topologySupported) {
      throw new Error([
        DEVICE_ERROR_CODES.CAPABILITY_MISMATCH,
        !paradigmSupported ? `PARADIGM:${program.paradigmId}` : "",
        !topologySupported ? `TOPOLOGY:${normalizedTopologyId}` : "",
      ].filter(Boolean).join(":"));
    }
    const impedancePreserved = Boolean(
      this.program?.montageHash
      && program.montageHash
      && this.program.montageHash === program.montageHash
      && this.impedanceResult?.status === "PASSED",
    );
    this.program = structuredClone({
      ...program,
      topologyId: normalizedTopologyId,
    });
    if (!impedancePreserved) this.impedanceResult = null;
    this.runState = "PREPARED";
    this.emit(DEVICE_EVENTS.STIMULATION_PREPARED, {
      protocolHash: program.protocolHash,
      paradigmId: program.paradigmId,
      montageHash: program.montageHash || null,
      impedancePreserved,
    });
    return {
      accepted: true,
      protocolHash: program.protocolHash,
      impedancePreserved,
    };
  }

  startImpedanceCheck({ channels = [], runId = null } = {}) {
    if (!this.program) throw new Error(DEVICE_ERROR_CODES.STIMULATION_PROGRAM_INVALID);
    this.stopImpedanceCheck();
    const measurementId = `IMP-${this.scenario.seed}-${Math.round(this.clock.nowUs() / 1000)}`;
    this.emit(DEVICE_EVENTS.IMPEDANCE_STARTED, { measurementId }, { runId });
    const failedChannel = this.scenario.stimulationImpedancePass === false
      ? (channels[0]?.channelId || "STIM-1")
      : null;
    const channelModels = channels.map((channel, index) => {
      const failed = channel.channelId === failedChannel;
      const finalKohm = failed
        ? 48
        : Number((5.5 + index * 0.7 + this.random.between(-0.2, 0.2)).toFixed(2));
      return {
        channel,
        finalKohm,
        startKohm: Number((finalKohm + 7 + this.random.between(0.5, 2)).toFixed(2)),
      };
    });
    const emitFrame = (progress, completed = false) => {
      const results = channelModels.map((model) => {
        const valueKohm = interpolateImpedance(model.startKohm, model.finalKohm, progress);
        return {
          ...model.channel,
          valueKohm,
          quality: impedanceQuality(valueKohm),
        };
      });
      const frame = {
        measurementId,
        status: completed ? (failedChannel ? "FAILED" : "PASSED") : "MEASURING",
        progress,
        stable: completed,
        channels: results,
        sourceKind: "SIMULATED",
      };
      this.emit(DEVICE_EVENTS.IMPEDANCE_FRAME, frame, { runId });
      if (completed) {
        this.impedanceResult = frame;
        this.impedanceCancellations = [];
        this.emit(DEVICE_EVENTS.IMPEDANCE_COMPLETED, frame, { runId });
      }
    };
    [0.25, 0.6, 1].forEach((progress) => {
      this.impedanceCancellations.push(this.clock.schedule(
        () => emitFrame(progress, progress === 1),
        this.scenario.impedanceDurationMs * progress,
      ));
    });
    return { measurementId };
  }

  stopImpedanceCheck() {
    this.impedanceCancellations.forEach((cancel) => cancel());
    this.impedanceCancellations = [];
    return { stopped: true };
  }

  arm(prerequisites) {
    const validation = validateStimulationPrerequisites({
      capability: this.capability,
      program: this.program,
      ...prerequisites,
    });
    if (!validation.isValid) throw new Error(validation.errors.join(","));
    this.runState = "ARMED";
    this.emit(DEVICE_EVENTS.STIMULATION_ARMED, {
      protocolHash: this.program.protocolHash,
    });
    return { armed: true };
  }

  startRun(runId) {
    if (this.runState !== "ARMED") throw new Error(DEVICE_ERROR_CODES.DEVICE_NOT_ARMED);
    this.clearRunSchedule();
    this.runState = "RUNNING";
    this.emit(DEVICE_EVENTS.STIMULATION_STARTED, {
      protocolHash: this.program.protocolHash,
    }, { runId });
    let elapsedMs = 0;
    this.program.segments.forEach((segment, index) => {
      const stageStartMs = elapsedMs;
      this.runCancellations.push(this.clock.schedule(() => {
        if (this.runState !== "RUNNING") return;
        this.emit(DEVICE_EVENTS.STIMULATION_STAGE_CHANGED, {
          stage: segment.type,
          segmentIndex: index,
          programTimeUs: Math.round(stageStartMs * 1000),
        }, { runId });
      }, stageStartMs));
      elapsedMs += Number(segment.durationMs);
    });
    this.runCancellations.push(this.clock.schedule(() => {
      if (this.runState !== "RUNNING") return;
      this.runState = "COMPLETED";
      this.emit(DEVICE_EVENTS.STIMULATION_COMPLETED, {
        protocolHash: this.program.protocolHash,
        totalDurationMs: elapsedMs,
      }, { runId });
      this.runCancellations = [];
    }, elapsedMs));
    return { started: true, runId };
  }

  abortRun(reason = DEVICE_ERROR_CODES.OPERATOR_ABORT, runId = null) {
    if (this.runState !== "RUNNING") return { aborted: false };
    this.clearRunSchedule();
    this.runState = "ABORTED";
    this.emit(DEVICE_EVENTS.STIMULATION_ABORTED, {
      reason,
      safeStateConfirmed: true,
    }, { runId });
    return { aborted: true, reason };
  }

  clearRunSchedule() {
    this.runCancellations.forEach((cancel) => cancel());
    this.runCancellations = [];
  }

  reset() {
    this.stopImpedanceCheck();
    this.clearRunSchedule();
    this.runState = "IDLE";
    this.program = null;
    this.impedanceResult = null;
    return { reset: true };
  }

  destroy() {
    this.reset();
    this.destroySource();
  }
}

export class VirtualDeviceSession {
  constructor({
    scenarioId = "manual-happy-path",
    seed = "patient-demo-001",
    clockMode = "manual",
    timeScale = 1,
    capability,
    stimulationImpedancePass = true,
    acquisitionImpedancePass = true,
    impedanceDurationMs = 400,
    patientSignalProfile = {},
    packetLossEveryN = 0,
    packetLatencyMs = 0,
    clockDriftPpm = 0,
    runtimeElectrodeFaultAtMs = 0,
    runtimeElectrodePositionId = "F3",
    runtimeElectrodePolicy = "WARN",
    disconnectAtMs = 0,
    timestampRollbackAtPacket = 0,
    timestampRollbackUs = 0,
    timestampRollbackPolicy = "ABORT",
  } = {}) {
    this.scenario = {
      scenarioId,
      seed,
      simulatorVersion: SIMULATOR_VERSION,
      stimulationImpedancePass,
      acquisitionImpedancePass,
      impedanceDurationMs,
      patientSignalProfile: createPatientSignalProfile(seed, patientSignalProfile),
      packetLossEveryN: Math.max(0, Math.floor(Number(packetLossEveryN) || 0)),
      packetLatencyMs: Math.max(0, Number(packetLatencyMs) || 0),
      clockDriftPpm: Number(clockDriftPpm) || 0,
      runtimeElectrodeFaultAtMs: Math.max(0, Number(runtimeElectrodeFaultAtMs) || 0),
      runtimeElectrodePositionId: String(runtimeElectrodePositionId || "F3"),
      runtimeElectrodePolicy: runtimeElectrodePolicy === "ABORT" ? "ABORT" : "WARN",
      disconnectAtMs: Math.max(0, Number(disconnectAtMs) || 0),
      timestampRollbackAtPacket: Math.max(0, Math.floor(Number(timestampRollbackAtPacket) || 0)),
      timestampRollbackUs: Math.max(0, Number(timestampRollbackUs) || 0),
      timestampRollbackPolicy: timestampRollbackPolicy === "WARN" ? "WARN" : "ABORT",
    };
    this.clock = createVirtualClock({ mode: clockMode, timeScale });
    this.capability = capability || createDeviceCapability();
    this.eventLog = [];
    this.listeners = new Set();
    this.runtimeFaultCancellations = [];
    this.activeFaultRunId = null;
    const eventSink = (event) => this.recordEvent(event);
    this.acquisition = new VirtualAcquisitionAdapter({
      capability: this.capability,
      clock: this.clock,
      random: createSeededRandom(`${seed}:acquisition`),
      eventSink,
      scenario: this.scenario,
    });
    this.stimulation = new VirtualStimulationAdapter({
      capability: this.capability,
      clock: this.clock,
      random: createSeededRandom(`${seed}:stimulation`),
      eventSink,
      scenario: this.scenario,
    });
  }

  recordEvent(event) {
    const record = {
      receivedOrder: this.eventLog.length + 1,
      scenarioId: this.scenario.scenarioId,
      simulatorVersion: this.scenario.simulatorVersion,
      ...event,
    };
    this.eventLog.push(record);
    this.listeners.forEach((listener) => listener(record));
    if (
      event.messageType === DEVICE_EVENTS.STIMULATION_STARTED
      || event.messageType === DEVICE_EVENTS.STIMULATION_STAGE_CHANGED
      || event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED
      || event.messageType === DEVICE_EVENTS.STIMULATION_ABORTED
    ) {
      this.acquisition.pushMarker(event.messageType, event.payload, { runId: event.runId });
    }
    if (event.messageType === DEVICE_EVENTS.STIMULATION_STARTED) {
      this.acquisition.setStimulationActive(true);
    }
    if (
      event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED
      || event.messageType === DEVICE_EVENTS.STIMULATION_ABORTED
    ) {
      this.acquisition.setStimulationActive(false);
    }
    if (event.messageType === DEVICE_EVENTS.ACQUISITION_STARTED) {
      this.scheduleRuntimeFaults(event.runId);
    }
    if (event.messageType === DEVICE_EVENTS.FAULT) {
      this.applyFaultSafetyAction(event);
    }
    if (
      event.messageType === DEVICE_EVENTS.ACQUISITION_STOPPED
      || event.messageType === DEVICE_EVENTS.STIMULATION_COMPLETED
      || event.messageType === DEVICE_EVENTS.STIMULATION_ABORTED
    ) {
      this.clearRuntimeFaultSchedule(event.runId);
    }
  }

  scheduleRuntimeFaults(runId) {
    this.clearRuntimeFaultSchedule();
    this.activeFaultRunId = runId;
    if (this.scenario.runtimeElectrodeFaultAtMs > 0) {
      this.runtimeFaultCancellations.push(this.clock.schedule(() => {
        if (this.activeFaultRunId !== runId || this.acquisition.acquisitionState !== "RUNNING") return;
        const details = {
          runId,
          positionId: this.scenario.runtimeElectrodePositionId,
          valueKohm: 55,
          quality: "BAD",
          state: "DETACHED",
          action: this.scenario.runtimeElectrodePolicy,
        };
        this.acquisition.emit(DEVICE_EVENTS.ACQUISITION_CONTACT_CHANGED, details, { runId });
        this.acquisition.emit(DEVICE_EVENTS.FAULT, createSimulationFault(
          DEVICE_ERROR_CODES.ELECTRODE_CONTACT_LOST,
          details,
          {
            severity: this.scenario.runtimeElectrodePolicy === "ABORT" ? "CRITICAL" : "WARNING",
            recoverable: this.scenario.runtimeElectrodePolicy !== "ABORT",
          },
        ), { runId });
      }, this.scenario.runtimeElectrodeFaultAtMs));
    }
    if (this.scenario.disconnectAtMs > 0) {
      this.runtimeFaultCancellations.push(this.clock.schedule(() => {
        if (this.activeFaultRunId !== runId) return;
        this.acquisition.emit(DEVICE_EVENTS.FAULT, createSimulationFault(
          DEVICE_ERROR_CODES.TRANSPORT_DISCONNECTED,
          {
            runId,
            action: "ABORT",
            transport: this.capability.transport.type,
          },
          {
            severity: "CRITICAL",
            recoverable: true,
          },
        ), { runId });
      }, this.scenario.disconnectAtMs));
    }
  }

  clearRuntimeFaultSchedule(runId = null) {
    if (runId && this.activeFaultRunId && runId !== this.activeFaultRunId) return;
    this.runtimeFaultCancellations.forEach((cancel) => cancel());
    this.runtimeFaultCancellations = [];
    this.activeFaultRunId = null;
  }

  applyFaultSafetyAction(event) {
    const { code, details = {} } = event.payload || {};
    if (details.action !== "ABORT") return;
    const runId = event.runId || details.runId || null;
    this.clearRuntimeFaultSchedule(runId);
    this.acquisition.setStimulationActive(false);
    if (code === DEVICE_ERROR_CODES.TRANSPORT_DISCONNECTED) {
      this.acquisition.stopAcquisition({ runId });
      this.acquisition.connectionState = "DISCONNECTED";
      this.acquisition.emit(DEVICE_EVENTS.CONNECTION_CHANGED, {
        state: "DISCONNECTED",
        reason: code,
        sourceKind: "SIMULATED",
      }, { runId });
      this.stimulation.simulateDisconnect({ runId, reason: code });
      return;
    }
    this.stimulation.abortRun(code, runId);
    this.acquisition.stopAcquisition({ runId });
  }

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async connect() {
    await Promise.all([this.acquisition.connect(), this.stimulation.connect()]);
    return {
      acquisition: this.acquisition.getIdentity(),
      stimulation: this.stimulation.getIdentity(),
      capability: structuredClone(this.capability),
    };
  }

  disconnect() {
    this.clearRuntimeFaultSchedule();
    this.acquisition.setStimulationActive(false);
    this.acquisition.disconnect();
    this.stimulation.disconnect();
  }

  advanceBy(durationMs) {
    this.clock.advanceBy(durationMs);
  }

  getEventLog() {
    return structuredClone(this.eventLog);
  }

  getSnapshot() {
    return {
      scenario: structuredClone(this.scenario),
      clockUs: this.clock.nowUs(),
      acquisition: {
        connectionState: this.acquisition.connectionState,
        state: this.acquisition.acquisitionState,
        sampleIndex: this.acquisition.sampleIndex,
        impedance: this.acquisition.impedanceResult?.status || "NOT_RUN",
        stimulationActive: this.acquisition.stimulationActive,
      },
      stimulation: {
        connectionState: this.stimulation.connectionState,
        state: this.stimulation.runState,
        impedance: this.stimulation.impedanceResult?.status || "NOT_RUN",
      },
    };
  }

  destroy() {
    this.clearRuntimeFaultSchedule();
    this.acquisition.destroy();
    this.stimulation.destroy();
    this.clock.clear();
    this.listeners.clear();
  }
}

export function createSimulationFault(code, details = {}, options = {}) {
  return createDeviceError(
    code,
    SIMULATION_FAULT_MESSAGES[code] || `虚拟设备故障：${code}`,
    details,
    {
    source: "SYSTEM",
    recoverable: options.recoverable ?? code !== DEVICE_ERROR_CODES.HARDWARE_FAULT,
    severity: options.severity || "ERROR",
    },
  );
}
