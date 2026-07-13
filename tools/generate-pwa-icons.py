"""Generate branded BotNexus PWA icons.

Draws a robot mark (BotNexus / Farnsworth) on a dark-navy rounded background.
Produces an `any` variant (full-bleed) and a `maskable` variant (safe-zone padded).
Regenerate with:  python -X utf8 tools/generate-pwa-icons.py
"""
import os
from PIL import Image, ImageDraw

# Brand palette
NAVY = (22, 33, 62, 255)        # #16213e background
NAVY_DK = (13, 20, 40, 255)     # deeper shade for depth
INDIGO = (79, 70, 229, 255)     # #4f46e5 theme accent
LIGHT = (230, 236, 255, 255)    # #e6ecff robot body
EYE = (110, 231, 255, 255)      # cyan eyes/glow


def rounded(draw, box, r, fill):
    draw.rounded_rectangle(box, radius=r, fill=fill)


def draw_icon(size, safe_margin=0.0):
    """Render the robot mark at `size` px. safe_margin is fraction padding
    reserved around the glyph so maskable platforms don't clip it."""
    S = 4  # supersample factor for smooth edges
    px = size * S
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Background rounded square (full bleed).
    rounded(d, (0, 0, px - 1, px - 1), int(px * 0.18), NAVY)

    # Content region shrinks for the maskable safe zone.
    m = px * safe_margin
    cx0, cy0, cx1, cy1 = m, m, px - m, px - m
    cw = cx1 - cx0
    ch = cy1 - cy0

    def X(fx):
        return cx0 + fx * cw

    def Y(fy):
        return cy0 + fy * ch

    lw = max(2, int(cw * 0.02))

    # Antenna
    d.line([(X(0.5), Y(0.10)), (X(0.5), Y(0.22))], fill=INDIGO, width=lw)
    ar = cw * 0.045
    d.ellipse([X(0.5) - ar, Y(0.08) - ar, X(0.5) + ar, Y(0.08) + ar], fill=EYE)

    # Head
    head = (X(0.24), Y(0.22), X(0.76), Y(0.62))
    rounded(d, head, int(cw * 0.09), LIGHT)

    # Eyes
    er = cw * 0.075
    ey = Y(0.42)
    for ex in (X(0.39), X(0.61)):
        d.ellipse([ex - er, ey - er, ex + er, ey + er], fill=NAVY_DK)
        gr = er * 0.55
        d.ellipse([ex - gr, ey - gr, ex + gr, ey + gr], fill=EYE)

    # Mouth grille
    my = Y(0.54)
    mw = cw * 0.20
    d.rounded_rectangle([X(0.5) - mw / 2, my - cw * 0.02, X(0.5) + mw / 2, my + cw * 0.02],
                        radius=int(cw * 0.02), fill=INDIGO)

    # Body / shoulders
    body = (X(0.30), Y(0.66), X(0.70), Y(0.88))
    rounded(d, body, int(cw * 0.06), INDIGO)
    # Chest node
    nr = cw * 0.05
    d.ellipse([X(0.5) - nr, Y(0.77) - nr, X(0.5) + nr, Y(0.77) + nr], fill=EYE)

    # Ear pods
    pr = cw * 0.05
    for pxo in (X(0.24), X(0.76)):
        d.ellipse([pxo - pr, Y(0.42) - pr, pxo + pr, Y(0.42) + pr], fill=INDIGO)

    img = img.resize((size, size), Image.LANCZOS)
    return img


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    targets = [
        os.path.join(root, "src", "extensions",
                     "BotNexus.Extensions.Channels.SignalR.BlazorClient", "wwwroot"),
        os.path.join(root, "src", "extensions",
                     "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile", "wwwroot"),
    ]
    for wwwroot in targets:
        os.makedirs(wwwroot, exist_ok=True)
        draw_icon(192).save(os.path.join(wwwroot, "icon-192.png"), optimize=True)
        draw_icon(512).save(os.path.join(wwwroot, "icon-512.png"), optimize=True)
        draw_icon(192, safe_margin=0.10).save(
            os.path.join(wwwroot, "icon-192-maskable.png"), optimize=True)
        draw_icon(512, safe_margin=0.10).save(
            os.path.join(wwwroot, "icon-512-maskable.png"), optimize=True)
        print("Wrote icons to", wwwroot)


if __name__ == "__main__":
    main()
