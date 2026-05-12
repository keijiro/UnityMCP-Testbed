using NUnit.Framework;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests
{
    public class MeshSlicerPerformanceTests
    {
        // Slice a high-poly UV sphere (~16k tris) once per measurement.
        [Test, Performance]
        public void Naive_SliceHighPolySphere_Once()
        {
            var src = BenchmarkMeshes.CreateUVSphere(rings: 64, sectors: 128);
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(
                math.normalize(new float3(1, 1, 0.5f)), float3.zero);

            // Touch the implementation once to JIT, then measure.
            var first = NaiveMeshSlicer.Slice(src, plane);
            if (first.Positive) Object.DestroyImmediate(first.Positive);
            if (first.Negative) Object.DestroyImmediate(first.Negative);

            Measure.Method(() =>
            {
                var sr = NaiveMeshSlicer.Slice(src, plane);
                if (sr.Positive) Object.DestroyImmediate(sr.Positive);
                if (sr.Negative) Object.DestroyImmediate(sr.Negative);
            })
            .WarmupCount(2)
            .MeasurementCount(20)
            .IterationsPerMeasurement(1)
            .GC()
            .Run();

            Object.DestroyImmediate(src);
        }

        // Stress: continuously slice many high-poly meshes per "frame" (= per measurement).
        [Test, Performance]
        public void Naive_SliceHighPolySphere_PerFrameStress()
        {
            const int instances = 32;
            var src = BenchmarkMeshes.CreateUVSphere(rings: 32, sectors: 64); // ~4k tris × 32 instances
            var rng = new Unity.Mathematics.Random(1);

            // Pre-generate plane samples so RNG cost isn't measured.
            var planes = new Plane[instances];
            for (int i = 0; i < instances; i++)
            {
                var n = math.normalize(rng.NextFloat3() * 2 - 1);
                planes[i] = Plane.CreateFromUnitNormalAndPointInPlane(n, rng.NextFloat3(-0.2f, 0.2f));
            }

            Measure.Method(() =>
            {
                for (int i = 0; i < instances; i++)
                {
                    var sr = NaiveMeshSlicer.Slice(src, planes[i]);
                    if (sr.Positive) Object.DestroyImmediate(sr.Positive);
                    if (sr.Negative) Object.DestroyImmediate(sr.Negative);
                }
            })
            .WarmupCount(2)
            .MeasurementCount(15)
            .IterationsPerMeasurement(1)
            .GC()
            .SampleGroup(new SampleGroup("PerFrame_32x4k"))
            .Run();

            Object.DestroyImmediate(src);
        }
    }
}
