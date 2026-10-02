#!/usr/bin/env python
"""L0 procedural scatter meshes (plan-c-scatter.md 3.6, task S1): project-original, numpy only, a small glTF-binary writer.

Reads Scripts/scatter/meshes.json (slots and species) and writes ScatterSrc/L0/<Name>.glb plus ScatterSrc/L0/report.json:
  grass clumps   GrassT0, GrassT1   opaque tapered blades, 36-48 triangles (the final grass, plan 1.3)
                 GrassT0_LOD1/2, GrassT1_LOD1/2: the grass _L A/B LOD chain (LOD_CHAINS; report.json "lod_chains", not "meshes")
  tussock        Tussock            a dome of blades, <= 200
  flower         Flower             stems + 6-triangle heads + basal leaves, <= 64 (head mask in vertex colour B)
  fern           Fern               arching fronds of paired pinnae, <= 300
  shrub          ShrubA, ShrubB     lumpy leaf-mass blobs on short stems, 600-1,500
  broadleaf      TreeBroadA, B      trunk, branches and 7-14 displaced canopy lobes, <= 4,000, at the slot's nominal height (11 m)
  conifer        TreeConiferA, B    trunk and stacked noisy drooping cones, <= 3,000, nominal 12 m
  rock           RockA, RockB       displaced geodesic spheres with a flattened base, <= 800
Conventions: metres, +Z up, pivot at the base centre (min z = 0); written as glTF +Y up with (x, z, -y), the Blender exporter's mapping,
which Interchange maps back (GLTFCore ConversionUtilities.h ConvertVec3: UE = (gx, gz, gy) then the handedness flip). Vertex colour:
R = AO, G = part variation, B = part mask, A = height fraction (meshes.json "vertex_colour"). Materials carry names and a base colour
only; S3 replaces them (plan 3.6 Import).
Every random number comes from Mix32 (lowbias32), the same function the C++ generator uses (golden: Scripts/scatter/mix32_golden.json,
written with --golden; test_scatter_tools.py checks both). No clock, no unordered iteration: re-runs are byte-identical.
Usage: python make_scatter_meshes.py [--out DIR] [--golden]   -> MESHES_OK species=<n> files=<m> tris_ok lod_files=<k>
"""
import argparse
import hashlib
import json
import math
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scatter_glb  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
CONFIG = os.path.join(T, "Scripts", "scatter", "meshes.json")
GOLDEN = os.path.join(T, "Scripts", "scatter", "mix32_golden.json")
OUT = os.path.join(os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc")), "L0")
M32 = 0xFFFFFFFF


# ---------------------------------------------------------------- random (Mix32 = lowbias32, C. Wellons) ---------------------------
def mix32(x):
    """lowbias32 on a Python int (uint32 semantics). The C++ twin is TerrainScatterMath Mix32 (plan 3.1)."""
    x &= M32
    x ^= x >> 16
    x = (x * 0x7FEB352D) & M32
    x ^= x >> 15
    x = (x * 0x846CA68B) & M32
    x ^= x >> 16
    return x


def mix32v(x):
    """lowbias32 on a numpy uint32 array (wrapping multiply)."""
    x = np.asarray(x, dtype=np.uint32).copy()
    x ^= x >> np.uint32(16)
    x *= np.uint32(0x7FEB352D)
    x ^= x >> np.uint32(15)
    x *= np.uint32(0x846CA68B)
    x ^= x >> np.uint32(16)
    return x


class Rng:
    """Counter-based stream: draw n is Mix32(base ^ Mix32(n)), base = Mix32(seed ^ Mix32(stream))."""

    def __init__(self, seed, stream):
        self.base = mix32((seed & M32) ^ mix32(stream))
        self.n = 0

    def u32(self):
        self.n += 1
        return mix32(self.base ^ mix32(self.n))

    def unit(self):
        return self.u32() / 4294967296.0

    def uni(self, lo, hi):
        return lo + (hi - lo) * self.unit()

    def pick(self, rng2):
        return self.uni(rng2[0], rng2[1])


def value_noise3(p, seed, period):
    """Trilinear value noise in [-1, 1] on a lattice of the given period (m); hash = Mix32 chain of the integer corner."""
    q = p / period
    i0 = np.floor(q).astype(np.int64)
    f = q - i0
    f = f * f * (3.0 - 2.0 * f)
    out = np.zeros(len(p))
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                ix = (i0[:, 0] + dx).astype(np.uint32)
                iy = (i0[:, 1] + dy).astype(np.uint32)
                iz = (i0[:, 2] + dz).astype(np.uint32)
                h = mix32v(mix32v(mix32v(np.uint32(seed) ^ ix) ^ iy) ^ iz)
                v = h.astype(np.float64) / 4294967296.0 * 2.0 - 1.0
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                out += w * v
    return out


# ---------------------------------------------------------------- mesh container --------------------------------------------------
class Part:
    def __init__(self, material, pos, tri, uv=None, var=0.0, mask=0.0, ao=None):
        self.material = material
        self.pos = np.asarray(pos, dtype=np.float64)
        self.tri = np.asarray(tri, dtype=np.int64).reshape(-1, 3)
        self.uv = np.zeros((len(self.pos), 2)) if uv is None else np.asarray(uv, dtype=np.float64)
        self.var = np.full(len(self.pos), var) if np.isscalar(var) else np.asarray(var, dtype=np.float64)
        self.mask = np.full(len(self.pos), mask) if np.isscalar(mask) else np.asarray(mask, dtype=np.float64)
        self.ao = np.ones(len(self.pos)) if ao is None else np.asarray(ao, dtype=np.float64)


class Mesh:
    def __init__(self, name):
        self.name = name
        self.parts = []
        self.materials = {}  # name -> (rgb, roughness, double_sided)

    def add(self, part):
        self.parts.append(part)

    def material(self, name, rgb, roughness, double_sided):
        self.materials[name] = (list(rgb), roughness, double_sided)

    def triangles(self):
        return sum(len(p.tri) for p in self.parts)


def smooth_normals(pos, tri):
    n = np.zeros_like(pos)
    a, b, c = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
    fn = np.cross(b - a, c - a)
    for k in range(3):
        np.add.at(n, tri[:, k], fn)
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    ln[ln == 0] = 1.0
    n = n / ln
    bad = np.linalg.norm(n, axis=1) < 0.5
    n[bad] = (0.0, 0.0, 1.0)
    return n


# ---------------------------------------------------------------- primitives ------------------------------------------------------
def blade(base, yaw, lean, height, width, bend, rings, twist=0.0, tip=True):
    """A tapered blade or stem ribbon from base along a quadratic bend, leaning by lean (rad) toward yaw.
    rings cross-sections of 2 vertices (+ one tip vertex). Triangles: 2*(rings-1) + (1 if tip)."""
    d = np.array([math.cos(yaw), math.sin(yaw), 0.0])
    side = np.array([-math.sin(yaw + twist), math.cos(yaw + twist), 0.0])
    up = np.array([0.0, 0.0, 1.0])
    n_pts = rings + (1 if tip else 0)
    ts = np.linspace(0.0, 1.0, n_pts) if tip else np.linspace(0.0, 1.0, rings)
    pos, uv, tvals = [], [], []
    for i in range(rings):
        t = ts[i]
        ang = lean + bend * t * t * 2.0  # the bend increases the lean toward the tip
        centre = base + height * (up * (t - 0.25 * bend * t * t) * math.cos(lean) + d * (t * math.sin(ang)))
        w = width * (1.0 - 0.85 * t) ** 0.8 * 0.5
        pos += [centre - side * w, centre + side * w]
        uv += [(0.0, 1 - t), (1.0, 1 - t)]
        tvals += [t, t]
    tri = []
    for i in range(rings - 1):
        a, b, c, e = 2 * i, 2 * i + 1, 2 * i + 2, 2 * i + 3
        tri += [(a, b, e), (a, e, c)]
    if tip:
        t = 1.0
        ang = lean + bend * 2.0
        centre = base + height * (up * (t - 0.25 * bend) * math.cos(lean) + d * (t * math.sin(ang)))
        pos.append(centre)
        uv.append((0.5, 0.0))
        tvals.append(1.0)
        k = len(pos) - 1
        tri.append((2 * (rings - 1), 2 * (rings - 1) + 1, k))
    return np.array(pos), np.array(tri), np.array(uv), np.array(tvals)


def geodesic(freq):
    """Unit geodesic sphere from an icosahedron, each face split freq x freq: 20 * freq^2 triangles."""
    p = (1 + 5 ** 0.5) / 2
    v = np.array([(-1, p, 0), (1, p, 0), (-1, -p, 0), (1, -p, 0), (0, -1, p), (0, 1, p), (0, -1, -p), (0, 1, -p),
                  (p, 0, -1), (p, 0, 1), (-p, 0, -1), (-p, 0, 1)], dtype=np.float64)
    f = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
         (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)]
    verts, index, tris = [], {}, []

    def vid(fi, i, j):
        a, b, c = f[fi]
        # shared vertices by their exact barycentric position on the shared edge or corner (integer weights)
        wa, wb, wc = freq - i - j, i, j
        kk = tuple(sorted(((a, wa), (b, wb), (c, wc))))
        kk = tuple(x for x in kk if x[1] != 0)
        if kk not in index:
            pnt = (v[a] * wa + v[b] * wb + v[c] * wc) / freq
            index[kk] = len(verts)
            verts.append(pnt / np.linalg.norm(pnt))
        return index[kk]

    for fi in range(20):
        for i in range(freq):
            for j in range(freq - i):
                tris.append((vid(fi, i, j), vid(fi, i + 1, j), vid(fi, i, j + 1)))
                if i + j < freq - 1:
                    tris.append((vid(fi, i + 1, j), vid(fi, i + 1, j + 1), vid(fi, i, j + 1)))
    return np.array(verts), np.array(tris)


