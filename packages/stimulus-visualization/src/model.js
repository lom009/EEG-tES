export const TDCS_GRAPH = Object.freeze({
  width: 131,
  height: 38,
  lineStartX: 1,
  lineEndX: 130,
  rampEndX: 21,
  rampDownStartX: 110,
  lineBottomY: 37,
  lineTopY: 1,
});

export const TACS_GRAPH = Object.freeze({
  width: 131,
  height: 42.7516,
  lineStartX: 1,
  lineEndX: 126,
  minY: 0.751582,
  maxY: 41.7516,
});

export const TPCS_GRAPH = Object.freeze({
  width: 131,
  height: 38,
  lineStartX: 1,
  lineEndX: 130,
  positiveBaselineY: 37,
  positivePulseY: 11.8,
  negativeBaselineY: 1,
  negativePulseY: 26.2,
  defaultPulseWindows: Object.freeze([
    Object.freeze([0.216, 0.326]),
    Object.freeze([0.704, 0.813]),
  ]),
});

export const SHAM_DC_GRAPH = Object.freeze({
  width: 131,
  height: 38,
  lineStartX: 1,
  lineEndX: 130,
  positiveBaselineY: 37,
  positivePeakY: 4.6,
  negativeBaselineY: 1,
  negativePeakY: 33.4,
  defaultTriangleWindows: Object.freeze([
    Object.freeze([0, 0.065, 0.152]),
    Object.freeze([0.894, 0.946, 1]),
  ]),
});

export const SHAM_AC_GRAPH = Object.freeze({
  width: 131,
  height: 38,
  lineStartX: 1,
  lineEndX: 130,
  centerY: 19,
  amplitudeY: 18,
  defaultBurstWindows: Object.freeze([
    Object.freeze([0, 0.15]),
    Object.freeze([0.86, 1]),
  ]),
});

export const TRNS_GRAPH = Object.freeze({
  width: 131,
  height: 38,
  lineStartX: 1,
  lineEndX: 130,
  centerY: 19,
  amplitudeY: 16,
});

export const TRNS_PATH_POINTS = Object.freeze([
  Object.freeze([1, 15]),
  Object.freeze([16.48, 27.8]),
  Object.freeze([31.96, 7.8]),
  Object.freeze([48.73, 22.2]),
  Object.freeze([64.21, 11.8]),
  Object.freeze([79.69, 31]),
  Object.freeze([96.46, 13.4]),
  Object.freeze([111.94, 26.2]),
  Object.freeze([130, 15.8]),
]);

