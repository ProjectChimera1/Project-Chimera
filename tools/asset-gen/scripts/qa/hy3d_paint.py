# -*- coding: utf-8 -*-
"""Generate an albedo texture for an existing low-poly mesh with Hunyuan3D-Paint.

Run with the DEDICATED paint venv (never the asset-gen venv, which is deliberately torch-free):
    D:\\tools\\hy3dpaint-venv\\Scripts\\python.exe hy3d_paint.py
        --mesh <lp.glb> --image <plate_white.png> --out-texture <albedo.png>
        --models D:\\ai-models\\hunyuan3d [--views 6] [--render-size 1024] [--tex 1024]

Emits one JSON line: PAINT_JSON {...}. Exit 0 on success.

WHY THIS REPLACES THE PROJECTION BAKE
-------------------------------------
`hp_to_lp_bake.py` paints by projecting ONE concept image orthographically front-and-back. Two
defects follow from that by construction, and no tuning removes either: every surface running
parallel to the projection axis is stretched from a single column of pixels, and with no rear art
the front is MIRRORED onto the back. Hunyuan3D-Paint instead runs a multi-view diffusion model
that generates six mutually-consistent views (front / right / back / left / top / bottom) and
back-projects all six. The back is generated rather than mirrored, and the sides are seen rather
than smeared.

THE UV CONTRACT — THE THING THIS SCRIPT EXISTS TO PROTECT
--------------------------------------------------------
`pipelines.py:155` calls `mesh_uv_wrap(mesh)`, which runs `xatlas.parametrize()` and REPLACES the
mesh's vertices, faces and UVs wholesale. Our low-poly already carries a clean smart-projected UV
set that the shipped GLB and its baked normal map both depend on; letting xatlas re-unwrap would
produce a texture painted in a UV space the shipped mesh does not use. Every structural gate would
still pass and all 24 assets would be silently wrong.

So the wrap is monkeypatched to identity BEFORE the pipeline is constructed, `xatlas` is
deliberately NOT installed in this venv as a second line of defence, and the UV array is hashed
before and after — a mismatch is a hard failure, not a warning.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time

HY3D_ROOT = os.environ.get("CHIMERA_HY3D_ROOT", r"D:\tools\hy3d20")


def _sys_ram_used_gb():
    """Physical RAM in use system-wide. The paint stage is bounded by HOST memory as much as by
    VRAM, and the OS kills for the former without warning."""
    import ctypes

    class _MS(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

    m = _MS()
    m.dwLength = ctypes.sizeof(_MS)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return round((m.ullTotalPhys - m.ullAvailPhys) / 1e9, 2)


def uv_hash(mesh):
    """Stable fingerprint of a mesh's UV array, used to prove the unwrap did not run."""
    import numpy as np
    vis = getattr(mesh, "visual", None)
    uv = getattr(vis, "uv", None)
    if uv is None:
        return "none"
    a = np.ascontiguousarray(np.asarray(uv, dtype=np.float64))
    return hashlib.sha1(a.tobytes()).hexdigest()[:12]


