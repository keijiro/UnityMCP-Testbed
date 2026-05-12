using UnityEngine;

namespace MeshCutter
{
    public sealed class SliceResult
    {
        public Mesh PositiveSide;
        public Mesh NegativeSide;

        public void Dispose()
        {
            if (PositiveSide != null) Object.DestroyImmediate(PositiveSide);
            if (NegativeSide != null) Object.DestroyImmediate(NegativeSide);
            PositiveSide = null;
            NegativeSide = null;
        }
    }
}
