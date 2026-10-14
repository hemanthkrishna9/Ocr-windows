import random

from indicocr_bench import metrics as m


def dp(a, b):
    p = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        c = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            c[j] = min(p[j] + 1, c[j - 1] + 1, p[j - 1] + (a[i - 1] != b[j - 1]))
        p = c
    return p[-1]


def test_edit_distance_matches_reference_dp():
    rnd = random.Random(1)
    for _ in range(500):
        a = [rnd.choice("abcd") for _ in range(rnd.randint(0, 15))]
        b = [rnd.choice("abcd") for _ in range(rnd.randint(0, 15))]
        assert m.edit_distance(a, b) == dp(a, b)


def test_known_values():
    assert m.edit_distance("kitten", "sitting") == 3
    assert m.cer("abcd", "abxd") == 0.25
    assert m.wer("a b c d", "a x c d") == 0.25
    assert m.word_accuracy("a b c d", "a x c d") == 0.75
    assert m.cer("", "") == 0.0 and m.cer("", "x") == 1.0


def test_word_accuracy_floored_at_zero():
    assert m.word_accuracy("a", "x y z w") == 0.0


def test_nfc_and_whitespace_normalisation():
    composed = "é"
    decomposed = "é"
    assert m.cer(composed, decomposed) == 0.0
    # Telugu: precomposed vs decomposed vowel sign forms normalise identically
    assert m.cer("ఈ  రోజు\n", " ఈ రోజు") == 0.0
    assert m.wer("a\tb\n c", "a b c") == 0.0


def test_cer_nospace():
    assert m.cer_nospace("ab cd", "abcd") == 0.0
    assert m.cer("ab cd", "abcd") > 0


def test_aggregate_micro_average():
    s1 = m.score("aaaa bbbb", "aaaa bbbb")
    s2 = m.score("c", "x")
    a = m.aggregate([s1, s2])
    assert a["pages"] == 2
    assert abs(a["cer"] - 1 / 10) < 1e-9