def main() -> int:
    ap = argparse.ArgumentParser(prog="hy3d_paint")
    ap.add_argument("--mesh", required=True, help="low-poly GLB that already carries UVs")
    ap.add_argument("--image", required=True, help="reference plate (use the WHITE composite, not the RGBA)")
    ap.add_argument("--out-texture", required=True, help="where to write the painted albedo PNG")
    ap.add_argument("--models", default=r"D:\ai-models\hunyuan3d")
    ap.add_argument("--paint-dir", default="hunyuan3d-paint-v2-0")
    ap.add_argument("--delight-dir", default="hunyuan3d-delight-v2-0")
    ap.add_argument("--render-size", type=int, default=1024,
                    help="multi-view render resolution; the config default of 2048 is the main VRAM driver")
    ap.add_argument("--tex", type=int, default=1024, help="output texture size")
    # NOTE: "graphcut" is NOT a real option. `bake_from_multiview` has exactly one branch and its
    # else-arm raises a bare f-string, which dies with `TypeError: exceptions must derive from
    # BaseException` -- an inscrutable crash rather than an honest "unsupported". The flag is kept
    # so existing invocations do not break, but only "fast" exists.
    ap.add_argument("--merge", default="fast", choices=["fast"],
                    help="only 'fast' exists in this tree; graphcut is not implemented")
    ap.add_argument("--view-weights", type=float, nargs=6,
                    default=[1.0, 0.30, 0.60, 0.30, 0.40, 0.05],
                    metavar=("FRONT", "RIGHT", "BACK", "LEFT", "TOP", "BOTTOM"),
                    help="per-view trust for the bake. Stock is a hero turntable "
                         "(1.0 0.1 0.5 0.1 0.05 0.05); the default here is re-aimed at a top-down "
                         "RTS camera. Buildings benefit from a higher TOP (~0.5).")
    ap.add_argument("--bake-exp", type=float, default=6.0,
                    help="exponent on per-texel view selection (stock 4). Higher = each texel "
                         "takes the view that faces it rather than a grazing blend. Max ~8.")
    args = ap.parse_args()

    t0 = time.time()
    sys.path.insert(0, HY3D_ROOT)

    import torch
    import trimesh

    # MEMORY-MAP EVERY CHECKPOINT LOAD. This is a HOST-RAM fix, not a VRAM one, and it is what
    # stopped the batch: the paint UNet ships as a 3.6 GB `.bin` (there is no safetensors for it),
    # and the custom loader calls `torch.load(..., map_location='cpu')`, which materialises the
    # whole tensor set in RAM and then copies it into the model -- roughly 7 GB transiently, on top
    # of the delight model, on a 16 GB machine. The first attempt at a 24-asset run was killed by
    # the OS for low memory partway through asset 5.
    #
    # `mmap=True` pages tensors from disk instead of reading them whole, which removes the transient
    # copy entirely. It applies to any zip-format checkpoint, which is everything torch has written
    # since 1.6.
    _torch_load = torch.load

    def _mmap_load(f, *a, **kw):
        if isinstance(f, (str, os.PathLike)):
            kw.setdefault("mmap", True)
            kw.setdefault("map_location", "cpu")
        try:
            return _torch_load(f, *a, **kw)
        except (RuntimeError, ValueError, TypeError):
            kw.pop("mmap", None)
            return _torch_load(f, *a, **kw)

    torch.load = _mmap_load

    # STUB xatlas RATHER THAN INSTALL IT. `uv_warp_utils.py` does a module-level `import xatlas`,
    # so leaving it uninstalled makes `pipelines.py` unimportable — the tripwire fires too early to
    # be useful. Installing it for real would remove the tripwire entirely and leave only the
    # monkeypatch standing between us and a silent re-unwrap.
    #
    # A stub keeps both properties: the import succeeds, and any actual CALL into xatlas raises by
    # name instead of quietly replacing the mesh's vertices, faces and UVs.
    # The stub must be a WELL-FORMED module: give it a real __file__ and define only the names it
    # needs. A catch-all __getattr__ returning callables looks tidier and breaks torch — `inspect`
    # walks every entry in sys.modules and calls `getsourcefile(module)`, which reads __file__ and
    # then calls `.endswith` on it, so a module whose __file__ resolves to a function raises
    # `AttributeError: 'function' object has no attribute 'endswith'` from inside
    # torch.library._register_fake, thousands of frames from anything to do with xatlas.
    if "xatlas" not in sys.modules:
        import types
        stub = types.ModuleType("xatlas")
        stub.__file__ = "<xatlas stub: hy3d_paint.py>"

        def _refuse(*_a, **_k):
            raise RuntimeError(
                "xatlas.parametrize() was called — the mesh was about to be RE-UNWRAPPED, which "
                "would paint the texture into a UV space the shipped mesh does not use. The "
                "mesh_uv_wrap monkeypatch in hy3d_paint.py did not hold."
            )

        stub.parametrize = _refuse
        sys.modules["xatlas"] = stub

    # PATCH FIRST, CONSTRUCT SECOND. pipelines.py does `from .utils.uv_warp_utils import
    # mesh_uv_wrap`, so the name it calls lives in the pipelines module namespace — patching the
    # source module alone would not take effect.
    import hy3dgen.texgen.pipelines as P
    original_wrap = P.mesh_uv_wrap
    P.mesh_uv_wrap = lambda m: m

    # SIGNATURE SHIM. kijai's fork drives these models from its ComfyUI node classes, and its
    # standalone `pipelines.py` has drifted out of step with them: `load_models()` still calls
    # `Light_Shadow_Remover(self.config)` while the class now takes `(model_path, device)`. Adapt
    # rather than edit the vendored tree, so a later `git pull` of the wrapper does not silently
    # revert the fix or conflict with it.
    _LSR = P.Light_Shadow_Remover
    if "config" not in _LSR.__init__.__code__.co_varnames[:2]:
        P.Light_Shadow_Remover = lambda cfg: _LSR(cfg.light_remover_ckpt_path, cfg.device)

    # TRUST THE LOCAL CUSTOM PIPELINE. Modern diffusers refuses to execute a `custom_pipeline`
    # without `trust_remote_code=True`. Despite the name nothing here is remote: the module is
    # `hy3dgen/texgen/hunyuanpaint/pipeline.py` inside the wrapper checkout on this disk, which is
    # readable and pinned by the git sha that paint_identity() folds into the cache key. The
    # multiview loader does not pass the flag, so inject it at the diffusers entry point.
    from diffusers import DiffusionPipeline
    _orig_fp = DiffusionPipeline.from_pretrained.__func__

    def _fp(cls, *a, **kw):
        kw.setdefault("trust_remote_code", True)
        return _orig_fp(cls, *a, **kw)

    DiffusionPipeline.from_pretrained = classmethod(_fp)

    from hy3dgen.texgen.pipelines import Hunyuan3DPaintPipeline, Hunyuan3DTexGenConfig

    # RETURN-SHAPE SHIM, same drift as the constructor one. kijai's renderer returns
    # `(image, visible_mask)` from render_normal — the mask is his addition — while the stale
    # `pipelines.py` appends the whole tuple straight into the list it hands the multi-view net,
    # which then calls `.resize()` on a tuple. Unwrap defensively rather than editing the vendored
    # tree, and normalise to PIL so either return convention works.
    from PIL import Image as _PILImage

    def _as_image(x):
        if isinstance(x, tuple):
            x = x[0]
        if isinstance(x, _PILImage.Image):
            return x
        import numpy as _np
        a = x.detach().cpu().numpy() if hasattr(x, "detach") else _np.asarray(x)
        if a.dtype != _np.uint8:
            a = _np.clip(a * 255.0, 0, 255).astype(_np.uint8)
        if a.ndim == 3 and a.shape[-1] == 1:
            a = a[..., 0]
        return _PILImage.fromarray(a)

    def _render_normal_multiview(self, camera_elevs, camera_azims, use_abs_coor=True):
        return [_as_image(self.render.render_normal(e, a, use_abs_coor=use_abs_coor, return_type="pl"))
                for e, a in zip(camera_elevs, camera_azims)]

    def _render_position_multiview(self, camera_elevs, camera_azims):
        return [_as_image(self.render.render_position(e, a, return_type="pl"))
                for e, a in zip(camera_elevs, camera_azims)]

    Hunyuan3DPaintPipeline.render_normal_multiview = _render_normal_multiview
    Hunyuan3DPaintPipeline.render_position_multiview = _render_position_multiview

    delight = os.path.join(args.models, args.delight_dir)
    paint = os.path.join(args.models, args.paint_dir)
    for p in (delight, paint):
        if not os.path.isdir(p):
            print(json.dumps({"ok": False, "error": f"missing weights: {p}"}), file=sys.stderr)
            print("PAINT_JSON " + json.dumps({"ok": False, "error": f"missing weights: {p}"}))
            return 2

    cfg = Hunyuan3DTexGenConfig(delight, paint)
    # The stock config is 2048/2048, which is the single biggest VRAM term in the whole pipeline.
    cfg.render_size = args.render_size
    cfg.texture_size = args.tex
    cfg.merge_method = "fast"          # see the --merge note in the argparse block

    # RE-AIM THE BAKE AT THE ACTUAL CAMERA. This is the highest-value line in the file.
    #
    # The six views are front / right / back / left / TOP / bottom, and the stock weights are
    # [1.0, 0.1, 0.5, 0.1, 0.05, 0.05] -- a hero-turntable weighting, front-biased for a character
    # you orbit at eye level. Chimera is a TOP-DOWN RTS: the top and upper surfaces are the
    # majority of the pixels a player ever sees, and at 0.05 they were being reconstructed almost
    # entirely from the FRONT view smeared over them through the cosine falloff. Five minutes of
    # GPU per asset was being spent painting surfaces the camera never shows.
    #
    # Buildings suffer worst: a pitched roof at 40-60 degrees is past the 75-degree bake angle
    # threshold of every equator view, so it was covered ONLY by the top view, at 0.05.
    cfg.candidate_view_weights = args.view_weights
    # bake_exp is the exponent on the per-texel cosine view-selection weight. Raising it from the
    # stock 4 makes a texel take the view that genuinely faces it rather than a grazing-angle
    # blend of several -- that blend is what reads as "smeared" on side walls and corners. Do not
    # exceed ~8: hard seams appear where the winning view flips.
    cfg.bake_exp = args.bake_exp

    mesh = trimesh.load(args.mesh, force="mesh", process=False)
    h_in = uv_hash(mesh)
    if h_in == "none":
        print("PAINT_JSON " + json.dumps({"ok": False, "error": "input mesh has no UVs"}))
        return 2

    torch.cuda.reset_peak_memory_stats()

    # RUN DELIGHT BEFORE THE PAINT PIPELINE EXISTS, not after.
    #
    # `Hunyuan3DPaintPipeline.__init__` calls `load_models()`, which loads the delight model AND the
    # multi-view model back to back, so both sets of weights are resident before any user code gets
    # control. Freeing delight afterwards -- which is what this script did first -- lowers the
    # steady state but does nothing about the PEAK, and the peak is what the OS kills for. Measured:
    # 16.1 GB of 15.9 GB physical, spilling into the pagefile, and a 24-asset run was killed partway
    # through asset 5.
    #
    # Delight is a one-shot preprocessing step on a single image, so it does not need to coexist
    # with the painter at all. Load it alone, run it, free it, and only then build the pipeline with
    # `load_models` patched to skip the delight half.
    import gc
    _LSR_real = _LSR
    delight_model = _LSR_real(cfg.light_remover_ckpt_path, cfg.device)
    from PIL import Image as _IM
    ref = _IM.open(args.image)
    delit = delight_model(ref)
    del delight_model
    gc.collect()
    torch.cuda.empty_cache()
    ram_after_delight = _sys_ram_used_gb()

    def _load_multiview_only(self):
        torch.cuda.empty_cache()
        self.models["delight_model"] = lambda x: x      # already applied, above
        self.models["multiview_model"] = P.Multiview_Diffusion_Net(self.config)

    Hunyuan3DPaintPipeline.load_models = _load_multiview_only
    pipe = Hunyuan3DPaintPipeline(cfg)

    # INPUT-SHAPE SHIM. kijai COMMENTED OUT the PIL->tensor conversion inside the custom multi-view
    # pipeline (his ComfyUI nodes do it upstream), so it now expects `image` to arrive as a
    # (B, N, C, H, W) tensor while `Multiview_Diffusion_Net.__call__` still hands it a PIL Image and
    # the pipeline dies on `image.shape[0]`. The commented-out lines are still in the file and
    # specify the exact conversion, which is what is reproduced here.
    _mv = pipe.models["multiview_model"]
    _inner = _mv.pipeline
    # Patch the CLASS, not the instance: `self.pipeline(...)` invokes `type(pipeline).__call__`,
    # so an instance attribute named __call__ is never consulted.
    _inner_cls = type(_inner)
    _inner_call_unbound = _inner_cls.__call__

    def _coerce_call(self, image, *a, **kw):
        if isinstance(image, _PILImage.Image):
            import numpy as _np
            im = image
            if im.mode == "RGBA":
                bg = _PILImage.new("RGB", im.size, (255, 255, 255))
                bg.paste(im, mask=im.split()[3])
                im = bg
            else:
                im = im.convert("RGB")
            t = torch.from_numpy(_np.asarray(im, dtype=_np.float32) / 255.0)
            # (H,W,3) -> (1,3,H,W) -> (1,1,3,H,W): batch of 1, one reference view.
            t = t.unsqueeze(0).permute(0, 3, 1, 2).unsqueeze(0)
            image = t.to(device=self.vae.device, dtype=self.vae.dtype)
        return _inner_call_unbound(self, image, *a, **kw)

    _inner_cls.__call__ = _coerce_call

    after_delight = torch.cuda.memory_allocated() / 1e6
    mvp = pipe.models["multiview_model"].pipeline
    for fn in ("enable_attention_slicing", "enable_vae_slicing", "enable_vae_tiling"):
        try:
            getattr(mvp, fn)()
        except Exception:
            pass

    # LANCZOS, NOT BICUBIC, on the multiview upsample. `pipelines.py` resizes the six generated
    # views from their hard-coded 512 up to render_size with Pillow's DEFAULT filter (bicubic)
    # immediately before back-projection. That single resize is the mechanical source of the
    # "soft" look: every texel that reaches the atlas passes through it. Lanczos is strictly
    # sharper at identical cost. kijai's own reference workflow uses lanczos here.
    _orig_resize = _PILImage.Image.resize

    def _lanczos_resize(self, size, resample=None, *a, **kw):
        if resample is None:
            resample = _PILImage.LANCZOS
        return _orig_resize(self, size, resample, *a, **kw)

    _PILImage.Image.resize = _lanczos_resize
    try:
        textured = pipe(mesh, delit)
    finally:
        _PILImage.Image.resize = _orig_resize

    h_out = uv_hash(textured if hasattr(textured, "visual") else mesh)

    import numpy as np
    from PIL import Image
    tex = pipe.render.get_texture()
    img = Image.fromarray(np.clip(tex * 255.0, 0, 255).astype(np.uint8)).convert("RGB")
    os.makedirs(os.path.dirname(os.path.abspath(args.out_texture)) or ".", exist_ok=True)
    img.save(args.out_texture)

    # Sidecar the UV hash beside the texture so an interrupted batch can tell whether this albedo
    # is still valid for the mesh as it now stands, without repainting to find out.
    try:
        with open(args.out_texture + ".uvhash", "w", encoding="utf-8") as fh:
            fh.write(h_in)
    except OSError:
        pass

    arr = np.asarray(img)
    distinct = int(len({tuple(c) for c in arr.reshape(-1, 3)[::97]}))
    peak = torch.cuda.max_memory_allocated() / 1e6

    P.mesh_uv_wrap = original_wrap

    ok = (h_out == h_in or h_out == "none") and distinct > 200
    print("PAINT_JSON " + json.dumps({
        "ok": bool(ok),
        "mesh": args.mesh,
        "out_texture": args.out_texture,
        "uv_hash_in": h_in,
        "uv_hash_out": h_out,
        "uv_preserved": bool(h_out == h_in or h_out == "none"),
        "distinct_colours_sampled": distinct,
        "texture_size": list(img.size),
        "render_size": args.render_size,
        "view_weights": list(args.view_weights),
        "bake_exp": args.bake_exp,
        "peak_vram_mb": round(peak, 1),
        "vram_after_delight_mb": round(after_delight, 1),
        "host_ram_after_delight_gb": ram_after_delight,
        "host_ram_peak_gb": _sys_ram_used_gb(),
        "seconds": round(time.time() - t0, 1),
    }))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
