"""Adds the "Caravans" title to the rendered thumbnails.
python compose.py [renders dir] [out dir] [font.ttf]
With no arguments, composes renders/ with every font in fonts/ into output/<font>/.
For each shot writes Caravans_<shot>.png (title in front) and Caravans_<shot>_layered.png (hero wagon in front)."""
import glob
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

# shot: (title centre as a fraction of the image, font height as a fraction of the image height, tilt in degrees)
LAYOUT = {
    "hero_folktails": ((0.36, 0.25), 0.25, -4),
    "hero_ironteeth": ((0.36, 0.25), 0.25, -4),
    "convoy": ((0.5, 0.2), 0.24, -3),
    "departure": ((0.38, 0.82), 0.24, -4),
}
TOP, BOTTOM = (255, 236, 168), (242, 160, 52)
OUTLINE = (58, 33, 15)


def title_layer(size, centre, height, tilt, font_path, text="Caravans"):
    w, h = size
    font = ImageFont.truetype(font_path, int(h * height))
    stroke = max(4, int(h * height * 0.075))
    box = font.getbbox(text, stroke_width=stroke)
    tw, th = box[2] - box[0] + stroke * 4, box[3] - box[1] + stroke * 4
    origin = (stroke * 2 - box[0], stroke * 2 - box[1])

    text_mask = Image.new("L", (tw, th))
    ImageDraw.Draw(text_mask).text(origin, text, font=font, fill=255)
    outline_mask = Image.new("L", (tw, th))
    ImageDraw.Draw(outline_mask).text(origin, text, font=font, fill=255, stroke_width=stroke, stroke_fill=255)

    gradient = Image.new("RGB", (1, th))
    for y in range(th):
        t = min(max((y - stroke * 2) / max(th - stroke * 4, 1), 0), 1)
        gradient.putpixel((0, y), tuple(int(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)))
    gradient = gradient.resize((tw, th))
    # A darker lower band inside the letters reads like a carved, lit-from-above edge
    inner = text_mask.filter(ImageFilter.GaussianBlur(stroke * 0.4))
    shade = ImageChops.subtract(text_mask, inner.transform(inner.size, Image.AFFINE, (1, 0, 0, 0, 1, -stroke * 0.5)))
    fill = Image.composite(Image.new("RGB", (tw, th), (196, 112, 30)), gradient, shade.point(lambda v: v * 0.8))

    layer = Image.new("RGBA", (tw, th), OUTLINE + (0,))
    layer.putalpha(outline_mask)
    letters = fill.convert("RGBA")
    letters.putalpha(text_mask)
    layer.alpha_composite(letters)
    layer = layer.rotate(-tilt, resample=Image.BICUBIC, expand=True)

    shadow = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    shadow.putalpha(layer.getchannel("A").point(lambda v: v * 0.55))
    shadow = shadow.filter(ImageFilter.GaussianBlur(stroke * 1.2))

    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    x, y = int(centre[0] * w - layer.width / 2), int(centre[1] * h - layer.height / 2)
    canvas.alpha_composite(shadow, (x + stroke // 2, y + stroke))
    canvas.alpha_composite(layer, (x, y))
    return canvas


def main(src, out, font_path):
    os.makedirs(out, exist_ok=True)
    for shot, (centre, height, tilt) in LAYOUT.items():
        path = os.path.join(src, f"{shot}.png")
        if not os.path.exists(path):
            continue
        base = Image.open(path).convert("RGBA")
        title = title_layer(base.size, centre, height, tilt, font_path)
        front = base.copy()
        front.alpha_composite(title)
        front.convert("RGB").save(os.path.join(out, f"Caravans_{shot}.png"))
        mask_path = os.path.join(src, f"{shot}_mask.png")
        if os.path.exists(mask_path):
            wagon = base.copy()
            wagon.putalpha(Image.open(mask_path).getchannel("A"))
            layered = front.copy()
            layered.alpha_composite(wagon)
            layered.convert("RGB").save(os.path.join(out, f"Caravans_{shot}_layered.png"))
        print("composed", shot)


if __name__ == "__main__":
    if len(sys.argv) > 1:
        main(*sys.argv[1:4])
    else:
        here = os.path.dirname(os.path.abspath(__file__))
        for font in glob.glob(os.path.join(here, "fonts", "*.ttf")):
            name = os.path.basename(font).split("-")[0]
            main(os.path.join(here, "renders"), os.path.join(here, "output", name), font)
