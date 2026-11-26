"""Independent SciPy fixtures for configurable Butterworth + Hilbert nodes.

Run with NumPy/SciPy installed. This writes only the new parameter fixture,
never the pre-migration/default behavior baseline.
"""
import json
from pathlib import Path

import numpy as np
import scipy
from scipy.signal import butter, sosfiltfilt, hilbert

cases = []
for order, cutoff, rate, count in [
    (1, 150, 16000, 1), (2, 500, 16000, 2), (3, 300, 11025, 17),
    (4, 700, 16000, 128), (5, 150, 16000, 129), (6, 1000, 16000, 129),
    (7, 800, 16000, 127), (8, 800, 16000, 128), (9, 800, 16000, 127),
    (10, 800, 16000, 128), (11, 800, 16000, 127), (12, 800, 16000, 129),
]:
    t = np.arange(count) / rate
    samples = .3 * np.cos(2 * np.pi * 93 * t) + .15 * np.sin(2 * np.pi * 1300 * t)
    sos = butter(order, cutoff, fs=rate, output="sos")
    filtered = samples.copy() if count < 2 else sosfiltfilt(
        sos, samples, padtype="odd", padlen=min(3 * (order + 1), count - 1))
    envelope = np.abs(hilbert(filtered))
    cases.append(dict(order=order, cutoffHz=cutoff, sampleRate=rate,
                      input=samples.tolist(), filtered=filtered.tolist(), envelope=envelope.tolist()))

destination = Path(__file__).resolve().parents[1] / (
    "EGGtCSPlatform.Audio.Dsp.Tests/Fixtures/EnvelopeParametersReference.json")
destination.write_text(json.dumps(dict(
    generator="SciPy butter(output=sos), sosfiltfilt, hilbert",
    scipyVersion=scipy.__version__, numpyVersion=np.__version__, cases=cases
), indent=2) + "\n", encoding="utf-8")
