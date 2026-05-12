using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UPlane = Unity.Mathematics.Geometry.Plane;

namespace MeshCutter
{
    // Burst-compiled mesh slicer. Same algorithm as the managed reference but
    // executed in a single IJob over NativeContainers.
    //
    // Hybrid: triangle classification, edge-cut interpolation, and split-triangle
    // emission run inside a Burst job. The cap loop walking + triangulation runs
    // in managed code afterward (it's a small fraction of work and avoids
    // wrangling a multi-list adjacency map under Burst).
    public static class MeshSlicerBurst
    {
        public static SliceResult Slice(Mesh source, UPlane plane)
            => Slice(source, plane, true);

        public static SliceResult Slice(Mesh source, UPlane plane, bool buildCap)
        {
            // ── Read source ──────────────────────────────────────────────
            var verts = source.vertices;
            var nrms = source.normals;
            var tans = source.tangents;
            var uvList = new List<Vector2>();
            source.GetUVs(0, uvList);
            var inds = source.triangles;

            int vc = verts.Length;
            bool hasNrm = nrms != null && nrms.Length == vc;
            bool hasTan = tans != null && tans.Length == vc;
            bool hasUV = uvList.Count == vc;

            var srcPos = new NativeArray<float3>(vc, Allocator.TempJob);
            for (int i = 0; i < vc; i++) srcPos[i] = verts[i];

            var srcNrm = new NativeArray<float3>(hasNrm ? vc : 0, Allocator.TempJob);
            if (hasNrm) for (int i = 0; i < vc; i++) srcNrm[i] = nrms[i];

            var srcTan = new NativeArray<float4>(hasTan ? vc : 0, Allocator.TempJob);
            if (hasTan) for (int i = 0; i < vc; i++) srcTan[i] = new float4(tans[i].x, tans[i].y, tans[i].z, tans[i].w);

            var srcUV = new NativeArray<float2>(hasUV ? vc : 0, Allocator.TempJob);
            if (hasUV) for (int i = 0; i < vc; i++) srcUV[i] = uvList[i];

            var srcIdx = new NativeArray<int>(inds, Allocator.TempJob);

            // ── Output containers ────────────────────────────────────────
            int est = vc * 2;
            var posP = new NativeList<float3>(est, Allocator.TempJob);
            var negP = new NativeList<float3>(est, Allocator.TempJob);
            var posN = new NativeList<float3>(hasNrm ? est : 0, Allocator.TempJob);
            var negN = new NativeList<float3>(hasNrm ? est : 0, Allocator.TempJob);
            var posT = new NativeList<float4>(hasTan ? est : 0, Allocator.TempJob);
            var negT = new NativeList<float4>(hasTan ? est : 0, Allocator.TempJob);
            var posU = new NativeList<float2>(hasUV ? est : 0, Allocator.TempJob);
            var negU = new NativeList<float2>(hasUV ? est : 0, Allocator.TempJob);
            var posI = new NativeList<int>(inds.Length, Allocator.TempJob);
            var negI = new NativeList<int>(inds.Length, Allocator.TempJob);

            var posCapA = new NativeList<float3>(64, Allocator.TempJob);
            var posCapB = new NativeList<float3>(64, Allocator.TempJob);
            var negCapA = new NativeList<float3>(64, Allocator.TempJob);
            var negCapB = new NativeList<float3>(64, Allocator.TempJob);

            // ── Run job ──────────────────────────────────────────────────
            var job = new SliceJob
            {
                srcPos = srcPos, srcNrm = srcNrm, srcTan = srcTan, srcUV = srcUV, srcIdx = srcIdx,
                hasNrm = hasNrm, hasTan = hasTan, hasUV = hasUV,
                plane = plane.NormalAndDistance,
                posP = posP, negP = negP,
                posN = posN, negN = negN,
                posT = posT, negT = negT,
                posU = posU, negU = negU,
                posI = posI, negI = negI,
                posCapA = posCapA, posCapB = posCapB,
                negCapA = negCapA, negCapB = negCapB,
            };
            job.Run();

            // ── Cap building (managed) ───────────────────────────────────
            if (buildCap)
            {
                BurstCapBuilder.Build(plane.Normal, +1f,
                    posCapA, posCapB, posP, posN, posT, posU, posI, hasNrm, hasTan, hasUV);
                BurstCapBuilder.Build(plane.Normal, -1f,
                    negCapA, negCapB, negP, negN, negT, negU, negI, hasNrm, hasTan, hasUV);
            }

            // ── Build meshes ─────────────────────────────────────────────
            var posMesh = BuildMesh("PositiveSlice", posP, posN, posT, posU, posI, hasNrm, hasTan, hasUV);
            var negMesh = BuildMesh("NegativeSlice", negP, negN, negT, negU, negI, hasNrm, hasTan, hasUV);

            // ── Dispose ──────────────────────────────────────────────────
            srcPos.Dispose(); srcNrm.Dispose(); srcTan.Dispose(); srcUV.Dispose(); srcIdx.Dispose();
            posP.Dispose(); negP.Dispose();
            posN.Dispose(); negN.Dispose();
            posT.Dispose(); negT.Dispose();
            posU.Dispose(); negU.Dispose();
            posI.Dispose(); negI.Dispose();
            posCapA.Dispose(); posCapB.Dispose();
            negCapA.Dispose(); negCapB.Dispose();

            return new SliceResult { PositiveSide = posMesh, NegativeSide = negMesh };
        }

