#!/usr/bin/env python3
"""Local-only PDF comparison helper for controlled field acceptance.

The tool reports page count, dimensions, bytes, per-page rendered SHA-256 hashes and a pixel-difference
percentage. It never uploads files. Install PyMuPDF and Pillow locally before use:
    python -m pip install pymupdf pillow
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

try:
    import fitz  # PyMuPDF
    from PIL import Image, ImageChops, ImageStat
except ImportError as error:
    raise SystemExit("Missing dependency. Install locally with: python -m pip install pymupdf pillow") from error


def render_page(document: fitz.Document, index: int, dpi: int) -> Image.Image:
    page = document.load_page(index)
    scale = dpi / 72.0
    pixmap = page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=False)
    return Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)


def describe(path: Path, dpi: int, image_output: Path | None) -> dict:
    document = fitz.open(path)
    pages = []
    for index in range(document.page_count):
        page = document.load_page(index)
        image = render_page(document, index, dpi)
        raw = image.tobytes()
        if image_output:
            image_output.mkdir(parents=True, exist_ok=True)
            image.save(image_output / f"{path.stem}-page-{index + 1}.png")
        pages.append({
            "number": index + 1,
            "widthPoints": round(page.rect.width, 2),
            "heightPoints": round(page.rect.height, 2),
            "renderedSha256": hashlib.sha256(raw).hexdigest(),
            "renderedPixels": [image.width, image.height],
        })
    return {"path": str(path), "bytes": path.stat().st_size, "pageCount": document.page_count, "pages": pages}


def compare(left_path: Path, right_path: Path, dpi: int, diff_output: Path | None) -> dict:
    left = fitz.open(left_path)
    right = fitz.open(right_path)
    page_results = []
    for index in range(min(left.page_count, right.page_count)):
        left_image = render_page(left, index, dpi)
        right_image = render_page(right, index, dpi)
        if left_image.size != right_image.size:
            page_results.append({"number": index + 1, "comparable": False, "differencePercentage": 100.0, "reason": "rendered dimensions differ"})
            continue
        difference = ImageChops.difference(left_image, right_image)
        total = sum(ImageStat.Stat(difference).sum)
        maximum = 255 * 3 * left_image.width * left_image.height
        percentage = round(total / maximum * 100, 4)
        if diff_output:
            diff_output.mkdir(parents=True, exist_ok=True)
            difference.save(diff_output / f"page-{index + 1}-diff.png")
        page_results.append({"number": index + 1, "comparable": True, "differencePercentage": percentage})
    return {
        "pagesCompared": len(page_results),
        "pageCountMatches": left.page_count == right.page_count,
        "pages": page_results,
        "overallDifferencePercentage": round(sum(x["differencePercentage"] for x in page_results) / len(page_results), 4) if page_results else None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare two local PDFs without uploading PHI.")
    parser.add_argument("legacy_pdf", type=Path)
    parser.add_argument("new_pdf", type=Path)
    parser.add_argument("--dpi", type=int, default=150)
    parser.add_argument("--output", type=Path, required=True, help="JSON output path; use .runtime/evidence/.")
    parser.add_argument("--diff-images", type=Path, help="Optional local-only directory for page PNG differences.")
    parser.add_argument("--rendered-images", type=Path, help="Optional local-only directory for rendered page PNGs.")
    args = parser.parse_args()

    for path in (args.legacy_pdf, args.new_pdf):
        if not path.is_file():
            raise SystemExit(f"PDF does not exist: {path}")
    if args.dpi < 72 or args.dpi > 600:
        raise SystemExit("--dpi must be between 72 and 600")

    result = {
        "tool": "compare_pdf_visual.py",
        "dpi": args.dpi,
        "legacy": describe(args.legacy_pdf, args.dpi, args.rendered_images),
        "new": describe(args.new_pdf, args.dpi, args.rendered_images),
        "comparison": compare(args.legacy_pdf, args.new_pdf, args.dpi, args.diff_images),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["comparison"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