def tube(points, radii, sides, cap=False):
    """A tapered tube through points (k x 3) with radii (k): sides*2*(k-1) triangles (+ a top fan if cap)."""
    points = np.asarray(points, dtype=np.float64)
    k = len(points)
    pos, uv = [], []
    for i in range(k):
        t = points[min(i + 1, k - 1)] - points[max(i - 1, 0)]
        t = t / np.linalg.norm(t)
        ref = np.array([0.0, 0.0, 1.0]) if abs(t[2]) < 0.9 else np.array([1.0, 0.0, 0.0])
        u = np.cross(t, ref)
        u /= np.linalg.norm(u)
        w = np.cross(t, u)
        for s in range(sides):
            a = 2 * math.pi * s / sides
            pos.append(points[i] + radii[i] * (math.cos(a) * u + math.sin(a) * w))
            uv.append((s / sides, i / max(k - 1, 1)))
    tri = []
    for i in range(k - 1):
        for s in range(sides):
            a = i * sides + s
            b = i * sides + (s + 1) % sides
            c = a + sides
            e = b + sides
            tri += [(a, b, e), (a, e, c)]
    if cap:
        pos.append(points[-1])
        uv.append((0.5, 1.0))
        top = len(pos) - 1
        for s in range(sides):
            tri.append(((k - 1) * sides + s, (k - 1) * sides + (s + 1) % sides, top))
    return np.array(pos), np.array(tri), np.array(uv)


