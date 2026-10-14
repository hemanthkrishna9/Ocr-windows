"""PaddleOCR PP-OCRv3 (det + rec) on ONNX Runtime, CPU only. No network access anywhere."""
from __future__ import annotations

import math
import os
import time
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort
import pyclipper

# user-facing language code -> model file prefix
LANG_PREFIX = {
    "te": "te", "hi": "devanagari", "mr": "devanagari", "devanagari": "devanagari",
    "ta": "ta", "kn": "ka", "ka": "ka", "en": "en",
}
DET_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
DET_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
REC_H = 48
MAX_REC_W = 3200


def resolve_langs(spec) -> list[str]:
    """'te+en' or ['te','en'] -> ['te','en'] (canonical prefixes, order kept, de-duplicated)."""
    parts = spec.replace(",", "+").split("+") if isinstance(spec, str) else list(spec)
    out: list[str] = []
    for p in parts:
        p = p.strip().lower()
        if not p:
            continue
        if p not in LANG_PREFIX:
            raise ValueError(f"unknown language {p!r}; choose from {sorted(LANG_PREFIX)}")
        c = LANG_PREFIX[p]
        if c not in out:
            out.append(c)
    if not out:
        raise ValueError("no language given")
    return out


# The shipped en_dict.txt (63 lines, no punctuation) does not match en_PP_OCRv3_rec.onnx (97 classes).
# The model follows PaddleOCR's ppocr/utils/en_dict.txt order (94 printable ASCII chars, then a literal
# space line) + CTC space; the index->char mapping below was verified empirically against the model.
EN_PPOCR_CHARSET = ["<blank>"] + list("0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`"
                                      "abcdefghijklmnopqrstuvwxyz{|}~!\"#$%&'()*+,-./") + [" ", " "]


def model_paths(models_dir: Path, lang: str):
    """Support both folder layouts. v5: det.onnx, rec_<l>.onnx, dict_<l>.txt (hi/te/ta/kn/en).
    v3: en_PP_OCRv3_det.onnx, <prefix>_PP_OCRv3_rec.onnx, <prefix>_dict.txt."""
    if (models_dir / "det.onnx").exists():
        l = {"devanagari": "hi", "ka": "kn"}.get(lang, lang)
        return models_dir / "det.onnx", models_dir / f"rec_{l}.onnx", models_dir / f"dict_{l}.txt"
    return (models_dir / "en_PP_OCRv3_det.onnx", models_dir / f"{lang}_PP_OCRv3_rec.onnx",
            models_dir / f"{lang}_dict.txt")


def covers_latin(charset: list[str]) -> bool:
    return set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") <= set(charset)


def load_charset(path: Path) -> list[str]:
    """CTC charset: [blank] + dict lines + [' ']. Only line terminators are stripped."""
    with open(path, encoding="utf-8") as f:
        chars = [ln.rstrip("\r\n") for ln in f.read().split("\n")]
    if chars and chars[-1] == "":
        chars.pop()
    return ["<blank>"] + chars + [" "]


# ---------------------------------------------------------------- detection

def det_target_size(h: int, w: int, max_side: int) -> tuple[int, int]:
    ratio = max_side / max(h, w) if max(h, w) > max_side else 1.0
    nh = max(int(round(h * ratio / 32)) * 32, 32)
    nw = max(int(round(w * ratio / 32)) * 32, 32)
    return nh, nw


def det_preprocess(img_bgr: np.ndarray, max_side: int = 1920) -> np.ndarray:
    h, w = img_bgr.shape[:2]
    nh, nw = det_target_size(h, w, max_side)
    r = cv2.resize(img_bgr, (nw, nh), interpolation=cv2.INTER_LINEAR)
    x = (r.astype(np.float32) / 255.0 - DET_MEAN) / DET_STD
    return np.ascontiguousarray(x.transpose(2, 0, 1)[None])


