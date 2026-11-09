import importlib.util
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image


sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("render_icon", Path(__file__).with_name("render-icon.py"))
renderer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(renderer)
SIZES = (16, 24, 32, 48, 64, 128, 256)


class IconTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(dir=renderer.ROOT / ".tools", prefix="icon-test-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.output = Path(cls.temporary.name)
        cls.master = renderer.render_icon()
        with patch.multiple(renderer, ASSETS=cls.output, OUTPUT=cls.output):
            renderer.save_assets(cls.master)
        cls.fixture = Image.new("RGBA", (256, 256))
        cls.fixture.putdata([
            (x, y, (x + 2 * y) % 256, 0 if x < 64 and y < 96 else 128 if x > 192 else 255)
            for y in range(256) for x in range(256)
        ])
        cls.fixture_path = cls.output / "asymmetric.ico"
        renderer.write_bmp_ico(cls.fixture, cls.fixture_path)

    def assert_transparent_corners(self, image, corner_tolerance=0):
        self.assertEqual(image.mode, "RGBA")
        alpha = image.getchannel("A")
        self.assertEqual(alpha.getextrema(), (0, 255))
        for point in ((0, 0), (image.width - 1, 0), (0, image.height - 1), (image.width - 1, image.height - 1)):
            self.assertLessEqual(alpha.getpixel(point), corner_tolerance)
        self.assertTrue(any(alpha.histogram()[1:255]), "Antialiased edge alpha was lost")

    def test_browser_and_png_preserve_transparency(self):
        with self.subTest(artifact="browser"):
            self.assert_transparent_corners(self.master)
        with Image.open(self.output / "wencun-icon.png") as png:
            self.assertEqual(png.size, (512, 512))
            with self.subTest(artifact="PNG"):
                self.assert_transparent_corners(png)

    def test_asymmetric_rgba_round_trip(self):
        with Image.open(self.fixture_path) as icon:
            self.assertEqual(icon.ico.sizes(), {(size, size) for size in SIZES})
            for size in SIZES:
                with self.subTest(size=size):
                    expected = self.fixture.resize((size, size), Image.Resampling.LANCZOS)
                    actual = icon.ico.getimage((size, size)).convert("RGBA")
                    self.assertTrue(actual.tobytes() == expected.tobytes(), "ICO changed orientation, colors or alpha")

    def test_bmp_headers_and_padded_transparency_masks(self):
        data = self.fixture_path.read_bytes()
        self.assertEqual(struct.unpack_from("<HHH", data), (0, 1, len(SIZES)))
        offset = 6 + 16 * len(SIZES)
        for index, size in enumerate(SIZES):
            width, height, colors, reserved, planes, depth, length, position = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
            stride = ((size + 31) // 32) * 4
            with self.subTest(size=size, part="directory"):
                self.assertEqual((width, height, colors, reserved, planes, depth), (size % 256, size % 256, 0, 0, 1, 32))
                self.assertEqual(position, offset)
                self.assertEqual(length, 40 + size * size * 4 + stride * size)
            header = struct.unpack_from("<IiiHHIIiiII", data, position)
            with self.subTest(size=size, part="DIB header"):
                self.assertEqual(header, (40, size, 2 * size, 1, 32, 0, length - 40, 0, 0, 0, 0))
            mask = data[position + 40 + size * size * 4:position + length]
            decoded = Image.frombytes("1", (size, size), mask, "raw", "1", stride, -1).convert("L")
            expected = self.fixture.resize((size, size), Image.Resampling.LANCZOS).getchannel("A").point(lambda alpha: 255 if alpha == 0 else 0)
            with self.subTest(size=size, part="AND mask"):
                self.assertTrue(decoded.tobytes() == expected.tobytes(), "Mask must mark zero alpha in the same orientation")
                for row in range(size):
                    self.assertEqual(mask[row * stride + size // 8:(row + 1) * stride], bytes(stride - size // 8))
            offset += length
        self.assertEqual(offset, len(data))

    def test_saved_icon_frames_match_render(self):
        with Image.open(self.output / "wencun.ico") as icon:
            self.assertEqual(icon.ico.sizes(), {(size, size) for size in SIZES})
            for size in SIZES:
                actual = icon.ico.getimage((size, size)).convert("RGBA")
                expected = self.master.resize((size, size), Image.Resampling.LANCZOS).convert("RGBA")
                with self.subTest(size=size, part="orientation"):
                    self.assertTrue(actual.tobytes() == expected.tobytes(), "Saved ICO is not the rendered image")
                with self.subTest(size=size, part="transparency"):
                    # Tiny frames retain Lanczos edge ringing; the exact RGBA check above guards it.
                    self.assert_transparent_corners(actual, corner_tolerance=3)


if __name__ == "__main__":
    unittest.main()