def sphere_uv(d):
    return np.stack([np.arctan2(d[:, 1], d[:, 0]) / (2 * math.pi) + 0.5, np.arccos(np.clip(d[:, 2], -1, 1)) / math.pi], axis=1)


# ---------------------------------------------------------------- species ---------------------------------------------------------
def make_grass(sp, rng, mesh, tussock=False):
    mesh.material("M_Blade", sp["tip"], sp["roughness"], True)
    n = sp["blades"]
    golden = math.pi * (3 - 5 ** 0.5)
    for i in range(n):
        # base on a sunflower disc (even, no clumping), jittered
        r = sp["radius"] * math.sqrt((i + 0.5) / n) * rng.uni(0.7, 1.1)
        a = i * golden + rng.uni(-0.4, 0.4)
        base = np.array([r * math.cos(a), r * math.sin(a), 0.0])
        rad = r / sp["radius"]
        if tussock:  # outer blades lean out more and are shorter: a dome
            lean = math.radians(sp["lean_deg"][0] + (sp["lean_deg"][1] - sp["lean_deg"][0]) * min(1.0, rad ** 1.2) * rng.uni(0.8, 1.1))
            h = rng.pick(sp["height"]) * (1.0 - 0.35 * rad)
        else:
            lean = math.radians(rng.pick(sp["lean_deg"]))
            h = rng.pick(sp["height"])
        yaw = a + rng.uni(-0.5, 0.5)
        p, t, uv, tv = blade(base, yaw, lean, h, rng.pick(sp["width"]), rng.pick(sp["bend"]), sp["rings"], twist=rng.uni(-0.6, 0.6))
        ao = 0.30 + 0.70 * tv ** 0.7
        if tussock:
            ao *= 0.75 + 0.25 * rad
        mesh.add(Part("M_Blade", p, t, uv, var=rng.unit(), mask=1.0, ao=ao))


def make_flower(sp, rng, mesh):
    mesh.material("M_FlowerStem", sp["stem"], sp["roughness"], True)
    mesh.material("M_FlowerHead", sp["head"], sp["roughness"], True)
    n = sp["stems"]
    for i in range(n):
        a = 2 * math.pi * i / n + rng.uni(-0.5, 0.5)
        r = sp["radius"] * rng.uni(0.2, 1.0)
        base = np.array([r * math.cos(a), r * math.sin(a), 0.0])
        h = rng.pick(sp["height"])
        lean = math.radians(rng.pick(sp["lean_deg"]))
        p, t, uv, tv = blade(base, a, lean, h, 0.006, 0.05, 3, tip=False)
        mesh.add(Part("M_FlowerStem", p, t, uv, var=rng.unit(), mask=0.0, ao=0.45 + 0.55 * tv))
        top = (p[-1] + p[-2]) / 2
        hr = rng.pick(sp["head_radius"])
        tilt = rng.uni(0.0, 0.5)
        tdir = np.array([math.cos(a) * math.sin(tilt), math.sin(a) * math.sin(tilt), math.cos(tilt)])
        ref = np.array([1.0, 0.0, 0.0])
        u = np.cross(tdir, ref)
        u /= np.linalg.norm(u)
        w = np.cross(tdir, u)
        ring = [top + tdir * 0.004]
        for s in range(6):
            ang = 2 * math.pi * s / 6 + rng.uni(-0.2, 0.2)
            rr = hr * rng.uni(0.8, 1.15)
            ring.append(top + rr * (math.cos(ang) * u + math.sin(ang) * w) - tdir * 0.004)
        tri = [(0, 1 + s, 1 + (s + 1) % 6) for s in range(6)]
        huv = [(0.5, 0.5)] + [(0.5 + 0.5 * math.cos(2 * math.pi * s / 6), 0.5 + 0.5 * math.sin(2 * math.pi * s / 6)) for s in range(6)]
        mesh.add(Part("M_FlowerHead", np.array(ring), np.array(tri), np.array(huv), var=rng.unit(), mask=1.0, ao=np.ones(7)))
    for i in range(sp["leaves"]):
        a = 2 * math.pi * (i + 0.5) / sp["leaves"] + rng.uni(-0.4, 0.4)
        p, t, uv, tv = blade(np.zeros(3), a, math.radians(rng.uni(45, 70)), rng.pick(sp["leaf_len"]), 0.022, 0.25, 2)
        mesh.add(Part("M_FlowerStem", p, t, uv, var=rng.unit(), mask=0.0, ao=0.4 + 0.5 * tv))