def _mini_box(contour):
    rect = cv2.minAreaRect(contour)
    pts = sorted(cv2.boxPoints(rect).tolist(), key=lambda p: p[0])
    i1, i4 = (0, 1) if pts[1][1] > pts[0][1] else (1, 0)
    i2, i3 = (2, 3) if pts[3][1] > pts[2][1] else (3, 2)
    return np.array([pts[i1], pts[i2], pts[i3], pts[i4]], dtype=np.float32), min(rect[1])


def _box_score(pred: np.ndarray, box: np.ndarray) -> float:
    h, w = pred.shape
    x0 = int(np.clip(np.floor(box[:, 0].min()), 0, w - 1)); x1 = int(np.clip(np.ceil(box[:, 0].max()), 0, w - 1))
    y0 = int(np.clip(np.floor(box[:, 1].min()), 0, h - 1)); y1 = int(np.clip(np.ceil(box[:, 1].max()), 0, h - 1))
    mask = np.zeros((y1 - y0 + 1, x1 - x0 + 1), np.uint8)
    b = box.copy(); b[:, 0] -= x0; b[:, 1] -= y0
    cv2.fillPoly(mask, [b.astype(np.int32)], 1)
    return float(cv2.mean(pred[y0:y1 + 1, x0:x1 + 1], mask)[0])


