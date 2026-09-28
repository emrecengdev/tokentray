"""Composes hero-dark.png and hero-light.png: the panel above a taskbar, on a quiet desktop.
Run after `TokenTray.exe --render-media <dir>`:  python tools/make_hero.py <dir>"""
import sys, os
from PIL import Image, ImageDraw, ImageFilter

base = sys.argv[1] if len(sys.argv) > 1 else "docs/media"

def hero(theme):
    dark = theme == "dark"
    panel = Image.open(os.path.join(base, f"panel-{theme}.png")).convert("RGBA")
    strip = Image.open(os.path.join(base, f"widget-{theme}.png")).convert("RGBA")
    pad = 120
    W = panel.width + pad * 2
    H = panel.height + pad + 48 + strip.height
    canvas = Image.new("RGBA", (W, H), (17, 19, 23, 255) if dark else (236, 234, 230, 255))

    # Soft light behind the panel, the way a wallpaper glows through acrylic.
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(glow)
    d.ellipse([W * 0.35, H * 0.05, W * 1.1, H * 0.95], fill=(70, 86, 120, 110) if dark else (150, 165, 230, 90))
    d.ellipse([-W * 0.2, H * 0.3, W * 0.5, H * 1.1], fill=(120, 80, 60, 70) if dark else (230, 160, 130, 80))
    canvas = Image.alpha_composite(canvas, glow.filter(ImageFilter.GaussianBlur(160)))

    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    px, py = W - pad - panel.width + 40, pad
    ImageDraw.Draw(shadow).rounded_rectangle([px, py + 16, px + panel.width, py + panel.height + 16], 24, fill=(0, 0, 0, 150 if dark else 60))
    canvas = Image.alpha_composite(canvas, shadow.filter(ImageFilter.GaussianBlur(40)))
    canvas.alpha_composite(panel, (px, py))

    bar = Image.new("RGBA", (W, strip.height), strip.getpixel((2, strip.height // 2)))
    ImageDraw.Draw(bar).line([(0, 0), (W, 0)], fill=strip.getpixel((2, 0)), width=2)
    bar.alpha_composite(strip, (W - strip.width, 0))
    canvas.alpha_composite(bar, (0, H - strip.height))
    out = os.path.join(base, f"hero-{theme}.png")
    canvas.convert("RGB").save(out, optimize=True)
    print(out, canvas.size)

for t in ("dark", "light"):
    hero(t)
