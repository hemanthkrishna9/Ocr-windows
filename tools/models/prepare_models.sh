#!/usr/bin/env bash
# Reproduces how the ONNX models in models/ are prepared.
#
# Usage: tools/models/prepare_models.sh [WORK_DIR] [OUT_DIR]
#   WORK_DIR  scratch space for venvs, nupkgs and extracted files (default ./models-work)
#   OUT_DIR   where the models and dictionaries are written          (default ./models)
#
# Output layout in OUT_DIR:
#   det.onnx                      PP-OCRv5 mobile DB detector (dynamic N,H,W)
#   rec_{te,hi,ta,en}.onnx        PP-OCRv5 mobile recognizers (height 48, dynamic width)
#   rec_kn.onnx                   PP-OCRv3 Kannada recognizer (no v5 Kannada model exists)
#   dict_{te,hi,ta,en,kn}.txt     character dictionaries (classes = lines + 2: blank first, space last)
#
# Sources are NuGet packages: Sdcb.PaddleOCR.Models.LocalV5 3.3.1 (v5) and
# Sdcb.PaddleOCR.Models.LocalV3 2.7.0.1 (Kannada only). Their embedded resources are read
# as data with dnfile; the DLLs are never loaded or run. Two venvs are used because the
# v5 models need paddlepaddle 3.x / paddle2onnx 2.x and the v3 model needs the 2.6 / 1.3 pair.
#
# Reproducibility: every pip package is pinned exactly (see the *_PIP arrays below) and every
# downloaded artifact (two nupkgs, the paddleocr wheel) is verified by SHA-256 before use.
# The converted outputs are compared to tools/models/models.sha256 at the end; a mismatch only
# warns (ONNX export could differ across platforms), a missing/short output fails.
#
# Speed experiment (not adopted): onnxruntime.quantization.quantize_dynamic(QUInt8) on the
# rec models cut size 4x but was ~2x SLOWER on CPU, so the shipped models are FP32.
set -euo pipefail

WORK="${1:-./models-work}"
OUT="${2:-./models}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON="${PYTHON:-python3.11}"

V5_NUPKG_SHA=5a1f1469e2657df7ef2768f36533432ffc6be891496c43b50c4c92813693d4e2
V3_NUPKG_SHA=2a21f878c20ca1d7510a8cb6a37826bfe0c7acb559328cfede2046e1baa5b2e8
WHEEL_SHA=73fa76fa3be118d8469141ad72b2665f30795386bdd87b2bde5ff7ee3069e1be

V5_URL="https://api.nuget.org/v3-flatcontainer/sdcb.paddleocr.models.localv5/3.3.1/sdcb.paddleocr.models.localv5.3.3.1.nupkg"
V5_DLL="lib/netstandard2.0/Sdcb.PaddleOCR.Models.LocalV5.dll"
V5_PREFIX="Sdcb.PaddleOCR.Models.LocalV5.models."
V5_MODELS=(mobile_zh_det te_PP_OCRv5_mobile_rec ta_PP_OCRv5_mobile_rec devanagari_PP_OCRv5_mobile_rec en_PP_OCRv5_mobile_rec)
# resource name -> output name
declare -A V5_OUT=(
  [mobile_zh_det]=det
  [te_PP_OCRv5_mobile_rec]=rec_te
  [ta_PP_OCRv5_mobile_rec]=rec_ta
  [devanagari_PP_OCRv5_mobile_rec]=rec_hi
  [en_PP_OCRv5_mobile_rec]=rec_en
)
declare -A V5_DICT=(
  [te_PP_OCRv5_mobile_rec]=dict_te
  [ta_PP_OCRv5_mobile_rec]=dict_ta
  [devanagari_PP_OCRv5_mobile_rec]=dict_hi
  [en_PP_OCRv5_mobile_rec]=dict_en
)

V3_URL="https://api.nuget.org/v3-flatcontainer/sdcb.paddleocr.models.localv3/2.7.0.1/sdcb.paddleocr.models.localv3.2.7.0.1.nupkg"
V3_DLL="lib/netstandard2.0/Sdcb.PaddleOCR.Models.LocalV3.dll"
V3_PREFIX="Sdcb.PaddleOCR.Models.LocalV3.models."
V3_MODEL=ka_PP_OCRv3_rec