export const TACS_PATH_POINTS = Object.freeze([
  [1, 41.4332],
  [1.82782, 41.0867],
  [2.65563, 40.6179],
  [3.48345, 40.0297],
  [4.31126, 39.3257],
  [5.13908, 38.5103],
  [5.96689, 37.5885],
  [6.7947, 36.5659],
  [7.62252, 35.449],
  [8.45033, 34.2445],
  [9.27815, 32.9599],
  [10.106, 31.6031],
  [10.9338, 30.1825],
  [11.7616, 28.7069],
  [12.5894, 27.1852],
  [13.4172, 25.627],
  [14.245, 24.0418],
  [15.0729, 22.4394],
  [15.9007, 20.8297],
  [16.7285, 19.2226],
  [17.5563, 17.628],
  [18.3841, 16.0558],
  [19.2119, 14.5155],
  [20.0397, 13.0168],
  [20.8676, 11.5689],
  [21.6954, 10.1807],
  [22.5232, 8.8607],
  [23.351, 7.61712],
  [24.1788, 6.4576],
  [25.0066, 5.38929],
  [25.8344, 4.41878],
  [26.6623, 3.55205],
  [27.4901, 2.79443],
  [28.3179, 2.15062],
  [29.1457, 1.62457],
  [29.9735, 1.21952],
  [30.8013, 0.93798],
  [31.6291, 0.781677],
  [32.457, 0.751582],
  [33.2848, 0.847876],
  [34.1126, 1.06997],
  [34.9404, 1.41648],
  [35.7682, 1.88528],
  [36.596, 2.47349],
  [37.4238, 3.17746],
  [38.2517, 3.99287],
  [39.0795, 4.91469],
  [39.9073, 5.93723],
  [40.7351, 7.05419],
  [41.5629, 8.25867],
  [42.3907, 9.54327],
  [43.2185, 10.9],
  [44.0464, 12.3206],
  [44.8742, 13.7963],
  [45.702, 15.3179],
  [46.5298, 16.8761],
  [47.3576, 18.4613],
  [48.1854, 20.0637],
  [49.0132, 21.6734],
  [49.8411, 23.2805],
  [50.6689, 24.8751],
  [51.4967, 26.4474],
  [52.3245, 27.9876],
  [53.1523, 29.4863],
  [53.9801, 30.9343],
  [54.8079, 32.3225],
  [55.6358, 33.6425],
  [56.4636, 34.886],
  [57.2914, 36.0456],
  [58.1192, 37.1139],
  [58.947, 38.0844],
  [59.7748, 38.9511],
  [60.6026, 39.7087],
  [61.4305, 40.3525],
  [62.2583, 40.8786],
  [63.0861, 41.2836],
  [63.9139, 41.5652],
  [64.7417, 41.7215],
  [65.5695, 41.7516],
  [66.3974, 41.6553],
  [67.2252, 41.4332],
  [68.053, 41.0867],
  [68.8808, 40.6179],
  [69.7086, 40.0297],
  [70.5364, 39.3257],
  [71.3642, 38.5103],
  [72.1921, 37.5885],
  [73.0199, 36.5659],
  [73.8477, 35.449],
  [74.6755, 34.2445],
  [75.5033, 32.9599],
  [76.3311, 31.6031],
  [77.1589, 30.1825],
  [77.9868, 28.7069],
  [78.8146, 27.1852],
  [79.6424, 25.627],
  [80.4702, 24.0418],
  [81.298, 22.4394],
  [82.1258, 20.8297],
  [82.9536, 19.2226],
  [83.7815, 17.628],
  [84.6093, 16.0558],
  [85.4371, 14.5155],
  [86.2649, 13.0168],
  [87.0927, 11.5689],
  [87.9205, 10.1807],
  [88.7483, 8.8607],
  [89.5762, 7.61712],
  [90.404, 6.4576],
  [91.2318, 5.38929],
  [92.0596, 4.41878],
  [92.8874, 3.55205],
  [93.7152, 2.79443],
  [94.543, 2.15062],
  [95.3709, 1.62457],
  [96.1987, 1.21952],
  [97.0265, 0.93798],
  [97.8543, 0.781677],
  [98.6821, 0.751582],
  [99.5099, 0.847876],
  [100.338, 1.06997],
  [101.166, 1.41648],
  [101.993, 1.88528],
  [102.821, 2.47349],
  [103.649, 3.17746],
  [104.477, 3.99287],
  [105.305, 4.91469],
  [106.132, 5.93723],
  [106.96, 7.05419],
  [107.788, 8.25867],
  [108.616, 9.54327],
  [109.444, 10.9],
  [110.272, 12.3206],
  [111.099, 13.7963],
  [111.927, 15.3179],
  [112.755, 16.8761],
  [113.583, 18.4613],
  [114.411, 20.0637],
  [115.238, 21.6734],
  [116.066, 23.2805],
  [116.894, 24.8751],
  [117.722, 26.4474],
  [118.55, 27.9876],
  [119.377, 29.4863],
  [120.205, 30.9343],
  [121.033, 32.3225],
  [121.861, 33.6425],
  [122.689, 34.886],
  [123.517, 36.0456],
  [124.344, 37.1139],
  [125.172, 38.0844],
  [126, 38.9511],
]);

