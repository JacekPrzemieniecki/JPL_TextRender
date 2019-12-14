using UnityEngine;
using System.Collections.Generic;
using System;

namespace JPL.TextRender
{
    public class SerializedFontData : ScriptableObject
    {
        [Serializable]
        public struct CharToGlyph
        {
            public uint From;
            public uint To;
        }

        [Serializable]
        public struct GlyphToInfo
        {
            public uint Glyph;
            public GlyphInfo Info;
        }

        [Serializable]
        public struct KerningPairData
        {
            public long Key;
            public KerningPair Pair;
        }

        public List<CharToGlyph> CharacterLookupTable;
        public List<GlyphToInfo> GlyphLookupTable;
        public List<KerningPairData> Kerning;
        public float AtlasWidth;
        public float AtlasHeight;
        public float PointSize;
        public float Scale;

        public Material Material;
    }
}
