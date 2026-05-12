using UnityEngine;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Demo
{
    // Re-slices a high-poly source mesh N times per frame with random planes,
    // assigns the results to MeshFilters, and reports per-frame timing on screen.
    public class StressController : MonoBehaviour
    {
        public enum Backend { Naive, Burst }

        public Backend Implementation = Backend.Burst;
        public int InstanceCount = 32;
        public int Rings = 32;
        public int Sectors = 64;
        public Material BodyMaterial;
        public float Spacing = 1.4f;

        Mesh _source;
        Renderer[] _renderersA;     // positive halves
        Renderer[] _renderersB;     // negative halves
        Mesh[] _meshesA;
        Mesh[] _meshesB;
        Plane[] _planes;
        Unity.Mathematics.Random _rng;

        // Smoothed timing.
        double _emaMs;
        const double EmaAlpha = 0.05;

        void OnEnable()
        {
            _source = BenchmarkMeshes.CreateUVSphere(Rings, Sectors);
            _renderersA = new Renderer[InstanceCount];
            _renderersB = new Renderer[InstanceCount];
            _meshesA    = new Mesh[InstanceCount];
            _meshesB    = new Mesh[InstanceCount];
            _planes     = new Plane[InstanceCount];
            _rng        = new Unity.Mathematics.Random(12345);

            int side = Mathf.CeilToInt(Mathf.Sqrt(InstanceCount));
            for (int i = 0; i < InstanceCount; i++)
            {
                int x = i % side, y = i / side;
                var root = new GameObject($"Slice_{i}");
                root.transform.SetParent(transform, false);
                root.transform.localPosition = new Vector3((x - side * 0.5f) * Spacing, 0, (y - side * 0.5f) * Spacing);

                var a = new GameObject("Pos"); a.transform.SetParent(root.transform, false);
                var b = new GameObject("Neg"); b.transform.SetParent(root.transform, false);
                a.AddComponent<MeshFilter>();
                b.AddComponent<MeshFilter>();
                _renderersA[i] = a.AddComponent<MeshRenderer>();
                _renderersB[i] = b.AddComponent<MeshRenderer>();
                _renderersA[i].sharedMaterial = BodyMaterial != null ? BodyMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit"));
                _renderersB[i].sharedMaterial = _renderersA[i].sharedMaterial;
            }
        }

        // Exposed so editor scripts can drive one frame without entering Play Mode.
        public void Tick() { if (_planes == null) OnEnable(); Update(); }

        void Update()
        {
            // Animate planes over time.
            float t = Time.time;
            for (int i = 0; i < InstanceCount; i++)
            {
                var n = math.normalize(new float3(
                    math.cos(t * 0.8f + i),
                    math.sin(t * 0.6f + i * 0.7f),
                    math.cos(t * 0.5f + i * 0.3f)));
                var p = new float3(math.sin(t + i) * 0.15f, math.cos(t * 0.7f + i) * 0.15f, 0);
                _planes[i] = Plane.CreateFromUnitNormalAndPointInPlane(n, p);
            }

            double t0 = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < InstanceCount; i++)
            {
                // Free previous frame's allocations.
                if (_meshesA[i]) Destroy(_meshesA[i]);
                if (_meshesB[i]) Destroy(_meshesB[i]);

                var sr = Implementation == Backend.Burst
                    ? BurstMeshSlicer.Slice(_source, _planes[i])
                    : NaiveMeshSlicer.Slice(_source, _planes[i]);
                _meshesA[i] = sr.Positive;
                _meshesB[i] = sr.Negative;

                var mfA = _renderersA[i].GetComponent<MeshFilter>();
                var mfB = _renderersB[i].GetComponent<MeshFilter>();
                mfA.sharedMesh = sr.Positive;
                mfB.sharedMesh = sr.Negative;

                // Separate the halves slightly so the cap is visible.
                var off = (Vector3)_planes[i].Normal * 0.06f;
                _renderersA[i].transform.localPosition =  off;
                _renderersB[i].transform.localPosition = -off;
            }
            double dt = (Time.realtimeSinceStartupAsDouble - t0) * 1000.0;
            _emaMs = _emaMs == 0 ? dt : _emaMs * (1 - EmaAlpha) + dt * EmaAlpha;
        }

        void OnGUI()
        {
            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22 };
            int srcTris = _source != null ? _source.triangles.Length / 3 : 0;
            GUI.Label(new Rect(12, 12, 1200, 30),
                $"{Implementation}  |  {InstanceCount} × {srcTris} tris/frame  |  {_emaMs:F2} ms slicing  |  Total ~{(1f/Time.smoothDeltaTime):F0} fps", style);
        }

        void OnDisable()
        {
            for (int i = 0; i < InstanceCount; i++) {
                if (_meshesA[i]) Destroy(_meshesA[i]);
                if (_meshesB[i]) Destroy(_meshesB[i]);
            }
            if (_source) Destroy(_source);
        }
    }
}
