from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
out = root / 'assets'
out.mkdir(exist_ok=True)
im = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8, 8, 248, 248), radius=56, fill='#1b1e21')
d.arc((56, 56, 200, 200), 45, 315, fill='#f1f3f4', width=25)
d.ellipse((176, 45, 214, 83), fill='#f16470')
im.save(out / 'carbon.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
