"""Draws Marshal's plugin icon (1024x1024 PNG) in the app's language: asphalt, a yellow taxi line
(painted up to the agent, dashed beyond), the agent's dot, and a red stop bar with its lights.
Usage: python scripts/make-icon.py <out.png>"""
import sys
from PIL import Image, ImageDraw

S, K = 1024, 4  # final size, supersampling factor
W = S * K
ASPHALT, TAXI, STOP, LIGHT, INK = (23, 25, 28), (246, 203, 60), (255, 74, 58), (255, 214, 205), (243, 241, 234)

img = Image.new("RGB", (W, W), ASPHALT)
d = ImageDraw.Draw(img)
u = W / 100  # one unit = 1% of the side

# The taxi line: painted from the bottom edge to the agent's dot, dashed above the stop bar.
x = 50 * u
line = 7 * u
d.rectangle([x - line / 2, 66 * u, x + line / 2, W], fill=TAXI)
for y0 in (10, 24):
    d.rectangle([x - line / 2, y0 * u, x + line / 2, (y0 + 8) * u], fill=TAXI)

# The stop bar across the line, with five lights.
bar_top, bar_h = 40 * u, 10 * u
d.rounded_rectangle([14 * u, bar_top, 86 * u, bar_top + bar_h], radius=bar_h / 2, fill=STOP)
for cx in (24, 37, 50, 63, 76):
    r = 2.2 * u
    d.ellipse([cx * u - r, bar_top + bar_h / 2 - r, cx * u + r, bar_top + bar_h / 2 + r], fill=LIGHT)

# The agent: a dot on the painted line, just below the bar.
r = 8 * u
d.ellipse([x - r, 66 * u - r, x + r, 66 * u + r], fill=INK, outline=ASPHALT, width=int(2.5 * u))

img.resize((S, S), Image.LANCZOS).save(sys.argv[1], optimize=True)
print("written", sys.argv[1])
