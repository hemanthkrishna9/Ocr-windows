import os
from pathlib import Path

import pytest


@pytest.fixture(scope="session")
def models_dir():
    d = os.environ.get("INDICOCR_MODELS")
    if not d or not (any((Path(d) / n).exists() for n in ("det.onnx", "en_PP_OCRv3_det.onnx"))):
        pytest.skip("INDICOCR_MODELS not set or models missing")
    return Path(d)
