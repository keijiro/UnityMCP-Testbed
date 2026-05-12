using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests
{
    // Run all functional tests against both implementations so they share a contract.
    public delegate SliceResult SliceFn(Mesh m, Plane p);

    [TestFixture(nameof(NaiveMeshSlicer))]
    [TestFixture(nameof(BurstMeshSlicer))]
    public class MeshSlicerSharedTests
    {
        readonly SliceFn _slice;
        public MeshSlicerSharedTests(string impl)
        {
            _slice = impl == nameof(BurstMeshSlicer)
                ? (SliceFn)BurstMeshSlicer.Slice
                : (SliceFn)NaiveMeshSlicer.Slice;
        }

        SliceResult Slice(Mesh m, Plane p) => _slice(m, p);

        [Test]
        public void Cube_HalvedByHorizontalPlane_ProducesEqualHalves()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(new float3(0, 1, 0), float3.zero);
            var sr = Slice(src, plane);
            Assert.AreEqual(0.5f, MeshSlicerTestUtils.SignedVolume(sr.Positive), 1e-3f);
            Assert.AreEqual(0.5f, MeshSlicerTestUtils.SignedVolume(sr.Negative), 1e-3f);
            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Positive));
            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Negative));
            Object.DestroyImmediate(src);
            Object.DestroyImmediate(sr.Positive);
            Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void ObliquePlane_VolumesSumToCube()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            var n = math.normalize(new float3(1, 2, 3));
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(n, new float3(0.1f, -0.05f, 0.2f));
            var sr = Slice(src, plane);
            float vp = sr.Positive ? MeshSlicerTestUtils.SignedVolume(sr.Positive) : 0f;
            float vn = sr.Negative ? MeshSlicerTestUtils.SignedVolume(sr.Negative) : 0f;
            Assert.AreEqual(1f, vp + vn, 5e-3f);
            Object.DestroyImmediate(src);
            if (sr.Positive) Object.DestroyImmediate(sr.Positive);
            if (sr.Negative) Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void HighPolySphere_VolumeMatchesSourceWithinSliceTolerance()
        {
            var src = BenchmarkMeshes.CreateUVSphere(rings: 32, sectors: 64);
            float full = math.abs(MeshSlicerTestUtils.SignedVolume(src));
            var n = math.normalize(new float3(0.3f, 1f, 0.2f));
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(n, float3.zero);
            var sr = Slice(src, plane);
            float vp = sr.Positive ? math.abs(MeshSlicerTestUtils.SignedVolume(sr.Positive)) : 0f;
            float vn = sr.Negative ? math.abs(MeshSlicerTestUtils.SignedVolume(sr.Negative)) : 0f;
            Assert.AreEqual(full, vp + vn, full * 0.005f, "sliced halves should reconstruct the source volume");
            Object.DestroyImmediate(src);
            if (sr.Positive) Object.DestroyImmediate(sr.Positive);
            if (sr.Negative) Object.DestroyImmediate(sr.Negative);
        }
    }

    public class NaiveMeshSlicerTests
    {
        [Test]
        public void Cube_HalvedByHorizontalPlane_ProducesTwoEqualClosedHalves()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(new float3(0, 1, 0), float3.zero);

            var sr = NaiveMeshSlicer.Slice(src, plane);

            Assert.IsNotNull(sr.Positive, "Positive half should exist");
            Assert.IsNotNull(sr.Negative, "Negative half should exist");

            // Each half is half the volume of the unit cube (= 0.5).
            float vp = MeshSlicerTestUtils.SignedVolume(sr.Positive);
            float vn = MeshSlicerTestUtils.SignedVolume(sr.Negative);
            Assert.AreEqual(0.5f, vp, 1e-3f, "Positive half volume");
            Assert.AreEqual(0.5f, vn, 1e-3f, "Negative half volume");

            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Positive), "Positive half should be closed");
            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Negative), "Negative half should be closed");

            Assert.IsTrue(MeshSlicerTestUtils.AllVerticesOnSide(sr.Positive, plane, +1), "Positive vertices on positive side");
            Assert.IsTrue(MeshSlicerTestUtils.AllVerticesOnSide(sr.Negative, plane, -1), "Negative vertices on negative side");

            Object.DestroyImmediate(src);
            Object.DestroyImmediate(sr.Positive);
            Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void PlaneOutsideMesh_ReturnsOriginalOnOneSideOnly()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            // Plane far above the cube; positive side contains nothing, negative side contains the whole cube.
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(new float3(0, 1, 0), new float3(0, 5, 0));

            var sr = NaiveMeshSlicer.Slice(src, plane);

            Assert.IsNull(sr.Positive, "Positive side should be empty");
            Assert.IsNotNull(sr.Negative, "Negative side should contain the whole cube");

            float v = MeshSlicerTestUtils.SignedVolume(sr.Negative);
            Assert.AreEqual(1f, v, 1e-3f, "Negative side volume = full cube");

            Object.DestroyImmediate(src);
            if (sr.Negative) Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void ObliquePlane_VolumesSumToOriginal()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            var n = math.normalize(new float3(1, 2, 3));
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(n, new float3(0.1f, -0.05f, 0.2f));

            var sr = NaiveMeshSlicer.Slice(src, plane);

            float vp = sr.Positive ? MeshSlicerTestUtils.SignedVolume(sr.Positive) : 0f;
            float vn = sr.Negative ? MeshSlicerTestUtils.SignedVolume(sr.Negative) : 0f;
            // The unit cube has volume 1.
            Assert.AreEqual(1f, vp + vn, 5e-3f, "Volumes should sum to original cube");

            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Positive), "Positive piece closed");
            Assert.IsTrue(MeshSlicerTestUtils.LooksClosed(sr.Negative), "Negative piece closed");

            Object.DestroyImmediate(src);
            if (sr.Positive) Object.DestroyImmediate(sr.Positive);
            if (sr.Negative) Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void Sphere_HalvedByPlane_ProducesEqualHalves()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Sphere);
            // Unity's sphere primitive is radius 0.5, volume = 4/3 π r³ ≈ 0.5236
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(new float3(0, 1, 0), float3.zero);

            var sr = NaiveMeshSlicer.Slice(src, plane);

            float vp = MeshSlicerTestUtils.SignedVolume(sr.Positive);
            float vn = MeshSlicerTestUtils.SignedVolume(sr.Negative);
            float full = (4f / 3f) * Mathf.PI * 0.125f;
            // Naive cap fan-from-centroid is exact for a circular cross-section's polygon
            // approximation, so volumes will be slightly less than analytic full sphere
            // (Unity's sphere is itself a polygon approximation).
            Assert.AreEqual(full * 0.5f, vp, full * 0.05f, "Positive sphere half ≈ half full volume");
            Assert.AreEqual(full * 0.5f, vn, full * 0.05f, "Negative sphere half ≈ half full volume");
            Assert.AreEqual(vp, vn, full * 0.01f, "Halves should be roughly symmetric");

            Object.DestroyImmediate(src);
            Object.DestroyImmediate(sr.Positive);
            Object.DestroyImmediate(sr.Negative);
        }

        [Test]
        public void CapNormalsFacePlaneNormal()
        {
            var src = MeshSlicerTestUtils.CreatePrimitiveMesh(PrimitiveType.Cube);
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(new float3(0, 1, 0), float3.zero);

            var sr = NaiveMeshSlicer.Slice(src, plane);

            // Find cap vertices (those exactly on the plane, within eps) and verify normals.
            CheckCapNormals(sr.Positive, plane, expectedDir: -1);
            CheckCapNormals(sr.Negative, plane, expectedDir: +1);

            Object.DestroyImmediate(src);
            Object.DestroyImmediate(sr.Positive);
            Object.DestroyImmediate(sr.Negative);
        }

        static void CheckCapNormals(Mesh m, Plane plane, int expectedDir)
        {
            var verts = m.vertices;
            var nrms  = m.normals;
            int found = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                float d = plane.SignedDistanceToPoint(verts[i]);
                if (math.abs(d) > 1e-3f) continue;
                // Skip body cut vertices whose normals come from interpolation.
                // Cap vertices are those whose normal is parallel to the plane normal.
                float dot = math.dot((float3)nrms[i], plane.Normal);
                if (math.abs(dot) < 0.99f) continue;
                Assert.AreEqual(expectedDir > 0 ? 1f : -1f, dot, 1e-2f,
                    $"Cap vertex {i} normal should be {(expectedDir > 0 ? "+" : "-")}plane.normal");
                found++;
            }
            Assert.Greater(found, 2, "Expected at least 3 cap vertices with plane-aligned normals");
        }
    }
}
