# Erzeugt src/Krach/Assets/krach.ico (wird vom Build-Workflow aufgerufen)
import os
from PIL import Image, ImageDraw
os.makedirs("src/Krach/Assets", exist_ok=True)
S = 512
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
grad = Image.new("RGBA", (S, S))
px = grad.load()
for y in range(S):
    for x in range(S):
        t = (x + y) / (2 * S)
        px[x, y] = (int(124 + 131 * t * 0.6), int(92 - 40 * t), int(255 - 60 * t), 255)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([16, 16, S - 16, S - 16], radius=110, fill=255)
img.paste(grad, (0, 0), mask)
d = ImageDraw.Draw(img)
bars = [0.30, 0.55, 0.85, 0.60, 0.95, 0.50, 0.32]
w, gap = 34, 20
x0 = (S - (len(bars) * w + (len(bars) - 1) * gap)) // 2
for i, h in enumerate(bars):
    hh = int(300 * h); x = x0 + i * (w + gap); y = (S - hh) // 2
    d.rounded_rectangle([x, y, x + w, y + hh], radius=w // 2, fill=(255, 255, 255, 255))
img.save("src/Krach/Assets/krach.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("Symbol erzeugt")
