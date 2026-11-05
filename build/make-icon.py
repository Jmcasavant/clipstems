"""Draws assets/Clipstems.ico: the app's blue tile with three stacked bars.

The same design as Clipstems.App/Tray/TrayIcons.cs (which draws the tray's
state variants at runtime), on the same 32-unit grid, without a state badge.
The .ico is committed; rerun this only when the design changes:

    python build/make-icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SUPERSAMPLE = 8
TILE = (0x1F, 0x6F, 0xEB, 255)
BAR = (255, 255, 255, 255)


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    s = big / 32.0
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def rounded(x, y, w, h, r, fill):
        d.rounded_rectangle([x * s, y * s, (x + w) * s, (y + h) * s], radius=r * s, fill=fill)

    rounded(1, 1, 30, 30, 7, TILE)
    rounded(6, 7, 20, 4, 2, BAR)
    rounded(6, 14, 14, 4, 2, BAR)
    rounded(6, 21, 9, 4, 2, BAR)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    out = Path(__file__).resolve().parent.parent / "assets" / "Clipstems.ico"
    out.parent.mkdir(exist_ok=True)
    images = [render(n) for n in SIZES]
    images[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
