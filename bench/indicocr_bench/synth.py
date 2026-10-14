"""Synthetic A4 page generator (Noto fonts, Pillow+raqm shaping) with optional degradations.

Writes <name>.png (clean) or <name>.jpg (degraded) plus <name>.txt (one line per printed line)
and meta.json (per-page language list) into the output directory.
"""
from __future__ import annotations

import argparse
import json
import random
import subprocess
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont, features

from .corpus import CORPUS

A4_300 = (2480, 3508)
FONT_FAMILY = {"te": "Noto Sans Telugu", "hi": "Noto Sans Devanagari", "ta": "Noto Sans Tamil",
               "kn": "Noto Sans Kannada", "en": "Noto Sans"}
EN_INSERTS = ["application", "survey", "Aadhaar", "PAN", "OTP", "email", "document", "court", "online",
              "Rs. 25,000", "No. 12/3", "2026", "08/10/2025", "ID", "bank", "notice", "FIR"]


def find_font(family: str, bold: bool = False) -> str:
    style = "Bold" if bold else "Regular"
    try:
        out = subprocess.run(["fc-match", "-f", "%{file}|%{family}", f"{family}:style={style}"],
                             capture_output=True, text=True, check=True).stdout
        path, fam = out.split("|", 1)
    except (OSError, subprocess.CalledProcessError) as e:
        raise RuntimeError(f"fc-match unavailable, cannot locate font {family!r}: {e}")
    if family.lower() not in fam.lower():
        raise RuntimeError(f"font {family!r} ({style}) not installed (fc-match gave {fam!r}); "
                           f"install fonts-noto-core or equivalent")
    return path


def _is_latin(word: str) -> bool:
    return all(ord(c) < 0x250 for c in word)


class Typesetter:
    def __init__(self, script: str, size: int, bold: bool = False):
        if not features.check("raqm"):
            raise RuntimeError("Pillow lacks libraqm; Indic shaping would be wrong")
        self.size = size
        self.fonts = {
            "ind": ImageFont.truetype(find_font(FONT_FAMILY[script], bold), size, layout_engine=ImageFont.Layout.RAQM),
            "lat": ImageFont.truetype(find_font(FONT_FAMILY["en"], bold), size, layout_engine=ImageFont.Layout.RAQM),
        }
        self.script = script
        self.space = self.fonts["lat"].getlength(" ")

    def font_for(self, word):
        return self.fonts["lat"] if (self.script == "en" or _is_latin(word)) else self.fonts["ind"]

    def width(self, words):
        return sum(self.font_for(w).getlength(w) for w in words) + self.space * max(len(words) - 1, 0)

    def wrap(self, text: str, max_w: float) -> list[list[str]]:
        lines, cur = [], []
        for w in text.split():
            if cur and self.width(cur + [w]) > max_w:
                lines.append(cur)
                cur = [w]
            else:
                cur.append(w)
        if cur:
            lines.append(cur)
        return lines

    def draw(self, d: ImageDraw.ImageDraw, x: float, y: float, words, fill=0):
        for w in words:
            f = self.font_for(w)
            d.text((x, y), w, font=f, fill=fill, anchor="ls" if False else "la")
            x += f.getlength(w) + self.space


def make_paragraph_text(script: str, rng: random.Random, mixed: bool) -> str:
    n = rng.randint(3, 5)
    sents = []
    for _ in range(n):
        if mixed and rng.random() < 0.35:
            s = rng.choice(CORPUS["en"])
        else:
            s = rng.choice(CORPUS[script])
            if mixed and rng.random() < 0.7:
                ws = s.split()
                for _ in range(rng.randint(1, 2)):
                    ws.insert(rng.randint(0, len(ws)), rng.choice(EN_INSERTS))
                s = " ".join(ws)
        sents.append(s)
    return " ".join(sents)


