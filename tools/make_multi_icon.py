"""Generate the multi-tray application icon (PNG + multi-size ICO).

Run:  python tools/make_multi_icon.py

Why this exists as a committed script rather than a one-off: the icon must stay
distinct from the Logitech edition, and that distinction is easy to lose in a later
edit. Keeping the generator in the repo means the artwork is reproducible and the
design intent is recorded.

Design:
  - same dark circular badge as logi-tray, so the two icons read as one product family
  - three bars of different heights instead of the Logitech "G" letterform, meaning
    "several devices, each with its own level" -- exactly what this edition does
  - the app's own tech blue (#0078D4) instead of Logitech green

The bars are bottom-aligned, so BASE_Y is solved so the bounding box centres
vertically; placing the base naively leaves the artwork visibly low.
"""
import os
import struct

from PIL import Image, ImageDraw

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_PNG = os.path.join(REPO, "multi", "app.png")
OUT_ICO = os.path.join(REPO, "multi", "app.ico")
PREVIEW = os.path.join(REPO, "_shots", "icon_sizes.png")

SS = 4                       # 超采样倍数，用于平滑边缘
BADGE = (39, 43, 54)         # #272B36
BLUE = (0, 120, 212)         # #0078D4
BLUE_LIGHT = (86, 180, 245)

BARS = [0.36, 0.60, 0.86]    # 三根条的高度（相对可用高度），由矮到高
BAR_W = 0.105
BAR_GAP = 0.062
BAR_RADIUS = 0.052

# 条是底部对齐生长的，因此底边不能取在中线：直接放中线偏下会让包围盒整体偏低
# （实测底边 0.745 时偏低 15px，肉眼可见）。按「包围盒垂直居中」反解底边位置。
SPAN = 0.46
BASE_Y = 0.5 + (max(BARS) / 2) * SPAN
TOP_Y = BASE_Y - SPAN

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(size):
    """渲染 size x size 的图标。"""
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    pad = n * 0.035
    d.ellipse([pad, pad, n - pad, n - pad], fill=BADGE)

    total_w = len(BARS) * BAR_W + (len(BARS) - 1) * BAR_GAP
    x0 = (1.0 - total_w) / 2.0

    for i, frac in enumerate(BARS):
        bx = x0 + i * (BAR_W + BAR_GAP)
        left = bx * n
        right = (bx + BAR_W) * n
        top = (BASE_Y - SPAN * frac) * n
        bottom = BASE_Y * n
        # 最矮那根用亮一点的蓝，让层次更清楚
        color = BLUE_LIGHT if i == 0 else BLUE
        d.rounded_rectangle([left, top, right, bottom],
                            radius=BAR_RADIUS * n, fill=color)

    return img.resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    """手写 ICO 容器：6 字节头 + 每尺寸 16 字节目录项 + PNG 负载。

    不用 PIL 的 save(format='ICO')：实测传了 9 个尺寸却只写出 16x16 一帧，
    Windows 在任务栏与 Alt-Tab 只能放大糊图。手写才能保证每个尺寸都在。
    """
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)

    entries = b""
    payloads = b""
    offset = 6 + 16 * count

    for img in images:
        import io
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        blob = buf.getvalue()

        w, h = img.size
        entries += struct.pack(
            "<BBBBHHII",
            w if w < 256 else 0,   # 256 在 ICO 目录里以 0 表示
            h if h < 256 else 0,
            0, 0, 1, 32,
            len(blob), offset)
        payloads += blob
        offset += len(blob)

    with open(path, "wb") as f:
        f.write(header + entries + payloads)


def main():
    master = render(512)
    master.save(OUT_PNG)
    print(f"wrote {OUT_PNG}  {master.size}")

    frames = [render(s) for s in SIZES]
    write_ico(OUT_ICO, frames)
    print(f"wrote {OUT_ICO}  {os.path.getsize(OUT_ICO):,} bytes  sizes={SIZES}")

    # 放大预览：托盘里 16/20px 是否可辨，只能放大后肉眼核对
    tiles = [render(s).resize((s * 12, s * 12), Image.NEAREST)
             for s in (16, 20, 24, 32)]
    pad = 14
    W = sum(t.width for t in tiles) + pad * (len(tiles) + 1)
    H = max(t.height for t in tiles) + pad * 2
    sheet = Image.new("RGBA", (W, H), (245, 246, 248, 255))
    x = pad
    for t in tiles:
        sheet.paste(t, (x, pad), t)
        x += t.width + pad

    os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
    sheet.save(PREVIEW)
    print(f"wrote {PREVIEW}  {sheet.size}")


if __name__ == "__main__":
    main()
