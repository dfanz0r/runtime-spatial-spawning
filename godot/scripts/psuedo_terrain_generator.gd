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
@export var inset_offset: float = 0.0 ## Inset value. Positive values move tiles closer, negative values create gaps.
@export_tool_button("Regenerate") var regenerateAction = regenerate_terrain

var noise: FastNoiseLite = FastNoiseLite.new()

func _ready() -> void:
	if Engine.is_editor_hint():
		noise.noise_type = FastNoiseLite.TYPE_PERLIN
		noise.frequency = noise_scale

# Helper: true if any dimension <= 0
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

	# Compute the spacing for noise sampling based on the tile's actual scaled size.
	var aabb: AABB = _get_tile_aabb(tile_scene)
	var sampling_spacing_x: float = aabb.size.x * tile_scale.x
	var sampling_spacing_z: float = aabb.size.z * tile_scale.z

	# Compute the spacing for tile placement, including the inset offset.
	var placement_spacing_x: float = sampling_spacing_x - inset_offset
	var placement_spacing_z: float = sampling_spacing_z - inset_offset
	
	# The offset to center the whole terrain grid, based on the placement grid.
	var grid_offset: Vector3 = Vector3(
		float(terrain_size - 1) * placement_spacing_x / 2.0,
		0.0,
		float(terrain_size - 1) * placement_spacing_z / 2.0
	)

	var root = get_tree().edited_scene_root

	for xi in range(terrain_size - 1): # Iterate to -1 to have space for the 4th corner
		for zi in range(terrain_size - 1):
			# 1. Sample the noise at all 4 corners of the quad
			var h_00 = noise.get_noise_2d(xi * noise_scale, zi * noise_scale) * height_scale
			var h_10 = noise.get_noise_2d((xi + 1) * noise_scale, zi * noise_scale) * height_scale
			var h_01 = noise.get_noise_2d(xi * noise_scale, (zi + 1) * noise_scale) * height_scale
			var h_11 = noise.get_noise_2d((xi + 1) * noise_scale, (zi + 1) * noise_scale) * height_scale
			
			# 2. Define the 3D world positions using SAMPLING spacing to calculate correct normals
			var p_00 = Vector3(xi * sampling_spacing_x, h_00, zi * sampling_spacing_z)
			var p_10 = Vector3((xi + 1) * sampling_spacing_x, h_10, zi * sampling_spacing_z)
			var p_01 = Vector3(xi * sampling_spacing_x, h_01, (zi + 1) * sampling_spacing_z)
			var p_11 = Vector3((xi + 1) * sampling_spacing_x, h_11, (zi + 1) * sampling_spacing_z)

			# 3. Calculate and average the normals of the two triangles that form the quad
			var normal1 = (p_10 - p_00).cross(p_11 - p_00)
			var normal2 = (p_11 - p_00).cross(p_01 - p_00)
			var avg_normal = (normal1 + normal2).normalized()

			# 4. Blend between flat (Vector3.UP) and the slope normal based on slope_strength
			var up_vector = Vector3.UP.lerp(avg_normal, slope_strength).normalized()
			
			# --- START: Zero Y-Rotation Logic ---

			# 5. Manually construct the rotation basis to eliminate Y-axis rotation.
			# The new Y-axis is the 'up_vector' we calculated.
			var basis_y = up_vector
			
			# The new Z-axis is derived to be perpendicular to the world's X-axis
			# and our new Y-axis. This keeps the tile grid-aligned.
			var basis_z = Vector3.RIGHT.cross(basis_y).normalized()
			
			# The new X-axis is derived to be perpendicular to the new Y and Z axes,
			# completing the orthonormal basis.
			var basis_x = basis_y.cross(basis_z).normalized()

			# --- END: Zero Y-Rotation Logic ---
			
			# Instantiate and configure tile
			var tile: Node3D = tile_scene.instantiate()
			add_child(tile)
			if root:
				tile.owner = root
			
			# Position the tile at the center of the quad using PLACEMENT spacing
			var center_x = (xi * placement_spacing_x + (xi + 1) * placement_spacing_x) / 2.0
			var center_y = (h_00 + h_10 + h_01 + h_11) / 4.0 # Average height from sampling grid
			var center_z = (zi * placement_spacing_z + (zi + 1) * placement_spacing_z) / 2.0
			
			# Apply position, rotation, and scale via a single Transform3D
			tile.transform = Transform3D(
				Basis(basis_x, basis_y, basis_z),
				Vector3(center_x, center_y, center_z) - grid_offset
			)
			tile.scale = tile_scale
