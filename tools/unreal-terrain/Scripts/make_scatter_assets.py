"""Import the scatter meshes and build the scatter materials (plan-c-scatter.md 3.6-3.7, task S3). Runs inside UnrealEditor-Cmd through the
Python commandlet (PythonScriptCommandlet.cpp: -run=pythonscript -script="<this file> [report.json]"), started by Tools/run_commandlet.ps1:
  LOCK PS T/Tools/run_commandlet.ps1 -Script T/Scripts/make_scatter_assets.py -Tag s3_assets -Sentinel SCATTER_OK -TimeoutMin 60

It runs FROM CLEAN only: a full run stops at once with one error if /Game/Terrain/Scatter (Content/Terrain/Scatter) is not empty, because a
re-import onto existing assets would rename onto existing paths. Delete Content/Terrain/Scatter first (S6 and C11 do exactly that).

What it does (every step asserts what it did; one failure never stops the others, the run fails at the end with every error listed):
 1. Textures: the L0 canopy and bark textures from ScatterSrc/raw (Poly Haven CC0, flip-green on the GL normal maps) and three 4x4 defaults
    (white, packed AO/roughness/metal, flat normal) that fix the sampler type of every texture parameter.
 2. Master materials (Scripts/scatter/scatter_material_spec.py masters(): Blade, BladeFade, Triplanar, TexOpaque, TexMasked, Card), one
    Custom node each from Scripts/scatter/ChimeraScatter.hlsl, legacy pins as the ground material; two-sided only for the thin classes (plan
    3.7). bUsedWithInstancedStaticMeshes and, for Nanite rows, bUsedWithNanite are set and asserted (F7); per-instance random is asserted
    false twice (F25): the expression scan and the engine's own HasPerInstanceRandom asset-registry tag (MaterialInterface.cpp:1162).
 3. Meshes: every L0 glb (ScatterSrc/L0) and every prepared L1 glb (ScatterSrc/prepared) through Interchange (AssetImportTask, no factory,
    tools/unreal-looktest/lt_import.py:17-29) into /Game/Terrain/Scatter/Meshes/<L0|L1>/<Name>; per slot a material instance of a master
    (L0 from the species table, L1 from the glTF material and its textures), Interchange's own materials deleted, Nanite per plan 3.7,
    distance fields off (LOD0 DistanceFieldResolutionScale 0: the project forces r.GenerateMeshDistanceFields on), simple collision removed,
    lightmap UVs off. L1 textures are renamed to /Game/Terrain/Scatter/Textures/L1 and set per role (base sRGB, packed linear masks, normal
    with flip-green: Interchange's own True is gated, F23), NeverStream. The grass slots also get a non-Nanite 3-LOD copy (<Name>_L, the A/B
    of plan 3.7) whose instances use the fading Blade master (ISM PerInstanceFadeAmount); its LOD1 and LOD2 are make_scatter_meshes.py's
    blade-subset glbs (<Name>_LOD1/_LOD2), attached with SetLodFromStaticMesh.
 4. Checks: usage flags per (mesh, material) row, game dependencies of every scatter package under /Game/Terrain, /Engine, /BaseMaterial or
    /Script (editor-only import data excluded), normal-map flip per texture, the vertex colours of the FINAL saved assets read back through
    ASCII FBX exports (three L0 meshes' source descriptions and render data, every LOD of the grass _L variants; stored bytes, never the
    generator's own numbers) and HasVertexColors on every mesh whose material reads vertex colour, imported triangles against the
    generator's or Blender's report, nothing new anywhere under /Game outside /Game/Terrain/Scatter.
 5. Writes T/Out/scatter_assets/report.json (and argv[1]) and prints
    SCATTER_OK meshes=<n> materials=<m> errors=0 usage_ok=<k>/<k> deps_ok=1 flip_ok=1 vc_ok=1
    or SCATTER_FAIL errors=<n> (and raises, so the commandlet exits non-zero). Material compile errors: RecompileMaterial returns before the
    asynchronous shader compile has finished (MaterialEditingLibrary.cpp:1035-1060), so its list holds translation errors only; the shader
    compile is gated by get_statistics (FinishCompilation, :2116-2125) giving pixel instructions > 0, by this script's scan of its own log
    for shader-compile error lines, and by run_commandlet.ps1's log scan.
Debug knobs (environment): CHIMERA_S3_STEPS=textures,masters,l0,l1,lod,verify (default all; a partial run prints SCATTER_PARTIAL, never
SCATTER_OK), CHIMERA_S3_ONLY=<mesh names, comma separated> limits the mesh steps.
"""
import hashlib
import json
import os
import re
import struct
import sys
import time
import zlib

import unreal

T = os.path.abspath(unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_dir())).replace('\\', '/').rstrip('/')
sys.path.insert(0, T + '/Scripts/scatter')
import scatter_material_spec as S  # noqa: E402

EAL = unreal.EditorAssetLibrary
MEL = unreal.MaterialEditingLibrary
SRC = T + '/ScatterSrc'
OUT_DIR = T + '/Out/scatter_assets'
WORK = SRC + '/work'
ALL_STEPS = ['textures', 'masters', 'l0', 'l1', 'lod', 'verify']
STEPS = [s for s in os.environ.get('CHIMERA_S3_STEPS', ','.join(ALL_STEPS)).split(',') if s]
ONLY = [s for s in os.environ.get('CHIMERA_S3_ONLY', '').split(',') if s]
FULL_RUN = STEPS == ALL_STEPS and not ONLY

ERRORS = []
WARNINGS = []
REPORT = {'textures': {}, 'masters': {}, 'instances': {}, 'meshes': {}, 'usage_rows': [], 'flip_rows': [], 'vc_rows': [], 'deps': {}, 'unexpected_assets': []}
TIMES = {}


def log(msg):
    unreal.log('make_scatter_assets: ' + msg)
    print('make_scatter_assets: ' + msg)


def err(msg):
    ERRORS.append(msg)
    unreal.log_error('SCATTER_ASSET ' + msg)
    print('SCATTER_ASSET ERROR ' + msg)


def warn(msg):
    WARNINGS.append(msg)
    unreal.log_warning('SCATTER_ASSET ' + msg)
    print('SCATTER_ASSET WARNING ' + msg)


def pkg(obj_or_path):
    """Package path ('/Game/x/Name') of an asset object or object path."""
    p = obj_or_path if isinstance(obj_or_path, str) else obj_or_path.get_path_name()
    return p.split('.')[0]


def norm(name):
    return re.sub(r'[^a-z0-9]+', '_', str(name).lower()).strip('_')


def prop_name(obj, wanted):
    """The Python spelling of a reflected property: `wanted` compared without underscores and case against dir(obj) (UE writes bGenerateLightmapUVs
    as generate_lightmap_u_vs), so the script never depends on a guessed spelling."""
    want = wanted.replace('_', '').lower()
    for n in dir(obj):
        if not n.startswith('_') and n.replace('_', '').lower() == want:
            return n
    return wanted


def set_props(obj, props, what=''):
    for k, v in props.items():
        try:
            obj.set_editor_property(prop_name(obj, k), v)
        except Exception as e:
            err('%s %s.%s = %r: %s' % (what, getattr(obj, 'get_name', lambda: type(obj).__name__)(), k, v, e))


def get_prop(obj, name, default=None):
    try:
        return obj.get_editor_property(prop_name(obj, name))
    except Exception:
        return default


def enum_exact(enum_cls, *names):
    """The member of a Python-wrapped enum whose name is exactly one of `names` (for enums where a suffix is ambiguous: WORLD,
    PERIODIC_WORLD, TRANSLATED_WORLD)."""
    have = [n for n in dir(enum_cls) if n.isupper()]
    for n in names:
        if n in have:
            return getattr(enum_cls, n)
    raise RuntimeError('%s has none of %s (members: %s)' % (enum_cls.__name__, names, have))


def enum_by_suffix(enum_cls, *suffixes):
    """First member of a Python-wrapped enum whose name ends with one of the suffixes (the wrapper's spelling of C++ names is not guessed)."""
    names = [n for n in dir(enum_cls) if n.isupper()]
    for suf in suffixes:
        for n in names:
            if n.endswith(suf):
                return getattr(enum_cls, n)
    raise RuntimeError('%s has no member ending %s (members: %s)' % (enum_cls.__name__, suffixes, names))


# ------------------------------------------------------------------------------------------------------------------ glTF (pure python)
def read_glb(path):
    with open(path, 'rb') as f:
        data = f.read()
    magic, ver, total = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67 or ver != 2:
        raise ValueError('%s: not a glTF 2 binary' % path)
    off, js, binb = 12, None, b''
    while off < total:
        n, kind = struct.unpack_from('<II', data, off)
        chunk = data[off + 8: off + 8 + n]
        if kind == 0x4E4F534A:
            js = json.loads(chunk.decode('utf-8'))
        elif kind == 0x004E4942:
            binb = chunk
        off += 8 + n
    return js, binb


def glb_info(path):
    """Materials (name, alphaMode, baseColorFactor, image names per role) and the image list of a glb."""
    js, _ = read_glb(path)
    imgs = [i.get('name') for i in js.get('images', [])]
    texs = js.get('textures', [])
    refs = [sum(1 for t in texs if t.get('source') == i) for i in range(len(imgs))]

    def img(tex):
        if not tex:
            return None
        return imgs[texs[tex['index']]['source']]
    mats = []
    for m in js.get('materials', []):
        pbr = m.get('pbrMetallicRoughness', {})
        mats.append({'name': m.get('name'), 'alphaMode': m.get('alphaMode', 'OPAQUE'), 'baseColorFactor': pbr.get('baseColorFactor'),
                     'base': img(pbr.get('baseColorTexture')), 'arm': img(pbr.get('metallicRoughnessTexture')),
                     'normal': img(m.get('normalTexture'))})
    return {'materials': mats, 'images': imgs, 'texture_refs': refs, 'meshes': [m.get('name') for m in js.get('meshes', [])]}


def glb_corner_colours(path):
    """COLOR_0 (normalised unsigned byte or short) at every triangle corner of every primitive, as (r, g, b, a) bytes."""
    js, binb = read_glb(path)
    fmt = {5121: ('B', 1), 5123: ('H', 2), 5125: ('I', 4)}
    ncomp = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}

    def read(ai):
        a = js['accessors'][ai]
        v = js['bufferViews'][a['bufferView']]
        code, size = fmt[a['componentType']]
        n = ncomp[a['type']]
        off = v.get('byteOffset', 0) + a.get('byteOffset', 0)
        stride = v.get('byteStride', 0) or size * n
        return [struct.unpack_from('<' + code * n, binb, off + i * stride) for i in range(a['count'])], a
    out = []
    for m in js['meshes']:
        for p in m['primitives']:
            cols, ca = read(p['attributes']['COLOR_0'])
            div = 65535.0 if ca['componentType'] == 5123 else 255.0
            cols = [tuple(int(round(c * 255.0 / div)) for c in col) + ((255,) if len(col) == 3 else ()) for col in cols]
            idx, _ = read(p['indices'])
            out.extend(cols[i[0]] for i in idx)
    return out


