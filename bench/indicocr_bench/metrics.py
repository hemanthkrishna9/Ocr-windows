"""CER / WER with Unicode NFC and whitespace normalisation."""
from __future__ import annotations

import re
import unicodedata
from typing import Sequence

_WS = re.compile(r"\s+")


def normalize(text: str) -> str:
    """NFC, drop zero-width/BOM noise that does not change the visible text, collapse whitespace."""
    text = unicodedata.normalize("NFC", text)
    text = text.replace("﻿", "")
    return _WS.sub(" ", text).strip()


def edit_distance(a: Sequence, b: Sequence) -> int:
    """Levenshtein distance (Hyyro's bit-parallel variant of Myers; exact)."""
    if len(a) < len(b):
        a, b = b, a  # a is the longer pattern
    # strip common prefix / suffix
    s = 0
    n, m = len(a), len(b)
    while s < m and a[s] == b[s]:
        s += 1
    a, b = a[s:], b[s:]
    while a and b and a[-1] == b[-1]:
        a, b = a[:-1], b[:-1]
    m = len(b)
    if m == 0:
        return len(a)
    if len(a) == 0:
        return m
    # pattern = b (shorter), text = a
    peq: dict = {}
    for i, c in enumerate(b):
        peq[c] = peq.get(c, 0) | (1 << i)
    mask = (1 << m) - 1
    hb = 1 << (m - 1)
    pv, mv, score = mask, 0, m
    for c in a:
        eq = peq.get(c, 0)
        xv = eq | mv
        xh = ((((eq & pv) + pv) ^ pv) | eq) & mask
        ph = (mv | ~(xh | pv)) & mask
        mh = pv & xh
        if ph & hb:
            score += 1
        elif mh & hb:
            score -= 1
        ph = ((ph << 1) | 1) & mask
        mh = (mh << 1) & mask
        pv = (mh | ~(xv | ph)) & mask
        mv = ph & xv
    return score


def cer(ref: str, hyp: str) -> float:
    """Character error rate on normalised text (spaces count as characters)."""
    r, h = normalize(ref), normalize(hyp)
    if not r:
        return 0.0 if not h else 1.0
    return edit_distance(r, h) / len(r)


def wer(ref: str, hyp: str) -> float:
    r, h = normalize(ref).split(), normalize(hyp).split()
    if not r:
        return 0.0 if not h else 1.0
    return edit_distance(r, h) / len(r)


def cer_nospace(ref: str, hyp: str) -> float:
    """CER with all whitespace removed: isolates glyph errors from missing/extra word spaces."""
    r, h = _WS.sub("", normalize(ref)), _WS.sub("", normalize(hyp))
    if not r:
        return 0.0 if not h else 1.0
    return edit_distance(r, h) / len(r)


def word_accuracy(ref: str, hyp: str) -> float:
    """1 - WER, floored at 0 (WER can exceed 1 with many insertions)."""
    return max(0.0, 1.0 - wer(ref, hyp))


def score(ref: str, hyp: str) -> dict:
    r, h = normalize(ref), normalize(hyp)
    rw, hw = r.split(), h.split()
    ce = edit_distance(r, h)
    we = edit_distance(rw, hw)
    rn, hn = _WS.sub("", r), _WS.sub("", h)
    cn = edit_distance(rn, hn)
    return {
        "ref_chars_ns": len(rn), "char_errors_ns": cn, "cer_ns": (cn / len(rn)) if rn else float(bool(hn)),
        "ref_chars": len(r), "ref_words": len(rw),
        "char_errors": ce, "word_errors": we,
        "cer": (ce / len(r)) if r else float(bool(h)),
        "wer": (we / len(rw)) if rw else float(bool(hw)),
        "word_acc": max(0.0, 1.0 - ((we / len(rw)) if rw else float(bool(hw)))),
    }


def aggregate(scores: list[dict]) -> dict:
    """Micro-average (error totals over reference totals), plus macro word accuracy."""
    rc = sum(s["ref_chars"] for s in scores)
    rw = sum(s["ref_words"] for s in scores)
    ce = sum(s["char_errors"] for s in scores)
    we = sum(s["word_errors"] for s in scores)
    return {
        "pages": len(scores),
        "cer": ce / rc if rc else 0.0,
        "wer": we / rw if rw else 0.0,
        "word_acc": max(0.0, 1 - we / rw) if rw else 1.0,
        "cer_ns": (sum(s["char_errors_ns"] for s in scores) / max(sum(s["ref_chars_ns"] for s in scores), 1)),
        "word_acc_macro": sum(s["word_acc"] for s in scores) / len(scores) if scores else 0.0,
    }