        static Mesh BuildMesh(
            string name,
            NativeList<float3> P, NativeList<float3> N, NativeList<float4> T, NativeList<float2> U,
            NativeList<int> I, bool hasNrm, bool hasTan, bool hasUV)
        {
            var m = new Mesh { name = name };
            int n = P.Length;
            if (n > 65535) m.indexFormat = IndexFormat.UInt32;

            var pos = new Vector3[n];
            var Pa = P.AsArray();
            for (int i = 0; i < n; i++) pos[i] = Pa[i];
            m.vertices = pos;

            if (hasNrm)
            {
                var v = new Vector3[n];
                var a = N.AsArray();
                for (int i = 0; i < n; i++) v[i] = a[i];
                m.normals = v;
            }
            if (hasTan)
            {
                var v = new Vector4[n];
                var a = T.AsArray();
                for (int i = 0; i < n; i++) { var t = a[i]; v[i] = new Vector4(t.x, t.y, t.z, t.w); }
                m.tangents = v;
            }
            if (hasUV)
            {
                var v = new Vector2[n];
                var a = U.AsArray();
                for (int i = 0; i < n; i++) v[i] = a[i];
                m.uv = v;
            }

            m.SetTriangles(I.AsArray().ToArray(), 0);
            if (!hasNrm) m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ───────────────────────────────────────────────────────────────────
        // Burst job
        // ───────────────────────────────────────────────────────────────────

        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        struct SliceJob : IJob
        {
            [ReadOnly] public NativeArray<float3> srcPos;
            [ReadOnly] public NativeArray<float3> srcNrm;
            [ReadOnly] public NativeArray<float4> srcTan;
            [ReadOnly] public NativeArray<float2> srcUV;
            [ReadOnly] public NativeArray<int> srcIdx;
            public bool hasNrm, hasTan, hasUV;
            public float4 plane;

            public NativeList<float3> posP, negP;
            public NativeList<float3> posN, negN;
            public NativeList<float4> posT, negT;
            public NativeList<float2> posU, negU;
            public NativeList<int> posI, negI;

            public NativeList<float3> posCapA, posCapB;
            public NativeList<float3> negCapA, negCapB;

            public void Execute()
            {
                int srcVc = srcPos.Length;
                var posMap = new NativeArray<int>(srcVc, Allocator.Temp);
                var negMap = new NativeArray<int>(srcVc, Allocator.Temp);
                for (int i = 0; i < srcVc; i++) { posMap[i] = -1; negMap[i] = -1; }

                // value: x = posIndex, y = negIndex
                var cache = new NativeHashMap<long, int2>(64, Allocator.Temp);

                int triCount = srcIdx.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    int i0 = srcIdx[t * 3 + 0];
                    int i1 = srcIdx[t * 3 + 1];
                    int i2 = srcIdx[t * 3 + 2];
                    float d0 = math.dot(plane.xyz, srcPos[i0]) + plane.w;
                    float d1 = math.dot(plane.xyz, srcPos[i1]) + plane.w;
                    float d2 = math.dot(plane.xyz, srcPos[i2]) + plane.w;
                    bool s0 = d0 >= 0f;
                    bool s1 = d1 >= 0f;
                    bool s2 = d2 >= 0f;

                    if (s0 && s1 && s2)
                    {
                        posI.Add(MapBody(i0, true, ref posMap));
                        posI.Add(MapBody(i1, true, ref posMap));
                        posI.Add(MapBody(i2, true, ref posMap));
                    }
                    else if (!s0 && !s1 && !s2)
                    {
                        negI.Add(MapBody(i0, false, ref negMap));
                        negI.Add(MapBody(i1, false, ref negMap));
                        negI.Add(MapBody(i2, false, ref negMap));
                    }
                    else
                    {
                        Split(i0, i1, i2, d0, d1, d2, s0, s1, s2,
                              ref posMap, ref negMap, ref cache);
                    }
                }

                cache.Dispose();
                posMap.Dispose();
                negMap.Dispose();
            }

            int MapBody(int srcI, bool toPos, ref NativeArray<int> map)
            {
                int mapped = map[srcI];
                if (mapped >= 0) return mapped;
                if (toPos)
                {
                    mapped = posP.Length;
                    posP.Add(srcPos[srcI]);
                    if (hasNrm) posN.Add(srcNrm[srcI]);
                    if (hasTan) posT.Add(srcTan[srcI]);
                    if (hasUV) posU.Add(srcUV[srcI]);
                }
                else
                {
                    mapped = negP.Length;
                    negP.Add(srcPos[srcI]);
                    if (hasNrm) negN.Add(srcNrm[srcI]);
                    if (hasTan) negT.Add(srcTan[srcI]);
                    if (hasUV) negU.Add(srcUV[srcI]);
                }
                map[srcI] = mapped;
                return mapped;
            }

            int2 Cut(int ia, int ib, float da, float db, ref NativeHashMap<long, int2> cache)
            {
                long key = ia < ib
                    ? ((long)ia << 32) | (uint)ib
                    : ((long)ib << 32) | (uint)ia;

                if (cache.TryGetValue(key, out var hit)) return hit;

                float t = math.saturate(da / (da - db));
                float3 p = math.lerp(srcPos[ia], srcPos[ib], t);

                int pi = posP.Length;
                int ni = negP.Length;
                posP.Add(p); negP.Add(p);

                if (hasNrm)
                {
                    float3 n = math.normalizesafe(math.lerp(srcNrm[ia], srcNrm[ib], t));
                    posN.Add(n); negN.Add(n);
                }
                if (hasTan)
                {
                    float4 ta = srcTan[ia], tb = srcTan[ib];
                    float3 tn = math.normalizesafe(math.lerp(ta.xyz, tb.xyz, t));
                    float4 tt = new float4(tn, ta.w);
                    posT.Add(tt); negT.Add(tt);
                }
                if (hasUV)
                {
                    float2 u = math.lerp(srcUV[ia], srcUV[ib], t);
                    posU.Add(u); negU.Add(u);
                }

                hit = new int2(pi, ni);
                cache.Add(key, hit);
                return hit;
            }

            void Split(int i0, int i1, int i2,
                       float d0, float d1, float d2,
                       bool s0, bool s1, bool s2,
                       ref NativeArray<int> posMap,
                       ref NativeArray<int> negMap,
                       ref NativeHashMap<long, int2> cache)
            {
                int posCount = (s0 ? 1 : 0) + (s1 ? 1 : 0) + (s2 ? 1 : 0);

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
                else
                {
                    aIsPos = false;
                    if (!s0) { ia = i0; ib = i1; ic = i2; da = d0; db = d1; dc = d2; }
                    else if (!s1) { ia = i1; ib = i2; ic = i0; da = d1; db = d2; dc = d0; }
                    else { ia = i2; ib = i0; ic = i1; da = d2; db = d0; dc = d1; }
                }

                int2 cAB = Cut(ia, ib, da, db, ref cache);
                int2 cCA = Cut(ic, ia, dc, da, ref cache);
                float3 pAB = posP[cAB.x];
                float3 pCA = posP[cCA.x];

                if (aIsPos)
                {
                    posI.Add(MapBody(ia, true, ref posMap));
                    posI.Add(cAB.x);
                    posI.Add(cCA.x);

                    negI.Add(cAB.y);
                    negI.Add(MapBody(ib, false, ref negMap));
                    negI.Add(MapBody(ic, false, ref negMap));

                    negI.Add(cAB.y);
                    negI.Add(MapBody(ic, false, ref negMap));
                    negI.Add(cCA.y);

                    posCapA.Add(pAB); posCapB.Add(pCA);
                    negCapA.Add(pCA); negCapB.Add(pAB);
                }
                else
                {
                    posI.Add(cAB.x);
                    posI.Add(MapBody(ib, true, ref posMap));
                    posI.Add(MapBody(ic, true, ref posMap));

                    posI.Add(cAB.x);
                    posI.Add(MapBody(ic, true, ref posMap));
                    posI.Add(cCA.x);

                    negI.Add(MapBody(ia, false, ref negMap));
                    negI.Add(cAB.y);
                    negI.Add(cCA.y);

                    posCapA.Add(pCA); posCapB.Add(pAB);
                    negCapA.Add(pAB); negCapB.Add(pCA);
                }
            }
        }
    }

