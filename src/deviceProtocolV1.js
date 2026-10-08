export const DEVICE_PROTOCOL_VERSION = "1.0";

export const DEVICE_DOMAINS = {
  SYSTEM: "SYSTEM",
  ACQUISITION: "ACQUISITION",
  STIMULATION: "STIMULATION",
};

export const DEVICE_COMMANDS = {
  CONNECT: "SYSTEM.CONNECT",
  DISCONNECT: "SYSTEM.DISCONNECT",
  QUERY_CAPABILITY: "SYSTEM.QUERY_CAPABILITY",
  RESET: "SYSTEM.RESET",
  CONFIGURE_ACQUISITION: "ACQUISITION.CONFIGURE",
  START_ACQUISITION_IMPEDANCE: "ACQUISITION.IMPEDANCE_START",
  STOP_ACQUISITION_IMPEDANCE: "ACQUISITION.IMPEDANCE_STOP",
  START_ACQUISITION: "ACQUISITION.START",
  STOP_ACQUISITION: "ACQUISITION.STOP",
  INSERT_MARKER: "ACQUISITION.INSERT_MARKER",
  PREPARE_STIMULATION: "STIMULATION.PREPARE",
  START_IMPEDANCE: "STIMULATION.IMPEDANCE_START",
  STOP_IMPEDANCE: "STIMULATION.IMPEDANCE_STOP",
  ARM_STIMULATION: "STIMULATION.ARM",
  START_STIMULATION: "STIMULATION.START",
  ABORT_STIMULATION: "STIMULATION.ABORT",
};

export const DEVICE_EVENTS = {
  COMMAND_ACKNOWLEDGED: "SYSTEM.COMMAND_ACKNOWLEDGED",
  COMMAND_REJECTED: "SYSTEM.COMMAND_REJECTED",
  CONNECTION_CHANGED: "SYSTEM.CONNECTION_CHANGED",
  CAPABILITY_REPORTED: "SYSTEM.CAPABILITY_REPORTED",
  ACQUISITION_CONFIGURED: "ACQUISITION.CONFIGURED",
  ACQUISITION_IMPEDANCE_STARTED: "ACQUISITION.IMPEDANCE_STARTED",
  ACQUISITION_IMPEDANCE_FRAME: "ACQUISITION.IMPEDANCE_FRAME",
  ACQUISITION_IMPEDANCE_COMPLETED: "ACQUISITION.IMPEDANCE_COMPLETED",
  ACQUISITION_STARTED: "ACQUISITION.STARTED",
  ACQUISITION_STOPPED: "ACQUISITION.STOPPED",
  EEG_FRAME: "ACQUISITION.EEG_FRAME",
  ACQUISITION_PACKET_DROPPED: "ACQUISITION.PACKET_DROPPED",
  ACQUISITION_FRAME_REJECTED: "ACQUISITION.FRAME_REJECTED",
  ACQUISITION_CONTACT_CHANGED: "ACQUISITION.CONTACT_CHANGED",
  MARKER: "ACQUISITION.MARKER",
  IMPEDANCE_STARTED: "STIMULATION.IMPEDANCE_STARTED",
  IMPEDANCE_FRAME: "STIMULATION.IMPEDANCE_FRAME",
  IMPEDANCE_COMPLETED: "STIMULATION.IMPEDANCE_COMPLETED",
  STIMULATION_PREPARED: "STIMULATION.PREPARED",
  STIMULATION_ARMED: "STIMULATION.ARMED",
  STIMULATION_STARTED: "STIMULATION.STARTED",
  STIMULATION_STAGE_CHANGED: "STIMULATION.STAGE_CHANGED",
  STIMULATION_COMPLETED: "STIMULATION.COMPLETED",
  STIMULATION_ABORTED: "STIMULATION.ABORTED",
  FAULT: "SYSTEM.FAULT",
};

export const DEVICE_ERROR_CODES = {
  INVALID_MESSAGE: "INVALID_MESSAGE",
  UNSUPPORTED_PROTOCOL_VERSION: "UNSUPPORTED_PROTOCOL_VERSION",
  DEVICE_NOT_CONNECTED: "DEVICE_NOT_CONNECTED",
  CAPABILITY_MISMATCH: "CAPABILITY_MISMATCH",
  ACQUISITION_CONFIG_INVALID: "ACQUISITION_CONFIG_INVALID",
  STIMULATION_PROGRAM_INVALID: "STIMULATION_PROGRAM_INVALID",
  IMPEDANCE_NOT_PASSED: "IMPEDANCE_NOT_PASSED",
  PATIENT_TOLERANCE_RECORD_MISSING: "PATIENT_TOLERANCE_RECORD_MISSING",
  PATIENT_TOLERANCE_RECORD_MISMATCH: "PATIENT_TOLERANCE_RECORD_MISMATCH",
  DEVICE_NOT_ARMED: "DEVICE_NOT_ARMED",
  DEVICE_BUSY: "DEVICE_BUSY",
  SEQUENCE_GAP: "SEQUENCE_GAP",
  DEVICE_TIME_ROLLBACK: "DEVICE_TIME_ROLLBACK",
  TRANSPORT_DISCONNECTED: "TRANSPORT_DISCONNECTED",
  ELECTRODE_CONTACT_LOST: "ELECTRODE_CONTACT_LOST",
  OPERATOR_ABORT: "OPERATOR_ABORT",
  HARDWARE_FAULT: "HARDWARE_FAULT",
};

