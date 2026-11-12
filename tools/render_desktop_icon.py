from io import BytesIO
from pathlib import Path
import xml.etree.ElementTree as ET

import cairosvg
from PIL import Image


workspace = Path(__file__).resolve().parents[1]
vector = ET.parse(workspace / "android/res/drawable/ic_pen.xml").getroot()
android = "{http://schemas.android.com/apk/res/android}"
svg = ET.Element("svg", {
    "xmlns": "http://www.w3.org/2000/svg",
    "viewBox": f'0 0 {vector.attrib[android + "viewportWidth"]} {vector.attrib[android + "viewportHeight"]}',
})
for path in vector.findall("path"):
    fields = {"d": path.attrib[android + "pathData"], "fill": path.get(android + "fillColor", "none")}
    for source, target in (("strokeColor", "stroke"), ("strokeWidth", "stroke-width"),
                           ("strokeLineCap", "stroke-linecap"), ("strokeLineJoin", "stroke-linejoin")):
        value = path.get(android + source)
        if value is not None:
            fields[target] = value
    ET.SubElement(svg, "path", fields)

raster = cairosvg.svg2png(bytestring=ET.tostring(svg), output_width=256, output_height=256)
icon = Image.open(BytesIO(raster)).convert("RGBA")
sizes = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
icon.save(workspace / "windows/app/icon.ico", sizes=sizes)