function finiteOr(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function cleanNumber(value) {
  const rounded = Number(value.toFixed(6));
  return Object.is(rounded, -0) ? 0 : rounded;
}

export function normalizeProgress(progress) {
  return clamp(finiteOr(progress, 0), 0, 1);
}

function normalizeTPCSPulseWindows(pulseWindows) {
  if (!Array.isArray(pulseWindows)) {
    return TPCS_GRAPH.defaultPulseWindows;
  }

  const normalizedWindows = pulseWindows
    .filter((window) => Array.isArray(window) && window.length >= 2)
    .map(([start, end]) => {
      const normalizedStart = normalizeProgress(start);
      const normalizedEnd = normalizeProgress(end);
      return [Math.min(normalizedStart, normalizedEnd), Math.max(normalizedStart, normalizedEnd)];
    })
    .filter(([start, end]) => end > start)
    .sort(([startA], [startB]) => startA - startB);

  return normalizedWindows.length > 0
    ? normalizedWindows
    : TPCS_GRAPH.defaultPulseWindows;
}

function getTPCSLevels(pulseCurrent) {
  return pulseCurrent < 0
    ? {
        baselineY: TPCS_GRAPH.negativeBaselineY,
        pulseY: TPCS_GRAPH.negativePulseY,
      }
    : {
        baselineY: TPCS_GRAPH.positiveBaselineY,
        pulseY: TPCS_GRAPH.positivePulseY,
      };
}

function tpcsProgressToX(progress) {
  return TPCS_GRAPH.lineStartX
    + progress * (TPCS_GRAPH.lineEndX - TPCS_GRAPH.lineStartX);
}

function progressToX(progress, graph) {
  return graph.lineStartX + progress * (graph.lineEndX - graph.lineStartX);
}

function normalizeTriangleWindows(triangleWindows) {
  if (!Array.isArray(triangleWindows)) {
    return SHAM_DC_GRAPH.defaultTriangleWindows;
  }

  const normalized = triangleWindows
    .filter((window) => Array.isArray(window) && window.length >= 3)
    .map(([start, peak, end]) => [
      normalizeProgress(start),
      normalizeProgress(peak),
      normalizeProgress(end),
    ])
    .filter(([start, peak, end]) => start < peak && peak < end)
    .sort(([startA], [startB]) => startA - startB);

  return normalized.length > 0 ? normalized : SHAM_DC_GRAPH.defaultTriangleWindows;
}

function normalizeBurstWindows(burstWindows) {
  if (!Array.isArray(burstWindows)) {
    return SHAM_AC_GRAPH.defaultBurstWindows;
  }

  const normalized = burstWindows
    .filter((window) => Array.isArray(window) && window.length >= 2)
    .map(([start, end]) => {
      const normalizedStart = normalizeProgress(start);
      const normalizedEnd = normalizeProgress(end);
      return [Math.min(normalizedStart, normalizedEnd), Math.max(normalizedStart, normalizedEnd)];
    })
    .filter(([start, end]) => end > start)
    .sort(([startA], [startB]) => startA - startB);

  return normalized.length > 0 ? normalized : SHAM_AC_GRAPH.defaultBurstWindows;
}

function getShamDCLevels(shamCurrent) {
  return shamCurrent < 0
    ? {
        baselineY: SHAM_DC_GRAPH.negativeBaselineY,
        peakY: SHAM_DC_GRAPH.negativePeakY,
      }
    : {
        baselineY: SHAM_DC_GRAPH.positiveBaselineY,
        peakY: SHAM_DC_GRAPH.positivePeakY,
      };
}

function getTriangleEnvelope(progress, triangleWindows) {
  for (const [start, peak, end] of triangleWindows) {
    if (progress >= start && progress <= peak) {
      return (progress - start) / (peak - start);
    }
    if (progress > peak && progress <= end) {
      return (end - progress) / (end - peak);
    }
  }
  return 0;
}

function getShamACWave(progress, burstWindows) {
  for (const [start, end] of burstWindows) {
    if (progress >= start && progress <= end) {
      const localProgress = (progress - start) / (end - start);
      return Math.sin(localProgress * Math.PI * 2);
    }
  }
  return 0;
}

function interpolatePathY(pathX, pathPoints) {
  if (pathX <= pathPoints[0][0]) {
    return pathPoints[0][1];
  }

  for (let index = 1; index < pathPoints.length; index += 1) {
    const [nextX, nextY] = pathPoints[index];
    if (pathX <= nextX) {
      const [previousX, previousY] = pathPoints[index - 1];
      const segmentProgress = (pathX - previousX) / (nextX - previousX);
      return previousY + segmentProgress * (nextY - previousY);
    }
  }

  return pathPoints.at(-1)[1];
}

export function getTDCSFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const targetCurrent = finiteOr(config.targetCurrent, 2);
  const graph = TDCS_GRAPH;
  const pathX = graph.lineStartX
    + normalizedProgress * (graph.lineEndX - graph.lineStartX);

  let envelope;
  let pathY;

  if (pathX <= graph.rampEndX) {
    envelope = (pathX - graph.lineStartX) / (graph.rampEndX - graph.lineStartX);
    pathY = graph.lineBottomY
      - envelope * (graph.lineBottomY - graph.lineTopY);
  } else if (pathX <= graph.rampDownStartX) {
    envelope = 1;
    pathY = graph.lineTopY;
  } else {
    envelope = (graph.lineEndX - pathX) / (graph.lineEndX - graph.rampDownStartX);
    pathY = graph.lineBottomY
      - envelope * (graph.lineBottomY - graph.lineTopY);
  }

  if (targetCurrent < 0) {
    pathY = graph.lineTopY + graph.lineBottomY - pathY;
  }

  return {
    progress: normalizedProgress,
    x: cleanNumber(pathX),
    y: cleanNumber(pathY),
    value: cleanNumber(targetCurrent * envelope),
  };
}

