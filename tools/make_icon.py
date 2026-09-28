"""Draws the TokenTray app icon: an allowance ring, three-quarters full, with a time notch."""
from PIL import Image, ImageDraw
import math

def draw(size):
    s = size * 4  # supersample
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = s * 0.22
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=(28, 27, 26, 255))
    pad = s * 0.2
    w = max(4, int(s * 0.11))
    box = [pad, pad, s - pad, s - pad]
    d.arc(box, 0, 360, fill=(255, 255, 255, 40), width=w)
    d.arc(box, -90, -90 + 360 * 0.72, fill=(232, 140, 102, 255), width=w)
    # time notch at 80%
    a = math.radians(-90 + 360 * 0.80)
    cx = cy = s / 2
    rin, rout = (s - 2 * pad) / 2 - w * 1.2, (s - 2 * pad) / 2 + w * 0.2
    d.line([cx + rin * math.cos(a), cy + rin * math.sin(a), cx + rout * math.cos(a), cy + rout * math.sin(a)],
           fill=(255, 255, 255, 235), width=max(3, int(s * 0.035)))
    return img.resize((size, size), Image.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
imgs = [draw(n) for n in sizes]
imgs[-1].save("src/TokenTray/Assets/TokenTray.ico", sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
imgs[-1].save("docs/icon.png") if __import__("os").path.isdir("docs") else None
print("ok")
