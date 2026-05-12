using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer
{
    // Burst- and Job-System-compiled mesh slicer using the advanced Mesh API
    // (AcquireReadOnlyMeshData / AllocateWritableMeshData) so input is read with
    // zero copies of vertex/index data into managed memory and outputs are written
    // straight into the destination GPU upload buffers.
    //
    // The algorithm is identical to NaiveMeshSlicer but every dictionary becomes
    // a NativeHashMap, every List becomes a NativeList, and the per-triangle loop
    // runs inside a Burst-compiled IJob. There is no managed allocation per slice.
    public static class BurstMeshSlicer
    {
        // Interleaved vertex format used in both the output mesh buffers and the
        // intermediate NativeLists. Matches the descriptors set on the MeshData.
        [StructLayout(LayoutKind.Sequential)]
        public struct Vertex
        {
            public float3 Pos;
            public float3 Nrm;
            public float4 Tan;
            public float2 Uv;
        }

        public static SliceResult Slice(Mesh source, Plane plane)
        {
            plane = Plane.Normalize(plane);

            using var meshData = Mesh.AcquireReadOnlyMeshData(source);
            var src = meshData[0];
            int vCount = src.vertexCount;
            int subCount = src.subMeshCount;
            if (subCount == 0) return new SliceResult(null, null);
            var sub = src.GetSubMesh(0);
            int iCount = sub.indexCount;

            // Allocate input buffers.
            var srcPos = new NativeArray<float3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcNrm = new NativeArray<float3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcTan = new NativeArray<float4>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcUv  = new NativeArray<float2>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcTri = new NativeArray<int>(iCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            src.GetVertices(srcPos.Reinterpret<Vector3>());
            bool hasNrm = src.HasVertexAttribute(VertexAttribute.Normal);
            bool hasTan = src.HasVertexAttribute(VertexAttribute.Tangent);
            bool hasUv  = src.HasVertexAttribute(VertexAttribute.TexCoord0);
            if (hasNrm) src.GetNormals(srcNrm.Reinterpret<Vector3>());
            if (hasTan) src.GetTangents(srcTan.Reinterpret<Vector4>());
            if (hasUv)  src.GetUVs(0, srcUv.Reinterpret<Vector2>());
            src.GetIndices(srcTri, 0);

            // Output containers.
            int triCountIn = iCount / 3;
            int initialVertexCapacity = vCount + triCountIn / 4 + 64;
            int initialTriCapacity    = iCount + triCountIn / 2 + 64;
            int initialCapCapacity    = math.max(64, triCountIn / 8);

            var posVerts = new NativeList<Vertex>(initialVertexCapacity, Allocator.TempJob);
            var negVerts = new NativeList<Vertex>(initialVertexCapacity, Allocator.TempJob);
            var posTris  = new NativeList<int>(initialTriCapacity, Allocator.TempJob);
            var negTris  = new NativeList<int>(initialTriCapacity, Allocator.TempJob);
            var capPts   = new NativeList<float3>(initialCapCapacity, Allocator.TempJob);
            var capByEdge = new NativeHashMap<long, int>(initialCapCapacity, Allocator.TempJob);
            var origPosIdx = new NativeHashMap<int, int>(vCount, Allocator.TempJob);
            var origNegIdx = new NativeHashMap<int, int>(vCount, Allocator.TempJob);
            var cutPosIdx  = new NativeHashMap<long, int>(initialCapCapacity, Allocator.TempJob);
            var cutNegIdx  = new NativeHashMap<long, int>(initialCapCapacity, Allocator.TempJob);
            var dist = new NativeArray<float>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var sliceJob = new SliceJob
            {
                SrcPos = srcPos, SrcNrm = srcNrm, SrcTan = srcTan, SrcUv = srcUv, SrcTri = srcTri,
                Plane = plane, HasNrm = hasNrm, HasTan = hasTan, HasUv = hasUv,
                PosVerts = posVerts, PosTris = posTris,
                NegVerts = negVerts, NegTris = negTris,
                CapPts = capPts, CapByEdge = capByEdge,
                OrigPosIdx = origPosIdx, OrigNegIdx = origNegIdx,
                CutPosIdx = cutPosIdx, CutNegIdx = cutNegIdx,
                Dist = dist,
            };
            sliceJob.Schedule().Complete();

            // Cap construction (separate Burst job so it can run in isolation/scaled later).
            if (capPts.Length >= 3)
            {
                new CapJob
                {
                    Plane = plane,
                    CapPoints = capPts.AsArray(),
                    PosVerts = posVerts, PosTris = posTris,
                    NegVerts = negVerts, NegTris = negTris,
                }.Schedule().Complete();
            }

            // Build output meshes via advanced Mesh API (zero managed copy on the write side).
            var result = new SliceResult(
                BuildMesh(posVerts, posTris, source.name + "_Pos"),
                BuildMesh(negVerts, negTris, source.name + "_Neg"));

            srcPos.Dispose(); srcNrm.Dispose(); srcTan.Dispose(); srcUv.Dispose(); srcTri.Dispose();
            posVerts.Dispose(); negVerts.Dispose();
            posTris.Dispose(); negTris.Dispose();
            capPts.Dispose(); capByEdge.Dispose();
            origPosIdx.Dispose(); origNegIdx.Dispose();
            cutPosIdx.Dispose(); cutNegIdx.Dispose();
            dist.Dispose();

            return result;
        }

        static Mesh BuildMesh(NativeList<Vertex> verts, NativeList<int> tris, string name)
        {
            int vc = verts.Length, ic = tris.Length;
            if (vc == 0 || ic == 0) return null;

            var meshArr = Mesh.AllocateWritableMeshData(1);
            var md = meshArr[0];

            var attrs = new NativeArray<VertexAttributeDescriptor>(4, Allocator.Temp);
            attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, 0);
            attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, 0);
            attrs[2] = new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4, 0);
            attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
            md.SetVertexBufferParams(vc, attrs);
            attrs.Dispose();

            md.SetIndexBufferParams(ic, IndexFormat.UInt32);

            // Write directly into the mesh-data backing storage. memcpy is fine — the
            // Vertex struct layout matches the attribute descriptors above.
            var dstVerts = md.GetVertexData<Vertex>();
            var dstTris  = md.GetIndexData<uint>();
            unsafe
            {
                UnsafeUtility.MemCpy(dstVerts.GetUnsafePtr(), verts.GetUnsafeReadOnlyPtr(),
                    (long)vc * UnsafeUtility.SizeOf<Vertex>());
                // Indices we have are int — copy with widening to uint.
                var src = (int*)tris.GetUnsafeReadOnlyPtr();
                var dst = (uint*)dstTris.GetUnsafePtr();
                for (int i = 0; i < ic; i++) dst[i] = (uint)src[i];
            }

            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, ic, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
                MeshUpdateFlags.DontNotifyMeshUsers   | MeshUpdateFlags.DontResetBoneBounds);

            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            Mesh.ApplyAndDisposeWritableMeshData(meshArr, mesh,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers |
                MeshUpdateFlags.DontResetBoneBounds);
            mesh.RecalculateBounds();
            return mesh;
        }

        // -----------------------------------------------------------------------
        // Slice job — Burst compiled, single-threaded. Walks every triangle once,
        // emitting body geometry on each side and recording cap intersection points.
        // -----------------------------------------------------------------------
        [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast,
                      OptimizeFor = OptimizeFor.Performance)]
        struct SliceJob : IJob
        {
            [ReadOnly] public NativeArray<float3> SrcPos;
            [ReadOnly] public NativeArray<float3> SrcNrm;
            [ReadOnly] public NativeArray<float4> SrcTan;
            [ReadOnly] public NativeArray<float2> SrcUv;
            [ReadOnly] public NativeArray<int>    SrcTri;
            public Plane Plane;
            public bool HasNrm, HasTan, HasUv;

            public NativeList<Vertex> PosVerts;
            public NativeList<int>    PosTris;
            public NativeList<Vertex> NegVerts;
            public NativeList<int>    NegTris;

            public NativeList<float3>     CapPts;
            public NativeHashMap<long, int> CapByEdge;
            public NativeHashMap<int,  int> OrigPosIdx;
            public NativeHashMap<int,  int> OrigNegIdx;
            public NativeHashMap<long, int> CutPosIdx;
            public NativeHashMap<long, int> CutNegIdx;
            public NativeArray<float>     Dist;

            public void Execute()
            {
                // Pre-compute signed distances.
                int vc = SrcPos.Length;
                for (int i = 0; i < vc; i++)
                    Dist[i] = math.dot(Plane.Normal, SrcPos[i]) + Plane.Distance;

                int tCount = SrcTri.Length;
                for (int t = 0; t < tCount; t += 3)
                {
                    int i0 = SrcTri[t], i1 = SrcTri[t + 1], i2 = SrcTri[t + 2];
                    float d0 = Dist[i0], d1 = Dist[i1], d2 = Dist[i2];
                    int s0 = d0 >= 0f ? 1 : -1;
                    int s1 = d1 >= 0f ? 1 : -1;
                    int s2 = d2 >= 0f ? 1 : -1;
                    int sum = s0 + s1 + s2;

                    if (sum == 3)
                    {
                        int a = AddOriginal(i0, true);
                        int b = AddOriginal(i1, true);
                        int c = AddOriginal(i2, true);
                        PosTris.Add(a); PosTris.Add(b); PosTris.Add(c);
                        continue;
                    }
                    if (sum == -3)
                    {
                        int a = AddOriginal(i0, false);
                        int b = AddOriginal(i1, false);
                        int c = AddOriginal(i2, false);
                        NegTris.Add(a); NegTris.Add(b); NegTris.Add(c);
                        continue;
                    }

                    int solo, sideOfSolo;
                    if (s0 != s1 && s0 != s2) { solo = 0; sideOfSolo = s0; }
                    else if (s1 != s0 && s1 != s2) { solo = 1; sideOfSolo = s1; }
                    else { solo = 2; sideOfSolo = s2; }

                    int A = (solo + 1) % 3, B = (solo + 2) % 3;
                    int iSolo = SrcTri[t + solo];
                    int iA    = SrcTri[t + A];
                    int iB    = SrcTri[t + B];

                    var vP = Interp(iSolo, iA);
                    var vQ = Interp(iB, iSolo);
                    AddCapPoint(iSolo, iA, vP.Pos);
                    AddCapPoint(iB,    iSolo, vQ.Pos);

                    if (sideOfSolo == 1)
                    {
                        int s = AddOriginal(iSolo, true);
                        int p = AddBodyCut(iSolo, iA, in vP, true);
                        int q = AddBodyCut(iB, iSolo, in vQ, true);
                        PosTris.Add(s); PosTris.Add(p); PosTris.Add(q);

                        int a  = AddOriginal(iA, false);
                        int b  = AddOriginal(iB, false);
                        int p2 = AddBodyCut(iSolo, iA, in vP, false);
                        int q2 = AddBodyCut(iB, iSolo, in vQ, false);
                        NegTris.Add(a); NegTris.Add(b); NegTris.Add(q2);
                        NegTris.Add(a); NegTris.Add(q2); NegTris.Add(p2);
                    }
                    else
                    {
                        int s = AddOriginal(iSolo, false);
                        int p = AddBodyCut(iSolo, iA, in vP, false);
                        int q = AddBodyCut(iB, iSolo, in vQ, false);
                        NegTris.Add(s); NegTris.Add(p); NegTris.Add(q);

                        int a  = AddOriginal(iA, true);
                        int b  = AddOriginal(iB, true);
                        int p2 = AddBodyCut(iSolo, iA, in vP, true);
                        int q2 = AddBodyCut(iB, iSolo, in vQ, true);
                        PosTris.Add(a); PosTris.Add(b); PosTris.Add(q2);
                        PosTris.Add(a); PosTris.Add(q2); PosTris.Add(p2);
                    }
                }
            }

            int AddOriginal(int origIdx, bool positive)
            {
                if (positive)
                {
                    if (OrigPosIdx.TryGetValue(origIdx, out var idx)) return idx;
                    var v = MakeOrigVertex(origIdx);
                    int n = PosVerts.Length;
                    PosVerts.Add(v);
                    OrigPosIdx.Add(origIdx, n);
                    return n;
                }
                else
                {
                    if (OrigNegIdx.TryGetValue(origIdx, out var idx)) return idx;
                    var v = MakeOrigVertex(origIdx);
                    int n = NegVerts.Length;
                    NegVerts.Add(v);
                    OrigNegIdx.Add(origIdx, n);
                    return n;
                }
            }

            int AddBodyCut(int a, int b, in Vertex v, bool positive)
            {
                long key = EdgeKey(a, b);
                if (positive)
                {
                    if (CutPosIdx.TryGetValue(key, out var idx)) return idx;
                    int n = PosVerts.Length;
                    PosVerts.Add(v);
                    CutPosIdx.Add(key, n);
                    return n;
                }
                else
                {
                    if (CutNegIdx.TryGetValue(key, out var idx)) return idx;
                    int n = NegVerts.Length;
                    NegVerts.Add(v);
                    CutNegIdx.Add(key, n);
                    return n;
                }
            }

            void AddCapPoint(int a, int b, float3 p)
            {
                long key = EdgeKey(a, b);
                if (CapByEdge.ContainsKey(key)) return;
                int n = CapPts.Length;
                CapPts.Add(p);
                CapByEdge.Add(key, n);
            }

            static long EdgeKey(int a, int b)
            {
                int lo = math.min(a, b), hi = math.max(a, b);
                return ((long)lo << 32) | (uint)hi;
            }

            Vertex MakeOrigVertex(int i) => new Vertex
            {
                Pos = SrcPos[i],
                Nrm = HasNrm ? SrcNrm[i] : float3.zero,
                Tan = HasTan ? SrcTan[i] : float4.zero,
                Uv  = HasUv  ? SrcUv[i]  : float2.zero,
            };

            Vertex Interp(int a, int b)
            {
                int lo = math.min(a, b), hi = math.max(a, b);
                float da = Dist[lo], db = Dist[hi];
                float t = da / (da - db);
                var v = new Vertex { Pos = math.lerp(SrcPos[lo], SrcPos[hi], t) };
                if (HasNrm) v.Nrm = math.normalize(math.lerp(SrcNrm[lo], SrcNrm[hi], t));
                if (HasTan)
                {
                    var lerped = math.lerp(SrcTan[lo], SrcTan[hi], t);
                    var n3 = math.normalize(lerped.xyz);
                    v.Tan = new float4(n3, SrcTan[lo].w);
                }
                if (HasUv) v.Uv = math.lerp(SrcUv[lo], SrcUv[hi], t);
                return v;
            }
        }

        // -----------------------------------------------------------------------
        // Cap fan-triangulation job — sorts intersection points around centroid
        // in plane-local 2D, then emits one fan per side.
        // -----------------------------------------------------------------------
        [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast,
                      OptimizeFor = OptimizeFor.Performance)]
        struct CapJob : IJob
        {
            public Plane Plane;
            [ReadOnly] public NativeArray<float3> CapPoints;
            public NativeList<Vertex> PosVerts;
            public NativeList<int>    PosTris;
            public NativeList<Vertex> NegVerts;
            public NativeList<int>    NegTris;

            public void Execute()
            {
                int n = CapPoints.Length;
                if (n < 3) return;

                // Plane-local basis.
                float3 N = math.normalize(Plane.Normal);
                float3 reference = math.abs(N.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
                float3 U = math.normalize(math.cross(reference, N));
                float3 V = math.cross(N, U);

                // Compute centroid and bounds in plane-local 2D.
                float3 centroid = float3.zero;
                for (int i = 0; i < n; i++) centroid += CapPoints[i];
                centroid /= n;
                float2 c2 = new float2(math.dot(U, centroid), math.dot(V, centroid));

                float2 mn = new float2(float.PositiveInfinity);
                float2 mx = new float2(float.NegativeInfinity);
                var p2s    = new NativeArray<float2>(n, Allocator.Temp);
                var angles = new NativeArray<float>(n, Allocator.Temp);
                var indices = new NativeArray<int>(n, Allocator.Temp);
                for (int i = 0; i < n; i++)
                {
                    var p2 = new float2(math.dot(U, CapPoints[i]), math.dot(V, CapPoints[i]));
                    p2s[i] = p2;
                    var d = p2 - c2;
                    angles[i] = math.atan2(d.y, d.x);
                    indices[i] = i;
                    mn = math.min(mn, p2);
                    mx = math.max(mx, p2);
                }
                float2 size = math.max(mx - mn, new float2(1e-6f));

                // Insertion sort (n is small for typical cuts; avoids needing sort comparators).
                for (int i = 1; i < n; i++)
                {
                    float key = angles[i];
                    int   ki  = indices[i];
                    int j = i - 1;
                    while (j >= 0 && angles[j] > key)
                    {
                        angles[j + 1] = angles[j];
                        indices[j + 1] = indices[j];
                        j--;
                    }
                    angles[j + 1] = key;
                    indices[j + 1] = ki;
                }

                // Positive cap: normal = -N, winding = (centroid, p[i+1], p[i]).
                EmitCap(PosVerts, PosTris, p2s, indices, c2, mn, size, centroid, U, -N, +1);
                // Negative cap: normal = +N, winding = (centroid, p[i], p[i+1]).
                EmitCap(NegVerts, NegTris, p2s, indices, c2, mn, size, centroid, U, +N, -1);

                p2s.Dispose(); angles.Dispose(); indices.Dispose();
            }

            void EmitCap(NativeList<Vertex> verts, NativeList<int> tris,
                         NativeArray<float2> p2s, NativeArray<int> indices,
                         float2 c2, float2 mn, float2 size,
                         float3 centroid, float3 U, float3 normal, int winding)
            {
                int n = CapPoints.Length;
                int centroidIdx = verts.Length;
                verts.Add(new Vertex
                {
                    Pos = centroid,
                    Nrm = normal,
                    Tan = new float4(U, 1f),
                    Uv  = (c2 - mn) / size,
                });

                int firstCap = verts.Length;
                for (int i = 0; i < n; i++)
                {
                    int src = indices[i];
                    var p2 = p2s[src];
                    verts.Add(new Vertex
                    {
                        Pos = CapPoints[src],
                        Nrm = normal,
                        Tan = new float4(U, 1f),
                        Uv  = (p2 - mn) / size,
                    });
                }

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    int a = firstCap + i, b = firstCap + j;
                    if (winding > 0)
                    {
                        // (centroid, b, a) -> CCW from -N viewpoint.
                        tris.Add(centroidIdx); tris.Add(b); tris.Add(a);
                    }
                    else
                    {
                        tris.Add(centroidIdx); tris.Add(a); tris.Add(b);
                    }
                }
            }
        }
    }
}
