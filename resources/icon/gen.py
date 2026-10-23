
import os, struct, io
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)))
os.makedirs(OUT, exist_ok=True)

BASE = 1024
TOP = (0x63, 0x82, 0xFF)
BOT = (0x36, 0x49, 0xD6)

def gradient(size, top, bot):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x*0.32 + y*0.68) / (size-1)
            px[x,y] = (round(top[0]+(bot[0]-top[0])*t),
                       round(top[1]+(bot[1]-top[1])*t),
                       round(top[2]+(bot[2]-top[2])*t))
    return img

def squircle_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0,0,size-1,size-1], radius=radius, fill=255)
    return m

def stroke_line(d, p0, p1, w, fill):
    d.line([p0,p1], fill=fill, width=int(round(w)))
    r = w/2
    for (x,y) in (p0,p1):
        d.ellipse([x-r,y-r,x+r,y+r], fill=fill)

def render(size):
    ss = 4
    S = size*ss
    k = S/BASE
    img = gradient(S, TOP, BOT).convert("RGBA")
    img.putalpha(squircle_mask(S, 236*k))

    # 顶部内高光：纵向渐变淡出（不用椭圆，椭圆会留下一条硬的明暗分界带）
    gloss = Image.new("RGBA", (S,S), (0,0,0,0))
    gm = Image.new("L", (1,S), 0)
    gp = gm.load()
    for y in range(S):
        t = y/(S*0.55)
        gp[0,y] = int(max(0.0, (1.0-t))**2 * 30)
    gm = gm.resize((S,S))
    white = Image.new("RGBA", (S,S), (255,255,255,255))
    gloss = Image.composite(white, Image.new("RGBA",(S,S),(0,0,0,0)), gm)
    gd = ImageDraw.Draw(gloss)
    gd.rounded_rectangle([int(3*k), int(3*k), S-int(3*k), S-int(3*k)],
                         radius=int(233*k), outline=(255,255,255,42), width=max(1,int(9*k)))
    gloss.putalpha(Image.composite(gm, Image.new("L",(S,S),42), Image.new("L",(S,S),0)))
    img = Image.alpha_composite(img, gloss)

    d = ImageDraw.Draw(img)
    small = size <= 24
    W  = (74 if small else 62) * k      # 角标线宽
    WA = (72 if small else 60) * k      # 箭头线宽
    inset = (300 if small else 292) * k # 角标外沿
    arm = (126 if small else 134) * k   # 角标臂长
    L, R = inset, S-inset
    T, B = inset, S-inset
    corners = [
        ((L, T+arm), (L, T), (L+arm, T)),
        ((R-arm, T), (R, T), (R, T+arm)),
        ((R, B-arm), (R, B), (R-arm, B)),
        ((L+arm, B), (L, B), (L, B-arm)),
    ]
    for a,b,c in corners:
        stroke_line(d, a, b, W, (255,255,255,255))
        stroke_line(d, b, c, W, (255,255,255,255))

    cx = 512*k
    cy0, cy1 = 372*k, 600*k
    hw, hy = 128*k, 126*k
    stroke_line(d, (cx, cy0), (cx, cy1), WA, (255,255,255,255))
    stroke_line(d, (cx-hw, cy1-hy), (cx, cy1), WA, (255,255,255,255))
    stroke_line(d, (cx+hw, cy1-hy), (cx, cy1), WA, (255,255,255,255))

    return img.resize((size,size), Image.LANCZOS)

sizes = [16,20,24,32,40,48,64,96,128,256]
imgs = {s: render(s) for s in sizes}
for s,im in imgs.items():
    im.save(os.path.join(OUT, f"icon_{s}.png"))

def build_ico(path, images):
    entries = []
    for s in sorted(images):
        b = io.BytesIO()
        images[s].save(b, format="PNG", optimize=True)
        data = b.getvalue()
        entries.append((s, data))
    n = len(entries)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16*n
    dirs = b""
    blobs = b""
    for s, data in entries:
        w = 0 if s >= 256 else s
        h = 0 if s >= 256 else s
        dirs += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    with open(path, "wb") as f:
        f.write(header + dirs + blobs)

build_ico(os.path.join(OUT, "app.ico"), imgs)
imgs[256].save(os.path.join(OUT, "icon-256.png"))

# preview
strip = Image.new("RGBA", (256+8+128+8+64+8+32+8+16, 256), (245,247,251,255))
x = 0
for s in (256,128,64,32,16):
    strip.paste(imgs[s], (x, 256-s), imgs[s]); x += s+8
strip.save(os.path.join(OUT, "final_strip.png"))
print("ico bytes:", os.path.getsize(os.path.join(OUT,"app.ico")))