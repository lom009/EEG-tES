#!/usr/bin/env python3
# coding: utf-8
"""Serve the desktop naming prototype and run the existing Paraformer model on loopback."""
import argparse
import io
import json
import os
import subprocess
import sys
import tempfile
import threading
import wave
from array import array
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from math import sin, tau
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

ROOT = Path(__file__).resolve().parents[1]
HTML = ROOT / 'picture-naming-tdcs-prototype.html'
ASR = Path(os.environ.get('NAMING_ASR_DIR', ROOT / 'output' / 'picture-naming-local-asr'))
MODEL = ASR / 'paraformer-q8.gguf'
ENGINE = ASR / 'llama-funasr-paraformer'
EVALUATION = ASR / 'expanded-evaluation'
sys.path.insert(0, str(EVALUATION / '.pinyin-deps'))
from picture_naming_scoring import PronunciationScorer, normalize

READINGS = ASR / 'kTGHZ2013.txt'
SCORER = PronunciationScorer(READINGS if READINGS.is_file() else EVALUATION / READINGS.name)
CATALOG = {'apple': ('苹果',), 'cup': ('杯子',), 'key': ('钥匙',), 'umbrella': ('雨伞', '伞', '一把伞'),
           'clock': ('时钟',), 'fish': ('鱼',)}
KNOWN_ANSWERS = {word for answers in CATALOG.values() for word in answers}
MODEL_SLOT = threading.BoundedSemaphore(1)
PUBLIC_ORIGIN = (os.environ.get('NAMING_PUBLIC_ORIGIN') or os.environ.get('RENDER_EXTERNAL_URL') or '').rstrip('/')
ASR_TIMEOUT = 45 if PUBLIC_ORIGIN else 12
PREVIEW = '/picture-naming-tdcs-prototype.html'
MAX_WAV_BYTES = 257000
MAX_FRAMES = 16000 * 8


def pcm_frames(payload):
    with wave.open(io.BytesIO(payload), 'rb') as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) != (1, 2, 16000):
            raise ValueError('请使用 16 kHz 单声道录音')
        frames = audio.getnframes()
        if not 0 < frames <= MAX_FRAMES:
            raise ValueError('录音长度异常，请回听后评分')
        samples = array('h', audio.readframes(frames))
        if len(samples) != frames:
            raise ValueError('录音不完整，请回听后评分')
        if sys.byteorder == 'big':
            samples.byteswap()
        return samples


def evaluate(item, payload):
    samples = pcm_frames(payload)
    if not any(samples):
        return dict(item=item, transcript='', verdict='无反应', match='无反应')
    if max(abs(sample) for sample in samples) < 32:
        return dict(item=item, transcript='', verdict='待复核', match='录音音量过低')
    with tempfile.TemporaryDirectory(prefix='naming-asr-') as scratch:
        path = Path(scratch) / 'trial.wav'
        path.write_bytes(payload)
        # The model receives audio only. The current picture is used afterward.
        result = subprocess.run([str(ENGINE), '-m', str(MODEL), '-a', str(path)],
                                capture_output=True, text=True, check=True, timeout=ASR_TIMEOUT)
    transcript = result.stdout.strip()
    baseline = '错误' if normalize(transcript) in KNOWN_ANSWERS else '待复核'
    verdict, match = SCORER.score(CATALOG[item], transcript, baseline)
    return dict(item=item, transcript=transcript, verdict=verdict, match=match)


def check_model():
    # Exercise the native model in the final container without using a voice recording.
    payload = io.BytesIO()
    samples = array('h', (int(800 * sin(tau * 220 * i / 16000)) for i in range(16000)))
    with wave.open(payload, 'wb') as audio:
        audio.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
        audio.writeframes(samples.tobytes())
    try:
        evaluate('apple', payload.getvalue())
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.stderr or str(error)) from error
    print('Paraformer model smoke test passed', flush=True)


class Handler(BaseHTTPRequestHandler):
    def reply(self, status, body, mime='application/json; charset=utf-8'):
        if isinstance(body, dict):
            body = json.dumps(body, ensure_ascii=False).encode('utf-8')
        try:
            self.send_response(status)
            self.send_header('Content-Type', mime)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_GET(self):
        route = urlsplit(self.path).path
        if route in ('/', PREVIEW):
            html = HTML.read_text(encoding='utf-8')
            mode = 'hosted' if PUBLIC_ORIGIN else 'local'
            html = html.replace('<meta charset="UTF-8">', f'<meta charset="UTF-8"><meta name="naming-asr" content="{mode}">', 1)
            self.reply(200, html.encode('utf-8'), 'text/html; charset=utf-8')
        elif route == '/api/naming/status':
            ready = ENGINE.is_file() and MODEL.is_file()
            self.reply(200 if ready else 503, {'ready': ready, 'model': 'Paraformer Q8'})
        else:
            self.reply(404, {'error': '页面不存在'})

    def do_POST(self):
        route = urlsplit(self.path)
        if route.path != '/api/naming/score':
            return self.reply(404, {'error': '页面不存在'})
        origin = self.headers.get('Origin')
        allowed_origins = {PUBLIC_ORIGIN} if PUBLIC_ORIGIN else {
            f'http://127.0.0.1:{self.server.server_port}', f'http://localhost:{self.server.server_port}'
        }
        if origin and origin not in allowed_origins:
            return self.reply(403, {'error': '请从本地实验页发起录音识别'})
        item = parse_qs(route.query).get('item', [''])[0]
        if item not in CATALOG:
            return self.reply(400, {'error': '图片不在常见物品 01 中'})
        if self.headers.get_content_type() != 'audio/wav':
            return self.reply(415, {'error': '录音格式不支持'})
        try:
            length = int(self.headers.get('Content-Length', '0'))
        except ValueError:
            length = 0
        if not 0 < length <= MAX_WAV_BYTES:
            return self.reply(400, {'error': '录音长度异常'})
        if not MODEL_SLOT.acquire(blocking=False):
            return self.reply(429, {'error': '本地识别正忙，请回听并评分'})
        try:
            self.connection.settimeout(8)
            payload = self.rfile.read(length)
            if len(payload) != length:
                raise ValueError('录音不完整，请回听后评分')
            self.reply(200, evaluate(item, payload))
        except (ValueError, wave.Error, EOFError, TimeoutError) as error:
            message = str(error) if isinstance(error, ValueError) else '录音无法读取，请回听后评分'
            self.reply(400, {'error': message})
        except subprocess.TimeoutExpired:
            self.log_error('Paraformer inference timed out after %d seconds', ASR_TIMEOUT)
            self.reply(503, {'error': '识别超时，请回听后评分'})
        except (OSError, subprocess.SubprocessError) as error:
            self.log_error('Paraformer inference failed (%s, code %s)', type(error).__name__,
                           getattr(error, 'returncode', getattr(error, 'errno', 'unknown')))
            self.reply(503, {'error': '本地识别未完成，请回听后评分'})
        finally:
            MODEL_SLOT.release()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-model', action='store_true')
    parser.add_argument('--host', default=os.environ.get('NAMING_HOST', '127.0.0.1'))
    parser.add_argument('--port', type=int, default=int(os.environ.get('PORT', '7192')))
    args = parser.parse_args()
    if args.check_model:
        return check_model()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    url = PUBLIC_ORIGIN or f'http://127.0.0.1:{server.server_port}'
    print(f'{url}{PREVIEW}?preview=run', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        server.server_close()


if __name__ == '__main__':
    main()
