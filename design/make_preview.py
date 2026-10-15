"""Builds design/preview.html from the template + app fragment, embedding the brand fonts (base64) and the
RM Designs SVGs from ../branding (read only). The output is a local preview; it is not meant to be shared."""
import base64
import pathlib
import re

here = pathlib.Path(__file__).parent
brand = here.parent / "branding"


def font_face(family, path, weight, style="normal"):
    data = base64.b64encode(path.read_bytes()).decode()
    fmt = "opentype" if path.suffix.lower() == ".otf" else "truetype"
    return (f'@font-face{{font-family:"{family}";src:url(data:font/{path.suffix[1:]};base64,{data}) format("{fmt}");'
            f'font-weight:{weight};font-style:{style}}}')


faces = []
for name, w in [("Regular", 400), ("Bold", 700), ("Heavy", 800)]:
    p = brand / "Presicav" / f"Presicav-{name}.ttf"
    if p.exists():
        faces.append(font_face("Presicav", p, w))
faces.append(font_face("Presicav", brand / "Presicav" / "Presicav-Heavy.ttf", 900))
mono = sorted({*brand.rglob("RobotoMono*.ttf"), *brand.rglob("Roboto*Mono*.ttf")})
for p in mono:
    n = p.stem.lower()
    if "italic" in n:
        continue
    if "variablefont" in n:   # one file covering every weight
        faces.append(font_face("Roboto Mono", p, "100 700"))
        continue
    w = 700 if "bold" in n and "semi" not in n and "extra" not in n else 500 if "medium" in n else 400 if "regular" in n else None
    if w:
        faces.append(font_face("Roboto Mono", p, w))
print("fonts:", len(faces), "| roboto mono files:", [p.name for p in mono])


def inline_svg(name, height_class=""):
    s = (brand / name).read_text(encoding="utf-8")
    s = s[s.index("<svg"):]
    s = re.sub(r'\sid="[^"]*"', "", s)
    return s


wordmark = inline_svg("RM Designs Text on Purple.svg")
icon = inline_svg("RM Designs Icon on Purple.svg")
app = (here / "app.fragment.html").read_text(encoding="utf-8").replace("%%WORDMARK%%", wordmark).replace("%%ICON%%", icon)
html = (here / "preview.template.html").read_text(encoding="utf-8").replace("/*FONTS*/", "\n".join(faces)).replace("%%APP%%", app)
(here / "preview.html").write_text(html, encoding="utf-8")
print("wrote", here / "preview.html", len(html) // 1024, "KB")