const REQUIRED_CAPABILITY_SECTIONS = ["identity", "transport", "acquisition", "stimulation"];

export function createDeviceCapability({
  deviceId = "SIM-001",
  deviceModel = "EEG-tES Virtual Device",
  firmwareVersion = "SIM-1.0.0",
  transportType = "IN_PROCESS",
  acquisition = {},
  stimulation = {},
} = {}) {
  return {
    capabilityVersion: "1.0",
    identity: {
      deviceId,
      deviceModel,
      firmwareVersion,
      simulated: true,
    },
    transport: {
      type: transportType,
      protocol: DEVICE_PROTOCOL_VERSION,
    },
    acquisition: {
      channelCount: 8,
      channelLabels: ["FP1", "FP2", "F3", "F4", "C3", "C4", "P3", "P4"],
      supportedSampleRatesHz: [250, 500],
      sampleFormat: "FLOAT32_UV",
      timestampSource: "DEVICE_MONOTONIC_US",
      referenceOptions: ["CONFIGURABLE"],
      groundOptions: ["CONFIGURABLE"],
      supportedSourceKinds: ["VIRTUAL", "REPLAY", "LSL", "FIELDTRIP", "DIRECT_DEVICE"],
      referenceTopologies: ["COMMON_REFERENCE", "DIFFERENTIAL_PAIR"],
      markerSupport: true,
      impedanceSupport: true,
      ...acquisition,
    },
    stimulation: {
      channelCount: 5,
      independentGeneratorCount: 1,
      supportedParadigms: ["TDCS", "TACS", "TRNS", "SHAM"],
      supportedTopologies: ["DUAL_ELECTRODE", "HD_4X1"],
      impedanceSupport: true,
      emergencyAbortSupport: true,
      ...stimulation,
    },
  };
}

export function validateDeviceCapability(capability) {
  const errors = [];
  REQUIRED_CAPABILITY_SECTIONS.forEach((section) => {
    if (!capability?.[section] || typeof capability[section] !== "object") {
      errors.push(`CAPABILITY_SECTION_MISSING:${section}`);
    }
  });
  if (!capability?.identity?.deviceId) errors.push("CAPABILITY_DEVICE_ID_MISSING");
  if (!capability?.identity?.firmwareVersion) errors.push("CAPABILITY_FIRMWARE_MISSING");
  if (!Number.isInteger(capability?.acquisition?.channelCount) || capability.acquisition.channelCount < 1) {
    errors.push("ACQUISITION_CHANNEL_COUNT_INVALID");
  }
  if (
    !Array.isArray(capability?.acquisition?.supportedSampleRatesHz)
    || capability.acquisition.supportedSampleRatesHz.some((rate) => !Number.isFinite(rate) || rate <= 0)
  ) {
    errors.push("ACQUISITION_SAMPLE_RATES_INVALID");
  }
  if (!Number.isInteger(capability?.stimulation?.channelCount) || capability.stimulation.channelCount < 1) {
    errors.push("STIMULATION_CHANNEL_COUNT_INVALID");
  }
  return { isValid: errors.length === 0, errors };
}

export function createProtocolMessageFactory({
  deviceId = "UNKNOWN",
  nowUs = () => Date.now() * 1000,
  idFactory,
} = {}) {
  let sequence = 0;
  let requestSequence = 0;
  const nextRequestId = () => {
    requestSequence += 1;
    return idFactory?.("REQ", requestSequence) || `REQ-${String(requestSequence).padStart(6, "0")}`;
  };

  return {
    command(messageType, payload = {}, context = {}) {
      sequence += 1;
      return {
        protocolVersion: DEVICE_PROTOCOL_VERSION,
        messageType,
        domain: messageType.split(".")[0],
        deviceId,
        runId: context.runId || null,
        sequence,
        deviceTimeUs: context.deviceTimeUs ?? nowUs(),
        requestId: context.requestId || nextRequestId(),
        replyToRequestId: null,
        payload,
      };
    },
    event(messageType, payload = {}, context = {}) {
      sequence += 1;
      return {
        protocolVersion: DEVICE_PROTOCOL_VERSION,
        messageType,
        domain: messageType.split(".")[0],
        deviceId,
        runId: context.runId || null,
        sequence,
        deviceTimeUs: context.deviceTimeUs ?? nowUs(),
        requestId: null,
        replyToRequestId: context.replyToRequestId || null,
        payload,
      };
    },
  };
}

