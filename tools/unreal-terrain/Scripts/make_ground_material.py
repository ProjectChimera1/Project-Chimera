"""Build the ground material of the terrain trial (plan C 3.5, task C7). Runs inside UnrealEditor-Cmd through the Python commandlet
(PythonScriptCommandlet.cpp: -run=pythonscript -script="<this file> [report.json]"), started by Tools/run_commandlet.ps1.

1. Imports the 12 layer textures from T/Textures/<Layer>/T_<Layer>_{C,N,ARH}.png (fetch_textures.py, CC0 Poly Haven) into
   /Game/Terrain/Textures: C = sRGB albedo (TC_Default), N = DirectX normal (TC_Normalmap), ARH = AO/rough/height (TC_Masks, linear).
   Virtual texturing off (plain samplers) and never_stream on: the RMC chunk proxy gives the streamer no texture-usage data, so a
   streamed texture could stay on a low mip (the trial wants the real look, not a streaming question).
   Plus T_SplatDefault, a 4x4 pure-grass RGBA8 texture (uncompressed, linear, clamp) that is the default of the `Splat` parameter;
   at run time the actor's MID replaces it with the transient 640x640 splat (TerrainSplatTexture.cpp).
2. (Re)builds /Game/Terrain/M_ChimeraGround in place, wired like tools/unreal-looktest/lt_build.py:77-140 (legacy pins after
   delete_all_material_expressions, which also drops a default Substrate slab): one Custom node whose Code is Scripts/ChimeraGround.hlsl,
   every texture entering as a Texture Object (each with its own sampler, HLSLMaterialTranslator.cpp CustomExpression), the absolute
   world position and VertexNormalWS, and the scalar parameters the actor's MID drives (HalfExtentM, BrushX/BrushY/BrushRadius) or a
   run may override (-ChimeraTerrainGround=Name=Value,...).
3. recompile_material (translation errors, MaterialEditingLibrary.cpp RecompileMaterial), then get_statistics, which submits and
   FINISHES the shader compile for GMaxRHIShaderPlatform (MaterialEditingLibrary.cpp GetStatistics) so an HLSL error in the Custom node
   shows up as zero pixel-shader instructions instead of passing silently. Saves both assets.
Prints `MATERIAL_OK /Game/Terrain/M_ChimeraGround errors=0 ...` or `MATERIAL_FAIL <reason>` and raises (the commandlet then exits -1).
"""
import json
import os
import struct
import sys
import time
import zlib

import unreal

MEL = unreal.MaterialEditingLibrary
EAL = unreal.EditorAssetLibrary

T = os.path.abspath(unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_dir())).replace('\\', '/').rstrip('/')
TEX_SRC = T + '/Textures'
HLSL = T + '/Scripts/ChimeraGround.hlsl'
TEX_DIR = '/Game/Terrain/Textures'
MAT_DIR = '/Game/Terrain'
MAT_NAME = 'M_ChimeraGround'
MAT_PATH = MAT_DIR + '/' + MAT_NAME
NOISE_TEX = '/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT'  # tools/unreal-looktest/lt_common.py:39
LAYERS = ('Grass', 'Dirt', 'Rock', 'Snow')  # splat channels R, G, B, A = layer ids 0..3 (plan C 3.2)

# Scalar parameters and their defaults (plan C 3.5: tile 3-6 m, second scale x0.21, auto-rock below cos 35 deg).
SCALARS = [
    ('HalfExtentM', 160.0),     # E; the actor sets it from the running terrain
    ('TileGrass', 4.0),
    ('TileDirt', 3.5),
    ('TileRock', 6.0),
    ('TileSnow', 5.0),
    ('Scale2', 0.21),
    ('BlendDepth', 0.2),
    ('RockCos', 0.8192),        # cos 35 deg
    ('RockBand', 0.06),
    ('MacroM', 72.0),           # C7 look pass: 48 m / 0.18 read flat at rts80, 28 m / 0.35 showed the noise repeat; 72 m / 0.25 chosen
    ('MacroStrength', 0.25),
    ('NormalStrength', 1.0),
    ('AOStrength', 1.0),
    ('BrushX', 0.0),            # metres; the actor drives the ring
    ('BrushY', 0.0),
    ('BrushRadius', 0.0),       # 0 = no ring (compare mode)
    ('RingWidthM', 0.3),
    ('RingIntensity', 3.0),
]
OUTPUTS = [('CgNormal', 'CMOT_FLOAT3', unreal.MaterialProperty.MP_NORMAL),
           ('CgRough', 'CMOT_FLOAT1', unreal.MaterialProperty.MP_ROUGHNESS),
           ('CgAO', 'CMOT_FLOAT1', unreal.MaterialProperty.MP_AMBIENT_OCCLUSION),
           ('CgEmissive', 'CMOT_FLOAT3', unreal.MaterialProperty.MP_EMISSIVE_COLOR)]