    // Managed cap builder operating on NativeLists (so it can append to the
    // Burst job's output buffers in place).
    static class BurstCapBuilder
    {
        const float kQuantize = 1e-4f;

        public static void Build(
            float3 planeNormal, float sign,
            NativeList<float3> capA, NativeList<float3> capB,
            NativeList<float3> P, NativeList<float3> N, NativeList<float4> T, NativeList<float2> U,
            NativeList<int> I, bool hasNrm, bool hasTan, bool hasUV)
        {
            int segCount = capA.Length;
            if (segCount == 0) return;

            float3 capNormal = -sign * planeNormal;
            BuildBasis(planeNormal, out float3 basisU, out float3 basisV);

            var keyToNode = new Dictionary<int3, int>(segCount * 2);
            var nodePos = new List<float3>(segCount * 2);

            int GetNode(float3 p)
            {
                var k = Quantize(p);
                if (keyToNode.TryGetValue(k, out int id)) return id;
                id = nodePos.Count;
                nodePos.Add(p);
                keyToNode[k] = id;
                return id;
            }

            var adj = new Dictionary<int, List<int>>(segCount);
            void AddEdge(int from, int to)
            {
                if (!adj.TryGetValue(from, out var l)) { l = new List<int>(2); adj[from] = l; }
                l.Add(to);
            }

            var capArrA = capA.AsArray();
            var capArrB = capB.AsArray();
            for (int i = 0; i < segCount; i++)
            {
                int from = GetNode(capArrA[i]);
                int to = GetNode(capArrB[i]);
                if (from == to) continue;
                AddEdge(from, to);
            }

            var capVertOf = new Dictionary<int, int>(adj.Count);
            int CapVert(int node)
            {
                if (capVertOf.TryGetValue(node, out int idx)) return idx;
                float3 p = nodePos[node];
                idx = P.Length;
                P.Add(p);
                if (hasNrm) N.Add(capNormal);
                if (hasTan) T.Add(new float4(basisU, 1f));
                if (hasUV) U.Add(new float2(math.dot(p, basisU), math.dot(p, basisV)));
                capVertOf[node] = idx;
                return idx;
            }

            var loop = new List<int>(64);
            foreach (var start in new List<int>(adj.Keys))
            {
                while (adj.TryGetValue(start, out var sl) && sl.Count > 0)
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

                    int v0 = CapVert(loop[0]);
                    for (int i = 1; i < loop.Count - 1; i++)
                    {
                        int v1 = CapVert(loop[i]);
                        int v2 = CapVert(loop[i + 1]);
                        I.Add(v0); I.Add(v2); I.Add(v1);
                    }
                }
            }
        }

        static int3 Quantize(float3 p) => new int3(
            (int)math.round(p.x / kQuantize),
            (int)math.round(p.y / kQuantize),
            (int)math.round(p.z / kQuantize));

        static void BuildBasis(float3 normal, out float3 u, out float3 v)
        {
            float3 n = math.normalize(normal);
            float3 a = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            u = math.normalize(math.cross(n, a));
            v = math.cross(n, u);
        }
    }
}
