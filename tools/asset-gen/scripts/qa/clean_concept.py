# -*- coding: utf-8 -*-
"""
Isolate the subject in a concept plate before it conditions the 3D shape pass.

Run (in the asset-gen venv):
    python clean_concept.py <in.png> <out_rgba.png> [--white <out_white.png>]
                            [--model u2net] [--erode 2] [--keep-all] [--feather 1]

WHY THIS STAGE EXISTS
---------------------
Hunyuan3D's shape pipeline expects an RGBA image with a TRANSPARENT background; the reference
Tencent pipeline runs `rembg` on any RGB input before conditioning. ComfyUI's native Hunyuan3D
nodes do NOT do that — `LoadImage -> CLIPVisionEncode -> Hunyuan3Dv2Conditioning` passes whatever
you hand it. So this project was feeding SDXL plates complete with a grey backdrop and a cast
shadow straight into the conditioner, and Hunyuan faithfully reconstructed the shadow as a flat
slab of geometry under the model (visible in every shipped unit GLB) while the background
contaminated the silhouette.

Emits one JSON line: CLEAN_JSON {...}. Exit 0 on success.

Notes on the knobs:
  --erode    pulls the matte in by N pixels. rembg's default u2net matte leaves a faint halo of
             background-coloured pixels at the edge, which would otherwise be projected onto the
             model as a rim of backdrop colour.
  --keep-all keeps every matted blob. The default keeps only the component containing the matte's
             centroid, which drops the stray prop studies SDXL likes to scatter at the plate's
             bottom edge (boots, spare parts) — they are separate blobs, and Hunyuan would
             otherwise fuse them into the character.
"""
import sys, os, json, argparse
import numpy as np
from PIL import Image, ImageFilter


def matte(img, model_name):
    """Run rembg and return the alpha channel as float 0..1."""
    from rembg import remove, new_session
    session = new_session(model_name)
    cut = remove(img.convert("RGB"), session=session)          # RGBA
    return np.asarray(cut.convert("RGBA"), dtype=np.float32)[:, :, 3] / 255.0


def largest_component(mask):
    """Keep only the biggest connected blob in the matte.

    NOT PIL's ImageDraw.floodfill: in Pillow 12 that is a silent no-op — it returns without even
    writing the seed pixel, so the "component" came back empty and erased the whole subject. Verified
    on a synthetic two-blob mask. scipy.ndimage.label is the dependency-light thing that actually works.
    """
    from scipy import ndimage
    if not mask.any():
        return mask, 1.0
    labels, n = ndimage.label(mask)
    if n <= 1:
        return mask, 1.0
    sizes = ndimage.sum_labels(mask, labels, index=range(1, n + 1))
    keep = int(np.argmax(sizes)) + 1
    kept = labels == keep
    return kept, float(kept.sum()) / float(max(1, mask.sum()))


def main():
    ap = argparse.ArgumentParser(prog="clean_concept")
    ap.add_argument("src")
    ap.add_argument("dst", help="RGBA cutout — the image that should condition the shape pass")
    ap.add_argument("--white", help="also write an RGB copy composited on pure white")
    ap.add_argument("--model", default="u2net")
    ap.add_argument("--erode", type=int, default=2, help="pixels to pull the matte in (halo removal)")
    ap.add_argument("--feather", type=int, default=1, help="blur radius applied to the final alpha")
    ap.add_argument("--keep-all", action="store_true", help="keep stray blobs instead of the main subject only")
    ap.add_argument("--alpha-threshold", type=float, default=0.5)
    args = ap.parse_args()

    img = Image.open(args.src).convert("RGB")
    w, h = img.size
    alpha = matte(img, args.model)
    raw_cover = float((alpha > args.alpha_threshold).mean())

    mask = alpha > args.alpha_threshold
    kept_ratio = 1.0
    if not args.keep_all:
        mask, kept_ratio = largest_component(mask)

    if args.erode > 0:
        m = Image.fromarray((mask * 255).astype(np.uint8), mode="L")
        m = m.filter(ImageFilter.MinFilter(args.erode * 2 + 1))
        mask = np.asarray(m) > 127

    out_alpha = (mask * 255).astype(np.uint8)
    if args.feather > 0:
        out_alpha = np.asarray(
            Image.fromarray(out_alpha, mode="L").filter(ImageFilter.GaussianBlur(args.feather)))

    # Composite onto white FIRST, then attach alpha. The RGB channels must not retain the original
    # backdrop under transparent texels: ComfyUI's LoadImage hands CLIPVisionEncode the RGB and drops
    # the alpha, so an RGBA file carrying the old background in RGB would silently condition the
    # shape pass on the very backdrop this stage exists to remove.
    a = out_alpha.astype(np.float32)[:, :, None] / 255.0
    comp = (np.asarray(img, dtype=np.float32) * a + 255.0 * (1.0 - a)).astype(np.uint8)

    Image.fromarray(np.dstack([comp, out_alpha]), mode="RGBA").save(args.dst)
    if args.white:
        Image.fromarray(comp, mode="RGB").save(args.white)

    ys, xs = np.nonzero(mask)
    bbox = [int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1] if ys.size else None
    print("CLEAN_JSON " + json.dumps({
        "src": args.src, "dst": args.dst, "white": args.white,
        "size": [w, h], "model": args.model,
        "matte_coverage": round(raw_cover, 4),
        "final_coverage": round(float(mask.mean()), 4),
        "kept_of_matte": round(kept_ratio, 4),
        "stray_blobs_dropped": (not args.keep_all) and kept_ratio < 0.999,
        "subject_bbox_px": bbox,
        "ok": True,
    }))


if __name__ == "__main__":
    main()
