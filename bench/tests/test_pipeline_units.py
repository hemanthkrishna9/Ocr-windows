import numpy as np

from indicocr_bench import pipeline as P


def test_resolve_langs():
    assert P.resolve_langs("te+en") == ["te", "en"]
    assert P.resolve_langs("hi,mr") == ["devanagari"]
    assert P.resolve_langs(["kn", "ta"]) == ["ka", "ta"]


def test_det_target_size_multiple_of_32_and_limit():
    for h, w in [(3508, 2480), (100, 100), (1000, 5000), (33, 31)]:
        nh, nw = P.det_target_size(h, w, 1920)
        assert nh % 32 == 0 and nw % 32 == 0 and nh >= 32 and nw >= 32
        assert max(nh, nw) <= 1920 + 32
    assert P.det_target_size(3508, 2480, 1920)[0] == 1920


def test_det_preprocess_shape_dtype_and_norm():
    img = np.full((3508, 2480, 3), 255, np.uint8)
    x = P.det_preprocess(img, 1920)
    assert x.shape == (1, 3, 1920, 1344) and x.dtype == np.float32
    exp = (1.0 - P.DET_MEAN) / P.DET_STD
    np.testing.assert_allclose(x[0, :, 0, 0], exp, rtol=1e-5)


def test_rec_preprocess_shape_padding_and_norm():
    a = np.full((30, 300, 3), 255, np.uint8)   # -> 48 x 480
    b = np.zeros((48, 96, 3), np.uint8)        # -> 48 x 96
    x = P.rec_preprocess([a, b])
    assert x.shape == (2, 3, 48, 480) and x.dtype == np.float32
    assert np.allclose(x[0], 1.0)
    assert np.allclose(x[1, :, :, :96], -1.0) and np.allclose(x[1, :, :, 96:], 0.0)


def test_ctc_decode_collapse_blank_and_conf():
    cs = ["<blank>", "a", "b", " "]
    # path: a a blank a b b blank -> "aab"
    seq = [1, 1, 0, 1, 2, 2, 0]
    p = np.full((1, len(seq), 4), 0.01, np.float32)
    for t, k in enumerate(seq):
        p[0, t, k] = 0.9
    (txt, conf), = P.ctc_decode(p, cs)
    assert txt == "aab"
    assert abs(conf - 0.9) < 1e-6
    # all blank
    p2 = np.zeros((1, 3, 4), np.float32); p2[..., 0] = 1
    assert P.ctc_decode(p2, cs) == [("", 0.0)]


def test_load_charset_adds_blank_and_space(tmp_path):
    f = tmp_path / "d.txt"
    f.write_text("x\ny\n \n", encoding="utf-8")
    cs = P.load_charset(f)
    assert cs == ["<blank>", "x", "y", " ", " "]


def test_sort_boxes_reading_order():
    def box(x, y):
        return np.array([[x, y], [x + 50, y], [x + 50, y + 20], [x, y + 20]], np.float32)
    got = P.sort_boxes([box(300, 102), box(10, 100), box(10, 10)])
    assert [tuple(b[0]) for b in got] == [(10, 10), (10, 100), (300, 102)]


def test_db_postprocess_finds_rectangle():
    pred = np.zeros((128, 256), np.float32)
    pred[40:70, 30:200] = 0.9
    boxes = P.db_postprocess(pred, 256, 512)
    assert len(boxes) == 1
    b = boxes[0]
    assert b[:, 0].min() < 60 and b[:, 0].max() > 390  # mapped to 2x source coords, unclipped a bit
    assert P.rotate_crop(np.zeros((256, 512, 3), np.uint8), b).shape[0] > 0
