from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

out=Path('assets'); out.mkdir(exist_ok=True)
sizes=[16,24,32,48,64,128,256]
images=[]
for size in sizes:
    scale=4; n=size*scale
    base=Image.new('RGBA',(n,n),(3,6,14,255)); glow=Image.new('RGBA',(n,n),(0,0,0,0)); d=ImageDraw.Draw(glow)
    cx=n/2; pts=[(cx,n*.04),(n*.82,n*.22),(n*.72,n*.78),(cx,n*.96),(n*.28,n*.78),(n*.18,n*.22)]
    d.polygon(pts,fill=(20,210,255,255),outline=(210,55,255,255),width=max(2,n//35)); glow=glow.filter(ImageFilter.GaussianBlur(max(1,n//28))); base=Image.alpha_composite(base,glow); d=ImageDraw.Draw(base)
    d.polygon(pts,fill=(7,13,27,255),outline=(0,220,255,255),width=max(2,n//32)); d.ellipse((n*.18,n*.12,n*.82,n*.76),outline=(0,220,255,255),width=max(2,n//38))
    try: font=ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf',int(n*.25))
    except: font=ImageFont.load_default()
    text='GRL'; box=d.textbbox((0,0),text,font=font); tw=box[2]-box[0]; th=box[3]-box[1]; d.text((cx-tw/2,n*.40-th/2),text,font=font,fill=(245,250,255,255),stroke_width=max(1,n//120),stroke_fill=(0,100,180,255)); d.line((n*.22,n*.79,n*.78,n*.79),fill=(177,77,255,255),width=max(1,n//55))
    images.append(base.resize((size,size),Image.Resampling.LANCZOS))
images[-1].save(out/'GameRouteLab.ico',format='ICO',sizes=[(s,s) for s in sizes])
print('Created',out/'GameRouteLab.ico')
