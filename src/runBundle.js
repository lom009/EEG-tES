import { DEVICE_EVENTS } from "./deviceProtocolV1.js";

export const RUN_BUNDLE_VERSION = "1.0";
export const RUN_BUNDLE_PORTABLE_FORMAT = "EEG_TES_RUN_BUNDLE_PORTABLE_V1";
export const RUN_BUNDLE_SOURCE_KINDS = ["SIMULATED", "REPLAY", "HARDWARE"];

const REQUIRED_FILES = [
  "manifest.json",
  "patient-snapshot.json",
  "operator.json",
  "capability.json",
  "protocol.json",
  "montage.json",
  "tolerance-record.json",
  "impedance.ndjson",
  "eeg-raw.bin",
  "events.ndjson",
  "result.json",
  "checksums.json",
];

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function clone(value) {
  return value === undefined ? undefined : structuredClone(value);
}

function toBytes(value) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  return encoder.encode(String(value ?? ""));
}

function joinBytes(chunks) {
  const totalLength = chunks.reduce((total, chunk) => total + chunk.byteLength, 0);
  const result = new Uint8Array(totalLength);
  let offset = 0;
  chunks.forEach((chunk) => {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  });
  return result;
}

function checksumFNV1A32(value) {
  const bytes = toBytes(value);
  let hash = 2166136261;
  for (const byte of bytes) {
    hash ^= byte;
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}

function bytesToBase64(value) {
  const bytes = toBytes(value);
  let result = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const second = index + 1 < bytes.length ? bytes[index + 1] : 0;
    const third = index + 2 < bytes.length ? bytes[index + 2] : 0;
    const packed = (first << 16) | (second << 8) | third;
    result += BASE64_ALPHABET[(packed >> 18) & 63];
    result += BASE64_ALPHABET[(packed >> 12) & 63];
    result += index + 1 < bytes.length ? BASE64_ALPHABET[(packed >> 6) & 63] : "=";
    result += index + 2 < bytes.length ? BASE64_ALPHABET[packed & 63] : "=";
  }
  return result;
}

function base64ToBytes(value) {
  const normalized = String(value).replace(/\s/g, "");
  const output = [];
  for (let index = 0; index < normalized.length; index += 4) {
    const first = BASE64_ALPHABET.indexOf(normalized[index]);
    const second = BASE64_ALPHABET.indexOf(normalized[index + 1]);
    const third = normalized[index + 2] === "=" ? 0 : BASE64_ALPHABET.indexOf(normalized[index + 2]);
    const fourth = normalized[index + 3] === "=" ? 0 : BASE64_ALPHABET.indexOf(normalized[index + 3]);
    const packed = (first << 18) | (second << 12) | (third << 6) | fourth;
    output.push((packed >> 16) & 255);
    if (normalized[index + 2] !== "=") output.push((packed >> 8) & 255);
    if (normalized[index + 3] !== "=") output.push(packed & 255);
  }
  return Uint8Array.from(output);
}

function toJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function toNdjson(rows) {
  return rows.map((row) => JSON.stringify(row)).join("\n") + (rows.length ? "\n" : "");
}

function parseNdjson(value) {
  return String(value || "")
    .split("\n")
    .filter(Boolean)
    .map((row) => JSON.parse(row));
}

function getChannelPosition(channel, index) {
  return channel?.positionId || channel?.label || channel?.channelId || `CH-${index + 1}`;
}

function encodeEegFrames(events) {
  const chunks = [];
  const encodedEvents = [];
  let offsetBytes = 0;
  let totalSampleValues = 0;

  events.forEach((event) => {
    if (event.messageType !== DEVICE_EVENTS.EEG_FRAME) {
      encodedEvents.push(clone(event));
      return;
    }
    const encodedEvent = clone(event);
    const channelValues = event.payload?.samples?.channelValues || [];
    const sampleCount = Number(event.payload?.sampleCount) || 0;
    const channelCount = channelValues.length;
    const frameBytes = new Uint8Array(channelCount * sampleCount * Float32Array.BYTES_PER_ELEMENT);
    const view = new DataView(frameBytes.buffer);
    const channelOrder = [];
    const channelArtifactLabels = [];
    let byteOffset = 0;

    channelValues.forEach((entry, channelIndex) => {
      channelOrder.push(getChannelPosition(entry.channel, channelIndex));
      channelArtifactLabels.push(entry.artifactLabels || []);
      const samples = entry.samplesUv || [];
      for (let sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1) {
        view.setFloat32(byteOffset, Number(samples[sampleIndex]) || 0, true);
        byteOffset += Float32Array.BYTES_PER_ELEMENT;
      }
    });

    encodedEvent.payload.samples = {
      encoding: "FLOAT32_LE_CHANNEL_MAJOR",
      file: "eeg-raw.bin",
      offsetBytes,
      byteLength: frameBytes.byteLength,
      channelCount,
      sampleCount,
      channelOrder,
      channelArtifactLabels,
    };
    chunks.push(frameBytes);
    offsetBytes += frameBytes.byteLength;
    totalSampleValues += channelCount * sampleCount;
    encodedEvents.push(encodedEvent);
  });

  return {
    events: encodedEvents,
    raw: joinBytes(chunks),
    frameCount: encodedEvents.filter((event) => event.messageType === DEVICE_EVENTS.EEG_FRAME).length,
    totalSampleValues,
  };
}