def fbx_colour_blocks(path):
    """Vertex colours of an ASCII FBX per polygon corner, as bytes, one list per LayerElementColor block (one block per exported mesh: a
    LOD-group export has one per LOD). Render-data export: the stored FColor bytes / 255 (ReinterpretAsLinear, index-to-direct;
    FbxMainExport.cpp ExportStaticMeshFromRenderData). Source export: the mesh description's linear colour converted with ToFColor(true)
    (direct; ExportStaticMeshFromMeshDescription), the same conversion the static mesh build uses for the GPU colour buffer
    (StaticMeshBuilder.cpp:1708, StaticMesh.cpp:8608)."""
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        text = f.read()
    chunks = text.split('LayerElementColor:')[1:]
    out = []
    for ch in chunks:
        m = re.search(r'Colors:\s*\*\d+\s*\{\s*a:\s*([^}]*)\}', ch, flags=re.S)
        if not m:
            continue
        vals = [float(x) for x in m.group(1).replace('\n', '').replace(' ', '').split(',') if x]
        direct = [tuple(int(round(vals[i + k] * 255.0)) for k in range(4)) for i in range(0, len(vals) - 3, 4)]
        mi = re.search(r'ColorIndex:\s*\*\d+\s*\{\s*a:\s*([^}]*)\}', ch, flags=re.S)
        if mi:
            idx = [int(x) for x in mi.group(1).replace('\n', '').replace(' ', '').split(',') if x]
            out.append([direct[i] for i in idx])
        else:
            out.append(direct)
    if not out:
        raise RuntimeError('no Colors array in ' + path)
    return out


# ------------------------------------------------------------------------------------------------------------------ assets: textures
def write_png_rgba(path, w, h, rgba):
    """Minimal RGBA8 PNG writer (the editor's Python has no Pillow), as make_ground_material.py."""
    raw = b''.join(b'\x00' + bytes(rgba) * w for _ in range(h))

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def tex_props(kind):
    """Texture settings per role. kinds: color (sRGB albedo, alpha kept), masks (linear packed AO/rough/metal), normal (DirectX), normal_gl
    (GL source: green flipped on import, the Interchange glTF rule, F23)."""
    tc = unreal.TextureCompressionSettings
    common = {'virtual_texture_streaming': False, 'never_stream': True}
    if kind == 'color':
        return dict(common, srgb=True, compression_settings=tc.TC_DEFAULT)
    if kind == 'masks':
        return dict(common, srgb=False, compression_settings=tc.TC_MASKS)
    if kind == 'normal':
        return dict(common, srgb=False, compression_settings=tc.TC_NORMALMAP, flip_green_channel=False)
    if kind == 'normal_gl':
        return dict(common, srgb=False, compression_settings=tc.TC_NORMALMAP, flip_green_channel=True)
    raise ValueError(kind)


def apply_tex_props(tex, kind, what):
    props = tex_props(kind)
    set_props(tex, props, what)
    for k, v in props.items():
        got = get_prop(tex, k)
        if got != v:
            err('%s: %s.%s read back %r != %r' % (what, tex.get_name(), k, got, v))


def import_texture_file(src, name, kind, dest_dir):
    """Interchange import of one image file (no factory, as make_ground_material.py import_texture), then the role's settings."""
    if not os.path.isfile(src):
        err('texture source missing: ' + src)
        return None
    t = unreal.AssetImportTask()
    set_props(t, {'filename': src, 'destination_path': dest_dir, 'destination_name': name, 'automated': True, 'replace_existing': True,
                  'save': True}, 'import ' + name)
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([t])
    objs = [EAL.load_asset(p) for p in list(t.get_editor_property('imported_object_paths'))]
    tex = [o for o in objs if isinstance(o, unreal.Texture2D)]
    if len(tex) != 1:
        err('%s: %d Texture2D imported from %s' % (name, len(tex), src))
        return None
    tex = tex[0]
    apply_tex_props(tex, kind, name)
    if not EAL.save_loaded_asset(tex, False):
        err('save %s failed' % name)
    return tex


def step_textures():
    EAL.make_directory(S.TEX_DIR)
    os.makedirs(WORK, exist_ok=True)
    tex = {}
    for role, rgba in S.DEFAULT_PIXELS.items():
        name = S.DEFAULT_TEXTURES[role]
        png = '%s/%s.png' % (WORK, name)
        write_png_rgba(png, 4, 4, rgba)
        t = import_texture_file(png, name, 'normal' if role == 'normal' else role, S.TEX_DIR)
        if t is not None:
            tex[name] = t
    for name, (rel, kind) in S.L0_TEXTURES.items():
        t = import_texture_file('%s/raw/polyhaven/%s' % (SRC, rel), name, kind, S.TEX_DIR)
        if t is not None:
            tex[name] = t
    return tex


def load_texture(name_or_path, ground=False):
    path = name_or_path if name_or_path.startswith('/') else ('%s/%s' % (S.GROUND_TEX_DIR if ground else S.TEX_DIR, name_or_path))
    t = EAL.load_asset(path)
    if t is None:
        err('texture not found: ' + path)
    return t


# ------------------------------------------------------------------------------------------------------------------ assets: materials
def sampler_for(tex):
    """Sampler type the compiler expects for a texture (make_ground_material.py sampler_for)."""
    st, tc = unreal.MaterialSamplerType, unreal.TextureCompressionSettings
    srgb = bool(tex.get_editor_property('srgb'))
    cs = tex.get_editor_property('compression_settings')
    if cs == tc.TC_NORMALMAP:
        kind = 'NORMAL'
    elif cs == tc.TC_MASKS:
        kind = 'MASKS'
    elif cs == tc.TC_ALPHA:
        kind = 'ALPHA'
    else:
        kind = 'COLOR' if srgb else 'LINEAR_COLOR'
    return getattr(st, 'SAMPLERTYPE_' + kind)


def expr(mat, cls, x, y, **props):
    e = MEL.create_material_expression(mat, cls, x, y)
    if e is None:
        raise RuntimeError('create_material_expression(%s) failed' % cls.__name__)
    for k, v in props.items():
        e.set_editor_property(k, v)
    return e


def out_name(e, want):
    """The exact output name of expression `e` matching `want` (case-insensitive); '' = its first output."""
    if want == '':
        return ''
    names = [str(n) for n in MEL.get_material_expression_output_names(e)]
    for n in names:
        if n.lower() == want.lower():
            return n
    raise RuntimeError('%s has no output %s (outputs %s)' % (e.get_class().get_name(), want, names))


def link(a, out, b, inp):
    o = out_name(a, out)
    if not inp:
        names = [str(n) for n in MEL.get_material_expression_input_names(b)]
        if not names:
            raise RuntimeError('%s has no inputs' % b.get_class().get_name())
        inp = names[0]
    if not MEL.connect_material_expressions(a, o, b, inp):
        raise RuntimeError('connect %s.%s -> %s.%s failed' % (a.get_class().get_name(), o or '<0>', b.get_class().get_name(), inp))


def usage(name):
    return enum_by_suffix(unreal.MaterialUsage, name)


def set_usage(mat, flag, want=True):
    try:
        MEL.set_base_material_usage(mat, usage(flag), want)
    except Exception as e:
        warn('set_base_material_usage(%s) failed (%s); setting the property' % (flag, e))
        mat.set_editor_property({'INSTANCED_STATIC_MESHES': 'used_with_instanced_static_meshes', 'NANITE': 'used_with_nanite'}[flag], want)


