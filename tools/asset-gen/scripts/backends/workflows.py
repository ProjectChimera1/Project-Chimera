# -*- coding: utf-8 -*-
"""
ComfyUI API-format workflow builders (node graphs) for the asset-gen pipeline.
Derived from ComfyUI's built-in templates; parameterized for headless /prompt submission.
Input keys may need a one-time reconcile against /object_info on the running server
(orchestrator validates before the real batch).
"""

def sdxl_concept(prompt, negative, ckpt="sd_xl_base_1.0.safetensors",
                 seed=0, steps=30, cfg=7.0, width=1024, height=1024,
                 sampler="dpmpp_2m", scheduler="karras", out_prefix="concept/chimera"):
    """SDXL text->image concept (plain bg, centered subject) — input to Hunyuan3D, or terrain tiles."""
    return {
        "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": ckpt}},
        "2": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["1", 1]}},
        "3": {"class_type": "CLIPTextEncode", "inputs": {"text": negative, "clip": ["1", 1]}},
        "4": {"class_type": "EmptyLatentImage", "inputs": {"width": width, "height": height, "batch_size": 1}},
        "5": {"class_type": "KSampler", "inputs": {
            "seed": seed, "steps": steps, "cfg": cfg, "sampler_name": sampler,
            "scheduler": scheduler, "denoise": 1.0,
            "model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["4", 0]}},
        "6": {"class_type": "VAEDecode", "inputs": {"samples": ["5", 0], "vae": ["1", 2]}},
        "7": {"class_type": "SaveImage", "inputs": {"filename_prefix": out_prefix, "images": ["6", 0]}},
    }


def hunyuan3d_shape(image_name, ckpt="hunyuan3d-dit-v2_fp16.safetensors",
                    seed=0, steps=20, cfg=5.5, resolution=3072,
                    octree_resolution=256, num_chunks=8000, voxel_threshold=0.6,
                    out_prefix="mesh/chimera"):
    """Hunyuan3D-2 SHAPE-ONLY image->3D (native ComfyUI nodes). Writes .glb to output/<out_prefix>."""
    return {
        "54": {"class_type": "ImageOnlyCheckpointLoader", "inputs": {"ckpt_name": ckpt}},
        "70": {"class_type": "ModelSamplingAuraFlow", "inputs": {"shift": 1.0, "model": ["54", 0]}},
        "56": {"class_type": "LoadImage", "inputs": {"image": image_name}},
        "51": {"class_type": "CLIPVisionEncode", "inputs": {"crop": "none", "clip_vision": ["54", 1], "image": ["56", 0]}},
        "80": {"class_type": "Hunyuan3Dv2Conditioning", "inputs": {"clip_vision_output": ["51", 0]}},
        "66": {"class_type": "EmptyLatentHunyuan3Dv2", "inputs": {"resolution": resolution, "batch_size": 1}},
        "3":  {"class_type": "KSampler", "inputs": {
            "seed": seed, "steps": steps, "cfg": cfg, "sampler_name": "euler",
            "scheduler": "normal", "denoise": 1.0,
            "model": ["70", 0], "positive": ["80", 0], "negative": ["80", 1], "latent_image": ["66", 0]}},
        "61": {"class_type": "VAEDecodeHunyuan3D", "inputs": {
            "num_chunks": num_chunks, "octree_resolution": octree_resolution,
            "samples": ["3", 0], "vae": ["54", 2]}},
        "81": {"class_type": "VoxelToMesh", "inputs": {"algorithm": "surface net", "threshold": voxel_threshold, "voxel": ["61", 0]}},
        "82": {"class_type": "SaveGLB", "inputs": {"filename_prefix": out_prefix, "mesh": ["81", 0]}},
    }


def sdxl_seamless_terrain(prompt, negative, ckpt="sd_xl_base_1.0.safetensors",
                          seed=0, steps=30, cfg=7.0, size=1024, out_prefix="terrain/chimera"):
    """Tileable terrain albedo via SDXL + seamless-tiling node (circular padding). Node name
    reconciled at runtime against /object_info (custom node)."""
    wf = sdxl_concept(prompt, negative, ckpt=ckpt, seed=seed, steps=steps, cfg=cfg,
                      width=size, height=size, out_prefix=out_prefix)
    # Insert the seamless-tiling toggle on the model (exact class_type confirmed at runtime).
    wf["10"] = {"class_type": "SeamlessTile", "inputs": {"model": ["1", 0], "tiling": "enable", "copy_vae": "enable"}}
    wf["5"]["inputs"]["model"] = ["10", 0]
    return wf
