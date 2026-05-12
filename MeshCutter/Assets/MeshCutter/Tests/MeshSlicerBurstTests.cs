using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MeshCutter;
using UPlane = Unity.Mathematics.Geometry.Plane;

namespace MeshCutter.Tests
{
    // Mirror the managed test suite against the Burst implementation so the
    // two stay behaviorally identical.
    public class MeshSlicerBurstTests
    {
        static Mesh MakePrimitive(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var m = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(go);
            return m;
        }

        static float SurfaceArea(Mesh m)
        {
            var v = m.vertices; var t = m.triangles;
            float a = 0f;
            for (int i = 0; i < t.Length; i += 3)
                a += Vector3.Cross(v[t[i+1]]-v[t[i]], v[t[i+2]]-v[t[i]]).magnitude * 0.5f;
            return a;
        }

        [Test]
        public void Burst_Cube_AreaConserved()
        {
            var src = MakePrimitive(PrimitiveType.Cube);
            try
            {
                var srcArea = SurfaceArea(src);
                var r = MeshSlicerBurst.Slice(src, new UPlane(new float3(0, 1, 0), 0f));
                try
                {
                    float total = SurfaceArea(r.PositiveSide) + SurfaceArea(r.NegativeSide);
                    Assert.That(total, Is.EqualTo(srcArea + 2f).Within(1e-2f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Burst_Sphere_BothHalves()
        {
            var src = MakePrimitive(PrimitiveType.Sphere);
            try
            {
                var r = MeshSlicerBurst.Slice(src, new UPlane(new float3(0, 1, 0), 0f));
                try
                {
                    Assert.That(r.PositiveSide.bounds.center.y, Is.GreaterThan(0f));
                    Assert.That(r.NegativeSide.bounds.center.y, Is.LessThan(0f));
                }
                finally { r.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }

        [Test]
        public void Burst_MatchesManaged_VertexAndTriangleCounts()
        {
            // Same plane, same input → same topology counts.
            var src = HighPolyMesh.CreateUVSphere(32, 64, 1f);
            try
            {
                var plane = new UPlane(math.normalize(new float3(0.2f, 1f, 0.3f)), -0.05f);
                var rm = MeshSlicer.Slice(src, plane);
                var rb = MeshSlicerBurst.Slice(src, plane);
                try
                {
                    Assert.That(rb.PositiveSide.vertexCount, Is.EqualTo(rm.PositiveSide.vertexCount));
                    Assert.That(rb.NegativeSide.vertexCount, Is.EqualTo(rm.NegativeSide.vertexCount));
                    Assert.That(rb.PositiveSide.triangles.Length, Is.EqualTo(rm.PositiveSide.triangles.Length));
                    Assert.That(rb.NegativeSide.triangles.Length, Is.EqualTo(rm.NegativeSide.triangles.Length));

                    // Surface areas should match closely.
                    Assert.That(SurfaceArea(rb.PositiveSide), Is.EqualTo(SurfaceArea(rm.PositiveSide)).Within(1e-3f));
                    Assert.That(SurfaceArea(rb.NegativeSide), Is.EqualTo(SurfaceArea(rm.NegativeSide)).Within(1e-3f));
                }
                finally { rm.Dispose(); rb.Dispose(); }
            }
            finally { Object.DestroyImmediate(src); }
        }
    }
}