def make_fern(sp, rng, mesh):
    mesh.material("M_Frond", sp["colour"], sp["roughness"], True)
    n, segs = sp["fronds"], sp["segments"]
    for i in range(n):
        yaw = 2 * math.pi * i / n + rng.uni(-0.3, 0.3)
        L = rng.pick(sp["length"])
        rise = math.radians(rng.pick(sp["rise_deg"]))
        droop = rng.pick(sp["droop"])
        d = np.array([math.cos(yaw), math.sin(yaw), 0.0])
        side = np.array([-math.sin(yaw), math.cos(yaw), 0.0])
        # rachis: arching curve (rises, then droops)
        ts = np.linspace(0, 1, segs + 1)
        rach = np.array([d * (L * t * math.cos(rise)) + np.array([0, 0, L * (t * math.sin(rise) - droop * t * t)]) for t in ts])
        rach[:, 2] -= min(0.0, rach[:, 2].min())
        pos, tri, uv, tv = [], [], [], []
        pl = rng.pick(sp["pinna"])
        for k in range(segs):
            t0, t1 = ts[k], ts[k + 1]
            a0, a1 = rach[k], rach[k + 1]
            # envelope sin(pi (0.15 + 0.8 t0)) stays > 0 up to the last segment (it used to reach sin(pi) = 0 there: 32 zero-area triangles)
            length = pl * math.sin(math.pi * (0.15 + 0.8 * t0)) * (1.0 - 0.6 * t0)
            tang = a1 - a0
            tang /= np.linalg.norm(tang)
            for sgn in (-1, 1):
                outv = side * sgn * length + tang * length * 0.45 - np.array([0, 0, length * 0.25])
                b = len(pos)
                pos += [a0, a1, a1 + outv * 0.7, a0 + outv]
                uv += [(0.5, t0), (0.5, t1), (0.5 + 0.5 * sgn, t1), (0.5 + 0.5 * sgn, t0)]
                tv += [t0, t1, t1, t0]
                tri += [(b, b + 1, b + 2), (b, b + 2, b + 3)] if sgn > 0 else [(b, b + 2, b + 1), (b, b + 3, b + 2)]
        b = len(pos)  # tip leaflet: a real triangle (its base spans the rachis tip sideways; it used to be three collinear points)
        tip_w = pl * 0.18
        pos += [rach[-1] - side * tip_w, rach[-1] + (rach[-1] - rach[-2]) * 0.6, rach[-1] + side * tip_w]
        uv += [(0.5, 0.9), (0.5, 1.0), (0.6, 1.0)]
        tv += [1.0, 1.0, 1.0]
        tri.append((b, b + 2, b + 1))
        tv = np.array(tv)
        mesh.add(Part("M_Frond", np.array(pos), np.array(tri), np.array(uv), var=rng.unit(), mask=1.0, ao=0.35 + 0.65 * np.minimum(1, tv * 1.6)))


def displaced_lobe(centre, radius, freq, rng, noise, noise_m, seed, squash=1.0):
    d, tri = geodesic(freq)
    p = centre + d * radius * np.array([1.0, 1.0, squash])
    n1 = value_noise3(p, seed, noise_m)
    n2 = value_noise3(p, seed ^ 0x9E3779B9, noise_m * 0.45)
    p = p + d * radius * (noise * n1 + 0.5 * noise * n2)[:, None]
    return p, tri, sphere_uv(d), d


def make_shrub(sp, rng, mesh, seed):
    mesh.material("M_ShrubLeaf", sp["leaf"], sp["roughness"], False)
    mesh.material("M_ShrubBark", sp["bark"], sp["roughness"], False)
    sx, sz = sp["spread"]
    for i in range(sp["stems"]):
        a = 2 * math.pi * i / sp["stems"] + rng.uni(-0.4, 0.4)
        top = np.array([math.cos(a) * sx * 0.35, math.sin(a) * sx * 0.35, sz * 0.9])
        pts = [np.zeros(3) + np.array([math.cos(a), math.sin(a), 0]) * 0.03, top * 0.5 + np.array([0, 0, 0.02]), top]
        p, t, uv = tube(pts, [0.025, 0.018, 0.010], 6)
        mesh.add(Part("M_ShrubBark", p, t, uv, var=rng.unit(), mask=0.0, ao=0.35 + 0.3 * p[:, 2] / max(sz, 1e-6)))
    n = sp["lobes"]
    golden = math.pi * (3 - 5 ** 0.5)
    for i in range(n):
        # lobes on a squashed hemisphere cluster: rings outward and up, never below z = r * 0.5
        u = (i + 0.5) / n
        a = i * golden + rng.uni(-0.3, 0.3)
        rr = sx * math.sqrt(u) * rng.uni(0.75, 1.05)
        r = rng.pick(sp["lobe_r"])
        c = np.array([rr * math.cos(a), rr * math.sin(a), r * 0.75 + sz * (1.0 - u) * rng.uni(0.6, 1.0)])
        p, t, uv, d = displaced_lobe(c, r, sp["lobe_freq"], rng, sp["noise"], sp["noise_m"], (seed + i) & M32, squash=0.85)
        p[:, 2] = np.maximum(p[:, 2], 0.02)
        ao = np.clip(0.40 + 0.35 * d[:, 2] + 0.25 * np.clip(np.linalg.norm(p[:, :2], axis=1) / (sx + 0.4), 0, 1), 0.2, 1.0)
        mesh.add(Part("M_ShrubLeaf", p, t, uv, var=rng.unit(), mask=1.0, ao=ao))


