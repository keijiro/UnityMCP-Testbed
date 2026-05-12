using System;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace TriangleSplatting.Editor
{
    [ScriptedImporter(1, "off")]
    public sealed class CoffImporter : ScriptedImporter
    {
        public bool flipYZ = true;
        public float scale = 1.0f;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var bytes = File.ReadAllBytes(ctx.assetPath);
            var parser = new Parser(bytes);

            var hasColor = parser.ReadHeader(out var vertexCount, out var faceCount);

            var positions = new NativeArray<Vector3>(vertexCount, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                parser.ReadVertices(positions, flipYZ, scale);

                var triVerts = faceCount * 3;
                var meshVerts = new NativeArray<Vector3>(triVerts, Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);
                var meshColors = new NativeArray<Color32>(triVerts, Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);

                try
                {
                    parser.ReadFaces(positions, meshVerts, meshColors, hasColor);

                    var mesh = BuildMesh(meshVerts, meshColors, faceCount);
                    mesh.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

                    ctx.AddObjectToAsset("mesh", mesh);
                    ctx.SetMainObject(mesh);
                }
                finally
                {
                    meshVerts.Dispose();
                    meshColors.Dispose();
                }
            }
            finally
            {
                positions.Dispose();
            }
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        struct PosColVertex
        {
            public Vector3 position;
            public Color32 color;
        }

        static Mesh BuildMesh(NativeArray<Vector3> verts, NativeArray<Color32> colors, int faceCount)
        {
            var mesh = new Mesh
            {
                indexFormat = IndexFormat.UInt32
            };

            const int maxPerSub = 64_000_000;
            var submeshCount = (faceCount + (maxPerSub / 3) - 1) / (maxPerSub / 3);
            var facesPerSub = (faceCount + submeshCount - 1) / submeshCount;

            mesh.SetVertexBufferParams(
                verts.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, stream: 0));

            var interleaved = new NativeArray<PosColVertex>(verts.Length, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                for (var i = 0; i < verts.Length; i++)
                    interleaved[i] = new PosColVertex { position = verts[i], color = colors[i] };
                mesh.SetVertexBufferData(interleaved, 0, 0, interleaved.Length, 0,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }
            finally
            {
                interleaved.Dispose();
            }

            mesh.SetIndexBufferParams(verts.Length, IndexFormat.UInt32);
            var indices = new NativeArray<int>(verts.Length, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                for (var i = 0; i < indices.Length; i++) indices[i] = i;
                mesh.SetIndexBufferData(indices, 0, 0, indices.Length,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }
            finally
            {
                indices.Dispose();
            }

            mesh.subMeshCount = submeshCount;
            for (var s = 0; s < submeshCount; s++)
            {
                var startFace = s * facesPerSub;
                var endFace = Mathf.Min(startFace + facesPerSub, faceCount);
                var startVert = startFace * 3;
                var vertCount = (endFace - startFace) * 3;

                mesh.SetSubMesh(s,
                    new SubMeshDescriptor(startVert, vertCount, MeshTopology.Triangles)
                    {
                        firstVertex = startVert,
                        vertexCount = vertCount
                    },
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }

            ComputeBounds(verts, mesh);
            mesh.UploadMeshData(true);
            return mesh;
        }

        static void ComputeBounds(NativeArray<Vector3> verts, Mesh mesh)
        {
            if (verts.Length == 0) { mesh.bounds = new Bounds(); return; }
            var min = verts[0];
            var max = verts[0];
            for (var i = 1; i < verts.Length; i++)
            {
                var v = verts[i];
                if (v.x < min.x) min.x = v.x; else if (v.x > max.x) max.x = v.x;
                if (v.y < min.y) min.y = v.y; else if (v.y > max.y) max.y = v.y;
                if (v.z < min.z) min.z = v.z; else if (v.z > max.z) max.z = v.z;
            }
            mesh.bounds = new Bounds((min + max) * 0.5f, max - min);
        }

        // Byte-level parser for COFF/OFF — much faster than string.Split on multi-million-line files.
        struct Parser
        {
            readonly byte[] _data;
            int _pos;

            public Parser(byte[] data) { _data = data; _pos = 0; }

            public bool ReadHeader(out int vertexCount, out int faceCount)
            {
                SkipWhitespaceAndComments();
                var tokenStart = _pos;
                while (_pos < _data.Length && !IsWs(_data[_pos])) _pos++;
                var len = _pos - tokenStart;

                bool hasColor = false;
                if (len >= 4 && _data[tokenStart] == 'C' && _data[tokenStart + 1] == 'O' &&
                    _data[tokenStart + 2] == 'F' && _data[tokenStart + 3] == 'F') hasColor = true;
                else if (!(len >= 3 && _data[tokenStart] == 'O' && _data[tokenStart + 1] == 'F' &&
                           _data[tokenStart + 2] == 'F'))
                    throw new InvalidDataException("Not an OFF/COFF file.");

                vertexCount = ReadInt();
                faceCount = ReadInt();
                ReadInt(); // edge count, ignored
                return hasColor;
            }

            public void ReadVertices(NativeArray<Vector3> dst, bool flipYZ, float scale)
            {
                for (var i = 0; i < dst.Length; i++)
                {
                    var x = ReadFloat();
                    var y = ReadFloat();
                    var z = ReadFloat();
                    if (flipYZ) (y, z) = (-z, y);
                    dst[i] = new Vector3(x, y, z) * scale;
                }
            }

            public void ReadFaces(NativeArray<Vector3> verts,
                                  NativeArray<Vector3> outVerts,
                                  NativeArray<Color32> outColors,
                                  bool hasColor)
            {
                var faceCount = outVerts.Length / 3;
                for (var i = 0; i < faceCount; i++)
                {
                    var n = ReadInt();
                    if (n != 3) throw new InvalidDataException($"Face {i} has {n} vertices, expected 3.");
                    var i0 = ReadInt();
                    var i1 = ReadInt();
                    var i2 = ReadInt();

                    Color32 c = new Color32(255, 255, 255, 255);
                    if (hasColor)
                    {
                        var r = ReadInt();
                        var g = ReadInt();
                        var b = ReadInt();
                        // Optional alpha — peek to decide.
                        if (PeekIsNumber()) { var a = ReadInt(); c = new Color32((byte)r, (byte)g, (byte)b, (byte)a); }
                        else c = new Color32((byte)r, (byte)g, (byte)b, 255);
                    }

                    var baseIdx = i * 3;
                    outVerts[baseIdx + 0] = verts[i0];
                    outVerts[baseIdx + 1] = verts[i1];
                    outVerts[baseIdx + 2] = verts[i2];
                    outColors[baseIdx + 0] = c;
                    outColors[baseIdx + 1] = c;
                    outColors[baseIdx + 2] = c;
                }
            }

            void SkipWhitespaceAndComments()
            {
                while (_pos < _data.Length)
                {
                    var b = _data[_pos];
                    if (IsWs(b)) { _pos++; continue; }
                    if (b == '#') { while (_pos < _data.Length && _data[_pos] != '\n') _pos++; continue; }
                    break;
                }
            }

            bool PeekIsNumber()
            {
                var save = _pos;
                while (save < _data.Length && (_data[save] == ' ' || _data[save] == '\t')) save++;
                if (save >= _data.Length) return false;
                var b = _data[save];
                return (b >= '0' && b <= '9') || b == '-' || b == '+' || b == '.';
            }

            int ReadInt()
            {
                SkipWhitespaceAndComments();
                var sign = 1;
                if (_pos < _data.Length && (_data[_pos] == '+' || _data[_pos] == '-'))
                { if (_data[_pos] == '-') sign = -1; _pos++; }
                var v = 0;
                while (_pos < _data.Length)
                {
                    var b = _data[_pos];
                    if (b < '0' || b > '9') break;
                    v = v * 10 + (b - '0');
                    _pos++;
                }
                return v * sign;
            }

            float ReadFloat()
            {
                SkipWhitespaceAndComments();
                var start = _pos;
                if (_pos < _data.Length && (_data[_pos] == '+' || _data[_pos] == '-')) _pos++;
                while (_pos < _data.Length)
                {
                    var b = _data[_pos];
                    if (IsWs(b)) break;
                    _pos++;
                }
                var span = new ReadOnlySpan<byte>(_data, start, _pos - start);
                Span<char> chars = stackalloc char[span.Length];
                for (var i = 0; i < span.Length; i++) chars[i] = (char)span[i];
                return float.Parse(chars, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            static bool IsWs(byte b) => b == ' ' || b == '\t' || b == '\r' || b == '\n';
        }
    }
}
