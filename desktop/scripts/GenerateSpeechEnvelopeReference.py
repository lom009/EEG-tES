"""Generate checked-in numerical fixtures from research pure functions only.

Requires numpy and scipy (validation only; neither is an application dependency).
Example: python scripts/GenerateSpeechEnvelopeReference.py D:/desktop/research
Only AST-selected filter, Hilbert, map_to_tacs and process_wave_data functions are evaluated;
the original module's serial imports, hardware setup and playback are never run.
"""
import ast
import hashlib
import json
import pathlib
import sys
import wave

import numpy as np
import scipy
from scipy.signal import butter, filtfilt, hilbert

root = pathlib.Path(__file__).resolve().parents[1]
source = pathlib.Path(sys.argv[1]) / "ElectricalStimulationProject_0/Prepare_and_send_electrical_stimulation.py"
source_bytes = source.read_bytes()
tree = ast.parse(source_bytes.decode("utf-8-sig"))
functions = [node for node in tree.body if isinstance(node, ast.FunctionDef)
             and node.name in {"lowpass_filter", "extract_envelope_hilbert", "map_to_tacs", "process_wave_data"}]
assert len(functions) == 4
namespace = dict(np=np, butter=butter, filtfilt=filtfilt, hilbert=hilbert)
exec(compile(ast.Module(body=functions, type_ignores=[]), str(source), "exec"), namespace)

cases = []
def add_case(name, pcm, sample_rate, origin):
    samples = pcm.astype(np.float64) / 32768
    filtered = namespace["lowpass_filter"](samples, 150, sample_rate)
    envelope = namespace["extract_envelope_hilbert"](filtered)
    mapped = namespace["map_to_tacs"](envelope, 0, 1)
    averages = namespace["process_wave_data"](mapped, sample_rate, 100)
    amplitudes = namespace["map_to_tacs"](np.asarray(averages), 0, 1)
    cases.append(dict(name=name, sampleRate=sample_rate, origin=origin,
                      pcm=pcm.tolist(), filtered=filtered.tolist(), envelope=envelope.tolist(),
                      stimulusAmplitudes=amplitudes.tolist()))

for count in (1024, 1025):
    t = np.arange(count) / 16000
    signal = (0.35 + 0.15 * np.cos(2 * np.pi * 7 * t)) * np.sin(2 * np.pi * 90 * t)
    signal += 0.1 * np.sin(2 * np.pi * 1500 * t)
    add_case(f"am-{count}", np.rint(signal * 32768).astype(np.int16), 16000, "deterministic AM + 1500 Hz tone")

speech = root / "EGGtCSPlatform.SpeechTraining/Resources/MHINT-P/9.wav"
with wave.open(str(speech)) as wav:
    assert wav.getsampwidth() == 2 and wav.getnchannels() == 1
    rate = wav.getframerate()
    pcm = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
    start = min(max(0, int(np.argmax(np.abs(pcm.astype(float)))) - 2048), len(pcm) - 4097)
    add_case("speech-4097", pcm[start:start + 4097], rate,
             f"MHINT-P/9.wav samples [{start}:{start + 4097}], sha256={hashlib.sha256(speech.read_bytes()).hexdigest()}")

output = root / "EGGtCSPlatform.SpeechTraining.Tests/Fixtures/SpeechEnvelopeReference.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(dict(scipy=scipy.__version__, numpy=np.__version__,
    source=str(source.relative_to(pathlib.Path(sys.argv[1]))),
    sourceSha256=hashlib.sha256(source_bytes).hexdigest(), cases=cases),
    ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print(f"Generated {len(cases)} cases in {output}")