function decodeEegFrame(event, rawBytes) {
  if (event.messageType !== DEVICE_EVENTS.EEG_FRAME) return clone(event);
  const decodedEvent = clone(event);
  const descriptor = event.payload?.samples;
  if (descriptor?.encoding !== "FLOAT32_LE_CHANNEL_MAJOR") return decodedEvent;
  const view = new DataView(
    rawBytes.buffer,
    rawBytes.byteOffset + descriptor.offsetBytes,
    descriptor.byteLength,
  );
  let byteOffset = 0;
  const channelValues = descriptor.channelOrder.map((positionId, channelIndex) => {
    const samplesUv = [];
    for (let sampleIndex = 0; sampleIndex < descriptor.sampleCount; sampleIndex += 1) {
      samplesUv.push(Number(view.getFloat32(byteOffset, true).toFixed(4)));
      byteOffset += Float32Array.BYTES_PER_ELEMENT;
    }
    return {
      channel: decodedEvent.payload.channels?.[channelIndex] || { positionId },
      samplesUv,
      artifactLabels: descriptor.channelArtifactLabels?.[channelIndex] || [],
    };
  });
  decodedEvent.payload.samples = {
    encoding: "JSON_FLOAT_UV",
    channelValues,
  };
  return decodedEvent;
}

export function createRunBundle({
  runId,
  eventLog = [],
  patient = {},
  operator = {},
  capability = {},
  protocol = {},
  montage = {},
  toleranceRecord = null,
  result = {},
  sourceKind = "SIMULATED",
  createdAt = new Date().toISOString(),
} = {}) {
  if (!runId) throw new Error("RUN_BUNDLE_RUN_ID_REQUIRED");
  if (!RUN_BUNDLE_SOURCE_KINDS.includes(sourceKind)) {
    throw new Error(`RUN_BUNDLE_SOURCE_KIND_INVALID:${sourceKind}`);
  }
  const selectedEvents = eventLog.filter((event) => (
    event.runId == null || event.runId === runId
  ));
  const eeg = encodeEegFrames(selectedEvents);
  const deviceTimes = eeg.events
    .map((event) => Number(event.deviceTimeUs))
    .filter(Number.isFinite);
  const manifest = {
    bundleVersion: RUN_BUNDLE_VERSION,
    runId,
    createdAt,
    sourceKind,
    simulatorVersion: selectedEvents.find((event) => event.simulatorVersion)?.simulatorVersion || null,
    protocolVersion: selectedEvents.find((event) => event.protocolVersion)?.protocolVersion || null,
    timeRangeDeviceUs: {
      start: deviceTimes.length ? Math.min(...deviceTimes) : 0,
      end: deviceTimes.length ? Math.max(...deviceTimes) : 0,
    },
    eeg: {
      file: "eeg-raw.bin",
      encoding: "FLOAT32_LE_CHANNEL_MAJOR",
      frameCount: eeg.frameCount,
      totalSampleValues: eeg.totalSampleValues,
      byteLength: eeg.raw.byteLength,
    },
    files: REQUIRED_FILES,
  };
  const impedanceEvents = eeg.events.filter((event) => (
    event.messageType.includes("IMPEDANCE")
  ));
  const files = {
    "manifest.json": toJson(manifest),
    "patient-snapshot.json": toJson(patient),
    "operator.json": toJson(operator),
    "capability.json": toJson(capability),
    "protocol.json": toJson(protocol),
    "montage.json": toJson(montage),
    "tolerance-record.json": toJson(toleranceRecord),
    "impedance.ndjson": toNdjson(impedanceEvents),
    "eeg-raw.bin": eeg.raw,
    "events.ndjson": toNdjson(eeg.events),
    "result.json": toJson({
      ...result,
      runId,
      sourceKind,
    }),
  };
  const checksums = Object.fromEntries(
    Object.entries(files).map(([name, value]) => [name, checksumFNV1A32(value)]),
  );
  files["checksums.json"] = toJson({
    algorithm: "FNV1A32",
    files: checksums,
  });
  return {
    manifest,
    files,
  };
}

