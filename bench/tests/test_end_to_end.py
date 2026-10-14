import random

import pytest

from indicocr_bench import metrics, synth
from indicocr_bench.pipeline import OcrPipeline, read_image


def test_synthetic_telugu_page_end_to_end(models_dir, tmp_path):
    try:
        gt = synth.generate_page(tmp_path, "te", "te", random.Random(7), dpi=300)
    except RuntimeError:
        pytest.skip("Noto fonts missing")
    pipe = OcrPipeline(models_dir, "te+en", max_side=1920)
    res = pipe.run(read_image(tmp_path / "te.png"))
    assert len(res.lines) >= len(gt) * 0.7          # detector finds most lines
    assert all(l.script in ("te", "en") for l in res.lines)
    ref = "\n".join(gt)
    # Floor is deliberately low: PP-OCRv3 Telugu emits few spaces and drops some conjunct parts.
    assert 1 - metrics.cer_nospace(ref, res.text) > 0.5
    for k in ("det_s", "rec_s", "total_s"):
        assert res.timings[k] > 0