def make_broadleaf(sp, rng, mesh, seed):
    """Unit-height tree (scaled to the nominal height after). Trunk to the crown base, branches to the lobes, lobes on a crown ellipsoid."""
    mesh.material("M_Bark", sp["bark"], sp["roughness"], False)
    mesh.material("M_Canopy", sp["leaf"], sp["roughness"], False)
    cb, rx, rz = sp["crown_base"], sp["crown_rx"], sp["crown_rz"]
    cz = cb + rz * 0.9
    tr = sp["trunk_r"]
    lean = np.array([rng.uni(-0.02, 0.02), rng.uni(-0.02, 0.02), 0.0])
    ts = np.linspace(0, 1, 6)
    trunk = [np.array([0, 0, 0.0]) + lean * t * t + np.array([0, 0, (cz + rz * 0.2) * t]) for t in ts]
    radii = [tr * (1.45 if i == 0 else 1.0) * (1 - 0.55 * ts[i]) for i in range(6)]
    p, t, uv = tube(trunk, radii, 8)
    mesh.add(Part("M_Bark", p, t, uv, var=0.5, mask=0.0, ao=0.45 + 0.35 * p[:, 2] / (cz + rz)))
    golden = math.pi * (3 - 5 ** 0.5)
    n = sp["lobes"]
    lobes = []
    for i in range(n):
        # Fibonacci points on the upper 80 % of the crown ellipsoid, pulled 35 % toward the centre so the lobes overlap into a mass
        zf = 1.0 - 1.8 * (i + 0.5) / n
        rad = math.sqrt(max(0.0, 1 - zf * zf))
        a = i * golden + rng.uni(-0.25, 0.25)
        dvec = np.array([rad * math.cos(a), rad * math.sin(a), zf])
        c = np.array([0, 0, cz]) + dvec * np.array([rx, rx, rz]) * rng.uni(0.55, 0.72)
        lobes.append((c, rng.pick(sp["lobe_r"])))
    for bi in range(sp["branches"]):
        c, _ = lobes[(bi * 7 + 3) % n]
        start = np.array([0, 0, cb + (cz - cb) * rng.uni(0.0, 0.6)])
        mid = (start + c) / 2 + np.array([0, 0, 0.03])
        p, t, uv = tube([start, mid, c], [tr * 0.55, tr * 0.38, tr * 0.22], 6)
        mesh.add(Part("M_Bark", p, t, uv, var=rng.unit(), mask=0.0, ao=np.full(len(p), 0.5)))
    for i, (c, r) in enumerate(lobes):
        p, t, uv, d = displaced_lobe(c, r, sp["lobe_freq"], rng, sp["noise"], sp["noise_m"] / 11.0, (seed + 31 * i) & M32, squash=0.9)
        outward = (p - np.array([0, 0, cz])) / np.array([rx, rx, rz])
        o = np.clip(np.linalg.norm(outward, axis=1), 0, 1.2)
        ao = np.clip(0.25 + 0.55 * o + 0.20 * d[:, 2], 0.15, 1.0)
        mesh.add(Part("M_Canopy", p, t, uv, var=rng.unit(), mask=1.0, ao=ao))


def make_conifer(sp, rng, mesh):
    mesh.material("M_Bark", sp["bark"], sp["roughness"], False)
    mesh.material("M_Needles", sp["leaf"], sp["roughness"], False)
    tr = sp["trunk_r"]
    ts = np.linspace(0, 1, 9)
    p, t, uv = tube([np.array([0, 0, 0.97 * s]) for s in ts], [tr * (1.4 if i == 0 else 1.0) * (1 - 0.9 * ts[i]) for i in range(9)], 6)
    mesh.add(Part("M_Bark", p, t, uv, var=0.5, mask=0.0, ao=np.full(len(p), 0.45)))
    nt, ns = sp["tiers"], sp["sectors"]
    base, R = sp["base"], sp["radius"]
    for k in range(nt):
        f = k / (nt - 1)  # 0 bottom tier, 1 top tier
        top = base + (1.0 - base) * (0.10 + 0.90 * f) ** 0.92
        rim_r = R * (1.0 - 0.88 * f) ** 1.05 * rng.uni(0.9, 1.08)
        drop = (1.0 - base) / nt * (2.6 - 0.8 * f)
        apex = np.array([0, 0, min(1.0, top)])
        rows = [0.35, 0.7, 1.0]
        pos = [apex]
        uvs = [(0.5, 0.0)]
        jit = [rng.uni(1 - sp["jitter"], 1 + sp["jitter"]) for _ in range(ns)]
        for ri, rf in enumerate(rows):
            for s in range(ns):
                a = 2 * math.pi * (s + 0.5 * (k % 2)) / ns
                rr = rim_r * rf * (jit[s] if rf > 0.5 else 1.0)
                z = apex[2] - drop * rf - sp["droop"] * drop * rf * rf * jit[s]
                pos.append(np.array([rr * math.cos(a), rr * math.sin(a), z]))
                uvs.append((s / ns, rf))
        tri = [(0, 1 + s, 1 + (s + 1) % ns) for s in range(ns)]
        for ri in range(len(rows) - 1):
            for s in range(ns):
                a0 = 1 + ri * ns + s
                a1 = 1 + ri * ns + (s + 1) % ns
                tri += [(a0, a0 + ns, a1 + ns), (a0, a1 + ns, a1)]
        # underside: from the rim back in to a ring under the apex
        inner = len(pos)
        for s in range(ns):
            a = 2 * math.pi * (s + 0.5 * (k % 2)) / ns
            pos.append(np.array([rim_r * 0.25 * math.cos(a), rim_r * 0.25 * math.sin(a), apex[2] - drop * 0.9]))
            uvs.append((s / ns, 0.5))
        rim0 = 1 + (len(rows) - 1) * ns
        for s in range(ns):
            r0, r1 = rim0 + s, rim0 + (s + 1) % ns
            i0, i1 = inner + s, inner + (s + 1) % ns
            tri += [(r0, i0, i1), (r0, i1, r1)]
        pos = np.array(pos)
        rf_arr = np.array([0.0] + [rf for rf in rows for _ in range(ns)] + [0.25] * ns)
        ao = np.clip(0.35 + 0.55 * rf_arr * (0.6 + 0.4 * f) + 0.10 * f, 0.2, 1.0)
        ao[inner:] *= 0.55
        mesh.add(Part("M_Needles", pos, np.array(tri), np.array(uvs), var=rng.unit(), mask=1.0, ao=ao))


