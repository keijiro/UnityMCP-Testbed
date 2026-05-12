using UnityEngine;
using Unity.Mathematics;

namespace MeshSlicer.Tests
{
    internal static class MeshSlicerTestUtils
    {
        public static Mesh CreatePrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var src = go.GetComponent<MeshFilter>().sharedMesh;
            // Copy to detach from internal asset.
            var copy = Object.Instantiate(src);
            copy.name = src.name + "_copy";
            Object.DestroyImmediate(go);
            return copy;
        }

        // Signed volume of a closed triangle mesh via Σ (v0 · (v1 × v2)) / 6.
        // Positive for outward-facing CCW winding.
        public static float SignedVolume(Mesh m)
        {
            var verts = m.vertices;
            var tris  = m.triangles;
            double s = 0;
            for (int i = 0; i < tris.Length; i += 3)
            {
                var a = (double3)(float3)verts[tris[i]];
                var b = (double3)(float3)verts[tris[i + 1]];
                var c = (double3)(float3)verts[tris[i + 2]];
                s += math.dot(a, math.cross(b, c));
            }
            return (float)(s / 6.0);
        }

        // Approximate "is closed" test using position-merged vertices: each directed
        // edge in a closed manifold appears once in each direction, so the signed sum
        // of (a→b - b→a) over all undirected edges should be zero. Vertices are
        // merged by quantized position so that primitives with split vertices per
        // face (e.g. Unity's Cube) are still recognized as closed.
        public static bool LooksClosed(Mesh m, float eps = 1e-4f)
        {
            var verts = m.vertices;
            var tris  = m.triangles;
            // Build vertex remap by quantized position.
            var remap = new int[verts.Length];
            var bucket = new System.Collections.Generic.Dictionary<long, int>();
            float inv = 1f / eps;
            for (int i = 0; i < verts.Length; i++)
            {
                int qx = Mathf.RoundToInt(verts[i].x * inv);
                int qy = Mathf.RoundToInt(verts[i].y * inv);
                int qz = Mathf.RoundToInt(verts[i].z * inv);
                long key = (((long)qx & 0x1FFFFF) << 42) | (((long)qy & 0x1FFFFF) << 21) | ((long)qz & 0x1FFFFF);
                if (!bucket.TryGetValue(key, out var idx)) { idx = bucket.Count; bucket[key] = idx; }
                remap[i] = idx;
            }
            var counts = new System.Collections.Generic.Dictionary<long, int>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = remap[tris[i]], b = remap[tris[i + 1]], c = remap[tris[i + 2]];
                if (a == b || b == c || c == a) continue; // degenerate
                AddEdge(counts, a, b);
                AddEdge(counts, b, c);
                AddEdge(counts, c, a);
            }
            foreach (var kv in counts) if (kv.Value != 0) return false;
            return true;
        }

        static void AddEdge(System.Collections.Generic.Dictionary<long, int> map, int a, int b)
        {
            int lo = math.min(a, b), hi = math.max(a, b);
            long key = ((long)lo << 32) | (uint)hi;
            int delta = a < b ? 1 : -1;
            map.TryGetValue(key, out var v);
            map[key] = v + delta;
        }

        // Verify all vertices on a half-space side.
        public static bool AllVerticesOnSide(Mesh m, Unity.Mathematics.Geometry.Plane plane, int side, float eps = 1e-3f)
        {
            var verts = m.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                float d = plane.SignedDistanceToPoint(verts[i]);
                if (side > 0 && d < -eps) return false;
                if (side < 0 && d >  eps) return false;
            }
            return true;
        }
    }
}