export function validateProtocolMessage(message) {
  const errors = [];
  if (message?.protocolVersion !== DEVICE_PROTOCOL_VERSION) {
    errors.push(DEVICE_ERROR_CODES.UNSUPPORTED_PROTOCOL_VERSION);
  }
  if (!message?.messageType || typeof message.messageType !== "string") {
    errors.push("MESSAGE_TYPE_MISSING");
  }
  if (!message?.deviceId || typeof message.deviceId !== "string") {
    errors.push("DEVICE_ID_MISSING");
  }
  if (!Number.isInteger(message?.sequence) || message.sequence < 1) {
    errors.push("SEQUENCE_INVALID");
  }
  if (!Number.isFinite(message?.deviceTimeUs) || message.deviceTimeUs < 0) {
    errors.push("DEVICE_TIME_INVALID");
  }
  if (!message?.payload || typeof message.payload !== "object" || Array.isArray(message.payload)) {
    errors.push("PAYLOAD_INVALID");
  }
  return { isValid: errors.length === 0, errors };
}

export function createMessageSequenceGuard() {
  const stateByDevice = new Map();
  return (message) => {
    const validation = validateProtocolMessage(message);
    if (!validation.isValid) return validation;
    const previous = stateByDevice.get(message.deviceId);
    const errors = [];
    if (previous && message.sequence !== previous.sequence + 1) {
      errors.push(DEVICE_ERROR_CODES.SEQUENCE_GAP);
    }
    if (previous && message.deviceTimeUs < previous.deviceTimeUs) {
      errors.push(DEVICE_ERROR_CODES.DEVICE_TIME_ROLLBACK);
    }
    stateByDevice.set(message.deviceId, {
      sequence: message.sequence,
      deviceTimeUs: message.deviceTimeUs,
    });
    return { isValid: errors.length === 0, errors };
  };
}

export function validateAcquisitionConfiguration(configuration, capability) {
  const errors = [];
  const acquisition = capability?.acquisition;
  const channels = configuration?.channels || [];
  if (!acquisition) return { isValid: false, errors: ["ACQUISITION_CAPABILITY_MISSING"] };
  if (!channels.length || channels.length > acquisition.channelCount) {
    errors.push("ACQUISITION_CHANNEL_SELECTION_INVALID");
  }
  if (!acquisition.supportedSampleRatesHz.includes(Number(configuration.sampleRateHz))) {
    errors.push("ACQUISITION_SAMPLE_RATE_UNSUPPORTED");
  }
  if (!configuration.reference?.positionId) errors.push("REFERENCE_ELECTRODE_MISSING");
  if (!configuration.ground?.positionId) errors.push("GROUND_ELECTRODE_MISSING");
  if (
    configuration.reference?.positionId
    && configuration.reference.positionId === configuration.ground?.positionId
  ) {
    errors.push("REFERENCE_GROUND_COLLISION");
  }
  return { isValid: errors.length === 0, errors };
}

export function validateStimulationPrerequisites({
  capability,
  program,
  toleranceRecord,
  stimulationImpedance,
  acquisitionImpedance,
}) {
  const errors = [];
  if (!capability?.stimulation) errors.push("STIMULATION_CAPABILITY_MISSING");
  if (!program?.protocolHash) errors.push(DEVICE_ERROR_CODES.STIMULATION_PROGRAM_INVALID);
  if (!toleranceRecord?.recordId || toleranceRecord.status !== "COMPLETED") {
    errors.push(DEVICE_ERROR_CODES.PATIENT_TOLERANCE_RECORD_MISSING);
  }
  if (
    toleranceRecord?.paradigmId
    && program?.paradigmId
    && toleranceRecord.paradigmId !== program.paradigmId
  ) {
    errors.push(DEVICE_ERROR_CODES.PATIENT_TOLERANCE_RECORD_MISMATCH);
  }
  if (stimulationImpedance?.status !== "PASSED" || acquisitionImpedance?.status !== "PASSED") {
    errors.push(DEVICE_ERROR_CODES.IMPEDANCE_NOT_PASSED);
  }
  return { isValid: errors.length === 0, errors };
}

export function createDeviceError(code, message, details = {}, options = {}) {
  return {
    code,
    message,
    severity: options.severity || "ERROR",
    recoverable: options.recoverable ?? false,
    source: options.source || DEVICE_DOMAINS.SYSTEM,
    details,
  };
}