WORK="$(mkdir -p "$WORK" && cd "$WORK" && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

# verify_sha256 FILE EXPECTED
verify_sha256() {
  local actual
  actual="$(sha256sum "$1" | cut -d' ' -f1)"
  if [ "$actual" != "$2" ]; then
    echo "ERROR: SHA-256 mismatch for $1" >&2
    echo "  expected $2" >&2
    echo "  actual   $actual" >&2
    exit 1
  fi
  echo "    sha256 ok: $(basename "$1")"
}

# download_verified URL DEST EXPECTED_SHA256
download_verified() {
  if [ -f "$2" ] && [ "$(sha256sum "$2" | cut -d' ' -f1)" != "$3" ]; then rm -f "$2"; fi
  [ -f "$2" ] || curl -fL --retry 3 -o "$2" "$1"
  verify_sha256 "$2" "$3"
}

# Extracts embedded .NET resources as plain files: extract_resources DLL OUTDIR PREFIX PYTHON
extract_resources() {
  "$4" -I - "$1" "$2" "$3" <<'PYEOF'
import os
import sys

import dnfile

dll_path, res_dir, prefix = sys.argv[1], sys.argv[2], sys.argv[3]
pe = dnfile.dnPE(dll_path)
count = 0
for r in pe.net.resources:
    name = str(r.name)
    if name.startswith(prefix):
        name = name[len(prefix):]
    # Resource names are plain file names; refuse anything that could escape res_dir.
    if os.path.basename(name) != name or name in ("", ".", ".."):
        raise SystemExit(f"unexpected resource name: {name!r}")
    with open(os.path.join(res_dir, name), "wb") as f:
        f.write(r.data)
    count += 1
print(f"    wrote {count} resources")
PYEOF
}

# ---------------------------------------------------------------- PP-OCRv5 (detector + te/ta/hi/en)
V5ENV="$WORK/venv_v5"
echo "==> Creating Python 3.11 venv (paddle 3.x) in $V5ENV"
[ -x "$V5ENV/bin/python" ] || "$PYTHON" -m venv "$V5ENV"
"$V5ENV/bin/pip" install -r "$HERE/requirements-v5.txt"

echo "==> Downloading Sdcb.PaddleOCR.Models.LocalV5 3.3.1"
V5_NUPKG="$WORK/localv5.3.3.1.nupkg"
download_verified "$V5_URL" "$V5_NUPKG" "$V5_NUPKG_SHA"
mkdir -p "$WORK/nupkg_v5" "$WORK/resources_v5"
unzip -o -q "$V5_NUPKG" "$V5_DLL" -d "$WORK/nupkg_v5"
extract_resources "$WORK/nupkg_v5/$V5_DLL" "$WORK/resources_v5" "$V5_PREFIX" "$V5ENV/bin/python"

echo "==> Converting v5 models to ONNX (opset 14)"
for m in "${V5_MODELS[@]}"; do
  src="$WORK/model_src_v5/$m"
  mkdir -p "$src"
  cp "$WORK/resources_v5/$m.inference.json" "$src/inference.json"
  cp "$WORK/resources_v5/$m.inference.pdiparams" "$src/inference.pdiparams"
  "$V5ENV/bin/paddle2onnx" \
    --model_dir "$src" \
    --model_filename inference.json \
    --params_filename inference.pdiparams \
    --save_file "$OUT/${V5_OUT[$m]}.onnx" \
    --opset_version 14
done

echo "==> Writing v5 dictionaries from the model yml (PostProcess.character_dict)"
for m in "${!V5_DICT[@]}"; do
  "$V5ENV/bin/python" -I - "$WORK/resources_v5/$m.inference.yml" "$OUT/${V5_DICT[$m]}.txt" <<'PYEOF'
import sys

import yaml

with open(sys.argv[1], encoding="utf-8") as f:
    chars = yaml.safe_load(f)["PostProcess"]["character_dict"]
with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(chars) + "\n")
print(f"    {sys.argv[2]}: {len(chars)} entries")
PYEOF
done

echo "==> Ensuring detector input and output shapes are dynamic (N,H,W)"
"$V5ENV/bin/python" -I - "$OUT/det.onnx" <<'PYEOF'
import sys

