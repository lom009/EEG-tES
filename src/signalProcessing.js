function sanitizeSamples(samples) {
  return Array.isArray(samples) ? samples.map((value) => Number(value) || 0) : [];
}

function applyHighPass(samples, sampleRateHz, state, cutoffHz = 0.5) {
  if (!samples.length) return { samples, state };
  const deltaTime = 1 / sampleRateHz;
  const rc = 1 / (2 * Math.PI * cutoffHz);
  const alpha = rc / (rc + deltaTime);
  let previousInput = state.previousInput ?? samples[0];
  let previousOutput = state.previousOutput ?? 0;
  const output = samples.map((sample) => {
    const filtered = alpha * (previousOutput + sample - previousInput);
    previousInput = sample;
    previousOutput = filtered;
    return filtered;
  });
  return {
    samples: output,
    state: { previousInput, previousOutput },
  };
}

function applyLowPass(samples, sampleRateHz, state, cutoffHz = 40) {
  if (!samples.length) return { samples, state };
  const deltaTime = 1 / sampleRateHz;
  const rc = 1 / (2 * Math.PI * cutoffHz);
  const alpha = deltaTime / (rc + deltaTime);
  let previousOutput = state.previousOutput ?? samples[0];
  const output = samples.map((sample) => {
    previousOutput += alpha * (sample - previousOutput);
    return previousOutput;
  });
  return {
    samples: output,
    state: { previousOutput },
  };
}

function applyNotch(samples, sampleRateHz, state, notchHz = 50, qualityFactor = 30) {
  if (!samples.length || sampleRateHz <= notchHz * 2) return { samples, state };
  const omega = 2 * Math.PI * notchHz / sampleRateHz;
  const cosine = Math.cos(omega);
  const alpha = Math.sin(omega) / (2 * qualityFactor);
  const a0 = 1 + alpha;
  const b0 = 1 / a0;
  const b1 = (-2 * cosine) / a0;
  const b2 = 1 / a0;
  const a1 = (-2 * cosine) / a0;
  const a2 = (1 - alpha) / a0;
  let x1 = state.x1 ?? 0;
  let x2 = state.x2 ?? 0;
  let y1 = state.y1 ?? 0;
  let y2 = state.y2 ?? 0;
  const output = samples.map((sample) => {
    const filtered = b0 * sample + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
    x2 = x1;
    x1 = sample;
    y2 = y1;
    y1 = filtered;
    return filtered;
  });
  return {
    samples: output,
    state: { x1, x2, y1, y2 },
  };
}

export function processSignalSamples(
  inputSamples,
  sampleRateHz,
  filters = {},
  previousState = {},
) {
  const rate = Math.max(1, Number(sampleRateHz) || 500);
  let samples = sanitizeSamples(inputSamples);
  const nextState = {};

  if (filters.high) {
    const result = applyHighPass(samples, rate, previousState.high || {});
    samples = result.samples;
    nextState.high = result.state;
  }
  if (filters.low) {
    const result = applyLowPass(samples, rate, previousState.low || {});
    samples = result.samples;
    nextState.low = result.state;
  }
  if (filters.notch) {
    const result = applyNotch(samples, rate, previousState.notch || {});
    samples = result.samples;
    nextState.notch = result.state;
  }

  return {
    samplesUv: samples.map((value) => Number(value.toFixed(4))),
    state: nextState,
  };
}
