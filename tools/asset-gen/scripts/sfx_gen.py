# -*- coding: utf-8 -*-
"""
Generate the 7 Chimera game SFX with the EXISTING (TabletopMagic) Stable Audio 3 setup, using its
proven recipe. RUN WITH THAT REPO'S VENV PYTHON (it has stable_audio_3 + torch cu126):

  "D:\\Projects\\TabletopMagic\\TabletopMagic\\tools\\bird-audio-gen\\stable-audio-3\\.venv\\Scripts\\python.exe" sfx_gen.py

Proven recipe (HANDOFF.md): model='medium-base', steps=50, cfg_scale=7.0, rescale_cfg=False, seed=42,
NO negative_prompt (causes buzzing). Output runs hot -> peak-normalize -> ffmpeg mono .ogg.
Tonal cues (ui_click, training_complete) use cfg 4-5 to avoid saturation.
Run AFTER the GLB batch (shares the 12GB GPU — don't run both at once).
"""
import sys, os, subprocess

SA_DIR = r"D:\Projects\TabletopMagic\TabletopMagic\tools\bird-audio-gen\stable-audio-3"
FFMPEG = r"C:\Users\MD_Ki\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"
OUT_DIR = r"D:\Projects\Project_Chimera\godot\resources\audio\sfx"
WORK = r"D:\tools\asset-gen-work\sfx"
sys.path.insert(0, SA_DIR)
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(WORK, exist_ok=True)

# (filename, prompt, duration_s, cfg)  — concrete sound-words, "dry/close", no instrument metaphors, no negative
SFX = [
    ("melee_hit.ogg",        "a single sharp metallic sword-on-shield clash, short dry percussive impact, close-mic'd, quiet background", 1.0, 7.0),
    ("ranged_hit.ogg",       "a single arrow thudding into wood and flesh, a short whoosh then a dull impact, dry, close, quiet background", 1.0, 7.0),
    ("explosion.ogg",        "a single punchy mid-sized explosion, a debris burst with a tight low thump, no long tail, dry, close", 2.0, 7.0),
    ("unit_killed.ogg",      "a short armored body collapsing, metal clatter and a low grunt, one-shot, dry, close, quiet background", 1.5, 7.0),
    ("building_placed.ogg",  "a heavy structure settling onto the ground, a deep wood-and-stone thud with a brief creak, dry, close", 1.5, 7.0),
    ("training_complete.ogg","a short bright two-note rising confirmation tone, clean game UI cue, dry, no reverb, quiet background", 1.2, 4.5),
    ("ui_click.ogg",         "a single crisp short UI button click, a tight dry tick, no reverb, quiet background", 0.6, 4.5),
]

import torch, torchaudio
from stable_audio_3 import StableAudioModel

print("Loading Stable Audio 3 medium-base...", flush=True)
model = StableAudioModel.from_pretrained("medium-base")
sample_size = model.model_config["sample_size"]
sr = model.model.sample_rate

def gen(prompt, duration, cfg):
    kw = dict(prompt=prompt, duration=duration, steps=50, cfg_scale=cfg, seed=42,
              batch_size=1, sample_size=sample_size)
    try:
        return model.generate(rescale_cfg=False, **kw)
    except TypeError:
        return model.generate(**kw)

for fn, prompt, dur, cfg in SFX:
    print(f">>> {fn}  (cfg {cfg}, {dur}s)", flush=True)
    audio = gen(prompt, dur, cfg)
    a0 = audio[0].cpu().float()
    peak = a0.abs().max().clamp(min=1e-6)
    a0 = (a0 / peak) * 0.89  # peak-normalize to ~ -1 dBFS
    wav = os.path.join(WORK, fn.replace(".ogg", ".wav"))
    torchaudio.save(wav, a0, sr)
    ogg = os.path.join(OUT_DIR, fn)
    subprocess.run([FFMPEG, "-y", "-i", wav,
                    "-af", "silenceremove=start_periods=1:start_silence=0.02:start_threshold=-50dB:"
                           "stop_periods=1:stop_silence=0.05:stop_threshold=-50dB",
                    "-ac", "1", "-c:a", "libvorbis", "-q:a", "4", ogg],
                   check=True, capture_output=True)
    print(f"    -> {ogg}", flush=True)

print(f"DONE: 7 SFX -> {OUT_DIR}", flush=True)
