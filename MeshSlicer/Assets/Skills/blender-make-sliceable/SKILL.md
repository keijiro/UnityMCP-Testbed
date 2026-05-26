---
name: blender-make-sliceable
description: Prepare a mesh from this Unity project's `Assets/Models/` (typically a `.glb`) for the runtime mesh slicer in `Assets/MeshSlicer/Runtime`. Converts arbitrary input (self-intersections, poke-through geometry, open shells, non-manifold edges) into a closed, manifold, outward-oriented mesh that can be safely bisected by an arbitrary plane and capped at runtime. Invoke when the user asks to make a model "sliceable" / "slicer-ready", or when the runtime slicer produces broken caps on a model. Pipeline: import .glb → Voxel Remesh → Decimate → Data Transfer (UVs) → bisect-and-cap probe → export .glb back to Unity. Requires the Blender MCP server (configured in `mcp.json`); runs via `mcp__blender__execute_blender_code`.
---

# blender-make-sliceable

Prepare an arbitrary mesh from this Unity project for the runtime slicer. Output is a closed, manifold, outward-oriented `.glb` written back into `Assets/Models/...`, with a runtime-friendly face count and UVs approximately preserved. Verification empirically confirms the mesh can be bisected with an arbitrary plane and cleanly capped with `bmesh.ops.holes_fill`.

## When to invoke

Use this skill when the user wants to make a model under `Assets/Models/` safe for the project's runtime slicer (`BurstMeshSlicer` / `NaiveMeshSlicer` in `Assets/MeshSlicer/Runtime`). Typical triggers:

- "Make `<Asset>` sliceable" / "Prep this for the slicer."
- The runtime slicer produces visibly broken caps on a model — see `Project_Overview.md` §8 ("the slicer assumes manifold input").
- A new `.glb` was added under `Assets/Models/<Name>_Assets/` and needs a sliceable variant.

Before running: verify the Blender MCP server is reachable with a trivial call (e.g., `mcp__blender__execute_blender_code` running `import bpy; print(bpy.app.version)`). If not connected, ask the user to start it — do not attempt workarounds.

## Pipeline

1. Import the source `.glb` into Blender; capture the imported object's name.
2. Duplicate target; hide original as UV reference and rollback target.
3. **Voxel Remesh** — rebuild a watertight surface (drops UVs).
4. **Decimate (COLLAPSE)** — reduce to a runtime-friendly face count while preserving topological closure.
5. **Data Transfer** (loop UV, `POLYINTERP_NEAREST`) — project UVs back from the original.
6. **Verify** — watertight + manifold + outward normals + no degenerate faces + slice-and-cap probe + UV present.
7. Export `<source>_sliceable` as `.glb` next to the input so Unity re-imports it.

Result object is named `<source>_sliceable`. Original stays hidden in the Blender scene.

## Execution (via MCP)

Three discrete `mcp__blender__execute_blender_code` calls: **load → prepare → export**.

### Step 1 — Resolve / load the source object

Pick exactly one of:

- **Importing a Unity `.glb` (typical entry point).** Substitute `GLB_PATH` with the absolute path of the asset (e.g., `…/MeshSlicer/Assets/Models/Barrel_Assets/selected.glb`):

  ```python
  import bpy
  GLB_PATH = "<absolute path to .glb>"

  before = set(bpy.data.objects.keys())
  bpy.ops.import_scene.gltf(filepath=GLB_PATH)
  imported = [bpy.data.objects[n] for n in (set(bpy.data.objects.keys()) - before)
              if bpy.data.objects[n].type == 'MESH']
  assert len(imported) == 1, f"Expected 1 mesh, got {[o.name for o in imported]}"
  print("SRC_NAME=" + imported[0].name)
  ```

  Capture the printed `SRC_NAME` and use it in Step 2. If the glTF unpacks into multiple meshes, ask the user which to prepare (or join them first if that matches intent).

- **Already loaded in the current Blender scene.** List candidates and disambiguate:

  ```python
  import bpy
  meshes = [o.name for o in bpy.data.objects if o.type == 'MESH' and not o.hide_get()]
  active = bpy.context.view_layer.objects.active
  print({"meshes": meshes, "active": active.name if active else None})
  ```

  If there is exactly one mesh, or the active object is a mesh, use that as `SRC_NAME`. Otherwise ask the user. Never guess from a partial name match.

