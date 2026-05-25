---
name: blender-make-solid
description: Convert an arbitrary mesh loaded in Blender (including self-intersections, poke-through geometry, open shells, or non-manifold edges) into a closed solid (non-manifold edges = 0) while preserving its UVs as closely as possible. Uses a two-stage pipeline — Voxel Remesh to rebuild a watertight surface, then a Data Transfer modifier to copy UVs back from the original mesh. Intended for cases where robustness matters more than fidelity. Requires the Blender MCP server to be connected; runs via `mcp__blender__execute_blender_code`.
---

# blender-make-solid

Robustly convert any Blender mesh into a closed solid while keeping its UVs roughly intact. Effective for AI-generated meshes (e.g. Tripo output) and other inputs that are really collections of open shells. Sharp edges and thin features will round off depending on voxel size.

## Workflow

1. Duplicate the target; hide the original so it stays as a UV reference.
2. Apply a Voxel Remesh modifier on the copy — rebuilds geometry; UVs are lost.
3. Apply a Data Transfer modifier (loop UV, `POLYINTERP_NEAREST`) on the copy to project UVs back from the original.
4. Verify `non_manifold_edges == 0` and that a UV layer exists.

Result object is named `<source>_solid`. The original stays in the scene (hidden) as the UV source and rollback target.

## Execution (via MCP)

Pass the script below to `mcp__blender__execute_blender_code`, substituting `SRC_NAME` with the target object's name. Set `VOXEL_SIZE` to roughly `max_bbox_dimension / 100` (so `0.01` for a ~1 m object).

```python
import bpy, bmesh

SRC_NAME = "<target object name>"
VOXEL_SIZE = 0.01

src = bpy.data.objects[SRC_NAME]

# 1) Duplicate as working copy; keep source as hidden UV reference
bpy.ops.object.select_all(action='DESELECT')
src.select_set(True)
bpy.context.view_layer.objects.active = src
bpy.ops.object.duplicate(linked=False)
work = bpy.context.active_object
work.name = SRC_NAME + "_solid"
src.hide_set(True)

# 2) Voxel Remesh
m1 = work.modifiers.new("VoxelRemesh", 'REMESH')
m1.mode = 'VOXEL'
m1.voxel_size = VOXEL_SIZE
m1.use_smooth_shade = True
m1.adaptivity = 0.0
bpy.ops.object.modifier_apply(modifier=m1.name)

# 3) UV transfer from source
if not work.data.uv_layers:
    work.data.uv_layers.new(name="UVMap")
bpy.context.view_layer.objects.active = work
bpy.ops.object.select_all(action='DESELECT')
work.select_set(True)
m2 = work.modifiers.new("UVTransfer", 'DATA_TRANSFER')
m2.object = src
m2.use_loop_data = True
m2.data_types_loops = {'UV'}
m2.loop_mapping = 'POLYINTERP_NEAREST'
bpy.ops.object.datalayout_transfer(modifier=m2.name)
bpy.ops.object.modifier_apply(modifier=m2.name)

# 4) Verify
me = work.data
bm = bmesh.new(); bm.from_mesh(me)
nm = sum(1 for e in bm.edges if not e.is_manifold)
bm.free()
result = {
    "object": work.name,
    "faces": len(me.polygons),
    "non_manifold_edges": nm,
    "uv_layers": [l.name for l in me.uv_layers],
}
```

Pass conditions:

- `non_manifold_edges == 0`
- `uv_layers` is non-empty

## Tuning

- **voxel_size**: start at `max_bbox_dimension / 100`.
  - Corners or thin plates collapse too much → halve it (face count grows ~4×).
  - Too slow / face count explodes → double it.
- **Recovery on verification failure**: delete `<SRC>_solid`, unhide the original (`hide_set(False)`), adjust `voxel_size`, and rerun.

## Limitations

- Where voxel boundaries cross the original UV island seams, small UV discontinuities remain. If unacceptable, add a follow-up pass that Smart-UV-Unwraps the result and bakes the original material into the new UVs.
- For inputs that are already closed and need sharp detail preserved, Boolean Union is the better tool — out of scope for this skill.
- Only material slots and UVs are carried over. To preserve vertex colors or other loop/poly attributes, extend `data_types_loops` / `data_types_polys` on the Data Transfer modifier.