def log(msg):
    unreal.log('make_ground_material: ' + msg)
    print('make_ground_material: ' + msg)


def fail(msg):
    print('MATERIAL_FAIL ' + msg)
    unreal.log_error('MATERIAL_FAIL ' + msg)
    raise RuntimeError(msg)


def set_props(obj, props):
    for k, v in props.items():
        try:
            obj.set_editor_property(k, v)
        except Exception as e:  # name the object and property (lt_common.set_props)
            fail('%s.%s = %r: %s' % (obj.get_name(), k, v, e))


def load(path):
    obj = EAL.load_asset(path)
    if obj is None:
        fail('could not load ' + path)
    return obj


def write_png_rgba(path, w, h, rgba):
    """Minimal RGBA8 PNG writer (the editor's Python has no Pillow)."""
    raw = b''.join(b'\x00' + bytes(rgba) * w for _ in range(h))

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def import_texture(src, name, props):
    """Interchange import (no factory, lt_import._import_file), then the texture settings, then save; returns the Texture2D."""
    if not os.path.isfile(src):
        fail('texture source missing: ' + src)
    t = unreal.AssetImportTask()
    set_props(t, {'filename': src, 'destination_path': TEX_DIR, 'destination_name': name,
                  'automated': True, 'replace_existing': True, 'save': True})
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([t])
    paths = list(t.get_editor_property('imported_object_paths'))
    tex = [o for o in (EAL.load_asset(p) for p in paths) if isinstance(o, unreal.Texture2D)]
    if len(tex) != 1:
        fail('%s: %d Texture2D from %s (%s)' % (name, len(tex), src, paths))
    tex = tex[0]
    set_props(tex, props)
    for k, v in props.items():
        got = tex.get_editor_property(k)
        if got != v:
            fail('%s.%s read back %r != %r' % (name, k, got, v))
    if not EAL.save_asset(tex.get_path_name().split('.')[0], False):
        fail('save_asset %s failed' % name)
    return tex


def sampler_for(tex):
    """Sampler type the compiler expects for a texture (tools/unreal-looktest/lt_build.py _sampler_for)."""
    st, tc = unreal.MaterialSamplerType, unreal.TextureCompressionSettings
    srgb = bool(tex.get_editor_property('srgb'))
    vt = bool(tex.get_editor_property('virtual_texture_streaming'))
    cs = tex.get_editor_property('compression_settings')
    if cs == tc.TC_NORMALMAP:
        kind = 'NORMAL'
    elif cs == tc.TC_GRAYSCALE:
        kind = 'GRAYSCALE' if srgb else 'LINEAR_GRAYSCALE'
    elif cs == tc.TC_ALPHA:
        kind = 'ALPHA'
    elif cs == tc.TC_MASKS:
        kind = 'MASKS'
    else:
        kind = 'COLOR' if srgb else 'LINEAR_COLOR'
    return getattr(st, ('SAMPLERTYPE_VIRTUAL_' if vt else 'SAMPLERTYPE_') + kind)


def expr(mat, cls, x, y, **props):
    e = MEL.create_material_expression(mat, cls, x, y)
    if e is None:
        fail('create_material_expression(%s) failed' % cls.__name__)
    set_props(e, props)
    return e


def link(a, out, b, inp):
    if not MEL.connect_material_expressions(a, out, b, inp):
        fail('connect %s.%s -> %s.%s failed' % (a.get_name(), out or '<0>', b.get_name(), inp or '<0>'))


