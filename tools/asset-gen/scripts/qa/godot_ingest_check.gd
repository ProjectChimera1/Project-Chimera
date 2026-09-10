extends SceneTree
# Authoritative in-engine ingest gate: loads a .glb through the EXACT runtime path the
# shipped game uses (GLTFDocument.AppendFromFile -> GenerateScene), NOT editor import.
# Catches the silent corruption (winding/dropped tris/degenerate UV/NaN normals) and the
# compression-rejection (err 43) that would send an asset to the box-placeholder fallback.
#
# Run headless:
#   godot --headless --path <godot_project_dir> -s res://path/to/godot_ingest_check.gd -- <ABS_PATH_TO.glb> [--require-textured]
# Prints:  INGEST_OK path=... meshinstances=N   (exit 0)
#      or  INGEST_FAIL err=.. / reason   (exit 1)
#
# --require-textured asserts the SURFACE-0 ALBEDO TEXTURE, and does it here rather than only in the
# numeric gate because this is the one place that reads what the ENGINE ends up with. The runtime
# asks `mesh.SurfaceGetMaterial(0) is BaseMaterial3D` and then takes `bm.AlbedoTexture`
# (TeamTintMaterial.ArtOf); if that comes back null, TeamTintPolicy.Resolve collapses the asset to
# the Flat arm and the texture never renders. A GLB can carry a perfectly good baseColorTexture in
# its JSON and still land as a null AlbedoTexture here — different question, different gate.
# It also checks TEX_UV, because a normal map without UVs has no tangent space to live in.

func _init() -> void:
	var uargs := OS.get_cmdline_user_args()
	if uargs.is_empty():
		printerr("INGEST_FAIL no path arg (pass after --)")
		quit(1); return
	var path := uargs[0]
	var require_textured := uargs.has("--require-textured")
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_file(path, state)
	if err != OK:
		printerr("INGEST_FAIL err=%d path=%s" % [err, path])
		quit(1); return
	var scene := doc.generate_scene(state)
	if scene == null:
		printerr("INGEST_FAIL generate_scene_returned_null path=%s" % path)
		quit(1); return
	# Authoritative mesh-presence: GLTFState.get_meshes() is runtime-safe (no editor classes).
	var gltf_meshes := state.get_meshes().size()
	# Also count any scene node exposing a "mesh" (MeshInstance3D / ImporterMeshInstance3D).
	var nodes_with_mesh := 0
	var meshes: Array = []
	var stack: Array = [scene]
	while not stack.is_empty():
		var n = stack.pop_back()
		if n != null and ("mesh" in n) and n.get("mesh") != null:
			nodes_with_mesh += 1
			meshes.append(n.get("mesh"))
		for c in n.get_children():
			stack.append(c)
	if gltf_meshes <= 0 and nodes_with_mesh <= 0:
		printerr("INGEST_FAIL no_mesh_in_scene path=%s" % path)
		quit(1); return

	var name := path.get_file()
	var surfaces := 0
	var with_albedo := 0
	var with_uv := 0
	var mat_kinds := {}
	for m in meshes:
		if m == null or m.get_surface_count() == 0:
			continue
		surfaces += m.get_surface_count()
		# Surface 0 only -- the runtime reads surface 0 and nothing else.
		var mat = m.surface_get_material(0)
		var kind = "null" if mat == null else mat.get_class()
		mat_kinds[kind] = int(mat_kinds.get(kind, 0)) + 1
		if mat is BaseMaterial3D and mat.albedo_texture != null:
			with_albedo += 1
		var fmt = m.surface_get_format(0)
		if (fmt & Mesh.ARRAY_FORMAT_TEX_UV) != 0:
			with_uv += 1

	if require_textured:
		if with_uv <= 0:
			printerr("INGEST_FAIL no_uvs name=%s path=%s (surface 0 has no ARRAY_FORMAT_TEX_UV)" % [name, path])
			quit(1); return
		if with_albedo <= 0:
			printerr("INGEST_FAIL no_albedo_texture name=%s path=%s surface0_material=%s (the runtime reads BaseMaterial3D.albedo_texture; null here means the asset renders FLAT)" % [name, path, str(mat_kinds)])
			quit(1); return

	print("INGEST_OK path=%s gltf_meshes=%d nodes_with_mesh=%d surfaces=%d with_uv=%d with_albedo=%d mats=%s" % [path, gltf_meshes, nodes_with_mesh, surfaces, with_uv, with_albedo, str(mat_kinds)])
	quit(0)
