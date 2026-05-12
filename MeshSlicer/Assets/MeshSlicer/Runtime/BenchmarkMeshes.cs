using UnityEngine;
using UnityEngine.Rendering;

namespace MeshSlicer
{
    public static class BenchmarkMeshes
    {
        // Generates a UV sphere of unit diameter (radius 0.5).
        // rings = vertical subdivisions, sectors = horizontal subdivisions.
        // (rings+1) * (sectors+1) vertices, ~rings*sectors*2 triangles.
        public static Mesh CreateUVSphere(int rings = 64, int sectors = 128, float radius = 0.5f)
        {
            int rows = rings + 1;
            int cols = sectors + 1;
            var pos = new Vector3[rows * cols];
            var nrm = new Vector3[rows * cols];
            var tan = new Vector4[rows * cols];
            var uv  = new Vector2[rows * cols];
            var tri = new int[rings * sectors * 6];

            for (int y = 0; y < rows; y++)
            {
                float v = (float)y / rings;
                float phi = v * Mathf.PI;
                float sinPhi = Mathf.Sin(phi), cosPhi = Mathf.Cos(phi);
                for (int x = 0; x < cols; x++)
                {
                    float u = (float)x / sectors;
                    float th = u * Mathf.PI * 2f;
                    float sinTh = Mathf.Sin(th), cosTh = Mathf.Cos(th);
                    var n = new Vector3(sinPhi * cosTh, cosPhi, sinPhi * sinTh);
                    int i = y * cols + x;
                    pos[i] = n * radius;
                    nrm[i] = n;
                    tan[i] = new Vector4(-sinTh, 0, cosTh, -1);
                    uv[i] = new Vector2(u, v);
                }
            }

            int t = 0;
            for (int y = 0; y < rings; y++)
            {
                for (int x = 0; x < sectors; x++)
                {
                    int a = y * cols + x;
                    int b = a + 1;
                    int c = a + cols;
                    int d = c + 1;
                    tri[t++] = a; tri[t++] = c; tri[t++] = b;
                    tri[t++] = b; tri[t++] = c; tri[t++] = d;
                }
            }

            var m = new Mesh { name = $"UVSphere_{rings}x{sectors}", indexFormat = IndexFormat.UInt32 };
            m.vertices = pos;
            m.normals  = nrm;
            m.tangents = tan;
            m.uv       = uv;
            m.triangles = tri;
            m.RecalculateBounds();
            return m;
        }
    }
}