def import_all():
    tc = unreal.TextureCompressionSettings
    common = {'virtual_texture_streaming': False, 'never_stream': True}
    textures = {}
    for layer in LAYERS:
        d = '%s/%s' % (TEX_SRC, layer)
        textures[layer + 'C'] = import_texture('%s/T_%s_C.png' % (d, layer), 'T_%s_C' % layer,
                                               dict(common, srgb=True, compression_settings=tc.TC_DEFAULT))
        textures[layer + 'N'] = import_texture('%s/T_%s_N.png' % (d, layer), 'T_%s_N' % layer,
                                               dict(common, srgb=False, compression_settings=tc.TC_NORMALMAP, flip_green_channel=False))
        textures[layer + 'ARH'] = import_texture('%s/T_%s_ARH.png' % (d, layer), 'T_%s_ARH' % layer,
                                                 dict(common, srgb=False, compression_settings=tc.TC_MASKS))
        log('imported %s (C, N, ARH)' % layer)
    splat_png = TEX_SRC + '/T_SplatDefault.png'
    write_png_rgba(splat_png, 4, 4, (255, 0, 0, 0))
    textures['SplatDefault'] = import_texture(splat_png, 'T_SplatDefault', dict(
        common, srgb=False, compression_settings=tc.TC_VECTOR_DISPLACEMENTMAP, filter=unreal.TextureFilter.TF_BILINEAR,
        address_x=unreal.TextureAddress.TA_CLAMP, address_y=unreal.TextureAddress.TA_CLAMP,
        mip_gen_settings=unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS))
    return textures


