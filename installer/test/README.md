# Installer end-to-end fixture

`telugu-sample.png` is three lines of Telugu rendered with the bundled Noto Sans Telugu (SIL OFL) at 44 px.
`telugu-sample.txt` is the exact text that was rendered (ground truth).
`expected-words.txt` lists words the CI check requires in the OCR output (one per line, UTF-8).
The check uses whole words, not the exact text, because a single wrong character is normal OCR noise
(on Linux the engine reads `మా` as `మూ` in line 3); the check is meant to prove the installed app reads Telugu offline, not to measure accuracy.
Accuracy is measured in `bench/`.

Regenerate with Pillow (needs libraqm for correct Telugu shaping):

```python
from PIL import Image, ImageDraw, ImageFont
f = ImageFont.truetype('app/IndicOcr.App/fonts/NotoSansTelugu-Regular.ttf', 44)
lines = open('installer/test/telugu-sample.txt', encoding='utf-8').read().splitlines()
im = Image.new('L', (900, 70 * len(lines) + 40), 255); d = ImageDraw.Draw(im)
for i, l in enumerate(lines): d.text((30, 25 + 70 * i), l, font=f, fill=0)
im.save('installer/test/telugu-sample.png', optimize=True)
```
