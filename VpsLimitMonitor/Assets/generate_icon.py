"""Generate the VpsLimitMonitor application icon.

The artwork is drawn at 4x resolution and reduced with Lanczos resampling so
the curved edges remain clean at Windows taskbar and notification-area sizes.
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw


OUTPUT_SIZE = 1024
SUPERSAMPLE = 4
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

BACKGROUND = "#102B42"
BACKGROUND_EDGE = "#173B57"
GAUGE_TRACK = "#31536A"
GAUGE_PROGRESS = "#32D3B5"
GAUGE_MARKER = "#FFB84A"
SERVER_FACE = "#F2F8FC"
SERVER_SHADOW = "#092033"
SERVER_SLOT = "#294C64"
SERVER_LED = "#20C997"
SERVER_LED_DARK = "#C7E1EA"


def _scaled(value: float) -> int:
    return round(value * SUPERSAMPLE)


def _box(left: float, top: float, right: float, bottom: float) -> tuple[int, ...]:
    return tuple(_scaled(value) for value in (left, top, right, bottom))


def _rounded_arc(
    draw: ImageDraw.ImageDraw,
    bounds: tuple[float, float, float, float],
    start: float,
    end: float,
    color: str,
    width: float,
) -> None:
    """Draw a thick arc with circular end caps."""

    scaled_bounds = _box(*bounds)
    scaled_width = _scaled(width)
    draw.arc(
        scaled_bounds,
        start=start,
        end=end,
        fill=color,
        width=scaled_width,
    )

    left, top, right, bottom = bounds
    center_x = (left + right) / 2
    center_y = (top + bottom) / 2
    radius_x = (right - left) / 2
    radius_y = (bottom - top) / 2
    cap_radius = width / 2

    for angle in (start, end):
        radians = math.radians(angle)
        x = center_x + radius_x * math.cos(radians)
        y = center_y + radius_y * math.sin(radians)
        draw.ellipse(
            _box(
                x - cap_radius,
                y - cap_radius,
                x + cap_radius,
                y + cap_radius,
            ),
            fill=color,
        )


def draw_icon() -> Image.Image:
    canvas_size = OUTPUT_SIZE * SUPERSAMPLE
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # A dark rounded tile keeps the mark legible on both light and dark trays.
    draw.rounded_rectangle(
        _box(64, 64, 960, 960),
        radius=_scaled(224),
        fill=BACKGROUND_EDGE,
    )
    draw.rounded_rectangle(
        _box(78, 78, 946, 946),
        radius=_scaled(210),
        fill=BACKGROUND,
    )

    # The partially filled ring represents measured traffic against a limit.
    gauge_bounds = (190, 190, 834, 834)
    _rounded_arc(
        draw,
        gauge_bounds,
        start=-90,
        end=269.8,
        color=GAUGE_TRACK,
        width=112,
    )
    _rounded_arc(
        draw,
        gauge_bounds,
        start=-90,
        end=170,
        color=GAUGE_PROGRESS,
        width=112,
    )

    # A warm end marker makes the monitored threshold apparent without text.
    marker_angle = math.radians(170)
    marker_x = 512 + 322 * math.cos(marker_angle)
    marker_y = 512 + 322 * math.sin(marker_angle)
    draw.ellipse(
        _box(marker_x - 42, marker_y - 42, marker_x + 42, marker_y + 42),
        fill=GAUGE_MARKER,
    )

    # Three bold rack units stay recognizable even after reduction to 16 px.
    rack_left = 254
    rack_right = 770
    rack_height = 112
    rack_radius = 30
    for top in (330, 466, 602):
        bottom = top + rack_height
        draw.rounded_rectangle(
            _box(rack_left + 8, top + 14, rack_right + 8, bottom + 14),
            radius=_scaled(rack_radius),
            fill=SERVER_SHADOW,
        )
        draw.rounded_rectangle(
            _box(rack_left, top, rack_right, bottom),
            radius=_scaled(rack_radius),
            fill=SERVER_FACE,
        )

        slot_top = top + 39
        draw.rounded_rectangle(
            _box(304, slot_top, 546, slot_top + 34),
            radius=_scaled(17),
            fill=SERVER_SLOT,
        )
        draw.ellipse(
            _box(648, top + 36, 690, top + 78),
            fill=SERVER_LED_DARK,
        )
        draw.ellipse(
            _box(704, top + 36, 746, top + 78),
            fill=SERVER_LED,
        )

    return image.resize(
        (OUTPUT_SIZE, OUTPUT_SIZE),
        resample=Image.Resampling.LANCZOS,
    )


def main() -> None:
    output_dir = Path(__file__).resolve().parent
    master = draw_icon()

    preview_path = output_dir / "app-256.png"
    icon_path = output_dir / "app.ico"

    preview = master.resize((256, 256), Image.Resampling.LANCZOS)
    preview.save(preview_path, format="PNG", optimize=True)
    master.save(
        icon_path,
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
    )

    print(f"Wrote {preview_path}")
    print(f"Wrote {icon_path}")


if __name__ == "__main__":
    main()
