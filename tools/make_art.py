"""Generates JellyScale branding from the logo in art/source/JellyScale-logo.ico:
  art/JellyScale.ico       app/installer icon (canonical multi-size .ico)
  art/JellyScale-128.png   logo shown inside the app window
  art/wizard.bmp           installer sidebar (welcome and finish pages)
Run: python3 tools/make_art.py [preview.png]   (requires Pillow)

To change the logo, replace art/source/JellyScale-logo.ico and run this again, then rebuild."""
import io, math, os, struct, sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.join(HERE, '..', 'art')
SOURCE = os.path.join(ART, 'source', 'JellyScale-logo.ico')
NAME = 'JellyScale'

# Colours taken from the logo (magenta -> pink -> orange -> yellow).
MAGENTA = (236, 20, 200)
PINK = (250, 56, 160)
ORANGE = (254, 146, 10)

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def gradient(size, c1, c2, angle_deg=45):
    w, h = size
    g = Image.new('RGB', size)
    px = g.load()
    a = math.radians(angle_deg)
    dx, dy = math.cos(a), math.sin(a)
    proj = [x * dx + y * dy for x, y in ((0, 0), (w, 0), (0, h), (w, h))]
    lo, hi = min(proj), max(proj)
    for y in range(h):
        for x in range(w):
            t = ((x * dx + y * dy) - lo) / (hi - lo)
            px[x, y] = lerp(c1, c2, t)
    return g


def load_logo():
    """Every frame of the source .ico, keyed by pixel size."""
    ico = Image.open(SOURCE)
    frames = {}
    for size in sorted(ico.info['sizes']):
        ico.size = size
        ico.load()
        frames[size[0]] = ico.copy().convert('RGBA')
    if 256 not in frames:
        sys.exit('The source logo needs a 256x256 image.')
    return frames


def icon_images(frames):
    """The logo's own frames where it has them; missing sizes (e.g. 20, 40, 96 for 125%/150% scaling)
    are downscaled from the 256px master."""
    return [frames[s] if s in frames else frames[256].resize((s, s), Image.LANCZOS) for s in ICON_SIZES]


def write_ico(path, images):
    """Canonical Windows .ico: 32-bit BMP entries for small sizes, PNG for 256px.
    (BMP entries keep older Windows/.NET icon APIs happy; the pixels are unchanged.)"""
    blobs = []
    for im in images:
        w, h = im.size
        if w >= 256:
            buf = io.BytesIO()
            im.save(buf, format='PNG')
            data = buf.getvalue()
        else:
            px = im.convert('RGBA').tobytes('raw', 'BGRA')
            row = w * 4
            xor = b''.join(px[(h - 1 - y) * row:(h - y) * row] for y in range(h))
            mask_row = ((w + 31) // 32) * 4
            and_mask = b'\x00' * (mask_row * h)
            hdr = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
            data = hdr + xor + and_mask
        blobs.append((w, h, data))
    out = struct.pack('<HHH', 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = b''
    for w, h, data in blobs:
        entries += struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, 'wb') as f:
        f.write(out + entries + b''.join(d for _, _, d in blobs))


def build_icon(frames):
    imgs = icon_images(frames)
    write_ico(os.path.join(ART, NAME + '.ico'), imgs)
    imgs[ICON_SIZES.index(128)].save(os.path.join(ART, NAME + '-128.png'))
    return imgs


def font(size, bold=False):
    cands = (['/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'] if bold else []) + [
        '/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf' if bold else '/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf',
        '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf']
    for c in cands:
        if os.path.exists(c):
            return ImageFont.truetype(c, size)
    return ImageFont.load_default()


def fit_font(draw, text, size, max_width, bold=True):
    """Largest font up to `size` that keeps `text` within max_width."""
    while size > 20:
        f = font(size, bold)
        if draw.textlength(text, font=f) <= max_width:
            return f
        size -= 1
    return font(size, bold)


def build_wizard(frames):
    W, H = 328, 628
    bg = gradient((W, H), (17, 22, 42), (40, 24, 66), 80)
    # Soft glows in the logo's colours.
    glow = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-120, 380, 260, 760], fill=MAGENTA + (64,))
    gd.ellipse([150, -80, 460, 230], fill=ORANGE + (40,))
    glow = glow.filter(ImageFilter.GaussianBlur(60))
    bg = Image.alpha_composite(bg.convert('RGBA'), glow)
    logo = frames[256].resize((104, 104), Image.LANCZOS)
    bg.alpha_composite(logo, (26, 40))
    d = ImageDraw.Draw(bg)
    d.text((32, 168), NAME, font=fit_font(d, NAME, 40, W - 32 - 24), fill=(255, 255, 255))
    d.text((33, 228), 'Your Jellyfin server.', font=font(19), fill=(214, 208, 240))
    d.text((33, 254), 'On the big screen.', font=font(19), fill=(214, 208, 240))
    steps = ['Install', 'Connect your server', 'Turn ON', 'Enjoy']
    y = 352
    for i, s in enumerate(steps, 1):
        d.rounded_rectangle([32, y - 2, 60, y + 24], radius=8, fill=PINK + (255,) if i == 1 else (62, 46, 96, 255))
        d.text((41 if i != 1 else 42, y), str(i), font=font(17, True), fill=(255, 255, 255))
        d.text((74, y), s, font=font(18), fill=(232, 226, 248))
        y += 46
    d.text((32, 584), 'by Demolisher9', font=font(14), fill=(160, 152, 196))
    bg.convert('RGB').save(os.path.join(ART, 'wizard.bmp'))
    return bg


if __name__ == '__main__':
    frames = load_logo()
    imgs = build_icon(frames)
    wizard = build_wizard(frames)
    if len(sys.argv) < 2:
        print('ok')
        sys.exit(0)
    # optional preview sheet: python3 tools/make_art.py preview.png
    strip_w = sum(i.size[0] for i in imgs) + 12 * len(imgs)
    sheet = Image.new('RGBA', (strip_w + wizard.size[0] + 12, max(270, wizard.size[1])), (30, 30, 40, 255))
    x = 6
    for im in imgs:
        sheet.alpha_composite(im, (x, 6))
        x += im.size[0] + 12
    sheet.alpha_composite(wizard, (strip_w + 6, 0))
    sheet.save(sys.argv[1])
    print('ok')
