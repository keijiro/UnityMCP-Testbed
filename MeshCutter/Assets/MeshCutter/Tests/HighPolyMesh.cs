using Unity.Mathematics;
using UnityEngine;

namespace MeshCutter.Tests
{
    public static class HighPolyMesh
    {
        // UV-sphere generator. lat × lon segments.
        // verts ≈ (lat+1)(lon+1), tris ≈ 2 * lat * lon.
        public static Mesh CreateUVSphere(int lat, int lon, float radius)
        {
            int vc = (lat + 1) * (lon + 1);
            var pos = new Vector3[vc];
            var nrm = new Vector3[vc];
            var tan = new Vector4[vc];
            var uv = new Vector2[vc];

            int idx = 0;
            for (int y = 0; y <= lat; y++)
            {
                float v = (float)y / lat;
                float theta = v * math.PI;
                float sinT = math.sin(theta), cosT = math.cos(theta);
                for (int x = 0; x <= lon; x++)
                {
                    float u = (float)x / lon;
                    float phi = u * 2f * math.PI;
                    float sinP = math.sin(phi), cosP = math.cos(phi);
                    var n = new Vector3(sinT * cosP, cosT, sinT * sinP);
                    pos[idx] = n * radius;
                    nrm[idx] = n;
                    tan[idx] = new Vector4(-sinP, 0, cosP, 1f);
                    uv[idx] = new Vector2(u, v);
                    idx++;
                }
            }

            var tris = new int[lat * lon * 6];
            int t = 0;
            int rowSize = lon + 1;
            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = y * rowSize + x;
                    int b = a + 1;
                    int c = a + rowSize;
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var m = new Mesh { name = "UVSphere_" + lat + "x" + lon };
            if (vc > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = pos;
            m.normals = nrm;
            m.tangents = tan;
            m.uv = uv;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }
    }
}
