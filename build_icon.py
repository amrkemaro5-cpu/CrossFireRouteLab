from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

out = Path('assets')
out.mkdir(exist_ok=True)
sizes = [16, 24, 32, 48, 64, 128, 256]
images = []

for size in sizes:
    scale = 4
    n = size * scale
    base = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    glow = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    cx = n / 2
    shield = [(cx, n*.025), (n*.86, n*.22), (n*.73, n*.74), (cx, n*.975), (n*.27, n*.74), (n*.14, n*.22)]
    gd.line(shield + [shield[0]], fill=(0, 224, 255, 220), width=max(3, n//18), joint='curve')
    glow = glow.filter(ImageFilter.GaussianBlur(max(2, n//18)))
    base = Image.alpha_composite(base, glow)
    d = ImageDraw.Draw(base)
    d.polygon(shield, fill=(4, 9, 21, 245), outline=(181, 70, 255, 235), width=max(2, n//32))
    inner = [(cx + (x-cx)*.86, cx + (y-cx)*.86) for x, y in shield]
    d.polygon(inner, fill=(4, 9, 21, 255), outline=(0, 224, 255, 235), width=max(2, n//32))
    d.ellipse((n*.205, n*.145, n*.795, n*.735), outline=(0, 224, 255, 220), width=max(2, n//30))
    d.arc((n*.205, n*.145, n*.795, n*.735), 210, 425, fill=(181, 70, 255, 220), width=max(1, n//42))
    bolt = [(n*.31,n*.20),(n*.43,n*.11),(n*.39,n*.25),(n*.51,n*.17)]
    d.line(bolt, fill=(0, 224, 255, 235), width=max(2, n//34), joint='curve')
    try:
        font = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', int(n*.245))
    except OSError:
        font = ImageFont.load_default()
    text = 'GRL'
    box = d.textbbox((0,0), text, font=font)
    tw, th = box[2]-box[0], box[3]-box[1]
    d.text((cx-tw/2, n*.32-th/2), text, font=font, fill=(245,250,255,255), stroke_width=max(1,n//120), stroke_fill=(0,100,180,255))
    d.line((n*.22,n*.80,n*.78,n*.80), fill=(181,70,255,235), width=max(1,n//55))
    images.append(base.resize((size,size), Image.Resampling.LANCZOS))

images[-1].save(out/'GameRouteLab.ico', format='ICO', sizes=[(s,s) for s in sizes])
print('Created', out/'GameRouteLab.ico')
