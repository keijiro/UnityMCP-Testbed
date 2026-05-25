---
name: blender-make-sliceable
description: Convert an arbitrary mesh loaded in Blender (with self-intersections, poke-through geometry, open shells, or non-manifold edges) into a closed, manifold, slicer-ready mesh that can be safely cut by an arbitrary plane and capped at runtime. Pipeline is Voxel Remesh → Decimate (to keep face count tractable for game runtime) → Data Transfer to project UVs back from the original. Verification empirically runs a bisect-and-cap probe on the result to confirm the mesh survives slicing cleanly. Intended for preparing game props for a runtime mesh-cutting feature. Requires the Blender MCP server to be connected; runs via `mcp__blender__execute_blender_code`.
---

# blender-make-sliceable

Prepare an arbitrary Blender mesh for runtime mesh slicing. Output is a closed, manifold, outward-oriented mesh with a runtime-friendly face count and UVs approximately preserved. Verification empirically confirms the mesh can be bisected with an arbitrary plane and cleanly capped with `bmesh.ops.holes_fill`.

## Pipeline

1. Duplicate target; hide original as UV reference and rollback target.
2. **Voxel Remesh** — rebuild a watertight surface (drops UVs).
3. **Decimate (COLLAPSE)** — reduce to a runtime-friendly face count while preserving topological closure.
4. **Data Transfer** (loop UV, `POLYINTERP_NEAREST`) — project UVs back from the original.
5. **Verify** — watertight + manifold + outward normals + no degenerate faces + slice-and-cap probe + UV present.

Result object is named `<source>_sliceable`. Original stays hidden in scene.

## Execution (via MCP)

Pass the script below to `mcp__blender__execute_blender_code`. Substitute `SRC_NAME` with the target object's name. Set `VOXEL_SIZE` to roughly `max_bbox_dimension / 100` and `DECIMATE_RATIO` to the fraction of faces to keep after remeshing.

