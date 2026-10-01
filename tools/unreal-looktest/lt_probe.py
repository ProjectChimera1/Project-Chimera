"""Probe every unreal Python spelling, enum member, struct field and content path the look-test jobs use.

Writes run/probe.json: {"results": {name: bool}, "failures": {name: {"error", "dir"}}, "used_by": {name: module}}.
A failure carries a filtered dir() of the owner, so a wrong spelling is fixed from data, not guessed.
PROBES is the single list; its `used_by` column names the module (and function) that relies on the entry.
"""
import os
import time

import unreal

import lt_common as C

# (kind, owner, member, used_by)
#   attr   : hasattr(unreal.<owner>, member)            - classes' functions, enum members, module functions
#   cdo    : get_default_object(unreal.<owner>).get_editor_property(member)
#   struct : unreal.<owner>().get_editor_property(member) (structs and transient UObjects)
#   asset  : EditorAssetLibrary.does_asset_exist(member)
#   file   : os.path.isfile(member)
#   custom : CUSTOM[member]() returns truthy
PROBES = [
    # bridge / admin
    ('attr', 'EditorPythonScripting', 'set_keep_python_script_alive', 'ue_bridge, lt_admin.quit'),
    ('attr', '', 'register_slate_post_tick_callback', 'ue_bridge'),
    # EditorPerformanceSettings is not Python-exposed; ue_bridge sets bThrottleCPUWhenNotForeground on its CDO by path (verified live).
    ('attr', 'SystemLibrary', 'get_engine_version', 'ue_bridge, lt_admin.ping'),
    ('attr', 'UnrealEditorSubsystem', 'get_editor_world', 'lt_common.editor_world'),
    ('attr', '', 'get_editor_subsystem', 'lt_common'),
    ('attr', '', 'get_default_object', 'lt_probe'),
    # assets
    ('attr', 'EditorAssetLibrary', 'does_asset_exist', 'all'),
    ('attr', 'EditorAssetLibrary', 'load_asset', 'lt_common.load'),
    ('attr', 'EditorAssetLibrary', 'delete_asset', 'lt_common.fresh_level'),
    ('attr', 'EditorAssetLibrary', 'list_assets', 'lt_import._folder_objects'),
    ('attr', 'EditorAssetLibrary', 'save_asset', 'lt_import, lt_build'),
    ('attr', 'EditorAssetLibrary', 'does_directory_have_assets', 'lt_import'),
    ('attr', 'AssetToolsHelpers', 'get_asset_tools', 'lt_import, lt_build'),
    ('attr', 'AssetTools', 'import_asset_tasks', 'lt_import._import_file'),
    ('attr', 'AssetTools', 'create_asset', 'lt_import._team_mi, lt_build._ground_material'),
    ('struct', 'AssetImportTask', 'filename', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'destination_path', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'destination_name', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'automated', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'replace_existing', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'save', 'lt_import._import_file'),
    ('struct', 'AssetImportTask', 'imported_object_paths', 'lt_import._import_file'),
    # meshes / anims
    ('attr', 'StaticMesh', 'get_bounding_box', 'lt_import, lt_build._scatter_bounds'),
    ('attr', 'StaticMesh', 'get_material', 'lt_import._static_materials'),
    ('cdo', 'StaticMesh', 'static_materials', 'lt_import._static_materials'),
    ('cdo', 'StaticMesh', 'nanite_settings', 'lt_import (nanite flag)'),
    ('struct', 'MeshNaniteSettings', 'enabled', 'lt_import (nanite flag)'),
    ('struct', 'Box', 'min', 'lt_import, lt_build'),
    ('struct', 'Box', 'max', 'lt_import, lt_build'),
    ('attr', 'SkeletalMesh', 'get_imported_bounds', 'lt_import (skeletal bounds)'),
    ('cdo', 'SkeletalMesh', 'materials', 'lt_import._skeletal_materials'),
    ('struct', 'SkeletalMaterial', 'material_interface', 'lt_import._skeletal_materials'),
    ('struct', 'BoxSphereBounds', 'origin', 'lt_import (skeletal bounds)'),
    ('struct', 'BoxSphereBounds', 'box_extent', 'lt_import (skeletal bounds)'),
    ('attr', 'AnimSequence', 'get_play_length', 'lt_import (anim length)'),
    ('attr', 'unreal', 'Skeleton', 'lt_import (classify)'),
    # materials
    ('attr', 'MaterialEditingLibrary', 'get_vector_parameter_names', 'lt_import._material_info'),
    ('attr', 'MaterialEditingLibrary', 'get_material_instance_vector_parameter_value', 'lt_import._team_mi'),
    ('attr', 'MaterialEditingLibrary', 'set_material_instance_parent', 'lt_import._team_mi'),
    ('attr', 'MaterialEditingLibrary', 'set_material_instance_vector_parameter_value', 'lt_import._team_mi'),
    ('attr', 'MaterialEditingLibrary', 'set_material_instance_scalar_parameter_value', 'lt_build._ground_material'),
    ('attr', 'MaterialEditingLibrary', 'update_material_instance', 'lt_import, lt_build'),
    ('attr', 'MaterialEditingLibrary', 'delete_all_material_expressions', 'lt_build._ground_material'),
    ('attr', 'MaterialEditingLibrary', 'create_material_expression', 'lt_build._ground_material'),
    ('attr', 'MaterialEditingLibrary', 'connect_material_expressions', 'lt_build._ground_material'),
    ('attr', 'MaterialEditingLibrary', 'connect_material_property', 'lt_build._ground_material'),
    ('attr', 'MaterialEditingLibrary', 'recompile_material', 'lt_build._ground_material'),
    ('attr', 'MaterialInterface', 'get_blend_mode', 'lt_import._material_info'),
    ('attr', 'unreal', 'MaterialFactoryNew', 'lt_build._ground_material'),
    ('attr', 'unreal', 'MaterialInstanceConstantFactoryNew', 'lt_import._team_mi, lt_build'),
    ('attr', 'unreal', 'MaterialInstanceConstant', 'lt_import, lt_build'),
    ('attr', 'BlendMode', 'BLEND_OPAQUE', 'lt_import._material_info'),
    ('attr', 'BlendMode', 'BLEND_MASKED', 'lt_import._material_info'),
    ('attr', 'MaterialProperty', 'MP_BASE_COLOR', 'lt_build._ground_material'),
    ('attr', 'MaterialProperty', 'MP_ROUGHNESS', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionTextureSample', 'texture', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionTextureSample', 'sampler_type', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionTextureCoordinate', 'coordinate_index', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionScalarParameter', 'parameter_name', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionScalarParameter', 'default_value', 'lt_build._ground_material'),
    ('cdo', 'MaterialExpressionConstant', 'r', 'lt_build._ground_material'),
    ('attr', 'unreal', 'MaterialExpressionMultiply', 'lt_build._ground_material'),
    ('attr', 'unreal', 'MaterialExpressionMax', 'lt_build._ground_material'),
    ('attr', 'unreal', 'MaterialExpressionLinearInterpolate', 'lt_build._ground_material'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_COLOR', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_LINEAR_COLOR', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_MASKS', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_GRAYSCALE', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_LINEAR_GRAYSCALE', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_NORMAL', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_ALPHA', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_VIRTUAL_COLOR', 'lt_build._sampler_for'),
    ('attr', 'MaterialSamplerType', 'SAMPLERTYPE_VIRTUAL_LINEAR_COLOR', 'lt_build._sampler_for'),
    ('attr', 'TextureCompressionSettings', 'TC_NORMALMAP', 'lt_build._sampler_for'),
    ('attr', 'TextureCompressionSettings', 'TC_GRAYSCALE', 'lt_build._sampler_for'),
    ('attr', 'TextureCompressionSettings', 'TC_ALPHA', 'lt_build._sampler_for'),
    ('attr', 'TextureCompressionSettings', 'TC_MASKS', 'lt_build._sampler_for'),
    ('cdo', 'Texture2D', 'srgb', 'lt_import (ground sRGB), lt_build._sampler_for'),
    ('cdo', 'Texture2D', 'virtual_texture_streaming', 'lt_import (ground), lt_build._sampler_for'),
    ('cdo', 'Texture2D', 'compression_settings', 'lt_build._sampler_for'),
    # level / actors
    ('attr', 'LevelEditorSubsystem', 'new_level', 'lt_common.fresh_level'),
    ('attr', 'LevelEditorSubsystem', 'load_level', 'lt_common.fresh_level, lt_capture'),
    ('attr', 'LevelEditorSubsystem', 'save_current_level', 'lt_build, lt_facing'),
    ('attr', 'LevelEditorSubsystem', 'pilot_level_actor', 'lt_capture'),
    ('attr', 'LevelEditorSubsystem', 'eject_pilot_level_actor', 'lt_capture'),
    ('attr', 'LevelEditorSubsystem', 'editor_set_game_view', 'lt_capture'),
    ('attr', 'LevelEditorSubsystem', 'editor_set_viewport_realtime', 'lt_capture'),
    ('attr', 'EditorActorSubsystem', 'spawn_actor_from_object', 'lt_common.spawn_mesh'),
    ('attr', 'EditorActorSubsystem', 'spawn_actor_from_class', 'lt_common.spawn_class'),
    ('attr', 'EditorActorSubsystem', 'get_all_level_actors', 'lt_common.find_actor'),
    ('attr', 'Actor', 'set_actor_scale3d', 'lt_common.spawn_mesh'),
    ('attr', 'Actor', 'set_actor_label', 'lt_common.spawn_*'),
    ('attr', 'Actor', 'get_actor_label', 'lt_common.find_actor'),
    ('attr', 'Actor', 'set_folder_path', 'lt_common.spawn_*'),
    ('attr', 'Actor', 'set_actor_rotation', 'lt_common.apply_recipe'),
    ('attr', 'Actor', 'get_actor_location', 'lt_build manifest'),
    ('attr', 'Actor', 'get_actor_rotation', 'lt_build manifest'),
    ('attr', 'Actor', 'get_actor_scale3d', 'lt_build manifest'),
    ('attr', 'Actor', 'get_component_by_class', 'lt_common.component'),
    ('attr', 'SceneComponent', 'set_mobility', 'lt_common.apply_recipe'),
    ('attr', 'ComponentMobility', 'MOVABLE', 'lt_common.apply_recipe'),
    ('attr', 'MeshComponent', 'set_material', 'lt_build, lt_facing'),
    ('attr', 'MeshComponent', 'get_materials', 'lt_build manifest'),
    ('attr', 'unreal', 'StaticMeshComponent', 'lt_build._place, lt_common.component'),
    ('attr', 'unreal', 'SkeletalMeshComponent', 'lt_build._place'),
    ('attr', 'ActorComponent', 'get_owner', 'lt_build._pose (error message)'),
    ('attr', 'unreal', 'PlayerStart', 'lt_build'),
    # skeletal animation (D4)
    ('attr', 'SkeletalMeshComponent', 'set_animation_mode', 'lt_build._pose'),
    ('attr', 'SkeletalMeshComponent', 'override_animation_data', 'lt_build._pose'),
    ('attr', 'AnimationMode', 'ANIMATION_SINGLE_NODE', 'lt_build._pose'),
    ('cdo', 'SkeletalMeshComponent', 'animation_data', 'lt_build._pose (read-back)'),
    ('cdo', 'SkeletalMeshComponent', 'update_animation_in_editor', 'lt_build._pose'),
    ('struct', 'SingleAnimationPlayData', 'anim_to_play', 'lt_build._pose'),
    ('struct', 'SingleAnimationPlayData', 'saved_looping', 'lt_build._pose'),
    ('struct', 'SingleAnimationPlayData', 'saved_playing', 'lt_build._pose'),
    ('struct', 'SingleAnimationPlayData', 'saved_position', 'lt_build._pose'),
    # cameras
    ('cdo', 'CameraActor', 'camera_component', 'lt_common.spawn_camera'),
    ('cdo', 'CameraActor', 'auto_activate_for_player', 'lt_common.spawn_camera'),
    ('attr', 'AutoReceiveInput', 'PLAYER0', 'lt_common.spawn_camera'),
    ('cdo', 'CameraComponent', 'field_of_view', 'lt_common.spawn_camera'),
    ('cdo', 'CameraComponent', 'constrain_aspect_ratio', 'lt_common.spawn_camera'),
    # sky / post (PLAN §7.5)
    ('attr', 'unreal', 'DirectionalLight', 'lt_common.spawn_sky'),
    ('attr', 'unreal', 'SkyAtmosphere', 'lt_common.spawn_sky'),
    ('attr', 'unreal', 'SkyLight', 'lt_common.spawn_sky'),
    ('attr', 'unreal', 'ExponentialHeightFog', 'lt_common.spawn_sky'),
    ('attr', 'unreal', 'VolumetricCloud', 'lt_common.spawn_sky'),
    ('attr', 'unreal', 'PostProcessVolume', 'lt_common.spawn_sky'),
    ('cdo', 'DirectionalLightComponent', 'intensity', 'lt_common.apply_recipe'),
    ('cdo', 'DirectionalLightComponent', 'light_source_angle', 'lt_common.apply_recipe'),
    ('cdo', 'DirectionalLightComponent', 'use_temperature', 'lt_common.apply_recipe'),
    ('cdo', 'DirectionalLightComponent', 'temperature', 'lt_common.apply_recipe'),
    ('cdo', 'DirectionalLightComponent', 'atmosphere_sun_light', 'lt_common.apply_recipe'),
    ('cdo', 'DirectionalLightComponent', 'cast_cloud_shadows', 'lt_common.apply_recipe'),
    ('cdo', 'SkyLightComponent', 'real_time_capture', 'lt_common.apply_recipe'),
    ('cdo', 'SkyLightComponent', 'intensity', 'lt_common.apply_recipe'),
    ('cdo', 'ExponentialHeightFogComponent', 'fog_density', 'lt_common.apply_recipe'),
    ('cdo', 'ExponentialHeightFogComponent', 'fog_height_falloff', 'lt_common.apply_recipe'),
    ('cdo', 'ExponentialHeightFogComponent', 'enable_volumetric_fog', 'lt_common.apply_recipe'),
    ('cdo', 'ExponentialHeightFogComponent', 'fog_inscattering_luminance', 'lt_common.apply_recipe'),
    ('attr', 'VolumetricCloudComponent', 'set_material', 'lt_common.apply_recipe'),
    ('cdo', 'PostProcessVolume', 'unbound', 'lt_common.apply_recipe'),
    ('cdo', 'PostProcessVolume', 'settings', 'lt_common.apply_recipe'),
    ('attr', 'DynamicGlobalIlluminationMethod', 'LUMEN', 'RECIPES'),
    ('attr', 'DynamicGlobalIlluminationMethod', 'SCREEN_SPACE', 'RECIPES'),
    ('attr', 'DynamicGlobalIlluminationMethod', 'NONE', 'RECIPES'),
    ('attr', 'ReflectionMethod', 'LUMEN', 'RECIPES'),
    ('attr', 'ReflectionMethod', 'SCREEN_SPACE', 'RECIPES'),
] + [('struct', 'PostProcessSettings', f, 'lt_common.apply_recipe') for f in (
    'override_dynamic_global_illumination_method', 'dynamic_global_illumination_method',
    'override_reflection_method', 'reflection_method', 'override_color_saturation', 'color_saturation',
    'override_color_contrast', 'color_contrast', 'override_bloom_intensity', 'bloom_intensity',
    'override_vignette_intensity', 'vignette_intensity', 'override_ambient_occlusion_intensity',
    'ambient_occlusion_intensity', 'override_motion_blur_amount', 'motion_blur_amount',
    'override_auto_exposure_bias', 'auto_exposure_bias')] + [
    # capture (PLAN §9)
    ('attr', 'AutomationLibrary', 'take_high_res_screenshot', 'lt_capture'),
    ('attr', 'AutomationEditorTask', 'is_task_done', 'lt_capture'),
    ('attr', 'SystemLibrary', 'execute_console_command', 'lt_capture'),
    # behaviour checks
    ('custom', '', 'rotator_keywords', 'lt_common.spawn_* (Rotator(roll=, pitch=, yaw=))'),
    ('custom', '', 'M_GLTF_has_BaseColorFactor', 'lt_import._team_mi (PLAN §6.4)'),
] + [('asset', '', p, 'lt_common constants') for p in C.ENGINE_CONTENT] + [
    ('file', '', C.GRASS_PNG, 'lt_import ground'),
    ('file', '', C.DIRT_PNG, 'lt_import ground'),
    ('file', '', C.ROSTER_JSON, 'lt_import, lt_build'),
]


def _rotator_keywords():
    r = unreal.Rotator(roll=1.0, pitch=2.0, yaw=3.0)
    return (r.roll, r.pitch, r.yaw) == (1.0, 2.0, 3.0)


def _gltf_factor():
    names = unreal.MaterialEditingLibrary.get_vector_parameter_names(C.load(C.M_GLTF))
    return 'BaseColorFactor' in [str(n) for n in names]


CUSTOM = {'rotator_keywords': _rotator_keywords, 'M_GLTF_has_BaseColorFactor': _gltf_factor}


def _owner(owner):
    return unreal if owner in ('', 'unreal') else getattr(unreal, owner)


def _check(kind, owner, member):
    if kind == 'attr':
        return hasattr(_owner(owner), member)
    if kind == 'cdo':
        unreal.get_default_object(_owner(owner)).get_editor_property(member)
        return True
    if kind == 'struct':
        _owner(owner)().get_editor_property(member)
        return True
    if kind == 'asset':
        return bool(unreal.EditorAssetLibrary.does_asset_exist(member))
    if kind == 'file':
        return os.path.isfile(member)
    if kind == 'custom':
        return bool(CUSTOM[member]())
    raise ValueError(kind)


def _filtered_dir(owner, member):
    """dir() of the owner, filtered to names sharing a token with the probed member."""
    try:
        names = dir(_owner(owner))
    except Exception as e:
        return [f'owner unavailable: {e!r}']
    toks = [t for t in member.lower().replace('.', '_').split('_') if len(t) >= 3]
    hits = [n for n in names if any(t in n.lower() for t in toks)]
    return hits or names[:400]


def run():
    """Run every probe (yielding every 20), write run/probe.json, return the counts and failed names."""
    results, failures, used_by = {}, {}, {}
    for i, (kind, owner, member, users) in enumerate(PROBES):
        name = f'{kind}:{owner}.{member}' if owner else f'{kind}:{member}'
        used_by[name] = users
        try:
            ok = bool(_check(kind, owner, member))
            err = None if ok else 'false'
        except Exception as e:
            ok, err = False, repr(e)
        results[name] = ok
        if not ok:
            failures[name] = {'error': err,
                              'dir': _filtered_dir(owner, member) if kind in ('attr', 'cdo', 'struct') else []}
        if i % 20 == 19:
            yield
    C.write_json(C.PROBE_JSON, {'t': time.time(), 'engine': unreal.SystemLibrary.get_engine_version(),
                                'results': results, 'failures': failures, 'used_by': used_by})
    return {'total': len(results), 'ok': sum(results.values()), 'failed': sorted(failures)}
