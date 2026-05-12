using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UPlane = Unity.Mathematics.Geometry.Plane;

namespace MeshCutter
{
    // Pure managed reference implementation.
    // Slices a mesh by an arbitrary plane and produces two sealed meshes.
    public static class MeshSlicer
    {
        public static SliceResult Slice(Mesh source, UPlane plane)
            => Slice(source, plane, true);

        public static SliceResult Slice(Mesh source, UPlane plane, bool buildCap)
        {
            var data = new SourceMesh(source);
            var pos = new SideBuilder(data);
            var neg = new SideBuilder(data);
            var edges = new EdgeCutCache(data, plane, pos, neg);

            float3 n = plane.Normal;
            float w = plane.NormalAndDistance.w;

            int triCount = data.indices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = data.indices[t * 3 + 0];
                int i1 = data.indices[t * 3 + 1];
                int i2 = data.indices[t * 3 + 2];

                float d0 = math.dot(n, data.positions[i0]) + w;
                float d1 = math.dot(n, data.positions[i1]) + w;
                float d2 = math.dot(n, data.positions[i2]) + w;

                // Snap d == 0 to positive side. Avoids degenerate cases.
                bool s0 = d0 >= 0f;
                bool s1 = d1 >= 0f;
                bool s2 = d2 >= 0f;

                if (s0 && s1 && s2)
                {
                    pos.AddBodyTriangle(i0, i1, i2);
                }
                else if (!s0 && !s1 && !s2)
                {
                    neg.AddBodyTriangle(i0, i1, i2);
                }
                else
                {
                    SplitTriangle(
                        i0, i1, i2, d0, d1, d2, s0, s1, s2,
                        pos, neg, edges);
                }
            }

            if (buildCap)
            {
                CapBuilder.Build(pos, n, +1f);
                CapBuilder.Build(neg, n, -1f);
            }

            return new SliceResult
            {
                PositiveSide = pos.ToMesh("PositiveSlice"),
                NegativeSide = neg.ToMesh("NegativeSlice")
            };
        }

