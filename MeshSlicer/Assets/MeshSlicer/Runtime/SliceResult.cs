using UnityEngine;

namespace MeshSlicer
{
    public readonly struct SliceResult
    {
        public readonly Mesh Positive;
        public readonly Mesh Negative;

        public SliceResult(Mesh positive, Mesh negative)
        {
            Positive = positive;
            Negative = negative;
        }

        public bool HasBoth => Positive != null && Negative != null;
    }
}