def make_rock(sp, rng, mesh, seed):
    mesh.material("M_Rock", sp["colour"], sp["roughness"], False)
    d, tri = geodesic(sp["freq"])
    p = d * np.array(sp["aspect"])
    n1 = value_noise3(p, seed, sp["noise_m"][0])
    n2 = value_noise3(p, seed ^ 0x85EBCA6B, sp["noise_m"][1])
    p = p * (1.0 + sp["noise"][0] * n1 + sp["noise"][1] * n2)[:, None]
    # flatten the base: compress everything below the flatten plane
    zmin = p[:, 2].min()
    plane = zmin + sp["flatten"] * (p[:, 2].max() - zmin)
    below = p[:, 2] < plane
    p[below, 2] = plane - (plane - p[below, 2]) * 0.15
    ao = np.clip(0.55 + 0.45 * d[:, 2], 0.3, 1.0)
    mesh.add(Part("M_Rock", p, tri, sphere_uv(d), var=rng.unit(), mask=0.0, ao=ao))


# ---------------------------------------------------------------- finalise and write ----------------------------------------------
def finalise(mesh, nominal):
    """Pivot at the base centre (min z = 0, x/y centred on the origin as built) and scale uniformly to the nominal height."""
    allp = np.concatenate([p.pos for p in mesh.parts])
    zmin, zmax = allp[:, 2].min(), allp[:, 2].max()
    s = 1.0 if nominal is None else nominal / (zmax - zmin)
    for p in mesh.parts:
        p.pos = (p.pos - np.array([0.0, 0.0, zmin])) * s
    return s


def pad4(b, fill):
    return b + fill * ((4 - len(b) % 4) % 4)