If `<SRC_NAME>_sliceable` already exists in `bpy.data.objects`, remove it before Step 2 — leftover state from a previous run breaks the duplicate step.

### Step 2 — Prepare the mesh

Substitute `SRC_NAME` with the value resolved in Step 1. `VOXEL_SIZE` is auto-computed from the source bbox; only override `DECIMATE_RATIO` per the Tuning section.

```python
import bpy, bmesh
from mathutils import Vector

SRC_NAME       = "<value from Step 1>"
DECIMATE_RATIO = 0.3   # keep 30% of remesh faces

src = bpy.data.objects[SRC_NAME]

# Auto-compute voxel size from the world-space bbox (~max_dim / 100).
bb = [src.matrix_world @ Vector(c) for c in src.bound_box]
bb_min = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
bb_max = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
max_dim = max((bb_max - bb_min)[:])
VOXEL_SIZE = max_dim / 100.0

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
    "voxel_size": round(VOXEL_SIZE, 6),
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
print(result)
```

Pass condition: `result["pass"] == True`. Inspect each sub-block on failure — any single failed check means the mesh is not slicer-ready. Do not proceed to Step 3 unless `pass == True` (or the user explicitly accepts the failure).

### Step 3 — Export back to Unity

Write the prepared object next to the source as a sibling `.glb`. Default naming: `<input_stem>_sliceable.glb` (e.g., `Assets/Models/Barrel_Assets/selected_sliceable.glb`). If that path already exists, confirm with the user before overwriting.

```python
import bpy
OUT_PATH  = "<absolute path to <input_stem>_sliceable.glb>"
WORK_NAME = "<SRC_NAME>_sliceable"

bpy.ops.object.select_all(action='DESELECT')
work = bpy.data.objects[WORK_NAME]
work.select_set(True)
bpy.context.view_layer.objects.active = work

bpy.ops.export_scene.gltf(
    filepath=OUT_PATH,
    export_format='GLB',
    use_selection=True,
    export_apply=True,   # bake any remaining transforms / modifiers
    export_yup=True,     # glTF +Y up; Unity's importer handles the rest
)
print("EXPORTED=" + OUT_PATH)
```

Unity re-imports the `.glb` on next editor focus. No special importer settings are required for slicing itself, but if the runtime path mutates the mesh at edit time, the importer's **Read/Write Enabled** flag must be on.

## Tuning

- **`VOXEL_SIZE`** is auto-set to `max_bbox_dim / 100`. To override, edit the computed value before the `REMESH` block:
  - Corners or thin plates collapse too much → halve it (face count grows ~4× before decimate).
  - Too slow / face count explodes → double it.
  - Safety floor: do not go below `max_bbox_dim / 1000` without confirming — Blender can hang on extremely small voxel sizes.
- **`DECIMATE_RATIO`**: 0.3 keeps silhouette well in most cases. Lower for lighter assets, but watch the slice probe — aggressive decimation can introduce zero-area faces or break manifoldness. Raise toward 1.0 if `pass == False`. Set to `1.0` to disable decimation if face count is not a constraint.
- **Recovery on verify failure**: delete `<SRC>_sliceable`, unhide source (`hide_set(False)`), adjust parameters, rerun Step 2. The source `.glb` on disk is never touched until Step 3.

## Limitations

- **glTF round-trip splits vertices at UV seams and sharp normals.** After Unity imports the exported `.glb`, the mesh will appear non-manifold even though it remains topologically closed in 3D space. The runtime slicer **must perform a merge-by-distance / weld pass on import** (tolerance ≈ `1e-5` of model scale) before computing cap geometry. Without this, the slicer will see spurious boundaries along the seams and produce broken caps. This is an export-format limitation, not a defect of the prepared mesh.
- **Cap-face UVs are out of scope.** The runtime slicer must generate UVs for the newly created cap polygons (typically by planar projection in the cut plane).
- **UV continuity at voxel boundaries.** Where Voxel Remesh boundaries cross original UV island seams, small UV discontinuities remain on the surface. If unacceptable, follow this skill with a Smart UV Unwrap + texture bake into the new UVs.
- **Only material slots and UVs are preserved.** Vertex colors and other loop/poly attributes are dropped unless `data_types_loops` / `data_types_polys` on the Data Transfer modifier are extended.
- **Inputs already closed with sharp detail to preserve** are better handled by Boolean Union — out of scope here.
