import random

import pytest

from indicocr_bench import synth


def test_find_font_or_fail_clearly():
    try:
        p = synth.find_font("Noto Sans Telugu")
    except RuntimeError as e:
        assert "not installed" in str(e) or "fc-match" in str(e)
        pytest.skip("fonts missing")
    assert p.endswith((".ttf", ".otf"))


def test_generate_page_writes_png_and_gt(tmp_path):
    try:
        gt = synth.generate_page(tmp_path, "t", "te", random.Random(1), dpi=150)
    except RuntimeError:
        pytest.skip("fonts missing")
    assert (tmp_path / "t.png").exists() and (tmp_path / "t.txt").exists()
    assert len(gt) > 10
    assert (tmp_path / "t.txt").read_text(encoding="utf-8").splitlines() == gt


def test_degraded_is_jpeg(tmp_path):
    try:
        synth.generate_page(tmp_path, "d", "hi", random.Random(2), dpi=150, degraded=True)
    except RuntimeError:
        pytest.skip("fonts missing")
    assert (tmp_path / "d.jpg").exists()
