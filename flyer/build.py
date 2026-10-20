import re, subprocess, pathlib
from PIL import Image

root = pathlib.Path(__file__).parent
html = (root / "template.html").read_text()

def icon(m):
    name = m.group(1)
    svg = (root / "assets" / f"{name}.svg").read_text()
    svg = re.sub(r"<!--.*?-->", "", svg, flags=re.S)
    if name == "qr":
        svg = re.sub(r"<\?xml.*?\?>", "", svg).replace('width="25mm" height="25mm"', "").replace("<path", '<path fill="#3A4A40"')
    return svg

(root / "flyer.html").write_text(re.sub(r"\{\{([a-z0-9-]+)\}\}", icon, html))

out = root / "out"; out.mkdir(exist_ok=True)
pdf = out / "MyMedicalApp_Flyer_A5_tipar.pdf"
subprocess.run(["google-chrome", "--headless=new", "--no-sandbox", "--disable-gpu", "--no-pdf-header-footer",
                "--virtual-time-budget=15000", f"--print-to-pdf={pdf}", f"file://{root/'flyer.html'}"], check=True,
               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
subprocess.run(["pdftoppm", "-r", "300", "-png", str(pdf), str(out / "page")], check=True)
for i, side in ((1, "fata"), (2, "verso")):
    src = next(out.glob(f"page-{i}.png"))
    im = Image.open(src).convert("RGB")
    b = round(3 / 25.4 * 300)
    im = im.crop((b, b, im.width - b, im.height - b))
    im.save(out / f"MyMedicalApp_Flyer_{side}_HD.png")
    im.resize((1240, round(1240 * im.height / im.width)), Image.LANCZOS).save(out / f"MyMedicalApp_Flyer_{side}_WhatsApp.png", optimize=True)
    src.unlink()
print(sorted(p.name for p in out.iterdir()))
