using UnityEngine;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Demo
{
    // Spawns the two halves of a sliced primitive at the given pose, separated
    // along the cut plane normal so the cap is visible.
    [ExecuteAlways]
    public class SliceDemo : MonoBehaviour
    {
        public PrimitiveType Source = PrimitiveType.Cube;
        public Vector3 PlaneNormal = new Vector3(1, 1, 0.4f);
        public Vector3 PlanePoint  = Vector3.zero;
        public float Separation = 0.25f;
        public Material BodyMaterial;

        Mesh _pos, _neg;
        GameObject _posGo, _negGo;

        void OnEnable() { Build(); }
        void OnDisable() { Clear(); }
        void OnValidate() { if (isActiveAndEnabled) Rebuild(); }

        public void Rebuild() { Clear(); Build(); }

        void Build()
        {
            var srcGo = GameObject.CreatePrimitive(Source);
            var srcMesh = Object.Instantiate(srcGo.GetComponent<MeshFilter>().sharedMesh);
            DestroyImmediate(srcGo);

            var n = math.normalize((float3)PlaneNormal);
            var plane = Plane.CreateFromUnitNormalAndPointInPlane(n, (float3)PlanePoint);
            var sr = NaiveMeshSlicer.Slice(srcMesh, plane);

            _pos = sr.Positive;
            _neg = sr.Negative;
            DestroyImmediate(srcMesh);

            var off = (Vector3)n * Separation;
            if (_pos != null) _posGo = MakeChild("Positive", _pos,  off);
            if (_neg != null) _negGo = MakeChild("Negative", _neg, -off);
        }

        GameObject MakeChild(string name, Mesh m, Vector3 offset)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = offset;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = m;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BodyMaterial != null ? BodyMaterial
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            return go;
        }

        void Clear()
        {
            if (_posGo) DestroyImmediate(_posGo); _posGo = null;
            if (_negGo) DestroyImmediate(_negGo); _negGo = null;
            if (_pos)   DestroyImmediate(_pos);   _pos = null;
            if (_neg)   DestroyImmediate(_neg);   _neg = null;
        }
    }
}
