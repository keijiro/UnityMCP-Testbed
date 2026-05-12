using Unity.Mathematics;

namespace MeshSlicer
{
    // Deterministic right-handed orthonormal basis (U, V, N) for a plane normal.
    internal readonly struct PlaneBasis
    {
        public readonly float3 N;
        public readonly float3 U;
        public readonly float3 V;

        public PlaneBasis(float3 normal)
        {
            N = math.normalize(normal);
            var reference = math.abs(N.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            U = math.normalize(math.cross(reference, N));
            V = math.cross(N, U);
        }

        public float2 Project(float3 p) => new float2(math.dot(U, p), math.dot(V, p));
    }
}