```python
import bpy, bmesh
from mathutils import Vector

SRC_NAME       = "<target object name>"
VOXEL_SIZE     = 0.01   # ~ max_bbox_dimension / 100
DECIMATE_RATIO = 0.3    # keep 30% of remesh faces

src = bpy.data.objects[SRC_NAME]

# 1) Duplicate; hide source as UV reference
bpy.ops.object.select_all(action='DESELECT')
src.select_set(True)
bpy.context.view_layer.objects.active = src
bpy.ops.object.duplicate(linked=False)
work = bpy.context.active_object
work.name = SRC_NAME + "_sliceable"
src.hide_set(True)

# 2) Voxel Remesh
m1 = work.modifiers.new("VoxelRemesh", 'REMESH')
m1.mode = 'VOXEL'
m1.voxel_size = VOXEL_SIZE
m1.use_smooth_shade = True
m1.adaptivity = 0.0
bpy.ops.object.modifier_apply(modifier=m1.name)

# 3) Decimate
m2 = work.modifiers.new("Decimate", 'DECIMATE')
m2.decimate_type = 'COLLAPSE'
m2.ratio = DECIMATE_RATIO
m2.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=m2.name)

# 4) UV transfer from source
if not work.data.uv_layers:
    work.data.uv_layers.new(name="UVMap")
bpy.context.view_layer.objects.active = work
bpy.ops.object.select_all(action='DESELECT')
work.select_set(True)
m3 = work.modifiers.new("UVTransfer", 'DATA_TRANSFER')
m3.object = src
m3.use_loop_data = True
m3.data_types_loops = {'UV'}
m3.loop_mapping = 'POLYINTERP_NEAREST'
bpy.ops.object.datalayout_transfer(modifier=m3.name)
bpy.ops.object.modifier_apply(modifier=m3.name)

# 5) Verify
me = work.data
bm = bmesh.new(); bm.from_mesh(me); bm.normal_update()

non_manifold = sum(1 for e in bm.edges if not e.is_manifold)
boundary     = sum(1 for e in bm.edges if e.is_boundary)
zero_area    = sum(1 for f in bm.faces if f.calc_area() < 1e-12)
loose_verts  = sum(1 for v in bm.verts if not v.link_edges)

# Outward-normal check via signed volume (divergence theorem on fan-triangulated faces)
signed_vol = 0.0
for f in bm.faces:
    vs = [l.vert.co for l in f.loops]
    v0 = vs[0]
    for i in range(1, len(vs) - 1):
        signed_vol += v0.dot(vs[i].cross(vs[i + 1])) / 6.0

# Slice-and-cap probe: bisect with an oblique plane through bbox center,
# drop one half, fill the boundary as a cap, confirm result is still closed.
bb_center = sum((Vector(c) for c in work.bound_box), Vector()) / 8.0
bm_probe = bm.copy()
bmesh.ops.bisect_plane(
    bm_probe,
    geom=bm_probe.verts[:] + bm_probe.edges[:] + bm_probe.faces[:],
    plane_co=bb_center,
    plane_no=Vector((1.0, 0.5, 0.3)).normalized(),
    clear_inner=True, clear_outer=False,
)
boundary_after_cut = [e for e in bm_probe.edges if e.is_boundary]
fill = bmesh.ops.holes_fill(bm_probe, edges=boundary_after_cut, sides=0)
cap_faces = len(fill.get("faces", []))
post_nm  = sum(1 for e in bm_probe.edges if not e.is_manifold)
post_bnd = sum(1 for e in bm_probe.edges if e.is_boundary)
slice_clean = (post_nm == 0 and post_bnd == 0 and cap_faces > 0)

bm.free(); bm_probe.free()

passed = (
    non_manifold == 0 and boundary == 0 and
    zero_area == 0 and loose_verts == 0 and
    signed_vol > 0 and slice_clean and
    bool(me.uv_layers)
)

result = {
    "object": work.name,
    "faces": len(me.polygons),
    "uv_layers": [l.name for l in me.uv_layers],
    "watertight": {"non_manifold_edges": non_manifold, "boundary_edges": boundary},
    "geometry_health": {"zero_area_faces": zero_area, "loose_verts": loose_verts},
    "normals": {"signed_volume": round(signed_vol, 6),
                "outward_consistent": signed_vol > 0},
    "slice_probe": {"cap_faces": cap_faces,
                    "post_cut_non_manifold_edges": post_nm,
                    "post_cut_boundary_edges": post_bnd,
                    "clean_solid_half": slice_clean},
    "pass": passed,
}
```

Pass condition: `result["pass"] == True`. Inspect each sub-block on failure — any single failed check means the mesh is not slicer-ready.

## Tuning

- **`voxel_size`**: start at `max_bbox_dimension / 100`.
  - Corners or thin plates collapse too much → halve it (face count grows ~4× before decimate).
  - Too slow / face count explodes → double it.
- **`DECIMATE_RATIO`**: 0.3 keeps silhouette well in most cases. Lower for lighter assets, but watch the slice probe — aggressive decimation can introduce zero-area faces or break manifoldness. Raise toward 1.0 if `pass == False`. Set to `1.0` to disable decimation if face count is not a constraint.
- **Recovery on verify failure**: delete `<SRC>_sliceable`, unhide source (`hide_set(False)`), adjust parameters, rerun.

## Limitations

- **glTF / FBX round-trip splits vertices at UV seams and sharp normals.** After export and re-import into a game engine, the mesh will appear non-manifold even though it remains topologically closed in 3D space. The runtime slicer **must perform a merge-by-distance / weld pass on import** (tolerance ≈ `1e-5` of model scale) before computing cap geometry. Without this, the slicer will see spurious boundaries along the seams and produce broken caps. This is an export-format limitation, not a defect of the prepared mesh.
- **Cap-face UVs are out of scope.** The runtime slicer must generate UVs for the newly created cap polygons (typically by planar projection in the cut plane).
- **UV continuity at voxel boundaries.** Where Voxel Remesh boundaries cross original UV island seams, small UV discontinuities remain on the surface. If unacceptable, follow this skill with a Smart UV Unwrap + texture bake into the new UVs.
- **Only material slots and UVs are preserved.** Vertex colors and other loop/poly attributes are dropped unless `data_types_loops` / `data_types_polys` on the Data Transfer modifier are extended.
- **Inputs already closed with sharp detail to preserve** are better handled by Boolean Union — out of scope here.