function interpolateTACSPathY(pathX) {
  if (pathX <= TACS_PATH_POINTS[0][0]) {
    return TACS_PATH_POINTS[0][1];
  }

  for (let index = 1; index < TACS_PATH_POINTS.length; index += 1) {
    const [nextX, nextY] = TACS_PATH_POINTS[index];

    if (pathX <= nextX) {
      const [previousX, previousY] = TACS_PATH_POINTS[index - 1];
      const segmentProgress = (pathX - previousX) / (nextX - previousX);
      return previousY + segmentProgress * (nextY - previousY);
    }
  }

  return TACS_PATH_POINTS.at(-1)[1];
}

export function getTACSFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const peakCurrent = finiteOr(config.peakCurrent ?? config.targetCurrent, 2);
  const graph = TACS_GRAPH;
  const pathX = graph.lineStartX
    + normalizedProgress * (graph.lineEndX - graph.lineStartX);
  const pathY = interpolateTACSPathY(pathX);
  const centerY = (graph.minY + graph.maxY) / 2;
  const amplitudeY = (graph.maxY - graph.minY) / 2;
  const waveformValue = clamp((pathY - centerY) / amplitudeY, -1, 1);

  return {
    progress: cleanNumber(normalizedProgress),
    x: cleanNumber(pathX),
    y: cleanNumber(pathY),
    value: cleanNumber(peakCurrent * waveformValue),
  };
}

export function getTPCSPathPoints(config = {}) {
  const pulseCurrent = finiteOr(config.pulseCurrent ?? config.targetCurrent, 1.4);
  const pulseWindows = normalizeTPCSPulseWindows(config.pulseWindows);
  const { baselineY, pulseY } = getTPCSLevels(pulseCurrent);
  const points = [[TPCS_GRAPH.lineStartX, baselineY]];

  pulseWindows.forEach(([start, end]) => {
    const pulseStartX = tpcsProgressToX(start);
    const pulseEndX = tpcsProgressToX(end);
    points.push(
      [pulseStartX, baselineY],
      [pulseStartX, pulseY],
      [pulseEndX, pulseY],
      [pulseEndX, baselineY],
    );
  });

  points.push([TPCS_GRAPH.lineEndX, baselineY]);
  return points;
}

export function getTPCSFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const pulseCurrent = finiteOr(config.pulseCurrent ?? config.targetCurrent, 1.4);
  const pulseWindows = normalizeTPCSPulseWindows(config.pulseWindows);
  const { baselineY, pulseY } = getTPCSLevels(pulseCurrent);
  const pulseActive = pulseWindows.some(
    ([start, end]) => normalizedProgress >= start && normalizedProgress < end,
  );

  return {
    progress: cleanNumber(normalizedProgress),
    x: cleanNumber(tpcsProgressToX(normalizedProgress)),
    y: pulseActive ? pulseY : baselineY,
    value: pulseActive ? cleanNumber(pulseCurrent) : 0,
    baselineY,
    pulseY,
  };
}

