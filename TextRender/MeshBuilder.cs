using System;
using TMPro;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Assertions;

namespace JPL.TextRender
{
    [BurstCompile]
    struct MeshBuilder : IJob, IDisposable
    {
        [ReadOnly]
        [DeallocateOnJobCompletion]
        NativeArray<uint> TextBuffer;
        HorizontalAlignment.Kind HAlignment;

        NativeArray<int> Tris;
        NativeArray<Vector3> Verts;
        NativeArray<Vector2> UVs;
        NativeArray<Vector2> UV2s;
        [ReadOnly]
        NativeHashMap<long, KerningPair> _kerningInfo;
        [ReadOnly]
        NativeHashMap<uint, uint> _characterLookupTable;
        [ReadOnly]
        NativeHashMap<uint, GlyphInfo> _glyphLookupTable;
        float _atlasWidth;
        float _atlasHeight;
        float _scale;
        int Idx;
        int TriIdx;
        float X;
        NativeArray<int> _other;
        Color32 _color;

        public MeshBuilder(
            FontData font,
            float size,
            NativeArray<uint> textBuffer,
            HorizontalAlignment.Kind alignment,
            Color32 color)
        {
            int triCount = 2 * 3 * textBuffer.Length;
            int vertCount = 4 * textBuffer.Length;
            {
                TextBuffer = textBuffer;
                HAlignment = alignment;

                X = 0;
                Idx = 0;
                TriIdx = 0;
                Tris = new NativeArray<int>(triCount, Allocator.TempJob);
                Verts = new NativeArray<Vector3>(vertCount, Allocator.TempJob);
                UVs = new NativeArray<Vector2>(vertCount, Allocator.TempJob);
                UV2s = new NativeArray<Vector2>(vertCount, Allocator.TempJob);
                _kerningInfo = font.Kerning;
                _characterLookupTable = font.CharacterLookupTable;
                _glyphLookupTable = font.GlyphLookupTable;
                _atlasHeight = font.AtlasHeight;
                _atlasWidth = font.AtlasWidth;
                // This * 0.1f is to keep compatibility with the way TMPro calculates scale
                _scale = size / font.PointSize * 0.1f;
                _other = new NativeArray<int>(2, Allocator.TempJob);
                _color = color;
            };
        }

        static float Pack(float x, float y)
        {
            double x_pck = (int)(x * 511);
            double y_pck = (int)(y * 511);

            return (float)((x_pck * 4096) + y_pck);
        }

        static void PackUV(float scale, out Vector2 bl, out Vector2 br, out Vector2 tl, out Vector2 tr)
        {
            bl.x = Pack(0, 0); bl.y = scale;
            tl.x = Pack(0, 1); tl.y = scale;
            tr.x = Pack(1, 1); tr.y = scale;
            br.x = Pack(1, 0); br.y = scale;
        }

        public void Execute()
        {
            var glyphIdx = new NativeArray<uint>(TextBuffer.Length, Allocator.Temp);
            for (int i = 0; i < TextBuffer.Length; i++)
            {
                glyphIdx[i] = _characterLookupTable[TextBuffer[i]];
            }
            var kerning = new NativeArray<KerningRecord>(TextBuffer.Length, Allocator.Temp);
            for (int i = 0; i < TextBuffer.Length - 1; i++)
            {
                var key = (long)glyphIdx[i] << 32 | glyphIdx[i + 1];
                if (_kerningInfo.TryGetValue(key, out var record))
                {
                    kerning[i] += record.First;
                    kerning[i + 1] += record.Second;
                }
            }
            for (int i = 0; i < TextBuffer.Length; i++)
            {
                var glyph = _glyphLookupTable[glyphIdx[i]];
                PushChar(glyph, kerning[i]);
            }
            if (HAlignment == HorizontalAlignment.Kind.Right)
            {
                OffsetVertices(new Vector3(-X, 0));
            }
            else if (HAlignment == HorizontalAlignment.Kind.Center)
            {
                OffsetVertices(new Vector3(-X / 2, 0));
            }
            _other[0] = Idx;
            _other[1] = TriIdx;
        }

        void PushChar(GlyphInfo glyph, KerningRecord kerning)
        {
            var metrics = glyph.Metrics;
            var width = metrics.width * _scale;
            var height = metrics.height * _scale;
            {
                var x = X + (metrics.horizontalBearingX + kerning.xPlacement) * _scale;
                var y = (metrics.horizontalBearingY + kerning.yPlacement) * _scale;
                Verts[Idx + 0] = new Vector3(x, y - height);
                Verts[Idx + 1] = new Vector3(x + width, y - height);
                Verts[Idx + 2] = new Vector3(x, y);
                Verts[Idx + 3] = new Vector3(x + width, y);
            }

            {
                var uvRect = glyph.Rect;
                float x = uvRect.x / _atlasWidth;
                float y = uvRect.y / _atlasHeight;
                float x1 = x + uvRect.width / _atlasWidth;
                float y1 = y + uvRect.height / _atlasHeight;
                UVs[Idx + 0] = new Vector2(x, y);
                UVs[Idx + 1] = new Vector2(x1, y);
                UVs[Idx + 2] = new Vector2(x, y1);
                UVs[Idx + 3] = new Vector2(x1, y1);
            }
            {
                PackUV(_scale, out var bl, out var br, out var tl, out var tr);
                UV2s[Idx + 0] = bl;
                UV2s[Idx + 1] = br;
                UV2s[Idx + 2] = tl;
                UV2s[Idx + 3] = tr;
            }

            Tris[TriIdx] = Idx;
            Tris[TriIdx + 1] = Idx + 2;
            Tris[TriIdx + 2] = Idx + 1;
            Tris[TriIdx + 3] = Idx + 2;
            Tris[TriIdx + 4] = Idx + 3;
            Tris[TriIdx + 5] = Idx + 1;

            Idx += 4;
            TriIdx += 6;
            X += (metrics.horizontalAdvance + kerning.xAdvance) * _scale;
            Assert.IsTrue(Idx <= Verts.Length);
            Assert.IsTrue(TriIdx <= Tris.Length);
        }

        void OffsetVertices(Vector3 offset)
        {
            for (int i = 0; i < Verts.Length; i++)
            {
                Verts[i] = Verts[i] + offset;
            }
        }

        public void WriteToMesh(Mesh mesh)
        {
            mesh.Clear();
            var idx = _other[0];
            var triIdx = _other[1];
            mesh.SetVertices(Verts, 0, idx);
            mesh.SetUVs(0, UVs, 0, idx);
            mesh.SetUVs(1, UV2s, 0, idx);
            mesh.SetIndices(Tris, 0, triIdx, MeshTopology.Triangles, 0, true, 0);
            var colors = new NativeArray<Color32>(idx, Allocator.Temp);
            for (int i = 0; i < idx; i++)
            {
                colors[i] = _color;
            }
            mesh.SetColors(colors, 0, idx);
        }

        public void Dispose()
        {
            Verts.Dispose();
            Tris.Dispose();
            UVs.Dispose();
            UV2s.Dispose();
            _other.Dispose();
        }
    }
}