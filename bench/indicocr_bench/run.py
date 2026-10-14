"""Benchmark CLI.

python -m indicocr_bench.run --models DIR --data DIR --langs te+en [--max-side 1920] --report out.md
"""
from __future__ import annotations

import argparse
import json
import os
import platform
import sys
import time
from collections import defaultdict
from pathlib import Path

from . import metrics
from .pipeline import OcrPipeline, resolve_langs

EXTS = {".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp", ".pdf"}


def discover(data_dir: Path):
    """Yield (image_path, gt_path, page_meta) for every page that has a ground-truth .txt next to it."""
    meta = {}
    mp = data_dir / "meta.json"
    if mp.exists():
        meta = json.loads(mp.read_text(encoding="utf-8"))
    pages_meta = meta.get("pages", {})
    skipped = []
    for p in sorted(data_dir.iterdir()):
        if p.suffix.lower() not in EXTS:
            continue
        gt = p.with_suffix(".txt")
        if not gt.exists():
            skipped.append(p.name)
            continue
        pm = dict(pages_meta.get(p.stem, {}))
        if "langs" not in pm and "langs" in meta:
            pm["langs"] = meta["langs"]
        yield p, gt, pm
    if skipped:
        print(f"warning: {len(skipped)} file(s) without a .txt ground truth skipped: {skipped[:5]}", file=sys.stderr)


def run_benchmark(models, data, langs=None, max_side=1920, threads=None, pdf_dpi=200, limit=None,
                  rec_batch=6, quiet=False, run_all_recs=False):
    items = list(discover(Path(data)))
    if limit:
        items = items[:limit]
    if not items:
        raise SystemExit(f"no pages with ground truth found in {data}")
    page_langs = []
    for _, _, pm in items:
        if langs:
            pl = resolve_langs(langs)
        elif pm.get("langs"):
            pl = resolve_langs(pm["langs"])
        else:
            raise SystemExit("no --langs given and no meta.json languages for " + str(_))
        page_langs.append(pl)
    union = sorted({l for pl in page_langs for l in pl})
    load_before = os.getloadavg()[0] if hasattr(os, "getloadavg") else None
    t0 = time.perf_counter()
    pipe = OcrPipeline(models, union, threads=threads, max_side=max_side, rec_batch=rec_batch, run_all_recs=run_all_recs)
    pipe.warmup()
    load_s = time.perf_counter() - t0
    rows = []
    for (img, gt, pm), pl in zip(items, page_langs):
        ref = gt.read_text(encoding="utf-8")
        results = pipe.run_file(img, pl, pdf_dpi=pdf_dpi)
        hyp = "\n".join(r.text for r in results)
        sc = metrics.score(ref, hyp)
        tm = {k: sum(r.timings.get(k, 0) for r in results) for k in ("det_s", "crop_s", "rec_s", "total_s", "decode_s", "cpu_s")}
        row = {"page": img.name, "langs": pl, "variant": pm.get("variant", ""), "n_pdf_pages": len(results),
               "n_lines": sum(len(r.lines) for r in results), "seconds": tm["total_s"], "timings": tm,
               "script_counts": _count_scripts(results), "hyp_text": hyp, **sc}
        rows.append(row)
        if not quiet:
            print(f"{img.name:32s} CER {sc['cer']*100:5.1f}%  CERns {sc['cer_ns']*100:5.1f}%  wordacc {sc['word_acc']*100:5.1f}%  {tm['total_s']:.2f}s", flush=True)
    load_after = os.getloadavg()[0] if hasattr(os, "getloadavg") else None
    groups = defaultdict(list)
    for r in rows:
        groups[(r["page"].split("_")[0] if not r["variant"] else r["page"].split("_")[0] + "/" + r["variant"])].append(r)
    agg = {"all": _agg(rows), "groups": {k: _agg(v) for k, v in sorted(groups.items())}}
    return {
        "config": {"models": str(models), "data": str(data), "langs": langs or "per-page meta", "max_side": max_side,
                   "threads": pipe.threads, "nproc": os.cpu_count(), "rec_batch": rec_batch, "run_all_recs": run_all_recs, "pdf_dpi": pdf_dpi,
                   "onnxruntime": __import__("onnxruntime").__version__, "python": platform.python_version(),
                   "cpu": _cpu_name(),
                   "loadavg_1min_start_end": [load_before, load_after]},
        "load_seconds": load_s, "load_detail": pipe.load_seconds, "pages": rows, "aggregate": agg,
    }


def _count_scripts(results):
    c = defaultdict(int)
    for r in results:
        for l in r.lines:
            c[l.script] += 1
    return dict(c)