        // Split a triangle that straddles the plane.
        // The "lone" vertex is the single vertex on its own side.
        // Two edges of the triangle cross the plane; the cut introduces two
        // intersection vertices, and the cap segment connects them.
        static void SplitTriangle(
            int i0, int i1, int i2,
            float d0, float d1, float d2,
            bool s0, bool s1, bool s2,
            SideBuilder pos, SideBuilder neg, EdgeCutCache edges)
        {
            int posCount = (s0 ? 1 : 0) + (s1 ? 1 : 0) + (s2 ? 1 : 0);

            // Rotate so vertex `a` is the lone-side vertex.
            // Triangle remains wound (a, b, c) preserving original orientation.
            int ia, ib, ic;
            float da, db, dc;
            bool aIsPos;

            if (posCount == 1)
            {
                aIsPos = true;
                if (s0) { ia = i0; ib = i1; ic = i2; da = d0; db = d1; dc = d2; }
                else if (s1) { ia = i1; ib = i2; ic = i0; da = d1; db = d2; dc = d0; }
                else { ia = i2; ib = i0; ic = i1; da = d2; db = d0; dc = d1; }
            }
            else // posCount == 2 → lone is on negative side
            {
                aIsPos = false;
                if (!s0) { ia = i0; ib = i1; ic = i2; da = d0; db = d1; dc = d2; }
                else if (!s1) { ia = i1; ib = i2; ic = i0; da = d1; db = d2; dc = d0; }
                else { ia = i2; ib = i0; ic = i1; da = d2; db = d0; dc = d1; }
            }

            // Edges that cross: (a,b) and (c,a). Compute intersection vertices.
            // EdgeCut.GetCut returns paired indices into both side builders.
            var cutAB = edges.Get(ia, ib, da, db);
            var cutCA = edges.Get(ic, ia, dc, da);

            if (aIsPos)
            {
                // Lone (a) is on positive side; b, c are on negative side.
                // Positive piece: (a, p_ab, p_ca)
                // Negative piece: (p_ab, b, c) and (p_ab, c, p_ca)
                pos.AddIndex(pos.MapBody(ia));
                pos.AddIndex(cutAB.PosIndex);
                pos.AddIndex(cutCA.PosIndex);

                neg.AddIndex(cutAB.NegIndex);
                neg.AddIndex(neg.MapBody(ib));
                neg.AddIndex(neg.MapBody(ic));

                neg.AddIndex(cutAB.NegIndex);
                neg.AddIndex(neg.MapBody(ic));
                neg.AddIndex(cutCA.NegIndex);

                // Cap segments. The boundary of the positive piece walks
                // p_ab → p_ca along the plane; the cap polygon walks the loop.
                pos.AddCutSegment(cutAB.PosIndex, cutCA.PosIndex,
                                  cutAB.Position, cutCA.Position);
                neg.AddCutSegment(cutCA.NegIndex, cutAB.NegIndex,
                                  cutCA.Position, cutAB.Position);
            }
            else
            {
                // Lone (a) is on negative side; b, c are on positive side.
                // Positive piece: (p_ab, b, c) and (p_ab, c, p_ca)
                // Negative piece: (a, p_ab, p_ca)
                pos.AddIndex(cutAB.PosIndex);
                pos.AddIndex(pos.MapBody(ib));
                pos.AddIndex(pos.MapBody(ic));

                pos.AddIndex(cutAB.PosIndex);
                pos.AddIndex(pos.MapBody(ic));
                pos.AddIndex(cutCA.PosIndex);

                neg.AddIndex(neg.MapBody(ia));
                neg.AddIndex(cutAB.NegIndex);
                neg.AddIndex(cutCA.NegIndex);

                pos.AddCutSegment(cutCA.PosIndex, cutAB.PosIndex,
                                  cutCA.Position, cutAB.Position);
                neg.AddCutSegment(cutAB.NegIndex, cutCA.NegIndex,
                                  cutAB.Position, cutCA.Position);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────────────

    sealed class SourceMesh
    {
        public readonly float3[] positions;
        public readonly float3[] normals;
        public readonly float4[] tangents;
        public readonly float2[] uvs;
        public readonly int[] indices;
        public readonly bool hasNormals;
        public readonly bool hasTangents;
        public readonly bool hasUVs;

        public SourceMesh(Mesh m)
        {
            var v = m.vertices;
            positions = new float3[v.Length];
            for (int i = 0; i < v.Length; i++) positions[i] = v[i];

            var nrm = m.normals;
            hasNormals = nrm != null && nrm.Length == v.Length;
            normals = hasNormals ? new float3[v.Length] : null;
            if (hasNormals) for (int i = 0; i < v.Length; i++) normals[i] = nrm[i];

            var tan = m.tangents;
            hasTangents = tan != null && tan.Length == v.Length;
            tangents = hasTangents ? new float4[v.Length] : null;
            if (hasTangents) for (int i = 0; i < v.Length; i++) tangents[i] = new float4(tan[i].x, tan[i].y, tan[i].z, tan[i].w);

            var uvList = new List<Vector2>();
            m.GetUVs(0, uvList);
            hasUVs = uvList.Count == v.Length;
            uvs = hasUVs ? new float2[v.Length] : null;
            if (hasUVs) for (int i = 0; i < v.Length; i++) uvs[i] = uvList[i];

            indices = m.triangles;
        }
    }

    sealed class SideBuilder
    {
        public readonly SourceMesh src;

        public readonly List<float3> positions = new();
        public readonly List<float3> normals = new();
        public readonly List<float4> tangents = new();
        public readonly List<float2> uvs = new();
        public readonly List<int> indices = new();

        // Body remap: original src vertex index → side vertex index.
        readonly int[] bodyMap;

        // Cut segments — directed (A → B) by position only. The cap walker
        // dedupes by position because the source mesh may have multiple
        // vertices at the same location (Unity primitives duplicate per face).
        public readonly List<float3> cutSegPosA = new();
        public readonly List<float3> cutSegPosB = new();

        public SideBuilder(SourceMesh src)
        {
            this.src = src;
            bodyMap = new int[src.positions.Length];
            for (int i = 0; i < bodyMap.Length; i++) bodyMap[i] = -1;
        }

        public int MapBody(int srcIdx)
        {
            int mapped = bodyMap[srcIdx];
            if (mapped >= 0) return mapped;
            mapped = positions.Count;
            positions.Add(src.positions[srcIdx]);
            if (src.hasNormals) normals.Add(src.normals[srcIdx]);
            if (src.hasTangents) tangents.Add(src.tangents[srcIdx]);
            if (src.hasUVs) uvs.Add(src.uvs[srcIdx]);
            bodyMap[srcIdx] = mapped;
            return mapped;
        }

        public int AddInterpolated(float3 p, float3 n, float4 t, float2 uv)
        {
            int idx = positions.Count;
            positions.Add(p);
            if (src.hasNormals) normals.Add(n);
            if (src.hasTangents) tangents.Add(t);
            if (src.hasUVs) uvs.Add(uv);
            return idx;
        }

        // Add a cap-only vertex with explicit attributes (used for the cap surface).
        public int AddCapVertex(float3 p, float3 n, float4 t, float2 uv)
            => AddInterpolated(p, n, t, uv);

        public void AddBodyTriangle(int i0, int i1, int i2)
        {
            indices.Add(MapBody(i0));
            indices.Add(MapBody(i1));
            indices.Add(MapBody(i2));
        }

        public void AddIndex(int i) => indices.Add(i);

        public void AddCutSegment(int a, int b, float3 pa, float3 pb)
        {
            cutSegPosA.Add(pa);
            cutSegPosB.Add(pb);
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            if (positions.Count > 65535)
                m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            int n = positions.Count;
            var vp = new Vector3[n];
            for (int i = 0; i < n; i++) vp[i] = positions[i];
            m.vertices = vp;

            if (src.hasNormals)
            {
                var vn = new Vector3[n];
                for (int i = 0; i < n; i++) vn[i] = normals[i];
                m.normals = vn;
            }
            if (src.hasTangents)
            {
                var vt = new Vector4[n];
                for (int i = 0; i < n; i++) { var t = tangents[i]; vt[i] = new Vector4(t.x, t.y, t.z, t.w); }
                m.tangents = vt;
            }
            if (src.hasUVs)
            {
                var vu = new Vector2[n];
                for (int i = 0; i < n; i++) vu[i] = uvs[i];
                m.uv = vu;
            }
            m.SetTriangles(indices, 0);
            if (!src.hasNormals) m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    // Caches the per-edge intersection so triangles that share an edge share a vertex.
    sealed class EdgeCutCache
    {
        public struct Cut
        {
            public int PosIndex;
            public int NegIndex;
            public float3 Position;
        }

        readonly SourceMesh src;
        readonly SideBuilder pos, neg;
        readonly Dictionary<long, Cut> map = new();

        public EdgeCutCache(SourceMesh src, UPlane plane, SideBuilder pos, SideBuilder neg)
        {
            this.src = src;
            this.pos = pos;
            this.neg = neg;
        }

        public Cut Get(int ia, int ib, float da, float db)
        {
            long key = ia < ib
                ? ((long)ia << 32) | (uint)ib
                : ((long)ib << 32) | (uint)ia;

            if (map.TryGetValue(key, out var cut)) return cut;

            // Lerp parameter from a → b.
            float t = math.saturate(da / (da - db));

            var pa = src.positions[ia];
            var pb = src.positions[ib];
            float3 p = math.lerp(pa, pb, t);

            float3 n = default;
            if (src.hasNormals)
                n = math.normalizesafe(math.lerp(src.normals[ia], src.normals[ib], t));

            float4 tan = default;
            if (src.hasTangents)
            {
                float4 ta = src.tangents[ia];
                float4 tb = src.tangents[ib];
                float3 tan3 = math.normalizesafe(math.lerp(ta.xyz, tb.xyz, t));
                tan = new float4(tan3, ta.w);
            }

            float2 uv = default;
            if (src.hasUVs)
                uv = math.lerp(src.uvs[ia], src.uvs[ib], t);

            cut.Position = p;
            cut.PosIndex = pos.AddInterpolated(p, n, tan, uv);
            cut.NegIndex = neg.AddInterpolated(p, n, tan, uv);
            map[key] = cut;
            return cut;
        }
    }

    // Walks the collected cut segments into closed loops and fan-triangulates them.
    // Vertices are keyed by quantized position so caps stitch correctly across
    // source meshes that duplicate vertices per face.
    static class CapBuilder
    {
        const float kQuantize = 1e-4f;

        public static void Build(SideBuilder side, float3 planeNormal, float sign)
        {
            int segCount = side.cutSegPosA.Count;
            if (segCount == 0) return;

            // Cap normal: faces outward from this piece.
            float3 capNormal = -sign * planeNormal;

            BuildBasis(planeNormal, out float3 basisU, out float3 basisV);

            // Quantized-position → loop-node id.
            var keyToNode = new Dictionary<int3, int>(segCount * 2);
            var nodePositions = new List<float3>(segCount * 2);

            int GetNode(float3 p)
            {
                var key = Quantize(p);
                if (keyToNode.TryGetValue(key, out int id)) return id;
                id = nodePositions.Count;
                nodePositions.Add(p);
                keyToNode[key] = id;
                return id;
            }

            // Adjacency: directed graph node → list of next nodes.
            // Most cap vertices have degree-1 out, but a face diagonal can
            // produce a node with multiple outgoing edges (collinear chain).
            var adj = new Dictionary<int, List<int>>(segCount * 2);
            void AddEdge(int from, int to)
            {
                if (!adj.TryGetValue(from, out var list))
                {
                    list = new List<int>(2);
                    adj[from] = list;
                }
                list.Add(to);
            }

            for (int i = 0; i < segCount; i++)
            {
                int from = GetNode(side.cutSegPosA[i]);
                int to = GetNode(side.cutSegPosB[i]);
                if (from == to) continue;
                AddEdge(from, to);
            }

            // Cap vertex per node, lazily.
            var capVertOf = new Dictionary<int, int>(adj.Count);
            int CapVert(int node)
            {
                if (capVertOf.TryGetValue(node, out int idx)) return idx;
                float3 p = nodePositions[node];
                float u = math.dot(p, basisU);
                float v = math.dot(p, basisV);
                float4 tan = new float4(basisU, 1f);
                idx = side.AddCapVertex(p, capNormal, tan, new float2(u, v));
                capVertOf[node] = idx;
                return idx;
            }

            // Walk loops by consuming adjacency entries. Each visit pops one
            // outgoing edge from `adj`, so we walk every segment exactly once.
            var loop = new List<int>(64);
            foreach (var start in new List<int>(adj.Keys))
            {
                while (adj.TryGetValue(start, out var startList) && startList.Count > 0)
                {
                    loop.Clear();
                    int cur = start;
                    int guard = segCount + 4;
                    while (guard-- > 0)
                    {
                        if (!adj.TryGetValue(cur, out var outs) || outs.Count == 0) break;
                        int nxt = outs[outs.Count - 1];
                        outs.RemoveAt(outs.Count - 1);
                        loop.Add(cur);
                        if (nxt == start) break;
                        cur = nxt;
                    }

                    if (loop.Count < 3) continue;

                    // Reversed-winding fan so the cap faces away from this piece.
                    int v0 = CapVert(loop[0]);
                    for (int i = 1; i < loop.Count - 1; i++)
                    {
                        int v1 = CapVert(loop[i]);
                        int v2 = CapVert(loop[i + 1]);
                        side.AddIndex(v0);
                        side.AddIndex(v2);
                        side.AddIndex(v1);
                    }
                }
            }
        }

        static int3 Quantize(float3 p)
        {
            return new int3(
                (int)math.round(p.x / kQuantize),
                (int)math.round(p.y / kQuantize),
                (int)math.round(p.z / kQuantize));
        }

        static void BuildBasis(float3 normal, out float3 u, out float3 v)
        {
            float3 n = math.normalize(normal);
            float3 a = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            u = math.normalize(math.cross(n, a));
            v = math.cross(n, u);
        }
    }
}
