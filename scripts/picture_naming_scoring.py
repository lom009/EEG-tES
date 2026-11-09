# coding: utf-8
"""Deterministic text and tone-aware pronunciation scoring for the naming demo."""
import re
import unicodedata
from functools import lru_cache
from math import prod

from pypinyin import Style, pinyin
from pypinyin.contrib.tone_convert import to_tone3


def normalize(text):
    text = re.sub(r'<\|[^|]*\|>', '', text)
    return ''.join(c for c in unicodedata.normalize('NFKC', text)
                   if not c.isspace() and not unicodedata.category(c).startswith('P'))


class PronunciationScorer:
    def __init__(self, readings_file):
        self.standard = {}
        for line in readings_file.read_text(encoding='utf-8').splitlines():
            if not line.startswith('U+'):
                continue
            point, text = line.split('#', 1)[0].split(':', 1)
            self.standard[chr(int(point[2:], 16))] = tuple(
                to_tone3(part.strip(), neutral_tone_with_five=True) for part in text.split(',')
            )

    @lru_cache(maxsize=1024)
    def pronunciation(self, text):
        if not text or not all(unicodedata.name(c, '').startswith('CJK UNIFIED IDEOGRAPH') for c in text):
            return ()
        if len(text) == 1 and text in self.standard:
            return (self.standard[text],)
        groups = pinyin(text, style=Style.TONE3, heteronym=True, neutral_tone_with_five=True)
        if len(groups) != len(text) or not all(re.fullmatch(r'[a-züvê]+[1-5]', r) for g in groups for r in g):
            return ()
        resolved = []
        for character, group in zip(text, groups):
            readings = tuple(dict.fromkeys(group))
            if len(readings) > 1 and character in self.standard:
                standard = tuple(r for r in readings if r in self.standard[character])
                readings = standard or readings
            resolved.append(readings)
        return tuple(resolved)

    def score(self, answers, transcript, baseline, confirmed_readings=None):
        text = normalize(transcript)
        if text in answers:
            return '正确', '文字命中'
        heard = self.pronunciation(text)
        fallback = baseline, '已知错答' if baseline == '错误' else '待复核'
        if not heard:
            return fallback
        accepted = set()
        unresolved = False
        for answer in answers:
            groups = self.pronunciation(answer)
            patterns = (confirmed_readings or {}).get(answer)
            if patterns is None:
                if len(groups) == 1:
                    patterns = [(reading,) for reading in groups[0]]
                elif groups and all(len(group) == 1 for group in groups):
                    patterns = [tuple(group[0] for group in groups)]
                else:
                    patterns = []
            accepted.update(tuple(pattern) for pattern in patterns)
            if not patterns and len(heard) == len(groups):
                unresolved |= all(set(left).intersection(right) for left, right in zip(heard, groups))
        matches = sum(len(heard) == len(pattern) and all(r in group for group, r in zip(heard, pattern))
                      for pattern in accepted)
        if matches == prod(len(group) for group in heard):
            return '正确', '同音命中'
        if matches or unresolved:
            return '待复核', '读音不确定'
        return fallback
