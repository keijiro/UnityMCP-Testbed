using NUnit.Framework;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using UnityEngine;
using MeshCutter;
using UPlane = Unity.Mathematics.Geometry.Plane;

namespace MeshCutter.Tests
{
    public class MeshSlicerPerformanceTests
    {
        const int kWarmup = 2;
        const int kMeasure = 10;

        static (int lat, int lon, string label)[] kSizes = new (int, int, string)[]
        {
            (32,  64,  "small_4096"),
            (64,  128, "med_16384"),
            (128, 256, "large_65536"),
            (256, 512, "huge_262144"),
        };

        [Test, Performance]
        public void Slice_ManagedSphere([Values(0, 1, 2, 3)] int sizeIdx)
        {
            var (lat, lon, label) = kSizes[sizeIdx];
            var src = HighPolyMesh.CreateUVSphere(lat, lon, 1f);
            try
            {
                int triCount = src.triangles.Length / 3;
                var plane = new UPlane(math.normalize(new float3(0.2f, 1f, 0.3f)), -0.05f);

                Measure.Method(() =>
                {
                    var r = MeshSlicer.Slice(src, plane);
                    r.Dispose();
                })
                .WarmupCount(kWarmup)
                .MeasurementCount(kMeasure)
                .SampleGroup("ms_" + label + "_tris" + triCount)
                .Run();
            }
            finally { Object.DestroyImmediate(src); }
        }
    }
}