def build_material(textures):
    with open(HLSL, encoding='utf-8') as f:
        code = f.read()
    at = unreal.AssetToolsHelpers.get_asset_tools()
    # Always a fresh asset: rebuilding in place (delete_all_material_expressions, as lt_build does) kept the old scalar defaults in
    # the material's parameter cache (the second build read MacroStrength back as the first build's 0.18).
    if EAL.does_asset_exist(MAT_PATH) and not EAL.delete_asset(MAT_PATH):
        fail('delete_asset %s failed' % MAT_PATH)
    mat = at.create_asset(MAT_NAME, MAT_DIR, unreal.Material, unreal.MaterialFactoryNew())
    if mat is None:
        fail('create_asset %s failed' % MAT_PATH)
    MEL.delete_all_material_expressions(mat)  # drops the factory's default Substrate slab (legacy pins, lt_build.py:92)

    # /BaseMaterial is a built-in GameFeature plugin: in a commandlet its folder is not in the asset registry until scanned (the first
    # run failed with 'could not be found in the Asset Registry'), so scan it, then fall back to a direct package load.
    unreal.AssetRegistryHelpers.get_asset_registry().scan_paths_synchronous(['/BaseMaterial/Textures/Noises'], True)
    noise = EAL.load_asset(NOISE_TEX) or unreal.load_object(None, NOISE_TEX + '.' + NOISE_TEX.rsplit('/', 1)[1])
    if noise is None:
        fail('could not load ' + NOISE_TEX)
    tex_inputs = [('SplatTex', None), ('NoiseTex', noise)]
    for layer in LAYERS:
        for kind in ('C', 'N', 'ARH'):
            tex_inputs.append((layer + kind, textures[layer + kind]))

    custom = expr(mat, unreal.MaterialExpressionCustom, -400, 0, code=code, description='ChimeraGround',
                  output_type=unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    inputs = []
    for name in ['WP', 'VN'] + [n for n, _ in tex_inputs] + [n for n, _ in SCALARS]:
        ci = unreal.CustomInput()
        ci.set_editor_property('input_name', name)
        inputs.append(ci)
    custom.set_editor_property('inputs', inputs)
    outs = []
    for name, typ, _ in OUTPUTS:
        co = unreal.CustomOutput()
        co.set_editor_property('output_name', name)
        co.set_editor_property('output_type', getattr(unreal.CustomMaterialOutputType, typ))
        outs.append(co)
    custom.set_editor_property('additional_outputs', outs)
    got_in = [str(n) for n in MEL.get_material_expression_input_names(custom)]
    want_in = [str(ci.get_editor_property('input_name')) for ci in inputs]
    if got_in != want_in:
        fail('custom inputs %s != %s' % (got_in, want_in))
    got_out = [str(n) for n in MEL.get_material_expression_output_names(custom)]
    if got_out != ['return'] + [n for n, _, _ in OUTPUTS]:
        fail('custom outputs %s (RebuildOutputs did not run?)' % got_out)

    y = -1200
    wp = expr(mat, unreal.MaterialExpressionWorldPosition, -1000, y)
    link(wp, '', custom, 'WP')
    y += 80
    vn = expr(mat, unreal.MaterialExpressionVertexNormalWS, -1000, y)
    link(vn, '', custom, 'VN')
    y += 80
    for name, tex in tex_inputs:
        if name == 'SplatTex':
            t = textures['SplatDefault']
            e = expr(mat, unreal.MaterialExpressionTextureObjectParameter, -1000, y, parameter_name='Splat', texture=t,
                     sampler_type=sampler_for(t))
        else:
            e = expr(mat, unreal.MaterialExpressionTextureObject, -1000, y, texture=tex, sampler_type=sampler_for(tex))
        link(e, '', custom, name)
        y += 120
    for name, default in SCALARS:
        e = expr(mat, unreal.MaterialExpressionScalarParameter, -1000, y, parameter_name=name, default_value=default)
        link(e, '', custom, name)
        y += 60

    if not MEL.connect_material_property(custom, 'return', unreal.MaterialProperty.MP_BASE_COLOR):
        fail('connect return -> BaseColor failed')
    for name, _, prop in OUTPUTS:
        if not MEL.connect_material_property(custom, name, prop):
            fail('connect %s -> %s failed' % (name, prop))

    t0 = time.time()
    errors = [str(e) for e in MEL.recompile_material(mat)]
    if errors:
        fail('compile errors: ' + ' | '.join(errors))
    stats = MEL.get_statistics(mat)  # submits and finishes the shader compile (MaterialEditingLibrary.cpp GetStatistics)
    compile_s = time.time() - t0
    st = {k: int(stats.get_editor_property(k)) for k in (
        'num_vertex_shader_instructions', 'num_pixel_shader_instructions', 'num_samplers', 'num_vertex_texture_samples',
        'num_pixel_texture_samples', 'num_virtual_texture_samples', 'num_uv_scalars', 'num_interpolator_scalars')}
    if st['num_pixel_shader_instructions'] <= 0:
        fail('shader compile produced no pixel shader (an HLSL error in ChimeraGround.hlsl?): %s' % st)
    for name, default in SCALARS:
        got = MEL.get_material_default_scalar_parameter_value(mat, name)
        if abs(got - default) > 1e-4:
            fail('%s default read back %s != %s' % (name, got, default))
    if not EAL.save_asset(MAT_PATH, False):
        fail('save_asset %s failed' % MAT_PATH)
    return mat, st, compile_s


def main():
    report_path = sys.argv[1] if len(sys.argv) > 1 else ''
    t0 = time.time()
    log('project %s, rhi shader platform via get_statistics' % T)
    textures = import_all()
    mat, st, compile_s = build_material(textures)
    report = {'material': MAT_PATH, 'errors': 0, 'stats': st, 'compile_s': round(compile_s, 1), 'total_s': round(time.time() - t0, 1),
              'scalars': dict(SCALARS), 'hlsl': HLSL, 'textures': {k: v.get_path_name() for k, v in textures.items()},
              'noise': NOISE_TEX, 't': time.strftime('%Y-%m-%dT%H:%M:%S')}
    if report_path:
        with open(report_path, 'w', encoding='utf-8') as f:
            json.dump(report, f, indent=1)
    print('MATERIAL_OK %s errors=0 ps_instructions=%d vs_instructions=%d samplers=%d ps_texture_samples=%d compile_s=%.1f' % (
        MAT_PATH, st['num_pixel_shader_instructions'], st['num_vertex_shader_instructions'], st['num_samplers'],
        st['num_pixel_texture_samples'], compile_s))
    unreal.log('MATERIAL_OK %s errors=0' % MAT_PATH)


main()
