extends Node3D
# Dev-only asset preview: loads a representative set of the generated faction GLBs in a row
# with its own camera + lights, bypassing the main menu. Run via the Godot MCP, screenshot, delete.

const PATHS = [
	"res://assets/models/factions/alpha/acolyte_alchemist.glb",
	"res://assets/models/factions/alpha/covenant_transmuter.glb",
	"res://assets/models/factions/alpha/pierce_marksman.glb",
	"res://assets/models/factions/alpha/greycrest_bonded.glb",
	"res://assets/models/factions/alpha/covenant_sanctum.glb",
	"res://assets/models/factions/beta/cinderhand_thrall.glb",
	"res://assets/models/factions/beta/pride_colossus.glb",
	"res://assets/models/factions/beta/render_crawler.glb",
	"res://assets/models/factions/beta/sanguine_furnace.glb",
]

func _ready() -> void:
	# environment + lights (flat, readable)
	var we := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.16, 0.17, 0.22)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.55, 0.55, 0.6)
	env.ambient_light_energy = 0.7
	we.environment = env
	add_child(we)

	var key := DirectionalLight3D.new()
	key.rotation_degrees = Vector3(-50, -35, 0)
	add_child(key)
	var fill := DirectionalLight3D.new()
	fill.rotation_degrees = Vector3(-25, 150, 0)
	fill.light_energy = 0.5
	fill.light_specular = 0.0
	add_child(fill)

	# place models in a row
	var n := PATHS.size()
	var spacing := 3.3
	var x := -float(n - 1) * spacing * 0.5
	var loaded := 0
	for p in PATHS:
		var res = load(p)
		var inst: Node3D = null
		if res is PackedScene:
			inst = res.instantiate()
		elif res is Mesh:
			var mi := MeshInstance3D.new()
			mi.mesh = res
			inst = mi
		if inst != null:
			inst.position = Vector3(x, 0, 0)
			add_child(inst)
			loaded += 1
		x += spacing

	# camera framing the whole row
	var cam := Camera3D.new()
	cam.fov = 52.0
	cam.position = Vector3(0, 3.3, 21)
	cam.look_at(Vector3(0, 1.1, 0), Vector3.UP)
	cam.current = true
	add_child(cam)
	print("PREVIEW_LOADED %d / %d models" % [loaded, n])
