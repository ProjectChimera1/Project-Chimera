"""Independent Python reference of the scatter integer math (plan C scatter 3.3, task S2): Mix32, the candidate key, floor division, the value
noise on a world lattice (the surface and gradient are checked in C++ against SampleSurface instead, Chimera.Terrain.Scatter.
SurfaceQ16VsSampleSurface). test_scatter_constants.py regenerates the golden file from this and diffs it; the C++
test Chimera.Terrain.Scatter.MathGolden reads the checked-in file and compares what TerrainScatterMath.cpp computes. Two implementations of the
same documented formulas, so a disagreement is a bug on one side.

Usage: python scatter_math_ref.py --write    rewrite Scripts/scatter/scatter_math_golden.json
       python scatter_math_ref.py --check    exit 1 when the checked-in file differs
All integer arithmetic; Python's >> on negative ints is the same arithmetic (floor) shift as the C++ side.
"""
import json
import os
import sys

T = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GOLDEN = os.path.join(T, "Scripts", "scatter", "scatter_math_golden.json")
M32 = 0xFFFFFFFF


def mix32(x):
    x &= M32
    x ^= x >> 16
    x = (x * 0x7FEB352D) & M32
    x ^= x >> 15
    x = (x * 0x846CA68B) & M32
    x ^= x >> 16
    return x


def key(seed, stream, ix, iy, slot):
    """h0 of a candidate: Mix32 chain (seed ^ Mix32(stream)), then ^ ix, ^ iy, ^ slot (ix, iy as uint32 two's complement)."""
    h = mix32((seed & M32) ^ mix32(stream))
    h = mix32(h ^ (ix & M32))
    h = mix32(h ^ (iy & M32))
    h = mix32(h ^ (slot & M32))
    return h


def floor_div(a, b):
    return a // b  # Python floors


def fade(t):
    """Smoothstep in Q16: a = t*t >> 16; (a * (196608 - 2t)) >> 16."""
    a = (t * t) >> 16
    return (a * (196608 - 2 * t)) >> 16


def corner(stream_seed, ix, iy):
    h = mix32(mix32((stream_seed & M32) ^ (ix & M32)) ^ (iy & M32))
    return h >> 16


def value_noise_q16(stream_seed, xq, yq, period_q16):
    ix = floor_div(xq, period_q16)
    iy = floor_div(yq, period_q16)
    tx = ((xq - ix * period_q16) << 16) // period_q16
    ty = ((yq - iy * period_q16) << 16) // period_q16
    sx = fade(tx)
    sy = fade(ty)
    c00 = corner(stream_seed, ix, iy)
    c10 = corner(stream_seed, ix + 1, iy)
    c01 = corner(stream_seed, ix, iy + 1)
    c11 = corner(stream_seed, ix + 1, iy + 1)
    a = c00 + (((c10 - c00) * sx) >> 16)
    b = c01 + (((c11 - c01) * sx) >> 16)
    return a + (((b - a) * sy) >> 16)


def golden():
    """The golden cases: fixed, spread over signs and magnitudes, no randomness from outside."""
    keys = []
    s = 0x12345678
    for i in range(48):
        s = mix32(s + i)
        seed = s
        stream = 11 + (i % 9)
        ix = ((s >> 3) % 2049) - 1024
        iy = ((s >> 13) % 2049) - 1024
        slot = i % 3
        keys.append([seed, stream, ix, iy, slot, key(seed, stream, ix, iy, slot)])
    noise = []
    for i in range(64):
        s = mix32(s + 77 + i)
        seed = s
        xq = ((s % 40001) - 20000) * 1000          # about +-20,000 m * 1000 / 65536 -> +-305 m
        s = mix32(s)
        yq = ((s % 40001) - 20000) * 1000
        period = [7 * 65536, 18 * 65536, 37 * 65536, 60 * 65536, 96 * 65536, 120 * 65536][i % 6]
        noise.append([seed, xq, yq, period, value_noise_q16(seed, xq, yq, period)])
    floors = [[a, b, floor_div(a, b)] for a, b in [(7, 2), (-7, 2), (0, 5), (-1, 5), (-5, 5), (65535, 65536), (-65535, 65536), (-65536, 65536), (-65537, 65536), (123456789, 4096), (-123456789, 4096)]]
    return {"_doc": "Golden cases of the scatter integer math, written by Tools/scatter_math_ref.py. key = [seed, stream, ix, iy, slot, h0]; "
                    "noise = [stream_seed, xq, yq, period_q16, value]; floor_div = [a, b, a // b]. The C++ test MathGolden compares TerrainScatterMath.cpp.",
            "key": keys, "noise": noise, "floor_div": floors}


def render(g):
    return json.dumps(g, separators=(",", ":"), sort_keys=False).replace("],[", "],\n[").replace('"key":[', '"key":\n[').replace(',"noise":[', ',\n"noise":\n[').replace(',"floor_div":[', ',\n"floor_div":\n[') + "\n"


def main():
    text = render(golden())
    if "--check" in sys.argv:
        try:
            with open(GOLDEN, "r", encoding="utf-8", newline="") as f:
                cur = f.read()
        except OSError:
            print("MISSING " + GOLDEN)
            return 1
        if cur.replace("\r\n", "\n") != text:
            print("GOLDEN STALE")
            return 1
        print("GOLDEN OK")
        return 0
    with open(GOLDEN, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote " + GOLDEN)
    return 0


if __name__ == "__main__":
    sys.exit(main())