export function validateRunBundle(bundle) {
  const errors = [];
  if (!bundle?.files) return { isValid: false, errors: ["RUN_BUNDLE_FILES_MISSING"] };
  REQUIRED_FILES.forEach((name) => {
    if (!(name in bundle.files)) errors.push(`RUN_BUNDLE_FILE_MISSING:${name}`);
  });
  if (errors.length) return { isValid: false, errors };

  let manifest;
  let checksums;
  try {
    manifest = JSON.parse(String(bundle.files["manifest.json"]));
    checksums = JSON.parse(String(bundle.files["checksums.json"]));
    parseNdjson(bundle.files["events.ndjson"]);
  } catch (error) {
    return { isValid: false, errors: [`RUN_BUNDLE_PARSE_FAILED:${error.message}`] };
  }
  if (manifest.bundleVersion !== RUN_BUNDLE_VERSION) {
    errors.push(`RUN_BUNDLE_VERSION_UNSUPPORTED:${manifest.bundleVersion}`);
  }
  if (!manifest.runId) errors.push("RUN_BUNDLE_RUN_ID_MISSING");
  if (!RUN_BUNDLE_SOURCE_KINDS.includes(manifest.sourceKind)) {
    errors.push(`RUN_BUNDLE_SOURCE_KIND_INVALID:${manifest.sourceKind}`);
  }
  Object.entries(checksums.files || {}).forEach(([name, expected]) => {
    if (!(name in bundle.files)) return;
    const actual = checksumFNV1A32(bundle.files[name]);
    if (actual !== expected) errors.push(`RUN_BUNDLE_CHECKSUM_MISMATCH:${name}`);
  });
  return {
    isValid: errors.length === 0,
    errors,
    manifest,
  };
}

export function serializeRunBundlePortable(bundle) {
  const validation = validateRunBundle(bundle);
  if (!validation.isValid) throw new Error(validation.errors.join(","));
  return JSON.stringify({
    format: RUN_BUNDLE_PORTABLE_FORMAT,
    bundleVersion: RUN_BUNDLE_VERSION,
    files: Object.fromEntries(
      Object.entries(bundle.files).map(([name, value]) => (
        value instanceof Uint8Array
          ? [name, { encoding: "base64", data: bytesToBase64(value) }]
          : [name, { encoding: "utf8", data: String(value) }]
      )),
    ),
  });
}

export function deserializeRunBundlePortable(value) {
  const portable = typeof value === "string" ? JSON.parse(value) : value;
  if (portable?.format !== RUN_BUNDLE_PORTABLE_FORMAT) {
    throw new Error("RUN_BUNDLE_PORTABLE_FORMAT_INVALID");
  }
  const files = Object.fromEntries(
    Object.entries(portable.files || {}).map(([name, entry]) => [
      name,
      entry.encoding === "base64" ? base64ToBytes(entry.data) : String(entry.data),
    ]),
  );
  const bundle = {
    manifest: JSON.parse(String(files["manifest.json"])),
    files,
  };
  const validation = validateRunBundle(bundle);
  if (!validation.isValid) throw new Error(validation.errors.join(","));
  return bundle;
}

export function readRunBundleEvents(bundle) {
  const validation = validateRunBundle(bundle);
  if (!validation.isValid) throw new Error(validation.errors.join(","));
  const events = parseNdjson(bundle.files["events.ndjson"]);
  const raw = toBytes(bundle.files["eeg-raw.bin"]);
  return events.map((event) => decodeEegFrame(event, raw));
}

export class ReplayDeviceSession {
  constructor(bundle, { mode = "manual", speed = 1 } = {}) {
    const validation = validateRunBundle(bundle);
    if (!validation.isValid) throw new Error(validation.errors.join(","));
    this.bundle = bundle;
    this.manifest = validation.manifest;
    this.events = readRunBundleEvents(bundle).sort((left, right) => (
      Number(left.deviceTimeUs) - Number(right.deviceTimeUs)
      || Number(left.receivedOrder || left.sequence) - Number(right.receivedOrder || right.sequence)
    ));
    this.mode = mode;
    this.speed = Math.max(0.1, Number(speed) || 1);
    this.listeners = new Set();
    this.stateListeners = new Set();
    this.cursor = 0;
    this.state = "IDLE";
    this.timer = null;
    this.startDeviceTimeUs = this.events[0]?.deviceTimeUs || 0;
    this.endDeviceTimeUs = this.events.at(-1)?.deviceTimeUs || this.startDeviceTimeUs;
    this.positionDeviceTimeUs = this.startDeviceTimeUs;
  }

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  subscribeState(listener) {
    this.stateListeners.add(listener);
    listener(this.getSnapshot());
    return () => this.stateListeners.delete(listener);
  }

