extends SceneTree
# Authoritative in-engine ingest gate: loads a .glb through the EXACT runtime path the
# shipped game uses (GLTFDocument.AppendFromFile -> GenerateScene), NOT editor import.
# Catches the silent corruption (winding/dropped tris/degenerate UV/NaN normals) and the
# compression-rejection (err 43) that would send an asset to the box-placeholder fallback.
#
# Run headless:
#   godot --headless --path <godot_project_dir> -s res://path/to/godot_ingest_check.gd -- <ABS_PATH_TO.glb>
# Prints:  INGEST_OK path=... meshinstances=N   (exit 0)
#      or  INGEST_FAIL err=.. / reason   (exit 1)

func _init() -> void:
	var uargs := OS.get_cmdline_user_args()
	if uargs.is_empty():
		printerr("INGEST_FAIL no path arg (pass after --)")
		quit(1); return
	var path := uargs[0]
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
	var stack: Array = [scene]
	while not stack.is_empty():
		var n = stack.pop_back()
		if n != null and ("mesh" in n) and n.get("mesh") != null:
			nodes_with_mesh += 1
		for c in n.get_children():
			stack.append(c)
	if gltf_meshes <= 0 and nodes_with_mesh <= 0:
		printerr("INGEST_FAIL no_mesh_in_scene path=%s" % path)
		quit(1); return
	print("INGEST_OK path=%s gltf_meshes=%d nodes_with_mesh=%d" % [path, gltf_meshes, nodes_with_mesh])
	quit(0)
