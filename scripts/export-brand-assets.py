"""Export Windows icon sizes from the approved image-generation master.

Run with Python and Pillow after replacing the master artwork. This only fits,
resamples and converts the artwork; the application's build does not need Python.
"""
from pathlib import Path
from PIL import Image, ImageOps

root = Path(__file__).resolve().parent.parent
assets = root / "src/Toiqtty/Assets"
master = Image.open(assets / "Brand/toiqtty-icon-v1.png").convert("RGBA")
wordmark = Image.open(assets / "Brand/toiqtty-logo-v1.png").convert("RGBA")
assert master.width == master.height, "The approved icon master must be square."
sizes = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
master.save(assets / "AppIcon.ico", format="ICO", sizes=sizes)

for filename in assets.glob("*.png"):
    with Image.open(filename) as previous:
        size = previous.size
    source = wordmark if size[0] > size[1] * 1.5 else master
    fitted = ImageOps.contain(source, size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", size, (24, 24, 37, 255))
    canvas.alpha_composite(fitted, ((size[0] - fitted.width) // 2, (size[1] - fitted.height) // 2))
    canvas.save(filename)

with Image.open(assets / "AppIcon.ico") as icon:
    assert icon.ico.sizes() == set(sizes), "A Windows icon resolution is missing."
print(f"Exported {len(sizes)} ICO resolutions and Windows PNG assets.")
