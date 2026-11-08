from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
folder = root / 'Shitie' / 'Assets'
folder.mkdir(parents=True, exist_ok=True)
image = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((4, 4, 252, 252), radius=62, fill='#132B38')
draw.rounded_rectangle((61, 52, 195, 216), radius=23, fill='#54C8B8')
draw.rounded_rectangle((80, 74, 176, 194), radius=10, fill='#EBFAF6')
draw.rounded_rectangle((101, 38, 155, 78), radius=11, fill='#9EE4D7')
for y, length in [(109, 52), (135, 52), (161, 34)]:
    draw.rounded_rectangle((103, y, 103 + length, y + 8), radius=4, fill='#268A83')
image.save(folder / 'shitie.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
image.save(folder / 'shitie.png')