def _agg(rows):
    a = metrics.aggregate(rows)
    secs = sorted(r["seconds"] for r in rows)
    a["sec_mean"] = sum(secs) / len(secs)
    a["sec_median"] = secs[len(secs) // 2]
    a["sec_p95"] = secs[min(len(secs) - 1, int(len(secs) * 0.95))]
    a["sec_max"] = secs[-1]
    return a


def _cpu_name():
    try:
        for ln in open("/proc/cpuinfo"):
            if ln.startswith("model name"):
                return ln.split(":", 1)[1].strip()
    except OSError:
        pass
    return platform.processor() or platform.machine()


def to_markdown(res) -> str:
    c, a = res["config"], res["aggregate"]
    o = []
    o.append("# OCR benchmark report\n")
    o.append(f"- Data: `{c['data']}`  |  languages: {c['langs']}  |  det max side: {c['max_side']}")
    o.append(f"- Machine: {c['cpu']}, nproc={c['nproc']}, ORT intra-op threads used: **{c['threads']}**, run-all-recognisers: {c['run_all_recs']}, "
             f"onnxruntime {c['onnxruntime']}, Python {c['python']}")
    la = c.get("loadavg_1min_start_end") or [None, None]
    if la[0] is not None:
        o.append(f"- System 1-min load average at start/end: {la[0]:.1f} / {la[1]:.1f} "
                 f"(other processes on the machine inflate timings when this exceeds ~1)")
    o.append(f"- Models: `{c['models']}`")
    o.append(f"- Model load + warm-up (excluded from per-page time): **{res['load_seconds']:.2f} s** "
             f"({', '.join(f'{k} {v:.2f}s' for k, v in res['load_detail'].items())})")
    o.append("- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; "
             "decode time reported in JSON.\n")
    g = a["all"]
    o.append("## Aggregate\n")
    o.append("| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |")
    o.append("|---|---|---|---|---|---|---|---|---|")
    for name, x in [("**ALL**", g)] + list(a["groups"].items()):
        o.append(f"| {name} | {x['pages']} | {x['cer']*100:.2f}% | {x['cer_ns']*100:.2f}% | {x['word_acc']*100:.1f}% | {x['sec_mean']:.2f} | "
                 f"{x['sec_median']:.2f} | {x['sec_p95']:.2f} | {x['sec_max']:.2f} |")
    n = len(res["pages"])
    mt = {k: sum(r["timings"][k] for r in res["pages"]) / n for k in ("det_s", "crop_s", "rec_s", "total_s", "decode_s", "cpu_s")}
    o.append(f"\nTime split per page (mean): detect {mt['det_s']:.2f} s ({mt['det_s']/mt['total_s']*100:.0f}%), "
             f"crop {mt['crop_s']:.2f} s, recognise {mt['rec_s']:.2f} s ({mt['rec_s']/mt['total_s']*100:.0f}%), "
             f"process CPU time {mt['cpu_s']:.2f} s/page (divide by threads for an idle-machine wall estimate: "
             f"{mt['cpu_s']/c['threads']:.2f} s); image decode (not counted) {mt['decode_s']:.2f} s; mean text lines per page "
             f"{sum(r['n_lines'] for r in res['pages'])/n:.1f}.")
    o.append("\nCER/word accuracy are micro-averages (total errors / total reference units); "
             "word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.\n")
    o.append("## Per page\n")
    o.append("| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |")
    o.append("|---|---|---|---|---|---|---|---|")
    for r in res["pages"]:
        t = r["timings"]
        o.append(f"| {r['page']} | {r['n_lines']} | {r['cer']*100:.1f}% | {r['cer_ns']*100:.1f}% | {r['word_acc']*100:.1f}% | "
                 f"{t['det_s']:.2f} | {t['rec_s']:.2f} | {r['seconds']:.2f} |")
    return "\n".join(o) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(prog="indicocr_bench.run", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", default=os.environ.get("INDICOCR_MODELS"),
                    help="model directory (or env INDICOCR_MODELS)")
    ap.add_argument("--data", required=True, help="dataset dir: <name>.png|jpg|pdf + <name>.txt [+ meta.json]")
    ap.add_argument("--langs", default=None, help="e.g. te, te+en, hi+en (default: from meta.json)")
    ap.add_argument("--max-side", type=int, default=1920, help="detector long-side limit (default 1920)")
    ap.add_argument("--threads", type=int, default=None, help="ORT intra-op threads (default: all cores)")
    ap.add_argument("--pdf-dpi", type=int, default=200)
    ap.add_argument("--rec-batch", type=int, default=6)
    ap.add_argument("--run-all-recs", action="store_true",
                    help="run every listed recogniser per line (e.g. te AND en) even if the Indic dict already covers Latin")
    ap.add_argument("--limit", type=int, default=None, help="only the first N pages")
    ap.add_argument("--report", default=None, help="write Markdown report here (JSON goes next to it)")
    ap.add_argument("--json", default=None, help="JSON output path (default: report path with .json)")
    ap.add_argument("--dump-text", default=None, help="directory to write recognised text per page")
    a = ap.parse_args(argv)
    if not a.models:
        ap.error("--models or INDICOCR_MODELS is required")
    res = run_benchmark(a.models, a.data, a.langs, a.max_side, a.threads, a.pdf_dpi, a.limit, a.rec_batch, run_all_recs=a.run_all_recs)
    md = to_markdown(res)
    print("\n" + md.split("## Per page")[0])
    if a.dump_text:
        d = Path(a.dump_text); d.mkdir(parents=True, exist_ok=True)
        for r in res["pages"]:
            (d / (Path(r["page"]).stem + ".ocr.txt")).write_text(r["hyp_text"], encoding="utf-8")
    jp = a.json or (str(Path(a.report).with_suffix(".json")) if a.report else None)
    if a.report:
        Path(a.report).parent.mkdir(parents=True, exist_ok=True)
        Path(a.report).write_text(md, encoding="utf-8")
        print(f"report: {a.report}")
    if jp:
        slim = dict(res)
        slim["pages"] = [{k: v for k, v in r.items() if k != "hyp_text"} for r in res["pages"]]
        Path(jp).write_text(json.dumps(slim, indent=1, ensure_ascii=False), encoding="utf-8")
        print(f"json: {jp}")


if __name__ == "__main__":
    main()
