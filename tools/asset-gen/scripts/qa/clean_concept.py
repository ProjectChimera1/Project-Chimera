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
    ap.add_argument("--min-coverage", type=float, default=0.02,
                    help="below this matte coverage the matte is treated as FAILED and the unmatted "
                         "plate is used instead (see the matte-failure guard)")
    ap.add_argument("--square", action="store_true",
                    help="pad the outputs to a square canvas (see the note on aspect below)")
    args = ap.parse_args()

    img = Image.open(args.src).convert("RGB")
    w, h = img.size
    alpha = matte(img, args.model)
    raw_cover = float((alpha > args.alpha_threshold).mean())

    mask = alpha > args.alpha_threshold
    kept_ratio = 1.0
    if not args.keep_all:
        mask, kept_ratio = largest_component(mask)

    # MATTE-FAILURE GUARD. rembg's u2net is a SALIENT-OBJECT segmenter: it is trained to find one
    # foreground subject against a background. It does that well for a character on a plain plate,
    # and it fails completely on an architectural SCENE — a structure sitting in grounds among other
    # buildings has no single salient object, so the matte comes back near-empty and
    # largest_component then keeps a speck.
    #
    # Measured across the roster: every character plate mattes to 0.10-0.65 coverage, while four
    # building plates came back at 0.000-0.001. Those four emitted an essentially BLANK WHITE image,
    # reported `"ok": true`, and every downstream stage faithfully consumed it — the shape pass was
    # conditioned on nothing and the bake projected white, producing four grey assets whose cause
    # looked like a 3D problem and was not.
    #
    # A stage that destroys its input must not report success. Below the floor, fall back to the
    # untouched plate: an unmatted background is a far smaller defect than no image at all, and the
    # failure is named in the output rather than inferred later from a grey model.
    matte_failed = float(mask.mean()) < args.min_coverage
    if matte_failed:
        print("CLEAN_WARN matte failed on %s (coverage %.4f < %.4f) — falling back to the unmatted "
              "plate. This usually means the plate is a SCENE rather than one isolated subject."
              % (os.path.basename(args.src), float(mask.mean()), args.min_coverage), file=sys.stderr)
        mask = np.ones_like(mask, dtype=bool)
        kept_ratio = 1.0

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
    # BACKDROP GREY, NOT WHITE. This composite is what the texture stage's DELIGHT model consumes
    # (InstructPix2Pix, image_guidance 1.5). Hunyuan's own reference workflow composites onto 0.8
    # grey with the note "fully black generally doesn't work, too dark makes the image red, fully
    # white can be overbright" -- and the two most washed-out assets in the roster (alpha_mage at
    # 0.05 saturation, alpha_archery_range at 0.12) are precisely the two palest plates fed to
    # delight on pure white. Do not go below ~0.6 grey; per the same note, darker pushes it red.
    BACKDROP = 204.0                      # 0.8 * 255
    a = out_alpha.astype(np.float32)[:, :, None] / 255.0
    comp = (np.asarray(img, dtype=np.float32) * a + BACKDROP * (1.0 - a)).astype(np.uint8)

    rgba = Image.fromarray(np.dstack([comp, out_alpha]), mode="RGBA")
    white = Image.fromarray(comp, mode="RGB")

    # SQUARE PADDING. The shape pass conditions on CLIPVisionEncode with crop="none", which resizes
    # the whole plate to CLIP's square 224 input WITHOUT preserving aspect. A portrait plate (the
    # roster ships 832x1216) is therefore squashed horizontally before the 3D model ever sees it, so
    # the generator is asked to reconstruct a character it has been shown at the wrong proportions.
    # Padding to a square canvas first makes that resize a uniform scale instead of a distortion.
    # Measured on alpha/worker: a visibly cleaner silhouette with less floating debris.
    if args.square:
        side = max(w, h)
        sq_rgba = Image.new("RGBA", (side, side), (255, 255, 255, 0))
        sq_rgba.paste(rgba, ((side - w) // 2, (side - h) // 2))
        sq_white = Image.new("RGB", (side, side), (204, 204, 204))   # match BACKDROP above
        sq_white.paste(white, ((side - w) // 2, (side - h) // 2))
        rgba, white = sq_rgba, sq_white

    rgba.save(args.dst)
    if args.white:
        white.save(args.white)

    ys, xs = np.nonzero(mask)
    bbox = [int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1] if ys.size else None
    print("CLEAN_JSON " + json.dumps({
        "src": args.src, "dst": args.dst, "white": args.white,
        "size": [w, h], "out_size": list(rgba.size), "squared": bool(args.square),
        "model": args.model,
        "matte_coverage": round(raw_cover, 4),
        "final_coverage": round(float(mask.mean()), 4),
        "kept_of_matte": round(kept_ratio, 4),
        "stray_blobs_dropped": (not args.keep_all) and kept_ratio < 0.999,
        "matte_failed": bool(matte_failed),
        "subject_bbox_px": bbox,
        "ok": True,
    }))


if __name__ == "__main__":
    main()
