from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw
from playwright.sync_api import sync_playwright


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "WenCun.Desktop" / "Assets"
OUTPUT = ROOT / ".tools" / "logo" / "soft-3d"
OUTPUT.mkdir(parents=True, exist_ok=True)


def render_icon() -> Image.Image:
    svg = (ASSETS / "wencun-icon.svg").read_text(encoding="utf-8")
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(channel="msedge", headless=True)
        page = browser.new_page(viewport={"width": 1024, "height": 1024}, device_scale_factor=1)
        page.set_content(f"<style>html,body{{margin:0;background:transparent}}svg{{display:block;width:1024px;height:1024px}}</style>{svg}")
        png = page.locator("svg").screenshot()
        browser.close()
    image = Image.open(BytesIO(png)).convert("RGBA")
    background = Image.new("RGBA", image.size, "#173B32")
    return Image.alpha_composite(background, image).convert("RGB")


def save_assets(master: Image.Image) -> None:
    master.save(OUTPUT / "wencun-1024.png")
    master.resize((512, 512), Image.Resampling.LANCZOS).save(ASSETS / "wencun-icon.png")
    icon_path = ASSETS / "wencun.ico"
    master.save(icon_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    with Image.open(icon_path) as icon:
        assert sorted(icon.ico.sizes()) == [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    write_bmp_ico(master, icon_path)

    sheet = Image.new("RGB", (1120, 720), "#F1ECE2")
    draw = ImageDraw.Draw(sheet)
    draw.rounded_rectangle((32, 32, 688, 688), radius=36, fill="#FBF8F1")
    sheet.paste(master.resize((600, 600), Image.Resampling.LANCZOS), (60, 60))
    sizes = ((256, 724, 64), (128, 772, 392), (64, 724, 568), (48, 820, 568), (32, 892, 568), (16, 956, 568))
    for size, x, y in sizes:
        icon = master.resize((size, size), Image.Resampling.LANCZOS)
        sheet.paste(icon, (x, y))
        draw.text((x, y + size + 12), f"{size} px", fill="#202723")
    sheet.save(OUTPUT / "size-review.png")

    review = Image.new("RGB", (960, 760), "#FBF8F1")
    review.paste(master.resize((512, 512), Image.Resampling.LANCZOS), (28, 36))
    for size, x, y in ((180, 624, 54), (60, 610, 324), (32, 746, 324)):
        review.paste(master.resize((size, size), Image.Resampling.LANCZOS), (x, y))
        draw = ImageDraw.Draw(review)
        draw.text((x, y + size + 12), f"{size} px", fill="#202723")
    for label, x, color in (("Light", 604, "#E6E6E6"), ("Dark", 780, "#202522")):
        tile = Image.new("RGB", (148, 182), color)
        tile.paste(master.resize((128, 128), Image.Resampling.LANCZOS), (10, 12))
        ImageDraw.Draw(tile).text((10, 152), label, fill="#202723" if label == "Light" else "#F5EFE2")
        review.paste(tile, (x, 484))
    grayscale = master.convert("L").convert("RGB").resize((180, 180), Image.Resampling.LANCZOS)
    review.paste(grayscale, (38, 564))
    review.save(OUTPUT / "quality-review.png")


def write_bmp_ico(master: Image.Image, path: Path) -> None:
    import struct

    frames = []
    for size in (16, 24, 32, 48, 64, 128, 256):
        frame = master.resize((size, size), Image.Resampling.LANCZOS).convert("RGBA")
        pixels = frame.tobytes()
        bgra = bytearray(len(pixels))
        bgra[0::4] = pixels[2::4]
        bgra[1::4] = pixels[1::4]
        bgra[2::4] = pixels[0::4]
        bgra[3::4] = pixels[3::4]
        header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(bgra) + size * size, 0, 0, 0, 0)
        mask = bytes(((size + 31) // 32 * 4) * size)
        data = header + bgra + mask
        frames.append((size, data))
    directory = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + len(frames) * 16
    for size, data in frames:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(directory + b"".join(data for _, data in frames))


if __name__ == "__main__":
    save_assets(render_icon())
