// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
@tool
extends Node3D
class_name TerrainGenerator

@export var terrain_size: int = 32
@export var height_scale: float = 10.0
@export var noise_scale: float = 0.1
@export var tile_scene: PackedScene
@export var tile_scale: Vector3 = Vector3.ONE
@export var slope_strength: float = 1.0
@export_tool_button("Regenerate") var regenerateAction = regenerate_terrain

var noise: FastNoiseLite = FastNoiseLite.new()
var tile_spacing_x: float = 1.0
var tile_spacing_z: float = 1.0

func _ready() -> void:
	if Engine.is_editor_hint():
		noise.noise_type = FastNoiseLite.TYPE_PERLIN
		noise.frequency = noise_scale

# Helper: true if any dimension <= 0# Helper: true if any dimension <= 0
func _aabb_is_empty(aabb: AABB) -> bool:
	return aabb.size.x <= 0.0 or aabb.size.y <= 0.0 or aabb.size.z <= 0.0

# Compute combined AABB recursively using local transforms (no tree needed)
func _get_combined_aabb(node: Node3D, parent_xform: Transform3D = Transform3D.IDENTITY) -> AABB:
	var aabb: AABB = AABB()
	var initialized: bool = false

	for child in node.get_children():
		if child is MeshInstance3D and child.mesh:
			var mesh_aabb: AABB = child.mesh.get_aabb()
			var xform: Transform3D = parent_xform * child.transform

			# sample 4 footprint corners (XZ plane)
			for p in [
				mesh_aabb.position,
				mesh_aabb.position + Vector3(mesh_aabb.size.x, 0, 0),
				mesh_aabb.position + Vector3(0, 0, mesh_aabb.size.z),
				mesh_aabb.position + mesh_aabb.size
			]:
				var wp: Vector3 = xform * p
				if not initialized:
					aabb = AABB(wp, Vector3.ZERO)
					initialized = true
				else:
					aabb = aabb.expand(wp)
		elif child is Node3D:
			var sub: AABB = _get_combined_aabb(child, parent_xform * child.transform)
			if not _aabb_is_empty(sub):
				if not initialized:
					aabb = sub
					initialized = true
				else:
					aabb = aabb.merge(sub)
	return aabb

# Get combined local-space AABB of PackedScene
func _get_tile_aabb(scene: PackedScene) -> AABB:
	if scene == null:
		return AABB(Vector3.ZERO, Vector3.ONE)
	var inst: Node3D = scene.instantiate()
	var aabb: AABB = _get_combined_aabb(inst)
	inst.free()
	return aabb

func regenerate_terrain() -> void:
	if not Engine.is_editor_hint():
		return
	if tile_scene == null:
		push_warning("No tile_scene assigned!")
		return

	# clear previous tiles
	for c in get_children():
		c.queue_free() # Use queue_free in editor to be safe

	noise.seed = randi()
	noise.frequency = noise_scale

	# compute spacing
	var aabb: AABB = _get_tile_aabb(tile_scene)
	var min_x: float = aabb.position.x * tile_scale.x
	var max_x: float = (aabb.position.x + aabb.size.x) * tile_scale.x
	var min_z: float = aabb.position.z * tile_scale.z
	var max_z: float = (aabb.position.z + aabb.size.z) * tile_scale.z

	tile_spacing_x = max_x - min_x
	tile_spacing_z = max_z - min_z
	# The offset to center the whole terrain grid
	var grid_offset: Vector3 = Vector3(
		float(terrain_size - 1) * tile_spacing_x / 2.0,
		0.0,
		float(terrain_size - 1) * tile_spacing_z / 2.0
	)

	var root = get_tree().edited_scene_root

	for xi in range(terrain_size - 1): # Iterate to -1 to have space for the 4th corner
		for zi in range(terrain_size - 1):
			# 1. Sample the noise at all 4 corners of the quad
			var h_00 = noise.get_noise_2d(xi * noise_scale, zi * noise_scale) * height_scale
			var h_10 = noise.get_noise_2d((xi + 1) * noise_scale, zi * noise_scale) * height_scale
			var h_01 = noise.get_noise_2d(xi * noise_scale, (zi + 1) * noise_scale) * height_scale
			var h_11 = noise.get_noise_2d((xi + 1) * noise_scale, (zi + 1) * noise_scale) * height_scale
			
			# 2. Define the 3D world positions of the four corners
			var p_00 = Vector3(xi * tile_spacing_x, h_00, zi * tile_spacing_z)
			var p_10 = Vector3((xi + 1) * tile_spacing_x, h_10, zi * tile_spacing_z)
			var p_01 = Vector3(xi * tile_spacing_x, h_01, (zi + 1) * tile_spacing_z)
			var p_11 = Vector3((xi + 1) * tile_spacing_x, h_11, (zi + 1) * tile_spacing_z)

			# 3. Calculate and average the normals of the two triangles that form the quad
			var normal1 = (p_10 - p_00).cross(p_11 - p_00)
			var normal2 = (p_11 - p_00).cross(p_01 - p_00)
			var avg_normal = (normal1 + normal2).normalized()

			# 4. Blend between flat (Vector3.UP) and the slope normal based on slope_strength
			var up_vector = Vector3.UP.lerp(avg_normal, slope_strength).normalized()
			
			# --- START: Grid-Aligned Rotation Logic ---

			# 5. Manually construct the rotation basis to prevent Y-axis rotation.
			# The new Y-axis is the 'up_vector' we calculated.
			var basis_y = up_vector
			
			# The new X-axis is perpendicular to the world's Z-axis and our new Y-axis.
			# This keeps the tile from twisting.
			var basis_x = Vector3.FORWARD.cross(basis_y).normalized()
			
			# The new Z-axis is perpendicular to our new X and Y axes.
			var basis_z = basis_x.cross(basis_y).normalized()

			# --- END: Grid-Aligned Rotation Logic ---
			
			# Instantiate and configure tile
			var tile: Node3D = tile_scene.instantiate()
			add_child(tile)
			if root:
				tile.owner = root
			
			# Position the tile at the center of the quad, with the average height
			var center_x = (p_00.x + p_10.x) / 2.0
			var center_y = (h_00 + h_10 + h_01 + h_11) / 4.0 # Average height
			var center_z = (p_00.z + p_01.z) / 2.0
			
			# Apply position, rotation, and scale via a single Transform3D
			tile.transform = Transform3D(
				Basis(basis_x, basis_y, basis_z),
				Vector3(center_x, center_y, center_z) - grid_offset
			)
			tile.scale = tile_scale