def _unclip(box: np.ndarray, ratio: float):
    area = cv2.contourArea(box)
    length = cv2.arcLength(box, True)
    if length < 1e-6:
        return None
    dist = area * ratio / length
    off = pyclipper.PyclipperOffset()
    off.AddPath(box.astype(np.int64).tolist(), pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
    res = off.Execute(dist)
    if not res:
        return None
    return np.array(res[0], dtype=np.float32).reshape(-1, 1, 2)


def db_postprocess(pred: np.ndarray, src_h: int, src_w: int, thresh=0.3, box_thresh=0.6,
                   unclip_ratio=1.5, min_size=3, max_candidates=1000) -> list[np.ndarray]:
    """pred: (H, W) probability map. Returns boxes [tl,tr,br,bl] (4,2) in source-image coordinates."""
    h, w = pred.shape
    bitmap = (pred > thresh).astype(np.uint8)
    contours, _ = cv2.findContours(bitmap * 255, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []
    for c in contours[:max_candidates]:
        box, ss = _mini_box(c)
        if ss < min_size:
            continue
        if _box_score(pred, box) < box_thresh:
            continue
        up = _unclip(box, unclip_ratio)
        if up is None:
            continue
        box, ss = _mini_box(up)
        if ss < min_size + 2:
            continue
        box[:, 0] = np.clip(np.round(box[:, 0] / w * src_w), 0, src_w)
        box[:, 1] = np.clip(np.round(box[:, 1] / h * src_h), 0, src_h)
        boxes.append(box)
    return sort_boxes(boxes)


def sort_boxes(boxes: list[np.ndarray]) -> list[np.ndarray]:
    """Top-to-bottom, then left-to-right within a row (PaddleOCR sorted_boxes)."""
    res = sorted(boxes, key=lambda b: (b[0][1], b[0][0]))
    for i in range(len(res) - 1):
        for j in range(i, -1, -1):
            if abs(res[j + 1][0][1] - res[j][0][1]) < 10 and res[j + 1][0][0] < res[j][0][0]:
                res[j], res[j + 1] = res[j + 1], res[j]
            else:
                break
    return res


def rotate_crop(img: np.ndarray, box: np.ndarray, rotate_vertical: bool = True) -> np.ndarray:
    b = box.astype(np.float32)
    w = max(int(max(np.linalg.norm(b[0] - b[1]), np.linalg.norm(b[2] - b[3]))), 1)
    h = max(int(max(np.linalg.norm(b[0] - b[3]), np.linalg.norm(b[1] - b[2]))), 1)
    dst = np.array([[0, 0], [w, 0], [w, h], [0, h]], dtype=np.float32)
    m = cv2.getPerspectiveTransform(b, dst)
    out = cv2.warpPerspective(img, m, (w, h), borderMode=cv2.BORDER_REPLICATE, flags=cv2.INTER_CUBIC)
    if rotate_vertical and h / w >= 1.5:
        out = np.rot90(out)
    return np.ascontiguousarray(out)


# -------------------------------------------------------------- recognition

def rec_width(crop: np.ndarray) -> int:
    h, w = crop.shape[:2]
    return int(min(max(math.ceil(REC_H * w / h), 1), MAX_REC_W))


def rec_preprocess(crops: list[np.ndarray], width: int | None = None) -> np.ndarray:
    """Batch (N,3,48,W): resize to height 48 keeping ratio, (x/255-0.5)/0.5, zero-pad right."""
    ws = [rec_width(c) for c in crops]
    W = width or max(ws)
    batch = np.zeros((len(crops), 3, REC_H, W), np.float32)
    for i, (c, rw) in enumerate(zip(crops, ws)):
        r = cv2.resize(c, (rw, REC_H), interpolation=cv2.INTER_LINEAR)
        x = (r.astype(np.float32) / 255.0 - 0.5) / 0.5
        batch[i, :, :, :rw] = x.transpose(2, 0, 1)
    return batch


def ctc_decode(probs: np.ndarray, charset: list[str]) -> list[tuple[str, float]]:
    """Greedy CTC. probs (N,T,C). Collapse repeats, drop blank (0). conf = mean max-prob of kept steps."""
    idx = probs.argmax(-1)
    mx = probs.max(-1)
    out = []
    for i in range(probs.shape[0]):
        prev = -1
        chars, confs = [], []
        for t, k in enumerate(idx[i]):
            if k != prev and k != 0:
                chars.append(charset[k] if k < len(charset) else "")
                confs.append(float(mx[i, t]))
            prev = k
        out.append(("".join(chars), float(np.mean(confs)) if confs else 0.0))
    return out


# ------------------------------------------------------------------ pipeline

@dataclass
class Line:
    text: str
    conf: float
    box: list  # [[x,y]*4] in source coordinates
    script: str


@dataclass
class PageResult:
    lines: list[Line]
    timings: dict = field(default_factory=dict)

    @property
    def text(self) -> str:
        return "\n".join(l.text for l in self.lines if l.text)


class OcrPipeline:
    def __init__(self, models_dir, langs="te+en", threads: int | None = None, max_side: int = 1920,
                 rec_batch: int = 6, det_params: dict | None = None, run_all_recs: bool = False):
        self.models_dir = Path(models_dir)
        self.langs = resolve_langs(langs)
        self.threads = threads or os.cpu_count() or 1
        self.max_side = max_side
        self.rec_batch = rec_batch
        self.run_all_recs = run_all_recs
        self.det_params = det_params or {}
        self.load_seconds: dict[str, float] = {}
        self._rec: dict = {}
        so = ort.SessionOptions()
        so.intra_op_num_threads = self.threads
        so.inter_op_num_threads = 1
        so.log_severity_level = 3
        self._so = so
        self.det = self._session(model_paths(self.models_dir, "en")[0], "det")
        self.det_in = self.det.get_inputs()[0].name
        for l in self.langs:
            self.ensure_lang(l)

    def _session(self, path: Path, key: str):
        if not path.exists():
            raise FileNotFoundError(f"model file missing: {path}")
        t = time.perf_counter()
        s = ort.InferenceSession(str(path), self._so, providers=["CPUExecutionProvider"])
        self.load_seconds[key] = time.perf_counter() - t
        return s

    def ensure_lang(self, lang: str):
        lang = resolve_langs(lang)[0]
        if lang in self._rec:
            return
        _, rec_p, dict_p = model_paths(self.models_dir, lang)
        s = self._session(rec_p, f"rec_{lang}")
        cs = load_charset(dict_p)
        c = s.get_outputs()[0].shape[-1]
        if isinstance(c, int) and c != len(cs) and lang == "en" and c == len(EN_PPOCR_CHARSET):
            cs = EN_PPOCR_CHARSET
        if isinstance(c, int) and c != len(cs):
            raise ValueError(f"{lang}: model has {c} classes but dict gives {len(cs)} (= len(dict)+2)")
        if lang not in self.langs:
            self.langs.append(lang)
        self._rec[lang] = (s, s.get_inputs()[0].name, cs)

    def effective_langs(self, langs: list[str]) -> list[str]:
        """If an Indic recogniser's dict already contains A-Z a-z 0-9 (PP-OCRv5), the English recogniser is
        redundant and skipped unless run_all_recs is set."""
        if self.run_all_recs or "en" not in langs or len(langs) < 2:
            return langs
        indic = [l for l in langs if l != "en"]
        if all(covers_latin(self._rec[l][2]) for l in indic):
            return indic
        return langs

    def warmup(self):
        self.run(np.full((256, 512, 3), 255, np.uint8), langs=None)

    # -- stages
    def detect(self, img_bgr: np.ndarray) -> list[np.ndarray]:
        h, w = img_bgr.shape[:2]
        x = det_preprocess(img_bgr, self.max_side)
        pred = self.det.run(None, {self.det_in: x})[0][0, 0]
        return db_postprocess(pred, h, w, **self.det_params)

    def recognize(self, crops: list[np.ndarray], lang: str) -> list[tuple[str, float]]:
        sess, name, cs = self._rec[resolve_langs(lang)[0]]
        order = sorted(range(len(crops)), key=lambda i: crops[i].shape[1] / crops[i].shape[0])
        res: list = [None] * len(crops)
        for s in range(0, len(order), self.rec_batch):
            ids = order[s:s + self.rec_batch]
            x = rec_preprocess([crops[i] for i in ids])
            probs = sess.run(None, {name: x})[0]
            for i, r in zip(ids, ctc_decode(probs, cs)):
                res[i] = r
        return res

    def run(self, img_bgr: np.ndarray, langs=None) -> PageResult:
        langs = resolve_langs(langs) if langs else list(self.langs)
        for l in langs:
            self.ensure_lang(l)
        langs = self.effective_langs(langs)
        c0 = time.process_time()
        t0 = time.perf_counter()
        boxes = self.detect(img_bgr)
        t1 = time.perf_counter()
        crops = [rotate_crop(img_bgr, b) for b in boxes]
        t2 = time.perf_counter()
        best: list = [("", -1.0, "")] * len(crops)
        for l in langs:
            for i, (txt, cf) in enumerate(self.recognize(crops, l) if crops else []):
                if cf > best[i][1]:
                    best[i] = (txt, cf, l)
        t3 = time.perf_counter()
        lines = [Line(t, c, b.tolist(), s) for (t, c, s), b in zip(best, boxes)]
        return PageResult(lines, {"det_s": t1 - t0, "crop_s": t2 - t1, "rec_s": t3 - t2, "total_s": t3 - t0,
                                  "cpu_s": time.process_time() - c0,
                                  "n_lines": len(lines)})

    def run_file(self, path, langs=None, pdf_dpi: int = 200) -> list[PageResult]:
        """Image -> [result]; PDF -> one result per page. Adds 'decode_s' to timings."""
        path = Path(path)
        out = []
        if path.suffix.lower() == ".pdf":
            for t, img in iter_pdf_pages(path, pdf_dpi):
                r = self.run(img, langs)
                r.timings["decode_s"] = t
                out.append(r)
        else:
            t = time.perf_counter()
            img = read_image(path)
            dt = time.perf_counter() - t
            r = self.run(img, langs)
            r.timings["decode_s"] = dt
            out.append(r)
        return out


def read_image(path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)  # also works for non-ASCII paths
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)  # BGR
    if img is None:
        raise ValueError(f"cannot decode image {path}")
    return img


def iter_pdf_pages(path, dpi: int = 200):
    import pypdfium2 as pdfium
    pdf = pdfium.PdfDocument(str(path))
    try:
        for i in range(len(pdf)):
            t = time.perf_counter()
            pil = pdf[i].render(scale=dpi / 72).to_pil().convert("RGB")
            img = cv2.cvtColor(np.asarray(pil), cv2.COLOR_RGB2BGR)
            yield time.perf_counter() - t, img
    finally:
        pdf.close()