export function getShamDCPathPoints(config = {}) {
  const shamCurrent = finiteOr(config.shamCurrent ?? config.targetCurrent, 1.8);
  const triangleWindows = normalizeTriangleWindows(config.triangleWindows);
  const { baselineY, peakY } = getShamDCLevels(shamCurrent);
  const points = [[SHAM_DC_GRAPH.lineStartX, baselineY]];

  triangleWindows.forEach(([start, peak, end]) => {
    points.push(
      [progressToX(start, SHAM_DC_GRAPH), baselineY],
      [progressToX(peak, SHAM_DC_GRAPH), peakY],
      [progressToX(end, SHAM_DC_GRAPH), baselineY],
    );
  });

  points.push([SHAM_DC_GRAPH.lineEndX, baselineY]);
  return points;
}

export function getShamDCFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const shamCurrent = finiteOr(config.shamCurrent ?? config.targetCurrent, 1.8);
  const triangleWindows = normalizeTriangleWindows(config.triangleWindows);
  const { baselineY, peakY } = getShamDCLevels(shamCurrent);
  const envelope = getTriangleEnvelope(normalizedProgress, triangleWindows);

  return {
    progress: cleanNumber(normalizedProgress),
    x: cleanNumber(progressToX(normalizedProgress, SHAM_DC_GRAPH)),
    y: cleanNumber(baselineY + envelope * (peakY - baselineY)),
    value: cleanNumber(shamCurrent * envelope),
    baselineY,
    peakY,
  };
}

export function getShamACPathPoints(config = {}) {
  const burstWindows = normalizeBurstWindows(config.burstWindows);
  const samplesPerBurst = 48;
  const points = [[SHAM_AC_GRAPH.lineStartX, SHAM_AC_GRAPH.centerY]];

  burstWindows.forEach(([start, end]) => {
    points.push([progressToX(start, SHAM_AC_GRAPH), SHAM_AC_GRAPH.centerY]);
    for (let sample = 1; sample <= samplesPerBurst; sample += 1) {
      const localProgress = sample / samplesPerBurst;
      const progress = start + localProgress * (end - start);
      const wave = Math.sin(localProgress * Math.PI * 2);
      points.push([
        progressToX(progress, SHAM_AC_GRAPH),
        SHAM_AC_GRAPH.centerY - wave * SHAM_AC_GRAPH.amplitudeY,
      ]);
    }
  });

  points.push([SHAM_AC_GRAPH.lineEndX, SHAM_AC_GRAPH.centerY]);
  return points;
}

export function getShamACFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const peakCurrent = Math.abs(finiteOr(
    config.shamPeakCurrent ?? config.peakCurrent ?? config.targetCurrent,
    0.9,
  ));
  const burstWindows = normalizeBurstWindows(config.burstWindows);
  const wave = getShamACWave(normalizedProgress, burstWindows);

  return {
    progress: cleanNumber(normalizedProgress),
    x: cleanNumber(progressToX(normalizedProgress, SHAM_AC_GRAPH)),
    y: cleanNumber(SHAM_AC_GRAPH.centerY - wave * SHAM_AC_GRAPH.amplitudeY),
    value: cleanNumber(peakCurrent * wave),
    centerY: SHAM_AC_GRAPH.centerY,
  };
}

export function getTRNSPathPoints() {
  return TRNS_PATH_POINTS;
}

export function getTRNSFrameAtProgress(progress, config = {}) {
  const normalizedProgress = normalizeProgress(progress);
  const noiseAmplitude = Math.abs(finiteOr(
    config.noiseAmplitude ?? config.targetCurrent,
    1.8,
  ));
  const pathX = progressToX(normalizedProgress, TRNS_GRAPH);

  return {
    progress: cleanNumber(normalizedProgress),
    x: cleanNumber(pathX),
    y: cleanNumber(interpolatePathY(pathX, TRNS_PATH_POINTS)),
    value: cleanNumber(noiseAmplitude),
  };
}

export function formatStimulusValue(value, unit = "mA", precision = 2) {
  const safePrecision = clamp(Math.trunc(finiteOr(precision, 2)), 0, 6);
  const rounded = Number(finiteOr(value, 0).toFixed(safePrecision));
  const normalized = Object.is(rounded, -0) ? 0 : rounded;
  return `${normalized.toFixed(safePrecision)} ${unit}`.trim();
}