def render_page(script: str, rng: random.Random, mixed: bool = False, dpi: int = 300):
    """Returns (PIL grayscale image at 300 dpi, ground-truth lines). Downscaling for lower dpi is done by caller."""
    W, H = A4_300
    size = rng.randint(40, 52)
    margin = rng.randint(200, 280)
    body = Typesetter(script, size)
    title_ts = Typesetter(script, int(size * 1.35), bold=True)
    img = Image.new("L", (W, H), 255)
    d = ImageDraw.Draw(img)
    lines_gt: list[str] = []
    asc = size * 1.0
    y = margin
    # title
    tl = title_ts.wrap(rng.choice(CORPUS[script]), W - 2 * margin)[0]
    title_ts.draw(d, margin, y, tl)
    lines_gt.append(" ".join(tl))
    y += int(title_ts.size * 2.2)
    lh = int(size * rng.uniform(1.7, 1.95))
    while True:
        para = make_paragraph_text(script, rng, mixed)
        wrapped = body.wrap(para, W - 2 * margin)
        if y + lh * len(wrapped) > H - margin:
            wrapped = wrapped[: max(0, int((H - margin - y) // lh))]
            if not wrapped:
                break
        for words in wrapped:
            body.draw(d, margin, y, words)
            lines_gt.append(" ".join(words))
            y += lh
        y += int(lh * 0.8)
        if y + lh > H - margin:
            break
    return img, lines_gt


def degrade(img: Image.Image, rng: random.Random, rotate=True, blur=True, noise=True, low_contrast=True):
    """Apply degradations on a grayscale PIL image; returns BGR uint8 ndarray (before JPEG)."""
    a = np.asarray(img.convert("L")).astype(np.float32)
    h, w = a.shape
    if rotate:
        ang = rng.uniform(-1.5, 1.5)
        m = cv2.getRotationMatrix2D((w / 2, h / 2), ang, 1.0)
        a = cv2.warpAffine(a, m, (w, h), flags=cv2.INTER_LINEAR, borderValue=255)
    if blur:
        a = cv2.GaussianBlur(a, (0, 0), rng.uniform(0.8, 1.6))
    if low_contrast:
        lo, hi = rng.uniform(40, 90), rng.uniform(170, 215)
        a = lo + a / 255.0 * (hi - lo)
    if noise:
        nrng = np.random.default_rng(rng.randint(0, 2**31))
        a = a + nrng.normal(0, rng.uniform(5, 12), a.shape)
    a = np.clip(a, 0, 255).astype(np.uint8)
    return cv2.cvtColor(a, cv2.COLOR_GRAY2BGR)


def save_page(path_noext: Path, img_bgr: np.ndarray, jpeg_quality: int | None):
    if jpeg_quality:
        ok, buf = cv2.imencode(".jpg", img_bgr, [cv2.IMWRITE_JPEG_QUALITY, jpeg_quality])
        out = path_noext.with_suffix(".jpg")
    else:
        ok, buf = cv2.imencode(".png", img_bgr)
        out = path_noext.with_suffix(".png")
    assert ok
    buf.tofile(str(out))
    return out


def generate_page(out_dir: Path, name: str, script: str, rng: random.Random, mixed=False, dpi=300,
                  degraded=False, jpeg_quality=55):
    img, gt = render_page(script, rng, mixed)
    if dpi != 300:
        s = dpi / 300
        img = img.resize((round(img.width * s), round(img.height * s)), Image.LANCZOS)
    if degraded:
        bgr = degrade(img, rng)
        save_page(out_dir / name, bgr, jpeg_quality)
    else:
        save_page(out_dir / name, cv2.cvtColor(np.asarray(img), cv2.COLOR_GRAY2BGR), None)
    (out_dir / f"{name}.txt").write_text("\n".join(gt) + "\n", encoding="utf-8")
    return gt


def generate_set(out_dir, per_lang=10, mixed=5, seed=1234, langs=("te", "hi", "ta", "kn"), variants=("clean", "degraded")):
    """Pages 0..4 of each language at 300 dpi, the rest at 150 dpi. Each page in each variant."""
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    rng = random.Random(seed)
    meta = {"description": "synthetic pages (generated, not real)", "pages": {}}
    jobs = [(l, i, False) for l in langs for i in range(per_lang)] + [("te", i, True) for i in range(mixed)]
    for script, i, is_mixed in jobs:
        tag = "tem" if is_mixed else script
        for v in variants:
            name = f"{tag}_{i:02d}_{v}"
            dpi = 300 if i < max(per_lang // 2, 1) else 150
            # same page seed for clean+degraded variants of one page would need re-render; seed per (page, variant)
            prng = random.Random(rng.randint(0, 2**31))
            generate_page(out_dir, name, script, prng, mixed=is_mixed, dpi=dpi, degraded=(v == "degraded"))
            meta["pages"][name] = {"langs": ["te", "en"] if is_mixed else [script if script != "hi" else "hi"],
                                   "dpi": dpi, "variant": v}
    meta["seed"] = seed
    (out_dir / "meta.json").write_text(json.dumps(meta, indent=1), encoding="utf-8")
    return out_dir


def main(argv=None):
    ap = argparse.ArgumentParser(description="Generate a synthetic OCR test set")
    ap.add_argument("--out", required=True)
    ap.add_argument("--per-lang", type=int, default=10)
    ap.add_argument("--mixed", type=int, default=5)
    ap.add_argument("--seed", type=int, default=1234)
    a = ap.parse_args(argv)
    generate_set(a.out, a.per_lang, a.mixed, a.seed)
    print(f"wrote set to {a.out}")


if __name__ == "__main__":
    main()