def build_master(name, spec, parsed, tex_defaults):
    """One master material: Custom node from the HLSL section, parameters, standard texture samples where the spec asks, usage flags,
    compile (get_statistics finishes the shader compile; zero pixel instructions means an HLSL error)."""
    sec = parsed['sections'][spec['section']]
    path = '%s/%s' % (S.MAT_DIR, name)
    at = unreal.AssetToolsHelpers.get_asset_tools()
    if EAL.does_asset_exist(path) and not EAL.delete_asset(path):
        err('delete_asset %s failed' % path)
        return None
    mat = at.create_asset(name, S.MAT_DIR, unreal.Material, unreal.MaterialFactoryNew())
    if mat is None:
        err('create_asset %s failed' % path)
        return None
    MEL.delete_all_material_expressions(mat)  # drops the factory's default Substrate slab (legacy pins, make_ground_material.py)
    kinds = []  # expression class names, for the per-instance-random check
    op_rb = None  # read-back of the instance-origin transform (Blade masters)
    t0 = time.time()

    def mk(cls, x, y, **props):
        e = expr(mat, cls, x, y, **props)
        kinds.append(cls.__name__)
        return e
    custom = mk(unreal.MaterialExpressionCustom, -400, 0, code=S.custom_code(spec['section'], parsed), description='ChimeraScatter_' + spec['section'],
                output_type=unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    inputs = []
    for nm in sec['inputs']:
        ci = unreal.CustomInput()
        ci.set_editor_property('input_name', nm)
        inputs.append(ci)
    custom.set_editor_property('inputs', inputs)
    outs = []
    for nm, typ in sec['outputs']:
        co = unreal.CustomOutput()
        co.set_editor_property('output_name', nm)
        co.set_editor_property('output_type', getattr(unreal.CustomMaterialOutputType, {'float1': 'CMOT_FLOAT1', 'float3': 'CMOT_FLOAT3'}[typ]))
        outs.append(co)
    custom.set_editor_property('additional_outputs', outs)
    got_in = [str(n) for n in MEL.get_material_expression_input_names(custom)]
    if got_in != sec['inputs']:
        err('%s: custom inputs %s != %s' % (name, got_in, sec['inputs']))
        return None
    got_out = [str(n) for n in MEL.get_material_expression_output_names(custom)]
    if got_out != ['return'] + [n for n, _ in sec['outputs']]:
        err('%s: custom outputs %s' % (name, got_out))
        return None

    y = -1400
    samples = {}
    for sname, role in spec.get('samples', {}).items():
        t = tex_defaults[role]
        samples[sname] = mk(unreal.MaterialExpressionTextureSampleParameter2D, -1000, y, parameter_name=sname, texture=t, sampler_type=sampler_for(t))
        y += 160
    for nm in sec['inputs']:
        b = S.BUILTIN_INPUTS.get(nm)
        if b == 'world_position':
            e = mk(unreal.MaterialExpressionWorldPosition, -1000, y)
        elif b == 'instance_origin':
            # The instance origin in the pixel shader: TransformPosition (MaterialExpressionTransformPosition.h) from Instance space to
            # Absolute World of (0,0,0). The translator emits GetInstanceToWorld(Parameters) and sets bUsesInstanceLocalToWorldPS
            # (HLSLMaterialTranslator.cpp, case MCB_Instance), and HAS_INSTANCE_LOCAL_TO_WORLD_PS covers USE_INSTANCE_CULLING (ISM) and
            # IS_NANITE_PASS (MaterialTemplate.ush:159, 885-891). ObjectPositionWS would be the component's bounds origin in a pixel shader
            # (MaterialTemplate.ush MakeMaterialLWCData(FMaterialPixelParameters) has no instancing branch).
            zero = mk(unreal.MaterialExpressionConstant3Vector, -1200, y, constant=unreal.LinearColor(0.0, 0.0, 0.0, 0.0))
            e = mk(unreal.MaterialExpressionTransformPosition, -1000, y,
                   transform_source_type=enum_exact(unreal.MaterialPositionTransformSource, 'INSTANCE', 'TRANSFORMPOSSOURCE_INSTANCE'),
                   transform_type=enum_exact(unreal.MaterialPositionTransformSource, 'WORLD', 'TRANSFORMPOSSOURCE_WORLD'))
            link(zero, '', e, '')
            ename = lambda v: str(getattr(v, 'name', v))  # the Python-wrapped enum's member name, e.g. TRANSFORMPOSSOURCE_WORLD
            op_rb = {'source': ename(get_prop(e, 'transform_source_type')), 'destination': ename(get_prop(e, 'transform_type'))}
            dst = op_rb['destination'].upper()
            if 'INSTANCE' not in op_rb['source'].upper() or not dst.endswith('WORLD') or 'PERIODIC' in dst or 'TRANSLATED' in dst:
                raise RuntimeError('%s: instance-origin transform read back %s' % (name, op_rb))
        elif b == 'vertex_normal':
            e = mk(unreal.MaterialExpressionVertexNormalWS, -1000, y)
        elif b == 'pixel_depth':
            e = mk(unreal.MaterialExpressionPixelDepth, -1000, y)
        elif b == 'vertex_color':
            e = mk(unreal.MaterialExpressionVertexColor, -1000, y)
        elif isinstance(b, tuple):
            e = mk(unreal.MaterialExpressionPerInstanceCustomData, -1000, y, data_index=b[1], const_default_value=0.0)
        elif nm == 'BaseRGB':
            link(samples['BaseColorTex'], 'RGB', custom, nm)
            continue
        elif nm == 'ARM':
            link(samples['ARMTex'], 'RGB', custom, nm)
            continue
        elif nm in spec['params']:
            kind, default = spec['params'][nm]
            if kind == 's':
                e = mk(unreal.MaterialExpressionScalarParameter, -1000, y, parameter_name=nm, default_value=float(default))
            elif kind == 'v':
                e = mk(unreal.MaterialExpressionVectorParameter, -1000, y, parameter_name=nm, default_value=unreal.LinearColor(*default))
            elif kind == 't':
                t = tex_defaults[default]
                e = mk(unreal.MaterialExpressionTextureObjectParameter, -1000, y, parameter_name=nm, texture=t, sampler_type=sampler_for(t))
            elif kind == 'tx':
                t = EAL.load_asset(default) or unreal.load_object(None, default + '.' + default.rsplit('/', 1)[1])
                if t is None:
                    raise RuntimeError('could not load ' + default)
                e = mk(unreal.MaterialExpressionTextureObject, -1000, y, texture=t, sampler_type=sampler_for(t))
            else:
                raise RuntimeError('%s: parameter kind %s' % (nm, kind))
        else:
            raise RuntimeError('%s: input %s has no source in the spec' % (name, nm))
        link(e, '', custom, nm)
        y += 80
    missing = [p for p in spec['params'] if p not in sec['inputs']]
    if missing:
        raise RuntimeError('%s: parameters not read by the HLSL: %s' % (name, missing))

    # Pins (legacy, as the ground material).
    if not MEL.connect_material_property(custom, 'return', unreal.MaterialProperty.MP_BASE_COLOR):
        raise RuntimeError('connect return -> BaseColor failed')
    if not MEL.connect_material_property(custom, 'CgRough', unreal.MaterialProperty.MP_ROUGHNESS):
        raise RuntimeError('connect CgRough failed')
    if not MEL.connect_material_property(custom, 'CgAO', unreal.MaterialProperty.MP_AMBIENT_OCCLUSION):
        raise RuntimeError('connect CgAO failed')
    if spec['normal'] == 'custom':
        tr = mk(unreal.MaterialExpressionTransform, -200, 300,
                transform_source_type=enum_by_suffix(unreal.MaterialVectorCoordTransformSource, 'WORLD'),
                transform_type=enum_by_suffix(unreal.MaterialVectorCoordTransform, 'TANGENT'))
        link(custom, 'CgNormalWS', tr, '')
        if not MEL.connect_material_property(tr, '', unreal.MaterialProperty.MP_NORMAL):
            raise RuntimeError('connect normal failed')
    else:
        if not MEL.connect_material_property(samples['NormalTex'], 'RGB', unreal.MaterialProperty.MP_NORMAL):
            raise RuntimeError('connect NormalTex -> Normal failed')
    if spec['blend'] == 'masked':
        if spec.get('fade') == 'blade':
            # Opacity mask = PerInstanceFadeAmount + 0.5 - vertex colour G (one value per blade), against clip 0.5: a blade shows while the
            # instance's fade is above its G, so a fading instance thins blade by blade. Standard expressions only (F24).
            fade = mk(unreal.MaterialExpressionPerInstanceFadeAmount, -900, 500)
            vcol = mk(unreal.MaterialExpressionVertexColor, -900, 600)
            gmask = mk(unreal.MaterialExpressionComponentMask, -700, 600, r=False, g=True, b=False, a=False)
            link(vcol, '', gmask, '')
            sub = mk(unreal.MaterialExpressionSubtract, -500, 500)
            link(fade, '', sub, 'A')
            link(gmask, '', sub, 'B')
            add = mk(unreal.MaterialExpressionAdd, -300, 500, const_b=0.5)
            link(sub, '', add, 'A')
            ok = MEL.connect_material_property(add, '', unreal.MaterialProperty.MP_OPACITY_MASK)
        elif spec.get('fade') == 'alpha':
            fade = mk(unreal.MaterialExpressionPerInstanceFadeAmount, -600, 500)
            mul = mk(unreal.MaterialExpressionMultiply, -300, 500)
            link(samples['BaseColorTex'], 'A', mul, 'A')
            link(fade, '', mul, 'B')
            ok = MEL.connect_material_property(mul, '', unreal.MaterialProperty.MP_OPACITY_MASK)
        else:
            ok = MEL.connect_material_property(samples['BaseColorTex'], 'A', unreal.MaterialProperty.MP_OPACITY_MASK)
        if not ok:
            raise RuntimeError('connect opacity mask failed')

    props = {'two_sided': spec['two_sided'], 'blend_mode': unreal.BlendMode.BLEND_MASKED if spec['blend'] == 'masked' else unreal.BlendMode.BLEND_OPAQUE}
    if spec['blend'] == 'masked':
        props['opacity_mask_clip_value'] = spec['clip']
    set_props(mat, props, name)
    set_usage(mat, 'INSTANCED_STATIC_MESHES', True)
    set_usage(mat, 'NANITE', bool(spec['nanite']))

    # RecompileMaterial returns before the asynchronous shader compile has finished: these are translation errors only (see the docstring).
    errors = [str(e) for e in MEL.recompile_material(mat)]
    if errors:
        err('%s: translation errors: %s' % (name, ' | '.join(errors)))
    stats = MEL.get_statistics(mat)  # submits and finishes the shader compile (zero pixel instructions = an HLSL error)
    st = {k: int(stats.get_editor_property(k)) for k in ('num_vertex_shader_instructions', 'num_pixel_shader_instructions', 'num_samplers',
                                                          'num_pixel_texture_samples')}
    if st['num_pixel_shader_instructions'] <= 0:
        err('%s: shader compile produced no pixel shader (an HLSL error in ChimeraScatter.hlsl section %s?): %s' % (name, spec['section'], st))
    sided = get_prop(mat, 'two_sided')
    if bool(sided) is not bool(spec['two_sided']):
        err('%s: two_sided read back %r (want %s)' % (name, sided, spec['two_sided']))
    for p, (kind, default) in spec['params'].items():
        if kind == 's':
            got = MEL.get_material_default_scalar_parameter_value(mat, p)
            if abs(got - float(default)) > 1e-5:
                err('%s: %s default read back %s != %s' % (name, p, got, default))
    if not EAL.save_loaded_asset(mat, False):
        err('save %s failed' % path)
    classes = sorted(set(k for k in (type(e).__name__ for e in MEL.get_material_expressions(mat))))
    pir = any('PerInstanceRandom' in c for c in classes) or any('PerInstanceRandom' in c for c in kinds) or 'PerInstanceRandom' in S.custom_code(spec['section'], parsed)
    if pir:
        err('%s: uses per-instance random (F25)' % name)
    TIMES['compile_' + name] = round(time.time() - t0, 1)
    REPORT['masters'][name] = {'path': path, 'section': spec['section'], 'blend': spec['blend'], 'two_sided': spec['two_sided'],
                               'nanite': spec['nanite'], 'expression_classes': classes, 'per_instance_random': pir,
                               'parameters': sorted(spec['params']) + sorted(spec.get('samples', {})), 'stats': st,
                               'translation_errors': len(errors), 'two_sided_read_back': bool(sided), 'fade': spec.get('fade'), 'clip': spec.get('clip'),
                               'instance_origin_transform': op_rb}
    log('master %s ok: ps=%d vs=%d samplers=%d (%.1f s)' % (name, st['num_pixel_shader_instructions'], st['num_vertex_shader_instructions'],
                                                          st['num_samplers'], time.time() - t0))
    return mat


def step_masters(tex_defaults):
    EAL.make_directory(S.MAT_DIR)
    # /BaseMaterial is a built-in GameFeature plugin: in a commandlet its folder is not in the asset registry until scanned (make_scatter_assets
    # logged 'LoadAsset failed ... could not be found in the Asset Registry', which makes the editor exit 1 even though the script succeeded).
    unreal.AssetRegistryHelpers.get_asset_registry().scan_paths_synchronous([S.NOISE_TEX.rsplit('/', 1)[0]], True)
    parsed = S.parse_hlsl()
    out = {}
    for name, spec in S.masters().items():
        try:
            m = build_master(name, spec, parsed, tex_defaults)
        except Exception as e:
            err('master %s: %s' % (name, e))
            m = None
        if m is not None:
            out[name] = m
    return out


def make_instance(level, name, master_name, masters, scalars, vectors, textures):
    """MaterialInstanceConstant of a master with the given overrides (textures: parameter -> Texture2D). Returns the asset."""
    d = '%s/%s' % (S.MAT_DIR, level)
    EAL.make_directory(d)
    path = '%s/%s' % (d, name)
    if EAL.does_asset_exist(path) and not EAL.delete_asset(path):
        err('delete_asset %s failed' % path)
        return None
    parent = masters.get(master_name)
    if parent is None:
        err('%s: master %s missing' % (name, master_name))
        return None
    at = unreal.AssetToolsHelpers.get_asset_tools()
    mic = at.create_asset(name, d, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())
    if mic is None:
        err('create_asset %s failed' % path)
        return None
    MEL.set_material_instance_parent(mic, parent)
    # The engine's setters return false whatever happens (MaterialEditingLibrary.cpp: bResult is never assigned), so every value is read back.
    for k, v in scalars.items():
        MEL.set_material_instance_scalar_parameter_value(mic, k, float(v))
    for k, v in vectors.items():
        MEL.set_material_instance_vector_parameter_value(mic, k, unreal.LinearColor(*[float(x) for x in v]))
    samplers = {}
    for k, t in textures.items():
        if t is None:
            err('%s: texture %s is missing' % (name, k))
            continue
        # The sampler type of a texture parameter is fixed by the master's default texture (sampler_for); an override of another kind
        # compiles but samples wrongly (MaterialEditingLibrary.h:301 GetMaterialDefaultTextureParameterValue).
        dflt = MEL.get_material_default_texture_parameter_value(parent, k)
        want_st, got_st = (None if dflt is None else sampler_for(dflt)), sampler_for(t)
        samplers[k] = str(getattr(got_st, 'name', got_st))
        if dflt is None:
            err('%s: master %s has no texture parameter %s' % (name, master_name, k))
        elif want_st != got_st:
            err('%s: texture %s is %s, the master parameter samples %s' % (name, k, samplers[k], getattr(want_st, 'name', want_st)))
        MEL.set_material_instance_texture_parameter_value(mic, k, t)
    MEL.update_material_instance(mic)
    for k, v in scalars.items():
        got = MEL.get_material_instance_scalar_parameter_value(mic, k)
        if abs(got - float(v)) > 1e-5:
            err('%s: scalar %s read back %s != %s' % (name, k, got, v))
    for k, v in vectors.items():
        got = MEL.get_material_instance_vector_parameter_value(mic, k)
        if max(abs(got.r - v[0]), abs(got.g - v[1]), abs(got.b - v[2])) > 1e-4:
            err('%s: vector %s read back (%s, %s, %s) != %s' % (name, k, got.r, got.g, got.b, v[:3]))
    for k, t in textures.items():
        got = MEL.get_material_instance_texture_parameter_value(mic, k)
        if t is not None and (got is None or got.get_path_name() != t.get_path_name()):
            err('%s: texture %s read back %s != %s' % (name, k, None if got is None else got.get_path_name(), t.get_path_name()))
    if not EAL.save_loaded_asset(mic, False):
        err('save %s failed' % path)
    REPORT['instances'][path] = {'parent': master_name, 'scalars': dict(sorted(scalars.items())),
                                 'vectors': {k: [round(float(x), 5) for x in v] for k, v in sorted(vectors.items())},
                                 'textures': {k: (None if t is None else pkg(t)) for k, t in sorted(textures.items())},
                                 'texture_samplers': dict(sorted(samplers.items()))}
    return mic


# ------------------------------------------------------------------------------------------------------------------ assets: meshes
_SMS = []


def mesh_sub():
    """The StaticMeshEditorSubsystem, or None. The commandlet does not load the StaticMeshEditor module, so its subsystem does not exist until
    unreal.load_module (PyCore.cpp load_module) loads it and the subsystem collection picks it up."""
    if not _SMS:
        sms = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
        if sms is None:
            try:
                unreal.load_module('StaticMeshEditor')
            except Exception as e:
                warn('load_module(StaticMeshEditor): %s' % e)
            sms = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
        if sms is None:
            warn('StaticMeshEditorSubsystem is not available; using the property fallbacks')
        _SMS.append(sms)
    return _SMS[0]


def set_nanite(mesh, enabled, fallback_percent=None):
    """Nanite on or off with a rebuild. Interchange builds Nanite on every imported mesh in this project (the log shows NaniteBuild), so the
    flag is always set explicitly. fallback_percent: an explicit fallback target (FMeshNaniteSettings FallbackTarget = PercentTriangles and
    FallbackPercentTriangles, EngineTypes.h:3349-3355) instead of the engine's Auto heuristic."""
    def fill(ns):
        ns.set_editor_property('enabled', bool(enabled))
        if fallback_percent is not None:
            ns.set_editor_property('fallback_target', enum_by_suffix(unreal.NaniteFallbackTarget, 'PERCENT_TRIANGLES'))
            ns.set_editor_property('fallback_percent_triangles', float(fallback_percent))
    sms = mesh_sub()
    if sms is not None:
        ns = sms.get_nanite_settings(mesh)
        fill(ns)
        sms.set_nanite_settings(mesh, ns, True)
        return
    ns = mesh.get_editor_property('nanite_settings')
    fill(ns)
    mesh.set_editor_property('nanite_settings', ns)
    mesh.post_edit_change()


def assets_in_folder(folder):
    """Packages under a folder by asset-registry path (EAL.list_assets matches by string prefix, so a folder named like an asset lists the asset)."""
    reg = unreal.AssetRegistryHelpers.get_asset_registry()
    reg.scan_paths_synchronous([folder], True)
    return sorted(str(a.get_editor_property('package_name')) for a in reg.get_assets_by_path(folder, True))


def list_assets(folder):
    return set(pkg(p) for p in EAL.list_assets(folder, True, False))


def import_glb(glb, dest_dir, name):
    """Interchange import of a glb; returns every object it made (imported_object_paths plus whatever appeared under dest_dir)."""
    EAL.make_directory(dest_dir)
    before = list_assets(dest_dir)
    t = unreal.AssetImportTask()
    set_props(t, {'filename': glb, 'destination_path': dest_dir, 'destination_name': name, 'automated': True, 'replace_existing': True,
                  'save': True}, 'import ' + name)
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([t])
    paths = set(pkg(p) for p in list(t.get_editor_property('imported_object_paths')))
    paths |= (list_assets(dest_dir) - before)
    objs, seen = [], set()
    for p in sorted(paths):
        o = EAL.load_asset(p)
        if o is not None and o.get_path_name() not in seen:
            seen.add(o.get_path_name())
            objs.append(o)
    return objs


def slot_names(mesh):
    out = []
    for sm in mesh.get_editor_property('static_materials'):
        out.append((str(sm.get_editor_property('material_slot_name')), str(sm.get_editor_property('imported_material_slot_name'))))
    return out


def export_fbx(mesh, fbx, source_mesh, lods):
    """ASCII FBX export of a saved mesh with vertex colours (FbxExportOption, Exporters/FbxExportOption.h). source_mesh=True exports LOD0's
    mesh description (bExportSourceMesh; for a Nanite mesh that is the full source, FbxMainExport.cpp:5469-5490), False the render data
    (for a Nanite mesh LOD0 is its fallback). lods=True exports every LOD (a LOD group)."""
    task = unreal.AssetExportTask()
    opt = unreal.FbxExportOption()
    set_props(opt, {'ascii': True, 'vertex_color': True, 'level_of_detail': bool(lods), 'collision': False,
                    'export_source_mesh': bool(source_mesh)}, 'fbx options')
    set_props(task, {'object': mesh, 'filename': fbx, 'automated': True, 'prompt': False, 'replace_identical': True, 'options': opt}, 'export task')
    if not unreal.Exporter.run_asset_export_task(task):
        raise RuntimeError('run_asset_export_task returned false for ' + fbx)
    return fbx_colour_blocks(fbx)


def compare_colours(want, got):
    """want/got: per-corner RGBA byte tuples. Multisets of whole tuples (sorted, element by element; corner order is not compared, a channel
    is never matched against another corner's). ok = same corner count and R and A within 2/255 (plan 3.6: a linear-vs-sRGB conversion
    fails here)."""
    row = {'glb_corners': len(want), 'fbx_corners': len(got)}
    if len(want) != len(got):
        row.update({'ok': False, 'error': 'corner count %d (glb) != %d (stored)' % (len(want), len(got))})
        return row
    a, b = sorted(want), sorted(got)
    worst = {}
    for ch, label in ((0, 'R'), (1, 'G'), (2, 'B'), (3, 'A')):
        worst[label] = max(abs(x[ch] - y[ch]) for x, y in zip(a, b)) if a else 0
    row['max_abs_diff_bytes'] = worst
    row['tuple_multiset_equal'] = a == b
    row['ok'] = bool(a) and worst['R'] <= 2 and worst['A'] <= 2
    if not row['ok']:
        row['error'] = 'stored vertex colours differ from the generator: %s (> 2/255 on R or A; a linear-vs-sRGB conversion?)' % worst
    return row


def step_vertex_colours():
    """Vertex colours of the FINAL saved assets, after every import step (material swap, Interchange-material delete, build settings,
    collision removal, Nanite rebuild, save; the first S3 gate exported right after import, before all of them):
      a) VC_MESHES (GrassT0, TreeBroadA, RockA, plan 3.6): the source mesh description (export_source_mesh) against the glb's COLOR_0;
         plus the render data (for a Nanite mesh its fallback, whose vertices the simplifier rebuilds) must carry a colour block that is
         not all white;
      b) every grass _L variant: the render data of every LOD (non-Nanite, so render = what the ISM draws) against that LOD's glb (matched by
         corner count; LOD1 and LOD2 carry the per-blade G the fade reads);
      c) every mesh whose material reads vertex colour (S.master_reads_vertex_colour) must report HasVertexColors
         (StaticMeshEditorSubsystem.cpp:1907-1941: a colour attribute with a non-white value) on the saved asset.
    Each failure is an error; vc_ok needs all three."""
    os.makedirs(OUT_DIR + '/vc', exist_ok=True)
    sms = mesh_sub()
    for name in VC_MESHES:
        path = '%s/%s' % (S.MESH_DIR['L0'], name)
        glb = '%s/L0/%s.glb' % (SRC, name)
        row = {'mesh': name, 'path': path, 'mode': 'final_source_mesh_description', 'ok': False}
        try:
            mesh = EAL.load_asset(path)
            if mesh is None:
                raise RuntimeError('missing ' + path)
            got = export_fbx(mesh, '%s/vc/%s_source.fbx' % (OUT_DIR, name), True, False)[0]
            row.update(compare_colours(glb_corner_colours(glb), got))
            row['nanite'] = bool(sms.get_nanite_settings(mesh).get_editor_property('enabled'))
            rend = export_fbx(mesh, '%s/vc/%s_render.fbx' % (OUT_DIR, name), False, False)[0]
            non_white = sum(1 for c in rend if c[:3] != (255, 255, 255))
            want_set = set(glb_corner_colours(glb))
            row['render_lod0'] = {'corners': len(rend), 'non_white_corners': non_white,
                                  'corners_with_a_glb_colour': sum(1 for c in rend if c in want_set)}
            if not rend or non_white == 0:
                row['ok'] = False
                row['error'] = 'the render data (Nanite fallback) has no vertex colours: %s' % row['render_lod0']
        except Exception as e:
            row['ok'] = False
            row['error'] = str(e)
        if not row['ok']:
            err('vertex colours %s: %s' % (name, row.get('error')))
        REPORT['vc_rows'].append(row)
    for name in S.GRASS_LOD_VARIANTS:
        path = '%s/%s_L' % (S.MESH_DIR['L0'], name)
        mrow = REPORT['meshes'].get(path) or {}
        glbs = mrow.get('lod_glbs') or []
        try:
            mesh = EAL.load_asset(path)
            if mesh is None or not glbs:
                raise RuntimeError('missing %s or its LOD glbs' % path)
            blocks = export_fbx(mesh, '%s/vc/%s_L_render_lods.fbx' % (OUT_DIR, name), False, True)
            if len(blocks) != len(glbs):
                raise RuntimeError('%d colour blocks for %d LODs' % (len(blocks), len(glbs)))
            for lod, g in enumerate(glbs):
                want = glb_corner_colours('%s/L0/%s' % (SRC, g))
                match = [b for b in blocks if len(b) == len(want)]
                row = {'mesh': name + '_L', 'path': path, 'mode': 'final_render_lod', 'lod': lod, 'glb': g, 'ok': False}
                if len(match) != 1:
                    row['error'] = '%d colour blocks with %d corners (block sizes %s)' % (len(match), len(want), [len(b) for b in blocks])
                else:
                    row.update(compare_colours(want, match[0]))
                if not row['ok']:
                    err('vertex colours %s LOD%d: %s' % (path, lod, row.get('error')))
                REPORT['vc_rows'].append(row)
        except Exception as e:
            err('vertex colours %s: %s' % (path, e))
            REPORT['vc_rows'].append({'mesh': name + '_L', 'path': path, 'mode': 'final_render_lod', 'ok': False, 'error': str(e)})
    parsed = S.parse_hlsl()
    reads = {m: S.master_reads_vertex_colour(m, parsed) for m in S.masters()}
    need, have, missing = 0, 0, []
    for path, mrow in sorted(REPORT['meshes'].items()):
        parents = [REPORT['instances'].get(m.get('instance') or '', {}).get('parent') for m in (mrow.get('materials') or [])]
        needs = any(reads.get(p, False) for p in parents)
        mesh = EAL.load_asset(path)
        has = bool(sms.has_vertex_colors(mesh)) if mesh is not None else False
        mrow['has_vertex_colors'] = has
        mrow['vertex_colours_needed'] = needs
        if needs:
            need += 1
            have += 1 if has else 0
            if not has:
                missing.append(path)
    REPORT['vc_expect'] = {'masters_reading_vc': sorted(m for m, r in reads.items() if r), 'meshes_needing_vc': need,
                           'meshes_needing_vc_with_vc': have, 'missing': missing}
    for p in missing:
        err('%s: its material reads vertex colour but the saved mesh has none (HasVertexColors false)' % p)
    n_rows = len(VC_MESHES) + sum(len((REPORT['meshes'].get('%s/%s_L' % (S.MESH_DIR['L0'], n)) or {}).get('lod_glbs') or [1, 2, 3])
                                  for n in S.GRASS_LOD_VARIANTS)
    REPORT['vc_ok'] = (len(REPORT['vc_rows']) == n_rows and all(r['ok'] for r in REPORT['vc_rows']) and not missing and need > 0)


def agg_geom_counts(mesh):
    """Simple collision shapes of the mesh's body setup by kind (the property route; the subsystem route needs the StaticMeshEditor module)."""
    bsetup = mesh.get_editor_property('body_setup')
    if bsetup is None:
        return {}
    ag = bsetup.get_editor_property('agg_geom')
    out = {}
    for prop in ('sphere_elems', 'box_elems', 'sphyl_elems', 'convex_elems', 'tapered_capsule_elems', 'level_set_elems', 'skinned_level_set_elems'):
        v = get_prop(ag, prop)
        if v is not None:
            out[prop] = len(v)
    return out


def build_settings_via_properties(mesh, write):
    """LOD0 build settings through SourceModels (the property route). write=True sets DistanceFieldResolutionScale 0 and lightmap UVs off."""
    models = mesh.get_editor_property('source_models')
    bs = models[0].get_editor_property('build_settings')
    if write:
        set_props(bs, {'distance_field_resolution_scale': 0.0, 'generate_lightmap_uvs': False}, 'build settings')
        models[0].set_editor_property('build_settings', bs)
        mesh.set_editor_property('source_models', models)
        mesh.post_edit_change()
        bs = mesh.get_editor_property('source_models')[0].get_editor_property('build_settings')
    return bs


def apply_mesh_settings(mesh, name, nanite, fallback_percent=None):
    """Distance field off, simple collision off, lightmap UVs off, Nanite per slot. Every value is read back."""
    sms = mesh_sub()
    try:
        if sms is not None:
            bs = sms.get_lod_build_settings(mesh, 0)
            set_props(bs, {'distance_field_resolution_scale': 0.0, 'generate_lightmap_uvs': False}, 'build settings ' + name)
            sms.set_lod_build_settings(mesh, 0, bs)
        else:
            build_settings_via_properties(mesh, True)
    except Exception as e:
        err('%s: build settings: %s' % (name, e))
    try:
        # Ignored while the project forces r.GenerateMeshDistanceFields; the LOD0 resolution scale above is the control.
        mesh.set_editor_property('generate_mesh_distance_field', False)
    except Exception as e:
        warn('%s: generate_mesh_distance_field: %s' % (name, e))
    try:
        if sms is not None:
            sms.remove_collisions(mesh)
    except Exception as e:
        err('%s: remove_collisions: %s' % (name, e))
    try:
        bsetup = mesh.get_editor_property('body_setup')
        if bsetup is not None:
            # Simple-as-complex with no simple shapes: no collision at all, and no triangle-mesh cook of a 30k-triangle tree.
            bsetup.set_editor_property('collision_trace_flag', enum_by_suffix(unreal.CollisionTraceFlag, 'USE_SIMPLE_AS_COMPLEX'))
    except Exception as e:
        warn('%s: collision trace flag: %s' % (name, e))
    try:
        set_nanite(mesh, nanite, fallback_percent)
    except Exception as e:
        err('%s: nanite settings: %s' % (name, e))


def read_back_mesh(mesh, name):
    sms = mesh_sub()
    row = {}
    try:
        if sms is not None:
            row['nanite'] = bool(sms.get_nanite_settings(mesh).get_editor_property('enabled'))
        else:
            row['nanite'] = bool(mesh.get_editor_property('nanite_settings').get_editor_property('enabled'))
    except Exception as e:
        err('%s: read nanite: %s' % (name, e))
        row['nanite'] = None
    try:
        bs = sms.get_lod_build_settings(mesh, 0) if sms is not None else build_settings_via_properties(mesh, False)
        row['distance_field_resolution_scale'] = float(get_prop(bs, 'distance_field_resolution_scale'))
        row['generate_lightmap_uvs'] = bool(get_prop(bs, 'generate_lightmap_uvs'))
    except Exception as e:
        err('%s: read build settings: %s' % (name, e))
    try:
        counts = agg_geom_counts(mesh)
        row['simple_collision_by_kind'] = counts
        row['simple_collision_shapes'] = sum(counts.values())
        if sms is not None and int(sms.get_simple_collision_count(mesh)) != row['simple_collision_shapes']:
            warn('%s: subsystem collision count %s != body setup %s' % (name, sms.get_simple_collision_count(mesh), row['simple_collision_shapes']))
    except Exception as e:
        err('%s: read collision: %s' % (name, e))
        row['simple_collision_shapes'] = None
    try:
        row['lod_count'] = int(sms.get_lod_count(mesh)) if sms is not None else int(mesh.get_num_lods())
    except Exception as e:
        warn('%s: lod count: %s' % (name, e))
        row['lod_count'] = None
    # Source triangles: LOD0's mesh description (UStaticMesh::GetStaticMeshDescription, StaticMesh.h:1822; UMeshDescriptionBase::GetTriangleCount,
    # MeshDescriptionBase.h:210), which is the full-resolution source also for a Nanite mesh; the render LOD0 of a Nanite mesh is its fallback.
    try:
        row['triangles_source'] = int(mesh.get_static_mesh_description(0).get_triangle_count())
    except Exception as e:
        err('%s: source triangles: %s' % (name, e))
        row['triangles_source'] = None
    try:
        row['triangles_render_lod0'] = int(mesh.get_num_triangles(0))
    except Exception as e:
        warn('%s: render triangles: %s' % (name, e))
    try:
        ns = sms.get_nanite_settings(mesh) if sms is not None else mesh.get_editor_property('nanite_settings')
        fbt = get_prop(ns, 'fallback_target')
        row['nanite_fallback'] = {'target': str(getattr(fbt, 'name', fbt)),
                                  'percent_triangles': round(float(get_prop(ns, 'fallback_percent_triangles')), 4)}
    except Exception as e:
        warn('%s: nanite fallback settings: %s' % (name, e))
    try:
        n_lods = row.get('lod_count') or 1
        if n_lods > 1:
            sizes = [round(float(x), 4) for x in sms.get_lod_screen_sizes(mesh)] if sms is not None else []
            row['lods'] = [{'lod': i, 'triangles': int(mesh.get_num_triangles(i)), 'screen_size': sizes[i] if i < len(sizes) else None}
                           for i in range(n_lods)]
    except Exception as e:
        warn('%s: per-LOD triangles: %s' % (name, e))
    try:
        row['has_vertex_colors'] = bool(sms.has_vertex_colors(mesh)) if sms is not None else None
    except Exception as e:
        warn('%s: has_vertex_colors: %s' % (name, e))
    try:
        bb = mesh.get_bounds()
        row['bounds_cm'] = {'origin': [round(bb.origin.x, 2), round(bb.origin.y, 2), round(bb.origin.z, 2)],
                            'extent': [round(bb.box_extent.x, 2), round(bb.box_extent.y, 2), round(bb.box_extent.z, 2)]}
    except Exception as e:
        warn('%s: bounds: %s' % (name, e))
    if row.get('distance_field_resolution_scale', 1.0) != 0.0:
        err('%s: distance field resolution scale %s (want 0)' % (name, row.get('distance_field_resolution_scale')))
    if row.get('simple_collision_shapes') not in (0, None):
        err('%s: %d simple collision shapes remain' % (name, row['simple_collision_shapes']))
    return row


def relocate_l1_textures(mesh_name, objs, info):
    """Rename the Interchange textures of an L1 mesh to /Game/Terrain/Scatter/Textures/L1/T_<mesh>_<image>; returns ({image: Texture2D}, duplicates).
    Interchange makes one texture per glTF *texture*, not per image, so a glb whose materials point several textures at one image (the searsia
    shrubs: 9 textures, 4 images) gets <image>, <image>1, <image>2 ...: identical data. The expected copies come from the glb itself (k glTF
    textures on one image -> <image> plus <image>1..<image>k-1); a copy name that is also another image's name, a missing copy or a copy of
    another size is an error. The first is kept; the others are returned to be deleted once the Interchange materials are gone."""
    EAL.make_directory(S.TEX_DIR + '/L1')
    names = [i for i in info['images']]
    if len(set(names)) != len(names):
        err('%s: the glb has two images with one name: %s' % (mesh_name, names))
    tex_objs = [o for o in objs if isinstance(o, unreal.Texture2D)]
    by_norm = {norm(o.get_name()): o for o in tex_objs}
    image_norms = set(norm(i) for i in names)
    out, dupes = {}, []
    for i, img in enumerate(info['images']):
        key = norm(img)
        k = max(1, info['texture_refs'][i])
        cands = [key] + [key + str(j) for j in range(1, k)]
        clash = [c for c in cands[1:] if c in image_norms]
        if clash:
            err('%s: copy names %s of image %s are also image names: duplicates cannot be told apart' % (mesh_name, clash, img))
            continue
        if key not in by_norm:
            alt = [n for n in (norm('Texture_%d' % i), norm('Image_%d' % i)) if n in by_norm][:1]
            if not alt or k > 1:
                err('%s: no imported texture for glb image %s (imported: %s)' % (mesh_name, img, sorted(by_norm)))
                continue
            cands = alt
        missing = [c for c in cands if c not in by_norm]
        if missing:
            err('%s: image %s is used by %d glTF textures but %s were not imported' % (mesh_name, img, k, missing))
            continue
        t = by_norm[cands[0]]
        size = tex_size(t)
        for c in cands[1:]:
            if tex_size(by_norm[c]) != size:
                err('%s: copy %s of image %s is %s, the kept texture %s' % (mesh_name, c, img, tex_size(by_norm[c]), size))
        dupes.extend(by_norm[c] for c in cands[1:])
        new = '%s/L1/T_%s_%s' % (S.TEX_DIR, mesh_name, re.sub(r'[^A-Za-z0-9_]', '_', img))
        old = pkg(t)
        if old != new:
            if EAL.does_asset_exist(new):
                EAL.delete_asset(new)
            if not EAL.rename_asset(old, new):
                err('%s: rename %s -> %s failed' % (mesh_name, old, new))
                continue
            if EAL.does_asset_exist(old):
                EAL.delete_asset(old)  # the redirector the rename left
        tex = EAL.load_asset(new)
        kind = role_of(info, img)
        if kind is None:
            warn('%s: image %s is used by no material role; treated as masks' % (mesh_name, img))
            kind = 'masks'
        if kind == 'normal_gl':
            # F23 gate: Interchange itself must have flipped the green of a glTF normal texture (the value before this script touches it).
            orig = bool(get_prop(tex, 'flip_green_channel'))
            REPORT.setdefault('flip_original', {})[new] = orig
            if not orig:
                err('%s: Interchange left flip_green_channel False on the glTF normal map %s (F23 says it flips)' % (mesh_name, new))
        apply_tex_props(tex, kind, new)
        if not EAL.save_loaded_asset(tex, False):
            err('save %s failed' % new)
        out[img] = tex
    return out, dupes


def tex_size(tex):
    """(width, height) of a Texture2D: UTexture2D::Blueprint_GetSizeX/Y (Texture2D.h:361), whose Python spelling is not guessed."""
    for fx, fy in (('blueprint_get_size_x', 'blueprint_get_size_y'), ('get_size_x', 'get_size_y')):
        if hasattr(tex, fx) and hasattr(tex, fy):
            return (int(getattr(tex, fx)()), int(getattr(tex, fy)()))
    err('%s: no size accessor among %s' % (tex.get_name(), [n for n in dir(tex) if 'size' in n.lower()]))
    return None


def role_of(info, image):
    for m in info['materials']:
        for role in ('base', 'arm', 'normal'):
            if m[role] == image:
                return {'base': 'color', 'arm': 'masks', 'normal': 'normal_gl'}[role]
    return None


def process_mesh(level, name, glb, slot, expected_tris, species=None):
    """Import one mesh, give it its material instances, apply the mesh settings, read everything back. Returns the StaticMesh or None."""
    dest = S.MESH_DIR[level]
    path = '%s/%s' % (dest, name)
    t0 = time.time()
    info = glb_info(glb)
    objs = import_glb(glb, dest, name)
    meshes = [o for o in objs if isinstance(o, unreal.StaticMesh)]
    if len(meshes) != 1:
        err('%s %s: %d StaticMesh imported (%s)' % (level, name, len(meshes), [o.get_path_name() for o in objs]))
        return None
    mesh = meshes[0]
    old_path = pkg(mesh)
    if old_path != path:
        warn('%s %s: Interchange named the mesh %s; renaming' % (level, name, old_path))
        if not EAL.rename_asset(old_path, path):
            err('%s %s: rename %s -> %s failed' % (level, name, old_path, path))
            return None
        if EAL.does_asset_exist(old_path):
            EAL.delete_asset(old_path)  # the redirector the rename left
        mesh = EAL.load_asset(path)
    row = {'path': path, 'level': level, 'slot': slot, 'glb': os.path.basename(glb), 'triangles_expected': expected_tris}
    # Vertex colours are read back on the final saved assets, after every step (step_vertex_colours), not here.
    textures, dupes = {}, []
    if level == 'L1':
        textures, dupes = relocate_l1_textures(name, objs, info)
    slots = slot_names(mesh)
    row['slots'] = [s[0] for s in slots]
    mats = []
    for idx, (sname, imported) in enumerate(slots):
        gm = next((m for m in info['materials'] if m['name'] in (sname, imported)), None)
        if gm is None and idx < len(info['materials']):
            gm = info['materials'][idx]
            warn('%s: slot %s matched glb material %s by index' % (name, sname, gm['name']))
        if gm is None:
            err('%s: slot %s has no glb material' % (name, sname))
            mats.append(None)
            continue
        mic_name = 'MI_%s_%s' % (name, re.sub(r'[^A-Za-z0-9_]', '_', gm['name']))
        if level == 'L0':
            try:
                master, scal, vec, texmap = S.l0_rule(species, gm['name'])
            except KeyError as e:
                err('%s: %s' % (name, e))
                mats.append(None)
                continue
            tex = {}
            for param, tname in texmap.items():
                tex[param] = load_texture(tname, ground=tname in S.GROUND_ROCK.values())
        else:
            master, scal, vec = S.l1_rule(name, slot, gm)
            tex = {}
            for param, key in (('BaseColorTex', 'base'), ('NormalTex', 'normal'), ('ARMTex', 'arm')):
                img = gm.get(key)
                if img is not None:
                    tex[param] = textures.get(img)
                else:
                    tex[param] = load_texture(S.DEFAULT_TEXTURES[{'base': 'color', 'normal': 'normal', 'arm': 'masks'}[key]])
        mic = make_instance(level, mic_name, master, MASTER_ASSETS, scal, vec, tex)
        mats.append(mic)
        if mic is not None:
            mesh.set_material(idx, mic)
    row['materials'] = [{'slot': slots[i][0], 'instance': None if m is None else pkg(m)} for i, m in enumerate(mats)]
    # Interchange's own materials and (L1) the textures they referenced are no longer needed by the mesh: delete the materials.
    for o in objs:
        if isinstance(o, unreal.MaterialInterface) and pkg(o) not in set(pkg(m) for m in mats if m is not None):
            if not EAL.delete_asset(pkg(o)):
                err('%s: could not delete the Interchange material %s' % (name, pkg(o)))
    for d in dupes:  # identical duplicate textures (see relocate_l1_textures); their materials are gone, so they can go too
        dp = pkg(d)
        if EAL.does_asset_exist(dp) and not EAL.delete_asset(dp):
            err('%s: could not delete the duplicate texture %s' % (name, dp))
    row['duplicate_textures_deleted'] = len(dupes)
    apply_mesh_settings(mesh, name, S.is_nanite_slot(slot), S.NANITE_FALLBACK_PERCENT.get(level, {}).get(name))
    if not EAL.save_loaded_asset(mesh, False):
        err('save %s failed' % path)
    row.update(read_back_mesh(mesh, name))
    if row.get('triangles_source') is not None and expected_tris:
        drift = abs(row['triangles_source'] - expected_tris) / float(expected_tris)
        row['triangles_drift'] = round(drift, 5)
        row['triangles_ok'] = drift <= S.MAX_TRI_DRIFT
        if not row['triangles_ok']:
            err('%s: imported source triangles %d vs %d expected (%.2f %%)' % (name, row['triangles_source'], expected_tris, drift * 100))
    want_nanite = S.is_nanite_slot(slot)
    if row.get('nanite') is not want_nanite:
        err('%s: nanite %s (want %s)' % (name, row.get('nanite'), want_nanite))
    # Interchange imports into <dest>/<name>/{StaticMeshes,Materials,Textures}: nothing may be left in there once the mesh is renamed out, the
    # materials deleted and (L1) the textures moved; then the empty folder goes (it shares its name with the mesh asset).
    folder = '%s/%s' % (dest, name)
    if EAL.does_directory_exist(folder):
        left = assets_in_folder(folder)
        if left:
            err('%s %s: Interchange left %s' % (level, name, left))
        EAL.delete_directory(folder)
    row['wall_s'] = round(time.time() - t0, 1)
    REPORT['meshes'][path] = row
    log('mesh %s %s: tris source=%s expected=%s nanite=%s (%.1f s)' % (level, name, row.get('triangles_source'), expected_tris, row.get('nanite'), row['wall_s']))
    return mesh


VC_MESHES = ['GrassT0', 'TreeBroadA', 'RockA']
OUTSIDE_BEFORE = set()
MASTER_ASSETS = {}


def step_meshes(level):
    cfg = S.meshes_config()
    species = {s['name']: s for s in cfg['species']}
    if level == 'L0':
        rep = json.load(open(SRC + '/L0/report.json', encoding='utf-8'))
        rows = [(m['name'], SRC + '/L0/' + m['file'], m['slot'], m['triangles']) for m in rep['meshes']]
    else:
        rep = json.load(open(SRC + '/prepared/report.json', encoding='utf-8'))
        rows = [(m['name'], SRC + '/prepared/' + m['file'], m['slot'], m['triangles']) for m in rep['meshes']]
    done = []
    for name, glb, slot, tris in rows:
        if ONLY and name not in ONLY:
            continue
        try:
            sp = species.get(name) if level == 'L0' else None
            if level == 'L0' and sp is None:
                err('L0 %s: no species row in meshes.json' % name)
                continue
            m = process_mesh(level, name, glb, slot, tris, sp)
            if m is not None:
                done.append(name)
        except Exception as e:
            import traceback
            err('%s %s: %s\n%s' % (level, name, e, traceback.format_exc()))
    return done


def delete_tmp_dir():
    """Delete S.TMP_DIR (the LOD glbs' temporary imports) and assert nothing is left in it."""
    if EAL.does_directory_exist(S.TMP_DIR):
        for p in assets_in_folder(S.TMP_DIR):
            if EAL.does_asset_exist(p) and not EAL.delete_asset(p):
                err('could not delete the temporary asset %s' % p)
        EAL.delete_directory(S.TMP_DIR)
    left = assets_in_folder(S.TMP_DIR) if EAL.does_directory_exist(S.TMP_DIR) else []
    if left:
        err('temporary assets left in %s: %s' % (S.TMP_DIR, left))


def attach_lod(mesh, dst, lod, glb, mic):
    """Import one LOD glb into S.TMP_DIR and copy its LOD0 into LOD `lod` of `mesh` (StaticMeshEditorSubsystem::SetLodFromStaticMesh,
    StaticMeshEditorSubsystem.h:143: it copies the source LOD's mesh description and build settings, and with bReuseExistingMaterialSlots
    maps a section whose material equals a destination slot's material onto that slot, so the temporary mesh gets the destination's
    instance first). The temporary mesh's LOD0 build settings are set as the destination's (no distance field, no lightmap UVs) before the
    copy. Returns the LOD index the engine set (negative = failed)."""
    sms = mesh_sub()
    name = os.path.splitext(os.path.basename(glb))[0]
    objs = import_glb(glb, S.TMP_DIR, name)
    tm = [o for o in objs if isinstance(o, unreal.StaticMesh)]
    if len(tm) != 1:
        err('%s LOD%d: %d StaticMesh imported from %s' % (dst, lod, len(tm), glb))
        return -1
    tmesh = tm[0]
    n_slots = len(tmesh.get_editor_property('static_materials'))
    if n_slots != 1:
        err('%s LOD%d: the LOD glb has %d material slots (want 1)' % (dst, lod, n_slots))
        return -1
    tmesh.set_material(0, mic)
    bs = sms.get_lod_build_settings(tmesh, 0)
    set_props(bs, {'distance_field_resolution_scale': 0.0, 'generate_lightmap_uvs': False}, 'build settings %s' % name)
    sms.set_lod_build_settings(tmesh, 0, bs)
    return int(sms.set_lod_from_static_mesh(mesh, lod, tmesh, 0, True))


def step_lod_variants():
    """Grass slots: <Name>_L = a copy with Nanite off and the three-LOD chain of make_scatter_meshes.py (the Nanite-or-LOD A/B of plan 1.3
    and 3.7): LOD0 the <Name> mesh itself, LOD1 and LOD2 the <Name>_LOD1/_LOD2 glbs (blade subsets in fade order, ScatterSrc/L0/report.json
    lod_chains), screen sizes S.GRASS_LOD_SCREEN_SIZES. Every LOD's render triangles must equal its glb's, and the chain must strictly
    reduce (the first S3 pass built 40/40/40 with set_lods: QuadricMeshReduction does not reduce open blade strips)."""
    sms = mesh_sub()
    l0rep = json.load(open(SRC + '/L0/report.json', encoding='utf-8'))
    chains = {c['name']: c for c in l0rep.get('lod_chains', [])}
    for name in S.GRASS_LOD_VARIANTS:
        if ONLY and name not in ONLY:
            continue
        src = '%s/%s' % (S.MESH_DIR['L0'], name)
        dst = src + '_L'
        try:
            chain = chains.get(name)
            if chain is None or len(chain['lods']) != len(S.GRASS_LOD_SCREEN_SIZES):
                err('%s: ScatterSrc/L0/report.json has no %d-LOD chain for %s (rerun make_scatter_meshes.py)' % (dst, len(S.GRASS_LOD_SCREEN_SIZES), name))
                continue
            if EAL.does_asset_exist(dst):
                EAL.delete_asset(dst)
            mesh = EAL.duplicate_asset(src, dst)
            if mesh is None:
                err('duplicate %s -> %s failed' % (src, dst))
                continue
            ns = sms.get_nanite_settings(mesh)
            ns.set_editor_property('enabled', False)
            sms.set_nanite_settings(mesh, ns, True)
            # The _L copy draws through the fading Blade master: same parameter values as the Nanite original's instance (plan 3.7, risk 8).
            mats = []
            for idx, m in enumerate(REPORT['meshes'].get(src, {}).get('materials') or []):
                orig = REPORT['instances'].get(m.get('instance') or '')
                if orig is None:
                    err('%s: slot %s has no original instance' % (dst, m.get('slot')))
                    mats.append({'slot': m.get('slot'), 'instance': None})
                    continue
                inst_name = 'MI_%s_L_%s' % (name, m['instance'].rsplit('/', 1)[1][len('MI_%s_' % name):])
                tex = {k: load_texture(v) for k, v in orig['textures'].items()}
                mic = make_instance('L0', inst_name, S.LOD_VARIANT_MASTER, MASTER_ASSETS, orig['scalars'], orig['vectors'], tex)
                if mic is not None:
                    mesh.set_material(idx, mic)
                mats.append({'slot': m.get('slot'), 'instance': None if mic is None else pkg(mic)})
            if len(mats) != 1 or mats[0]['instance'] is None:
                err('%s: want one material slot with an instance, have %s' % (dst, mats))
                continue
            mic = EAL.load_asset(mats[0]['instance'])
            for l in chain['lods'][1:]:
                got = attach_lod(mesh, dst, l['lod'], SRC + '/L0/' + l['file'], mic)
                if got != l['lod']:
                    err('%s: set_lod_from_static_mesh(LOD%d, %s) returned %s' % (dst, l['lod'], l['file'], got))
            delete_tmp_dir()
            if not sms.set_lod_screen_sizes(mesh, [float(x) for x in S.GRASS_LOD_SCREEN_SIZES]):
                err('%s: set_lod_screen_sizes failed' % dst)
            n_slots = len(mesh.get_editor_property('static_materials'))
            if n_slots != 1:
                err('%s: %d material slots after the LOD copy (want 1: the LOD sections reuse slot 0)' % (dst, n_slots))
            if not EAL.save_loaded_asset(mesh, False):
                err('save %s failed' % dst)
            row = {'path': dst, 'level': 'L0', 'slot': name, 'variant': 'lod', 'materials': mats,
                   'lod_glbs': [l['file'] for l in chain['lods']], 'lod_triangles_expected': [l['triangles'] for l in chain['lods']]}
            row.update(read_back_mesh(mesh, name + '_L'))
            if row.get('nanite') is not False:
                err('%s: nanite %s (want False)' % (dst, row.get('nanite')))
            if row.get('lod_count') != len(S.GRASS_LOD_SCREEN_SIZES):
                err('%s: %s LODs (want %d)' % (dst, row.get('lod_count'), len(S.GRASS_LOD_SCREEN_SIZES)))
            got_tris = [x['triangles'] for x in row.get('lods') or []]
            if got_tris != row['lod_triangles_expected']:
                err('%s: LOD triangles %s != the glbs %s' % (dst, got_tris, row['lod_triangles_expected']))
            row['lod_chain_reduces'] = len(got_tris) >= 2 and all(a > b for a, b in zip(got_tris, got_tris[1:]))
            if not row['lod_chain_reduces']:
                err('%s: the LOD chain does not reduce: %s' % (dst, got_tris))
            got_sizes = [x['screen_size'] for x in row.get('lods') or []]
            if len(got_sizes) != len(S.GRASS_LOD_SCREEN_SIZES) or any(g is None or abs(g - w) > 1e-3 for g, w in zip(got_sizes, S.GRASS_LOD_SCREEN_SIZES)):
                err('%s: LOD screen sizes %s != %s' % (dst, got_sizes, S.GRASS_LOD_SCREEN_SIZES))
            REPORT['meshes'][dst] = row
            log('lod variant %s: %d LODs, triangles %s, nanite=%s' % (dst, row.get('lod_count'), got_tris, row.get('nanite')))
        except Exception as e:
            import traceback
            err('lod variant %s: %s\n%s' % (name, e, traceback.format_exc()))


# ------------------------------------------------------------------------------------------------------------------ checks
def step_verify():
    reg = unreal.AssetRegistryHelpers.get_asset_registry()
    reg.scan_paths_synchronous([S.ROOT], True)  # dependencies are on-disk data: rescan what the run saved
    classes = {}
    for a in reg.get_assets_by_path(S.ROOT, True):
        try:
            cls = str(a.get_editor_property('asset_class_path').get_editor_property('asset_name'))
        except Exception:
            cls = str(get_prop(a, 'asset_class', ''))
        classes[str(a.get_editor_property('package_name'))] = cls
    pkgs = sorted(classes)

    # 1. usage flags per (mesh, material) row
    ok = 0
    rows = []
    for mpath, mrow in sorted(REPORT['meshes'].items()):
        for m in (mrow.get('materials') or []):
            inst = m.get('instance')
            if inst is None:
                rows.append({'mesh': mpath, 'slot': m.get('slot'), 'ok': False, 'why': 'no instance'})
                continue
            mic = EAL.load_asset(inst)
            ism = bool(MEL.has_material_usage(mic, usage('INSTANCED_STATIC_MESHES')))
            nan = bool(MEL.has_material_usage(mic, usage('NANITE')))
            want_nanite = bool(mrow.get('nanite'))
            good = ism and (nan or not want_nanite)
            ok += 1 if good else 0
            rows.append({'mesh': mpath, 'slot': m.get('slot'), 'instance': inst, 'instanced_static_meshes': ism, 'nanite_usage': nan,
                         'nanite_row': want_nanite, 'ok': good})
    REPORT['usage_rows'] = rows
    REPORT['usage_ok'] = '%d/%d' % (ok, len(rows))
    if ok != len(rows) or not rows:
        err('usage flags: %s rows ok' % REPORT['usage_ok'])
    # Every instance's parent master carries no per-instance random (the master rows hold the expression scan) ...
    for inst, r in REPORT['instances'].items():
        mr = REPORT['masters'].get(r['parent'])
        if mr is None or mr.get('per_instance_random'):
            err('%s: parent %s uses per-instance random or is missing' % (inst, r['parent']))
    # ... and the engine's own statistic: the HasPerInstanceRandom asset-registry tag of every master and instance (MaterialInterface.cpp:1162,
    # from FMaterialCachedExpressionData::bHasPerInstanceRandom, MaterialCachedData.cpp:675-678) must read 'False'.
    tag_rows = {}
    for a in reg.get_assets_by_path(S.ROOT + '/Materials', True):
        pth = str(a.get_editor_property('package_name'))
        row = {}
        for tag in ('HasPerInstanceRandom', 'HasPerInstanceCustomData'):
            res = unreal.AssetRegistryHelpers.get_tag_value(a, tag)
            val = res[1] if isinstance(res, tuple) else res
            row[tag] = None if val in (None, '') else str(val)
        tag_rows[pth] = row
        if row['HasPerInstanceRandom'] != 'False':
            err('%s: HasPerInstanceRandom tag %r (want False)' % (pth, row['HasPerInstanceRandom']))
    REPORT['material_tags'] = tag_rows
    want_mats = set(REPORT['instances']) | set(m['path'] for m in REPORT['masters'].values())
    if set(tag_rows) != want_mats:
        err('material tag rows %d != materials %d (missing %s)' % (len(tag_rows), len(want_mats), sorted(want_mats - set(tag_rows))[:10]))

    # 2. game dependencies
    opts = unreal.AssetRegistryDependencyOptions()
    set_props(opts, {'include_soft_package_references': True, 'include_hard_package_references': True,
                     'include_game_package_references': True, 'include_editor_only_package_references': False,
                     'include_searchable_names': False}, 'dependency options')
    deps_ok = True
    for p in pkgs:
        res = reg.get_dependencies(p, opts)
        deps = res[1] if isinstance(res, tuple) else res
        deps = sorted(str(d) for d in (deps or []))
        REPORT['deps'][p] = deps
        bad = [d for d in deps if not d.startswith(S.ALLOWED_DEP_PREFIXES)]
        if bad:
            deps_ok = False
            err('%s: game dependencies outside /Game/Terrain, /Engine, /BaseMaterial, /Script: %s' % (p, bad))
    REPORT['deps_ok'] = deps_ok

    # 3a. every asset this run made is under /Game/Terrain/Scatter: all of /Game outside it is exactly what it was before the run
    outside_now = [p for p in assets_in_folder('/Game') if not p.startswith(S.ROOT + '/')]
    new_outside = sorted(set(outside_now) - OUTSIDE_BEFORE)
    gone_outside = sorted(OUTSIDE_BEFORE - set(outside_now))
    REPORT['outside_scatter'] = {'before': len(OUTSIDE_BEFORE), 'after': len(outside_now), 'new': new_outside, 'gone': gone_outside}
    if new_outside or gone_outside:
        err('assets outside %s changed: new %s, gone %s' % (S.ROOT, new_outside, gone_outside))
    # 3. nothing stray under the mesh folders (Interchange sub-folders, redirectors)
    expect_mesh = set(REPORT['meshes'].keys())
    for p in pkgs:
        c = classes.get(p, '')
        if p.startswith(S.ROOT + '/Meshes/'):
            if p not in expect_mesh:
                REPORT['unexpected_assets'].append({'path': p, 'class': c})
        if c == 'ObjectRedirector':
            REPORT['unexpected_assets'].append({'path': p, 'class': c})
    if REPORT['unexpected_assets']:
        err('unexpected assets under the scatter root: %s' % [u['path'] for u in REPORT['unexpected_assets']][:20])

    # 4. textures: role settings, never_stream, flip-green on every normal map
    flip_ok = True
    for p in pkgs:
        if classes.get(p) != 'Texture2D':
            continue
        t = EAL.load_asset(p)
        cs = get_prop(t, 'compression_settings')
        is_normal = cs == unreal.TextureCompressionSettings.TC_NORMALMAP
        row = {'path': p, 'compression': getattr(cs, 'name', str(cs)), 'srgb': bool(get_prop(t, 'srgb')), 'never_stream': bool(get_prop(t, 'never_stream')),
               'virtual_texture_streaming': bool(get_prop(t, 'virtual_texture_streaming')), 'flip_green_channel': bool(get_prop(t, 'flip_green_channel')),
               'normal': is_normal, 'flip_original': REPORT.get('flip_original', {}).get(p)}
        if not row['never_stream']:
            err('%s: never_stream is false' % p)
        if is_normal and p != '%s/%s' % (S.TEX_DIR, S.DEFAULT_TEXTURES['normal']) and not row['flip_green_channel']:
            flip_ok = False
            err('%s: normal map without flip-green (GL source)' % p)
        REPORT['textures'][p] = row
        REPORT['flip_rows'].append({'path': p, 'normal': is_normal, 'flip_green_channel': row['flip_green_channel']})
    gl = REPORT.get('flip_original', {})
    REPORT['flip_counts'] = {'normal_maps': sum(1 for r in REPORT['textures'].values() if r['normal']),
                             'flipped': sum(1 for r in REPORT['textures'].values() if r['normal'] and r['flip_green_channel']),
                             'gltf_normal_maps': len(gl), 'flipped_by_interchange': sum(1 for v in gl.values() if v),
                             'flat_default_exempt': 1}
    if gl and not all(gl.values()):
        flip_ok = False
    REPORT['flip_ok'] = flip_ok
    if not REPORT.get('vc_ok'):
        err('vertex colour read-back: %s, expectation %s' % ([(r['mesh'], r.get('lod'), r['ok']) for r in REPORT['vc_rows']], REPORT.get('vc_expect')))


SHADER_ERR = re.compile(r'Failed to compile Material|Shader compile error|LogShaderCompilers: Error|LogMaterial: Error')


def scan_own_log():
    """Shader-compile error lines in this run's -ABSLOG file (the async compile's errors are not returned by RecompileMaterial)."""
    cl = str(unreal.SystemLibrary.get_command_line())
    m = re.search(r'-ABSLOG=(?:"([^"]+)"|(\S+))', cl, flags=re.I)
    path = (m.group(1) or m.group(2)) if m else None
    if not path or not os.path.isfile(path):
        warn('own log not found (%s): the shader-compile log scan is left to run_commandlet.ps1' % path)
        REPORT['shader_log'] = {'path': path, 'scanned': False}
        return
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        bad = [ln.strip() for ln in f if SHADER_ERR.search(ln)]
    REPORT['shader_log'] = {'path': path, 'scanned': True, 'error_lines': len(bad)}
    for ln in bad[:20]:
        err('shader compile: ' + ln)


ENGINE_DEP_PREFIXES = ('/BaseMaterial/', '/Engine/')
ENGINE_DEP_NOTES = {S.NOISE_TEX: 'engine plugin noise, shared with the ground material (grass patch field)'}


def engine_dependencies():
    """Every game dependency of a scatter package under /BaseMaterial or /Engine (/Script is code, not content), from the asset-registry
    rows REPORT['deps']: scatter_manifest_fill.py requires an 'epic' manifest row for each one."""
    paths = sorted(set(d for deps in REPORT['deps'].values() for d in deps if d.startswith(ENGINE_DEP_PREFIXES)))
    return [{'path': p, 'licence': 'epic', 'note': ENGINE_DEP_NOTES.get(p, 'engine content')} for p in paths]


SETTINGS_KEYS = ('triangles_source', 'triangles_render_lod0', 'nanite', 'nanite_fallback', 'lod_count', 'lods', 'distance_field_resolution_scale',
                 'simple_collision_shapes', 'generate_lightmap_uvs', 'has_vertex_colors', 'vertex_colours_needed', 'materials')
MASTER_SETTINGS_KEYS = ('section', 'blend', 'two_sided', 'two_sided_read_back', 'nanite', 'fade', 'clip')


def settings_digest():
    """The fields plan S6's from-clean re-run compares (triangle counts, flags, dependencies), without the material scalars and texture
    settings an S6 look round may change: per mesh the SETTINGS_KEYS; per master its MASTER_SETTINGS_KEYS (blend, sidedness, clip, Nanite
    usage, fade); every instance's parent; the usage rows; the dependencies; per texture never_stream and flip.
    S6 compares asset_settings_sha256; deterministic_sha256 is S3's own full-report reproducibility hash."""
    d = {'meshes': {p: {k: r.get(k) for k in SETTINGS_KEYS} for p, r in REPORT['meshes'].items()},
         'masters': {n: {k: m.get(k) for k in MASTER_SETTINGS_KEYS} for n, m in REPORT['masters'].items()},
         'instance_parents': {p: r['parent'] for p, r in REPORT['instances'].items()},
         'usage_rows': REPORT['usage_rows'], 'deps': REPORT['deps'],
         'textures': {p: {'never_stream': t['never_stream'], 'flip_green_channel': t['flip_green_channel'], 'normal': t['normal']}
                      for p, t in REPORT['textures'].items()}}
    return hashlib.sha256(json.dumps(d, sort_keys=True).encode('utf-8')).hexdigest()


def write_report(wall_s, report_arg):
    det = {'meshes': {p: {k: v for k, v in r.items() if k != 'wall_s'} for p, r in REPORT['meshes'].items()},
           'masters': {k: {kk: vv for kk, vv in v.items() if kk != 'stats'} for k, v in REPORT['masters'].items()},
           'instances': REPORT['instances'], 'textures': REPORT['textures'], 'usage_rows': REPORT['usage_rows'], 'deps': REPORT['deps'],
           'vc_rows': [{k: v for k, v in r.items() if k not in ('error',)} for r in REPORT['vc_rows']],
           'material_tags': REPORT.get('material_tags')}
    blob = json.dumps(det, sort_keys=True).encode('utf-8')
    report = {'format': 1, 'steps': STEPS, 'only': ONLY, 'full_run': FULL_RUN,
              'run': {'t': time.strftime('%Y-%m-%dT%H:%M:%S'), 'wall_s': round(wall_s, 1), 'compile_s': TIMES,
                      'engine': str(unreal.SystemLibrary.get_engine_version())},
              'checks': {'usage_ok': REPORT.get('usage_ok'), 'deps_ok': REPORT.get('deps_ok'), 'flip_ok': REPORT.get('flip_ok'),
                         'vc_ok': REPORT.get('vc_ok'), 'errors': len(ERRORS), 'flip_counts': REPORT.get('flip_counts'),
                         'shader_log': REPORT.get('shader_log')},
              'deterministic_sha256': hashlib.sha256(blob).hexdigest(),
              'asset_settings_sha256': settings_digest(),
              'triangle_source_note': 'triangles_source = UStaticMesh::GetStaticMeshDescription(0)->GetTriangleCount() (StaticMesh.h:1822, MeshDescriptionBase.h:210), '
                                      'the full-resolution source for Nanite rows; triangles_render_lod0 is the render LOD0 (the fallback mesh for Nanite rows).',
              'master_stats': {k: v['stats'] for k, v in REPORT['masters'].items()},
              'warnings': WARNINGS, 'errors': ERRORS,
              'masters': REPORT['masters'], 'instances': REPORT['instances'], 'meshes': REPORT['meshes'], 'textures': REPORT['textures'],
              'usage_rows': REPORT['usage_rows'], 'flip_rows': REPORT['flip_rows'], 'vc_rows': REPORT['vc_rows'], 'deps': REPORT['deps'],
              'unexpected_assets': REPORT['unexpected_assets'], 'outside_scatter': REPORT.get('outside_scatter'),
              'material_tags': REPORT.get('material_tags'), 'flip_original': REPORT.get('flip_original'), 'vc_expect': REPORT.get('vc_expect'),
              'packages': len(REPORT['deps']),
              'engine_dependencies': engine_dependencies()}
    os.makedirs(OUT_DIR, exist_ok=True)
    for p in [OUT_DIR + '/report.json'] + ([report_arg] if report_arg else []):
        with open(p, 'w', encoding='utf-8') as f:
            json.dump(report, f, indent=1)
    return report


def main():
    report_arg = sys.argv[1] if len(sys.argv) > 1 else ''
    t0 = time.time()
    log('project %s, steps %s, only %s' % (T, STEPS, ONLY))
    tex_defaults = {}
    OUTSIDE_BEFORE.update(p for p in assets_in_folder('/Game') if not p.startswith(S.ROOT + '/'))
    if FULL_RUN:
        existing = assets_in_folder(S.ROOT)
        if existing:
            msg = ('Content/Terrain/Scatter is not empty (%d packages, e.g. %s): a full run imports from clean only. Delete '
                   'Content/Terrain/Scatter first.' % (len(existing), existing[0]))
            print('SCATTER_FAIL errors=1 ' + msg)
            unreal.log_error('SCATTER_FAIL ' + msg)
            raise RuntimeError(msg)
    if 'textures' in STEPS:
        step_textures()
    for role, nm in S.DEFAULT_TEXTURES.items():
        t = EAL.load_asset('%s/%s' % (S.TEX_DIR, nm))
        if t is None:
            err('default texture %s missing (run the textures step)' % nm)
        tex_defaults[role] = t
    if 'masters' in STEPS:
        MASTER_ASSETS.update(step_masters(tex_defaults))
    else:
        for name in S.masters():
            m = EAL.load_asset('%s/%s' % (S.MAT_DIR, name))
            if m is not None:
                MASTER_ASSETS[name] = m
    if 'l0' in STEPS:
        step_meshes('L0')
    if 'l1' in STEPS:
        step_meshes('L1')
    if 'lod' in STEPS:
        step_lod_variants()
    if 'verify' in STEPS:
        step_vertex_colours()
        step_verify()
        scan_own_log()
    wall = time.time() - t0
    rep = write_report(wall, report_arg)
    n_meshes = len(REPORT['meshes'])
    n_mats = len(REPORT['masters']) + len(REPORT['instances'])
    if ERRORS:
        for e in ERRORS[:40]:
            print('SCATTER_FAIL_DETAIL ' + e.split('\n')[0])
        print('SCATTER_FAIL errors=%d meshes=%d materials=%d' % (len(ERRORS), n_meshes, n_mats))
        unreal.log_error('SCATTER_FAIL errors=%d' % len(ERRORS))
        raise RuntimeError('%d errors' % len(ERRORS))
    if not FULL_RUN:
        print('SCATTER_PARTIAL steps=%s only=%s meshes=%d materials=%d' % (STEPS, ONLY, n_meshes, n_mats))
        return
    k = rep['checks']['usage_ok']
    print('SCATTER_OK meshes=%d materials=%d errors=0 usage_ok=%s deps_ok=%d flip_ok=%d vc_ok=%d' % (
        n_meshes, n_mats, k, 1 if REPORT['deps_ok'] else 0, 1 if REPORT['flip_ok'] else 0, 1 if REPORT['vc_ok'] else 0))


main()