import onnx

path = sys.argv[1]
model = onnx.load(path)
changed = False
for value_info in (model.graph.input[0], model.graph.output[0]):
    dims = value_info.type.tensor_type.shape.dim
    for idx, sym in zip((0, 2, 3), ("N", "H", "W")):
        if dims[idx].HasField("dim_value"):  # static size: make it symbolic
            dims[idx].dim_param = sym
            changed = True
if changed:
    onnx.save(model, path)
    print(f"    made dynamic and saved {path}")
else:
    print("    already dynamic (paddle2onnx 2.x output), left unchanged")
PYEOF

# ---------------------------------------------------------------- PP-OCRv3 Kannada
V3ENV="$WORK/venv_v3"
echo "==> Creating Python 3.11 venv (paddle 2.6) in $V3ENV"
[ -x "$V3ENV/bin/python" ] || "$PYTHON" -m venv "$V3ENV"
"$V3ENV/bin/pip" install -r "$HERE/requirements-v3.txt"

echo "==> Downloading Sdcb.PaddleOCR.Models.LocalV3 2.7.0.1"
V3_NUPKG="$WORK/localv3.2.7.0.1.nupkg"
download_verified "$V3_URL" "$V3_NUPKG" "$V3_NUPKG_SHA"
mkdir -p "$WORK/nupkg_v3" "$WORK/resources_v3"
unzip -o -q "$V3_NUPKG" "$V3_DLL" -d "$WORK/nupkg_v3"
extract_resources "$WORK/nupkg_v3/$V3_DLL" "$WORK/resources_v3" "$V3_PREFIX" "$V3ENV/bin/python"

echo "==> Converting $V3_MODEL to rec_kn.onnx (opset 11)"
src="$WORK/model_src_v3/$V3_MODEL"
mkdir -p "$src"
cp "$WORK/resources_v3/$V3_MODEL.inference.pdmodel" "$src/inference.pdmodel"
cp "$WORK/resources_v3/$V3_MODEL.inference.pdiparams" "$src/inference.pdiparams"
"$V3ENV/bin/paddle2onnx" \
  --model_dir "$src" \
  --model_filename inference.pdmodel \
  --params_filename inference.pdiparams \
  --save_file "$OUT/rec_kn.onnx" \
  --opset_version 11

echo "==> Fetching the Kannada dictionary from the paddleocr 2.9.1 wheel"
WHEEL_DIR="$WORK/paddleocr_wheel"
mkdir -p "$WHEEL_DIR"
[ -f "$WHEEL_DIR/paddleocr-2.9.1-py3-none-any.whl" ] || "$V3ENV/bin/pip" download paddleocr==2.9.1 --no-deps -d "$WHEEL_DIR"
WHEEL="$WHEEL_DIR/paddleocr-2.9.1-py3-none-any.whl"
verify_sha256 "$WHEEL" "$WHEEL_SHA"
DICT_DIR="$WORK/paddleocr_dicts"
mkdir -p "$DICT_DIR"
unzip -o -q "$WHEEL" 'paddleocr/ppocr/utils/dict/ka_dict.txt' -d "$DICT_DIR"
cp "$DICT_DIR/paddleocr/ppocr/utils/dict/ka_dict.txt" "$OUT/dict_kn.txt"

echo "==> SHA-256 of output files"
(
  cd "$OUT"
  sha256sum ./det.onnx ./rec_*.onnx ./dict_*.txt
)

echo "==> Verifying outputs against $HERE/models.sha256"
EXPECTED="$HERE/models.sha256"
bad=0
while read -r want name; do
  [ -z "$want" ] && continue
  if [ ! -f "$OUT/$name" ]; then echo "ERROR: missing output $name" >&2; exit 1; fi
  got="$(sha256sum "$OUT/$name" | cut -d' ' -f1)"
  if [ "$got" = "$want" ]; then echo "    ok       $name"; else echo "    DIFFERS  $name (got $got)"; bad=1; fi
done < "$EXPECTED"
if [ "$bad" -ne 0 ]; then
  echo "WARNING: some outputs differ from models.sha256 (conversion may be platform dependent). Not failing." >&2
fi

echo "Done. Models written to $OUT"
