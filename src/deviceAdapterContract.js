export const DEVICE_ADAPTER_METHODS = [
  "connect",
  "disconnect",
  "getIdentity",
  "getCapabilities",
  "prepareProgram",
  "runToleranceCheck",
  "runImpedanceCheck",
  "arm",
  "startRun",
  "abortRun",
  "startAcquisition",
  "stopAcquisition",
  "pushMarker",
  "subscribe",
  "destroy",
];

export const DEVICE_SESSION_METHODS = [
  "connect",
  "disconnect",
  "getIdentity",
  "getCapabilities",
  "subscribe",
  "destroy",
];

export const ACQUISITION_ADAPTER_METHODS = [
  ...DEVICE_SESSION_METHODS,
  "configureAcquisition",
  "startImpedanceCheck",
  "stopImpedanceCheck",
  "startAcquisition",
  "stopAcquisition",
  "pushMarker",
];

export const STIMULATION_ADAPTER_METHODS = [
  ...DEVICE_SESSION_METHODS,
  "prepareProgram",
  "startImpedanceCheck",
  "stopImpedanceCheck",
  "arm",
  "startRun",
  "abortRun",
  "reset",
];

function assertMethods(adapter, methodNames, errorPrefix) {
  const missingMethods = methodNames.filter(
    (methodName) => typeof adapter?.[methodName] !== "function",
  );
  if (missingMethods.length > 0) {
    throw new Error(`${errorPrefix}:${missingMethods.join(",")}`);
  }
  return adapter;
}

export function assertDeviceAdapter(adapter) {
  return assertMethods(adapter, DEVICE_ADAPTER_METHODS, "DEVICE_ADAPTER_INCOMPLETE");
}

export function assertDeviceSession(adapter) {
  return assertMethods(adapter, DEVICE_SESSION_METHODS, "DEVICE_SESSION_INCOMPLETE");
}

export function assertAcquisitionAdapter(adapter) {
  return assertMethods(adapter, ACQUISITION_ADAPTER_METHODS, "ACQUISITION_ADAPTER_INCOMPLETE");
}

export function assertStimulationAdapter(adapter) {
  return assertMethods(adapter, STIMULATION_ADAPTER_METHODS, "STIMULATION_ADAPTER_INCOMPLETE");
}

export function createDeviceEventFactory({
  sourceId,
  sourceType = "DEVICE",
  now = () => new Date(),
} = {}) {
  let sequence = 0;
  return (type, message, payload = {}) => {
    sequence += 1;
    const occurredAt = now();
    return {
      eventId: `${sourceId || sourceType}-${sequence}`,
      sequence,
      sourceId: sourceId || "UNKNOWN",
      sourceType,
      type,
      message,
      payload,
      occurredAt: occurredAt.toISOString(),
      timestampMs: occurredAt.getTime(),
    };
  };
}