def write_glb(mesh, path, textures=None, height_ref=None):
    """glTF 2.0 binary: one node, one mesh, one primitive per material (in first-use order). Returns (bytes, stats).
    textures: optional {material: {"base": png bytes (RGBA: alpha = cut-out), "normal": png bytes (GL), "name": str}} embedded in the
    file; such a material gets alphaMode MASK (cutoff 0.5) and double sides (used by prep_polyhaven.py for the seed-head cards).
    height_ref: the height the vertex-colour A (height fraction) is taken against; None = this mesh's own top (a LOD subset passes its
    LOD0's, so a kept blade keeps exactly its LOD0 colours)."""
    textures = textures or {}
    order = []
    for p in mesh.parts:
        if p.material not in order:
            order.append(p.material)
    bin_parts, views, accessors, prims = [], [], [], []
    offset = 0
    allpos = []
    vc_sample = None

    def add_view(data, target):
        nonlocal offset
        data = pad4(data, b"\x00")
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(data), "target": target})
        bin_parts.append(data)
        offset += len(data)
        return len(views) - 1

    for mname in order:
        parts = [p for p in mesh.parts if p.material == mname]
        pos, nrm, uv, col, tri = [], [], [], [], []
        base = 0
        for p in parts:
            pos.append(p.pos)
            nrm.append(smooth_normals(p.pos, p.tri))
            uv.append(p.uv)
            col.append(np.stack([p.ao, p.var, p.mask, np.zeros(len(p.pos))], axis=1))
            tri.append(p.tri + base)
            base += len(p.pos)
        pos = np.concatenate(pos)
        nrm = np.concatenate(nrm)
        uv = np.concatenate(uv)
        col = np.concatenate(col)
        tri = np.concatenate(tri)
        allpos.append(pos)
        prims.append((mname, pos, nrm, uv, col, tri))
    allpos = np.concatenate(allpos)
    height = allpos[:, 2].max() if height_ref is None else height_ref
    prim_json = []
    for mname, pos, nrm, uv, col, tri in prims:
        col[:, 3] = np.clip(pos[:, 2] / height, 0, 1)
        g = np.stack([pos[:, 0], pos[:, 2], -pos[:, 1]], axis=1).astype(np.float32)  # Z-up -> glTF Y-up
        gn = np.stack([nrm[:, 0], nrm[:, 2], -nrm[:, 1]], axis=1).astype(np.float32)
        cb = np.clip(np.floor(col * 255.0 + 0.5), 0, 255).astype(np.uint8)
        if vc_sample is None:
            vc_sample = cb[:8].tolist()
        ib = tri.astype(np.uint16 if len(pos) < 65536 else np.uint32)
        a0 = len(accessors)
        accessors.append({"bufferView": add_view(g.tobytes(), 34962), "componentType": 5126, "count": len(g), "type": "VEC3",
                          "min": [float(x) for x in g.min(axis=0)], "max": [float(x) for x in g.max(axis=0)]})
        accessors.append({"bufferView": add_view(gn.tobytes(), 34962), "componentType": 5126, "count": len(g), "type": "VEC3"})
        accessors.append({"bufferView": add_view(uv.astype(np.float32).tobytes(), 34962), "componentType": 5126, "count": len(g), "type": "VEC2"})
        accessors.append({"bufferView": add_view(cb.tobytes(), 34962), "componentType": 5121, "normalized": True, "count": len(g), "type": "VEC4"})
        accessors.append({"bufferView": add_view(ib.reshape(-1).tobytes(), 34963), "componentType": 5123 if ib.dtype == np.uint16 else 5125,
                          "count": int(ib.size), "type": "SCALAR"})
        prim_json.append({
            "attributes": {"POSITION": a0, "NORMAL": a0 + 1, "TEXCOORD_0": a0 + 2, "COLOR_0": a0 + 3}, "indices": a0 + 4,
            "material": order.index(mname), "mode": 4})
    mats, images, texs = [], [], []
    for mname in order:
        rgb, rough, ds = mesh.materials[mname]
        mat = {"name": mname, "doubleSided": bool(ds),
               "pbrMetallicRoughness": {"baseColorFactor": [float(rgb[0]), float(rgb[1]), float(rgb[2]), 1.0], "metallicFactor": 0.0,
                                        "roughnessFactor": float(rough)}}
        tx = textures.get(mname)
        if tx:
            for kind in ("base", "normal"):
                if kind not in tx:
                    continue
                vi = add_view(tx[kind], None)
                views[vi].pop("target")
                images.append({"bufferView": vi, "mimeType": "image/png", "name": f"{tx['name']}_{'nor_gl' if kind == 'normal' else kind}"})
                texs.append({"source": len(images) - 1, "sampler": 0})
                if kind == "base":
                    mat["pbrMetallicRoughness"]["baseColorTexture"] = {"index": len(texs) - 1}
                    mat["pbrMetallicRoughness"]["baseColorFactor"] = [1.0, 1.0, 1.0, 1.0]
                else:
                    mat["normalTexture"] = {"index": len(texs) - 1}
            mat.update({"alphaMode": "MASK", "alphaCutoff": 0.5, "doubleSided": True})
        mats.append(mat)
    binb = b"".join(bin_parts)
    gltf = {"asset": {"version": "2.0", "generator": "ChimeraTerrain make_scatter_meshes.py"}, "scene": 0, "scenes": [{"nodes": [0]}],
            "nodes": [{"name": mesh.name, "mesh": 0}], "meshes": [{"name": mesh.name, "primitives": prim_json}], "materials": mats,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(binb)}]}
    if images:
        gltf.update({"images": images, "textures": texs, "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071}]})
    js = pad4(json.dumps(gltf, separators=(",", ":"), sort_keys=True).encode("utf-8"), b" ")
    total = 12 + 8 + len(js) + 8 + len(binb)
    out = struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(binb), 0x004E4942) + binb
    with open(path, "wb") as f:
        f.write(out)
    bmin = allpos.min(axis=0)
    bmax = allpos.max(axis=0)
    return out, {"vertices": int(len(allpos)), "bounds_min": [round(float(x), 5) for x in bmin], "bounds_max": [round(float(x), 5) for x in bmax],
                 "materials": order, "vc_first8_rgba8": vc_sample}


BUILDERS = {"grass": lambda sp, rng, m, s: make_grass(sp, rng, m), "tussock": lambda sp, rng, m, s: make_grass(sp, rng, m, tussock=True),
            "flower": lambda sp, rng, m, s: make_flower(sp, rng, m), "fern": lambda sp, rng, m, s: make_fern(sp, rng, m),
            "shrub": make_shrub, "broadleaf": make_broadleaf, "conifer": lambda sp, rng, m, s: make_conifer(sp, rng, m), "rock": make_rock}


# LOD chains of the grass _L A/B variant (plan 1.3 "non-Nanite LOD chain as the A/B", 3.6-3.7 "_L variant (Nanite off, 3 LODs)", task S3).
# QuadricMeshReduction cannot reduce these clumps (separate open blade strips: S3 read 40/40/40 triangles), so the generator writes the
# lower LODs itself: <Name>_LOD<i>.glb keeps ceil(blades x fraction) blades of LOD0, unchanged (positions, normals, UVs and vertex colours,
# A still the fraction of LOD0's height), chosen in ascending vertex-colour G byte, then blade index. G is the per-blade value
# M_ScatterBladeFade thins by (a blade shows while the instance fade is above its G), so a LOD keeps exactly the blades a fading instance
# keeps longest. make_scatter_assets.py imports them as LOD1 and LOD2 of <Name>_L (StaticMeshEditorSubsystem::SetLodFromStaticMesh).
LOD_CHAINS = {"GrassT0": (0.5, 0.25), "GrassT1": (0.5, 0.25)}


def g_byte(part):
    """The vertex-colour G byte a part's vertices carry (write_glb's rounding)."""
    return int(np.clip(np.floor(float(part.var[0]) * 255.0 + 0.5), 0, 255))


def lod_subset(mesh, fraction, lod):
    """A Mesh holding the ceil(n x fraction) parts (blades) with the lowest G byte (ties by part index), in their LOD0 order."""
    n = len(mesh.parts)
    k = max(1, int(math.ceil(n * fraction - 1e-9)))
    order = sorted(range(n), key=lambda i: (g_byte(mesh.parts[i]), i))
    keep = sorted(order[:k])
    sub = Mesh(f"{mesh.name}_LOD{lod}")
    sub.materials = dict(mesh.materials)
    for i in keep:
        sub.add(mesh.parts[i])
    return sub, keep


def lod_chain_reduces(triangles):
    """True when a LOD chain's triangle counts strictly decrease LOD by LOD (and it has at least two LODs)."""
    return len(triangles) >= 2 and all(a > b for a, b in zip(triangles, triangles[1:]))


def write_golden(path, seed):
    ins = [0, 1, 2, 3, 0x7F, 0x80, 0xFF, 0x100, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF, 0xDEADBEEF, 0x9E3779B9,
           seed, 20261002, 123456789, 987654321]
    rng = Rng(seed, 1)
    ins += [rng.u32() for _ in range(12)]
    gold = {"_doc": "Mix32 = lowbias32 (x ^= x>>16; x *= 0x7feb352d; x ^= x>>15; x *= 0x846ca68b; x ^= x>>16), uint32. Shared golden for "
                    "make_scatter_meshes.py and the C++ TerrainScatterMath Mix32 (plan-c-scatter.md 3.6, S2 test). Pairs are [in, out] as "
                    "unsigned decimal.", "pairs": [[x, mix32(x)] for x in ins]}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(gold, f, indent=1)


def build_all(cfg, out):
    os.makedirs(out, exist_ok=True)
    gen_sha = hashlib.sha256(open(os.path.abspath(__file__), "rb").read()).hexdigest()
    cfg_sha = hashlib.sha256(open(CONFIG, "rb").read()).hexdigest()
    seed = cfg["seed"]
    rows, bad, chains = [], [], []
    for sp in cfg["species"]:
        slot = cfg["slots"][sp["slot"]]
        mesh = Mesh(sp["name"])
        rng = Rng(seed, sp["stream"])
        BUILDERS[sp["kind"]](sp, rng, mesh, mix32(seed ^ sp["stream"] * 7919))
        scale = finalise(mesh, slot["nominal_height_m"])
        path = os.path.join(out, sp["name"] + ".glb")
        data, st = write_glb(mesh, path)
        # one definition of the vertex hash for L0 and L1: float32 glTF POSITION accessors in primitive order (scatter_glb.stats)
        st["vertex_position_sha256"] = scatter_glb.stats(path)["vertex_position_sha256"]
        zero = sum(int((scatter_glb.triangle_areas(pr) < 1e-7).sum()) for pr in scatter_glb.primitives(path))
        if zero:
            bad.append(f"{sp['name']} has {zero} zero-area triangles")
        tris = mesh.triangles()
        lo, hi = slot.get("tris_min", 1), slot["tris_max"]
        ok = lo <= tris <= hi
        if not ok:
            bad.append(f"{sp['name']} tris={tris} budget={lo}..{hi}")
        rows.append(dict({"name": sp["name"], "slot": sp["slot"], "kind": sp["kind"], "file": sp["name"] + ".glb",
                          "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data), "triangles": tris, "budget": [lo, hi],
                          "budget_ok": ok, "nominal_height_m": slot["nominal_height_m"], "build_scale": round(scale, 6),
                          "pivot": "base centre, min z = 0"}, **st))
        if sp["name"] in LOD_CHAINS:
            top = float(np.concatenate([p.pos for p in mesh.parts])[:, 2].max())
            lods = [{"lod": 0, "file": sp["name"] + ".glb", "triangles": tris, "blades": len(mesh.parts), "kept_parts": list(range(len(mesh.parts)))}]
            for i, frac in enumerate(LOD_CHAINS[sp["name"]], start=1):
                sub, keep = lod_subset(mesh, frac, i)
                fname = f"{sp['name']}_LOD{i}.glb"
                lpath = os.path.join(out, fname)
                ldata, lst = write_glb(sub, lpath, height_ref=top)
                lz = sum(int((scatter_glb.triangle_areas(pr) < 1e-7).sum()) for pr in scatter_glb.primitives(lpath))
                if lz:
                    bad.append(f"{fname} has {lz} zero-area triangles")
                lods.append({"lod": i, "file": fname, "keep_fraction": frac, "triangles": sub.triangles(), "blades": len(keep),
                             "kept_parts": keep, "kept_g_bytes": [g_byte(mesh.parts[j]) for j in keep],
                             "sha256": hashlib.sha256(ldata).hexdigest(), "bytes": len(ldata),
                             "vertex_position_sha256": scatter_glb.stats(lpath)["vertex_position_sha256"]})
            if not lod_chain_reduces([l["triangles"] for l in lods]):
                bad.append(f"{sp['name']} LOD chain does not reduce: {[l['triangles'] for l in lods]}")
            chains.append({"name": sp["name"], "slot": sp["slot"], "lods": lods})
    report = {"_doc": "L0 procedural scatter meshes (make_scatter_meshes.py). Bounds in metres, +Z up (the file is glTF +Y up).",
              "generator_sha256": gen_sha, "config_sha256": cfg_sha, "seed": seed, "date": "deterministic", "meshes": rows,
              "lod_chains": chains}
    with open(os.path.join(out, "report.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(report, f, indent=1)
    return report, bad


def main():
    if os.path.basename(T) == "unreal-terrain" and "CHIMERA_SCATTER_SRC" not in os.environ and "--out" not in sys.argv:
        print(f"REFUSED: {T} is the repo mirror; run the ChimeraTerrain copy (or pass --out / set CHIMERA_SCATTER_SRC)")
        return 2
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--golden", action="store_true", help="(re)write Scripts/scatter/mix32_golden.json")
    a = ap.parse_args()
    with open(CONFIG, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    if a.golden:
        write_golden(GOLDEN, cfg["seed"])
    report, bad = build_all(cfg, a.out)
    kinds = sorted({m["kind"] for m in report["meshes"]})
    for m in report["meshes"]:
        print(f"  {m['name']:<13} tris={m['triangles']:>5} budget={m['budget'][0]}..{m['budget'][1]} h={m['bounds_max'][2]:.2f} m")
    for c in report["lod_chains"]:
        print(f"  {c['name']:<13} LOD chain tris={'/'.join(str(l['triangles']) for l in c['lods'])} blades={'/'.join(str(l['blades']) for l in c['lods'])}")
    if bad:
        print("MESHES_FAIL " + "; ".join(bad))
        return 1
    n_lod = sum(len(c["lods"]) - 1 for c in report["lod_chains"])
    print(f"MESHES_OK species={len(kinds)} files={len(report['meshes'])} tris_ok lod_files={n_lod}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