  notifyState() {
    const snapshot = this.getSnapshot();
    this.stateListeners.forEach((listener) => listener(snapshot));
    return snapshot;
  }

  emit(event) {
    const replayed = {
      ...clone(event),
      sourceKind: "REPLAY",
      replay: {
        originalSourceKind: this.manifest.sourceKind,
        originalDeviceTimeUs: event.deviceTimeUs,
      },
    };
    this.listeners.forEach((listener) => listener(replayed));
    return replayed;
  }

  setSpeed(speed) {
    const wasPlaying = this.state === "PLAYING";
    if (wasPlaying) this.pause();
    this.speed = Math.max(0.1, Number(speed) || 1);
    if (wasPlaying) this.play();
    else this.notifyState();
    return this.speed;
  }

  seek(deviceTimeUs) {
    this.pause();
    const target = Math.min(
      this.endDeviceTimeUs,
      Math.max(this.startDeviceTimeUs, Number(deviceTimeUs) || this.startDeviceTimeUs),
    );
    this.positionDeviceTimeUs = target;
    this.cursor = this.events.findIndex((event) => Number(event.deviceTimeUs) >= target);
    if (this.cursor < 0) this.cursor = this.events.length;
    this.state = this.cursor >= this.events.length ? "COMPLETED" : "PAUSED";
    return this.notifyState();
  }

  advanceToDeviceTimeUs(deviceTimeUs) {
    const target = Math.min(this.endDeviceTimeUs, Math.max(this.positionDeviceTimeUs, deviceTimeUs));
    while (
      this.cursor < this.events.length
      && Number(this.events[this.cursor].deviceTimeUs) <= target
    ) {
      this.emit(this.events[this.cursor]);
      this.cursor += 1;
    }
    this.positionDeviceTimeUs = target;
    if (this.cursor >= this.events.length) this.state = "COMPLETED";
    else if (this.state !== "PLAYING") this.state = "PAUSED";
    return this.notifyState();
  }

  advanceBy(durationMs) {
    if (this.mode !== "manual") throw new Error("REPLAY_SESSION_NOT_MANUAL");
    return this.advanceToDeviceTimeUs(
      this.positionDeviceTimeUs + Math.max(0, Number(durationMs) || 0) * 1000 * this.speed,
    );
  }

  scheduleNext() {
    if (this.state !== "PLAYING") return;
    if (this.cursor >= this.events.length) {
      this.state = "COMPLETED";
      this.notifyState();
      return;
    }
    const event = this.events[this.cursor];
    const delayMs = Math.max(
      0,
      (Number(event.deviceTimeUs) - this.positionDeviceTimeUs) / 1000 / this.speed,
    );
    this.timer = globalThis.setTimeout(() => {
      this.timer = null;
      this.positionDeviceTimeUs = Number(event.deviceTimeUs);
      this.emit(event);
      this.cursor += 1;
      this.notifyState();
      this.scheduleNext();
    }, delayMs);
  }

  play() {
    if (this.mode !== "realtime") throw new Error("REPLAY_SESSION_NOT_REALTIME");
    if (this.state === "COMPLETED") this.seek(this.startDeviceTimeUs);
    this.state = "PLAYING";
    this.notifyState();
    this.scheduleNext();
    return this.getSnapshot();
  }

  pause() {
    if (this.timer) globalThis.clearTimeout(this.timer);
    this.timer = null;
    if (this.state === "PLAYING") this.state = "PAUSED";
    return this.notifyState();
  }

  getSnapshot() {
    return {
      runId: this.manifest.runId,
      sourceKind: "REPLAY",
      originalSourceKind: this.manifest.sourceKind,
      state: this.state,
      speed: this.speed,
      cursor: this.cursor,
      eventCount: this.events.length,
      positionDeviceTimeUs: this.positionDeviceTimeUs,
      startDeviceTimeUs: this.startDeviceTimeUs,
      endDeviceTimeUs: this.endDeviceTimeUs,
    };
  }

  destroy() {
    this.pause();
    this.listeners.clear();
    this.stateListeners.clear();
  }
}

export const ReplayDeviceAdapter = ReplayDeviceSession;
