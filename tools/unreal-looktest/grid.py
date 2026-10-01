"""Host: 2x2 labelled grid of four PNGs (A gameplay, B gameplay, A close, B close) for previews."""
import sys
from PIL import Image, ImageDraw

out, *ps = sys.argv[1:]
labels = ['A Manor-Lords-style - gameplay cam', 'B Northgard-style - gameplay cam', 'A - close cam', 'B - close cam']
ims = [Image.open(p).convert('RGB') for p in ps]
w, h = ims[0].size
o = Image.new('RGB', (w * 2, h * 2))
for i, im in enumerate(ims):
    x, y = (i % 2) * w, (i // 2) * h
    o.paste(im.resize((w, h)), (x, y))
    d = ImageDraw.Draw(o)
    d.rectangle([x, y, x + 330, y + 22], fill='black')
    d.text((x + 6, y + 5), labels[i], fill='white')
o.save(out)
print(out, o.size)
