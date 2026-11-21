"""Draws the VMCreate and VMCreate Manager app icons.

Writes VMCreate/Assets/vmcreate.ico and VMCreate.Manager/Assets/vmcreate-manager.ico (exe and taskbar
icon, 16-256 px) plus a 64 px PNG of each for the title bar. Run from the repository root:

    python Assets/make_icons.py [preview-dir]

With a preview directory it also writes a sheet of every size on a dark and a light background.
Needs Pillow.
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024  # master size; every icon size is a LANCZOS downscale of this
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def tile(top, bottom):
    """Rounded square with a vertical gradient, like the Windows 11 app tiles."""
    grad = Image.new("RGBA", (S, S))
    d = ImageDraw.Draw(grad)
    for y in range(S):
        d.line([(0, y), (S, y)], fill=lerp(top, bottom, y / (S - 1)) + (255,))
    mask = Image.new("L", (S, S), 0)
    m = 40
    ImageDraw.Draw(mask).rounded_rectangle([m, m, S - m, S - m], radius=210, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


WHITE = (255, 255, 255, 255)


def vmcreate():
    img = tile((70, 140, 245), (30, 84, 200))
    d = ImageDraw.Draw(img)
    # Monitor: thick outline, stand and foot.
    d.rounded_rectangle([200, 250, 824, 680], radius=56, outline=WHITE, width=72)
    d.rectangle([462, 680, 562, 790], fill=WHITE)
    d.rounded_rectangle([340, 770, 684, 838], radius=34, fill=WHITE)
    # "+" badge, bottom right, cut out of the tile colour with a ring so it reads at 16 px.
    cx, cy, r = 740, 720, 190
    d.ellipse([cx - r - 34, cy - r - 34, cx + r + 34, cy + r + 34], fill=(30, 84, 200, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(46, 204, 113, 255))
    a, w = 112, 40
    d.rounded_rectangle([cx - a, cy - w, cx + a, cy + w], radius=18, fill=WHITE)
    d.rounded_rectangle([cx - w, cy - a, cx + w, cy + a], radius=18, fill=WHITE)
    return img


def manager():
    img = tile((32, 184, 166), (12, 120, 120))
    d = ImageDraw.Draw(img)
    # Three stacked VM rows with a status light each, like a rack or the Manager's list.
    slot = (16, 128, 126, 255)
    for i, y in enumerate((228, 432, 636)):
        d.rounded_rectangle([200, y, 824, y + 160], radius=48, fill=WHITE)
        light = (46, 204, 113, 255) if i < 2 else (150, 160, 165, 255)
        d.ellipse([268, y + 44, 340, y + 116], fill=light)
        d.rounded_rectangle([560, y + 58, 756, y + 102], radius=22, fill=slot)
    return img


def save(img, name, out, preview):
    out.mkdir(parents=True, exist_ok=True)
    frames = [img.resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(out / f"{name}.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    img.resize((64, 64), Image.LANCZOS).save(out / f"{name}.png")
    if preview is None:
        return
    preview.mkdir(parents=True, exist_ok=True)
    sheet = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES) + 10, 2 * 276), (32, 32, 32, 255))
    ImageDraw.Draw(sheet).rectangle([0, 276, sheet.width, sheet.height], fill=(243, 243, 243, 255))
    for row in range(2):
        x = 10
        for f in frames:
            sheet.alpha_composite(f, (x, row * 276 + 10))
            x += f.width + 10
    sheet.save(preview / f"{name}-preview.png")


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    preview = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    save(vmcreate(), "vmcreate", root / "VMCreate" / "Assets", preview)
    save(manager(), "vmcreate-manager", root / "VMCreate.Manager" / "Assets", preview)
