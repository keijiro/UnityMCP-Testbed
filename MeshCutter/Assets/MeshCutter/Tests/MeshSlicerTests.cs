using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MeshCutter;
using UPlane = Unity.Mathematics.Geometry.Plane;

namespace MeshCutter.Tests
{
    public class MeshSlicerTests
    {
        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        static Mesh MakePrimitive(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var src = go.GetComponent<MeshFilter>().sharedMesh;
            var copy = Object.Instantiate(src);
            Object.DestroyImmediate(go);
            return copy;
        }

        static float MeshSurfaceArea(Mesh m)
        {
            var v = m.vertices;
            var t = m.triangles;
            float a = 0f;
            for (int i = 0; i < t.Length; i += 3)
                a += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude * 0.5f;
            return a;
        }

        // Sum of triangle "directed area vectors" — for a watertight mesh this
        // is approximately zero. Useful as a quick consistency check.
        static Vector3 NetAreaVector(Mesh m)
        {
            var v = m.vertices;
            var t = m.triangles;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < t.Length; i += 3)
                sum += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]) * 0.5f;
            return sum;
        }

        static (int caps, float capArea, Vector3 capNormalAvg) ClassifyCap(Mesh m, UPlane plane)
        {
            var v = m.vertices;
            var n = m.normals;
            var t = m.triangles;
            int caps = 0;
            float area = 0f;
            Vector3 normalSum = Vector3.zero;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                float d0 = math.dot(plane.Normal, (float3)a) + plane.NormalAndDistance.w;
                float d1 = math.dot(plane.Normal, (float3)b) + plane.NormalAndDistance.w;
                float d2 = math.dot(plane.Normal, (float3)c) + plane.NormalAndDistance.w;
                if (math.abs(d0) < 1e-3f && math.abs(d1) < 1e-3f && math.abs(d2) < 1e-3f)
                {
                    caps++;
                    area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    normalSum += n[t[i]];
                }
            }
            return (caps, area, caps == 0 ? Vector3.zero : normalSum / caps);
        }

        // ──────────────────────────────────────────────────────────────────
        // Tests
        // ──────────────────────────────────────────────────────────────────

        [Test]
        public void Slice_Cube_AtYZero_ProducesTwoHalves()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var plane = new UPlane(new float3(0, 1, 0), 0f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    Assert.IsNotNull(r.PositiveSide);
                    Assert.IsNotNull(r.NegativeSide);

                    Assert.That(r.PositiveSide.bounds.center.y, Is.GreaterThan(0f));
                    Assert.That(r.NegativeSide.bounds.center.y, Is.LessThan(0f));

                    // Each half's surface area should be roughly cube_side^2 * 4 (4 quarters of side faces)
                    // + 1 (top or bottom face) + 1 (cap). For a unit cube: 4*0.5 + 1 + 1 = 4.0.
                    Assert.That(MeshSurfaceArea(r.PositiveSide), Is.EqualTo(4f).Within(0.01f));
                    Assert.That(MeshSurfaceArea(r.NegativeSide), Is.EqualTo(4f).Within(0.01f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_Cube_CapIsUnitSquare()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var plane = new UPlane(new float3(0, 1, 0), 0f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    var posCap = ClassifyCap(r.PositiveSide, plane);
                    var negCap = ClassifyCap(r.NegativeSide, plane);

                    Assert.That(posCap.capArea, Is.EqualTo(1f).Within(1e-3f), "+ side cap area");
                    Assert.That(negCap.capArea, Is.EqualTo(1f).Within(1e-3f), "- side cap area");
                    Assert.That(posCap.caps, Is.GreaterThan(0));
                    Assert.That(negCap.caps, Is.GreaterThan(0));

                    // Cap normals point outward from each piece.
                    Assert.That(posCap.capNormalAvg.y, Is.LessThan(-0.99f));
                    Assert.That(negCap.capNormalAvg.y, Is.GreaterThan(0.99f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_Cube_AreaIsConserved()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var srcArea = MeshSurfaceArea(src); // = 6 for unit cube
                var plane = new UPlane(new float3(0, 1, 0), 0f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    // Total = original surface (6) + 2 × cap area (1 each) = 8.
                    float total = MeshSurfaceArea(r.PositiveSide) + MeshSurfaceArea(r.NegativeSide);
                    Assert.That(total, Is.EqualTo(srcArea + 2f).Within(1e-2f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_Sphere_HemispheresAndCircularCap()
        {
            var src = MakePrimitive(PrimitiveType.Sphere); // radius 0.5
            try
            {
                var plane = new UPlane(new float3(0, 1, 0), 0f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    var posCap = ClassifyCap(r.PositiveSide, plane);
                    var negCap = ClassifyCap(r.NegativeSide, plane);

                    // π * r^2 = π * 0.25 ≈ 0.7854. Tessellated polygon — within 5%.
                    float expected = math.PI * 0.25f;
                    Assert.That(posCap.capArea, Is.EqualTo(expected).Within(expected * 0.05f));
                    Assert.That(negCap.capArea, Is.EqualTo(expected).Within(expected * 0.05f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_PlaneAbove_AssignsAllToNegative()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                // Plane y = 10 (cube is well below it).
                var plane = new UPlane(new float3(0, 1, 0), -10f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    Assert.That(r.PositiveSide.vertexCount, Is.EqualTo(0));
                    Assert.That(r.NegativeSide.vertexCount, Is.GreaterThan(0));
                    Assert.That(MeshSurfaceArea(r.NegativeSide), Is.EqualTo(MeshSurfaceArea(src)).Within(1e-3f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_PlaneBelow_AssignsAllToPositive()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var plane = new UPlane(new float3(0, 1, 0), 10f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    Assert.That(r.NegativeSide.vertexCount, Is.EqualTo(0));
                    Assert.That(r.PositiveSide.vertexCount, Is.GreaterThan(0));
                    Assert.That(MeshSurfaceArea(r.PositiveSide), Is.EqualTo(MeshSurfaceArea(src)).Within(1e-3f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_NoCap_LeavesOpenMeshes()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var plane = new UPlane(new float3(0, 1, 0), 0f);
                var r = MeshSlicer.Slice(src, plane, buildCap: false);
                try
                {
                    var posCap = ClassifyCap(r.PositiveSide, plane);
                    var negCap = ClassifyCap(r.NegativeSide, plane);
                    Assert.That(posCap.caps, Is.EqualTo(0));
                    Assert.That(negCap.caps, Is.EqualTo(0));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_ObliquePlane_SeparatesByDistance()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var n = math.normalize(new float3(1, 1, 1));
                var plane = new UPlane(n, 0f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    // Validate every vertex lies on the expected side (within tolerance).
                    foreach (var v in r.PositiveSide.vertices)
                    {
                        float d = math.dot(n, (float3)v) + plane.NormalAndDistance.w;
                        Assert.That(d, Is.GreaterThanOrEqualTo(-1e-3f));
                    }
                    foreach (var v in r.NegativeSide.vertices)
                    {
                        float d = math.dot(n, (float3)v) + plane.NormalAndDistance.w;
                        Assert.That(d, Is.LessThanOrEqualTo(1e-3f));
                    }
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Slice_WatertightCheck_NetAreaApproxZero()
        {
            // For a closed (watertight) input, each half should also be closed
            // because the cap seals the cut. Net signed-area-vector of a closed
            // mesh is zero by the divergence theorem.
            var src = MakePrimitive(PrimitiveType.Sphere);
            try
            {
                var plane = new UPlane(math.normalize(new float3(0.3f, 1f, 0.2f)), -0.05f);
                var r = MeshSlicer.Slice(src, plane);
                try
                {
                    Assert.That(NetAreaVector(r.PositiveSide).magnitude, Is.LessThan(1e-2f));
                    Assert.That(NetAreaVector(r.NegativeSide).magnitude, Is.LessThan(1e-2f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }
    }
}
