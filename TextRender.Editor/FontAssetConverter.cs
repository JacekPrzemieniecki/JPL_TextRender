using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using static JPL.TextRender.SerializedFontData;

// This whole thing is a giant workaround
// to avoid touching TMP_FontAsset in any code
// that the IL PostProcessing ever looks at
// because there is a bug that crashes the build
// when you don't do that.
namespace JPL.TextRender
{
    public class FontAssetConverter : EditorWindow
    {
        [MenuItem("JPL/TextRender/Font Asset Converter")]
        public static void Init()
        {
            GetWindow<FontAssetConverter>().Show();
        }

        string _targetPath = "Assets/Resources/JPL.TextRender/";

        void OnGUI()
        {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label("Convert to", GUILayout.Width(80));
                _targetPath = GUILayout.TextField(_targetPath);
            }
            GUILayout.EndHorizontal();
            var fonts = AssetDatabase.FindAssets("t:TMP_FontAsset");
            foreach (var fontGUID in fonts)
            {
                var tmpFontPath = AssetDatabase.GUIDToAssetPath(fontGUID);
                var tmpFileName = Path.GetFileNameWithoutExtension(tmpFontPath);
                if (GUILayout.Button(tmpFileName))
                {
                    var data = GetOrCreateSerializedFont(tmpFileName, _targetPath);
                    var tmp = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(tmpFontPath);
                    ConvertTMProToFontData(tmp, data);
                    EditorUtility.SetDirty(data);
                }
            }
        }

        SerializedFontData GetOrCreateSerializedFont(string name, string path)
        {
            var fullPath = Path.Combine(path, name + ".asset");
            if (File.Exists(fullPath))
            {
                return AssetDatabase.LoadAssetAtPath<SerializedFontData>(fullPath);
            }
            else
            {
                Directory.CreateDirectory(path);
                var data = SerializedFontData.CreateInstance<SerializedFontData>();
                AssetDatabase.CreateAsset(data, fullPath);
                return data;
            }
        }

        void ConvertTMProToFontData(TMP_FontAsset tmp, SerializedFontData result)
        {
            result.Material = tmp.material;

            result.CharacterLookupTable = BuildCharacterLookup(tmp.characterLookupTable);
            result.GlyphLookupTable = BuildGlyphInfos(tmp.glyphLookupTable);
            var featureTableType = typeof(TMP_FontFeatureTable);
            var field = featureTableType.GetField("m_GlyphPairAdjustmentRecords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var featureTable = tmp.fontFeatureTable;
            var kerningSourceData = field.GetValue(featureTable) as List<TMP_GlyphPairAdjustmentRecord>;
            result.Kerning = BuildKerningInfo(kerningSourceData);
            result.AtlasWidth = tmp.atlasWidth;
            result.AtlasHeight = tmp.atlasHeight;
            result.PointSize = tmp.faceInfo.pointSize;
            result.Scale = tmp.faceInfo.scale;
        }

        List<CharToGlyph> BuildCharacterLookup(Dictionary<uint, TMP_Character> input)
        {
            var res = new List<CharToGlyph>(input.Count);
            foreach (var pair in input)
            {
                res.Add(new CharToGlyph
                {
                    From = pair.Key,
                    To = pair.Value.glyphIndex
                });
            }
            return res;
        }

        List<GlyphToInfo> BuildGlyphInfos(Dictionary<uint, Glyph> input)
        {
            var res = new List<GlyphToInfo>(input.Count);
            foreach (var pair in input)
            {
                var glyph = pair.Value;
                res.Add(
                    new GlyphToInfo
                    {
                        Glyph = pair.Key,
                        Info = new GlyphInfo
                        {
                            Metrics = glyph.metrics,
                            Rect = glyph.glyphRect,
                        }
                    });
            }
            return res;
        }

        List<KerningPairData> BuildKerningInfo(List<TMP_GlyphPairAdjustmentRecord> input)
        {
            var result = new List<KerningPairData>(input.Count);
            foreach (var inputRecord in input)
            {
                var first = inputRecord.firstAdjustmentRecord.glyphValueRecord;
                var second = inputRecord.secondAdjustmentRecord.glyphValueRecord;
                result.Add(
                    new KerningPairData
                    {
                        Key = (long)inputRecord.firstAdjustmentRecord.glyphIndex << 32 | inputRecord.secondAdjustmentRecord.glyphIndex,
                        Pair = new KerningPair
                        {
                            First = new KerningRecord
                            {
                                xPlacement = first.xPlacement,
                                yPlacement = first.yPlacement,
                                xAdvance = first.xAdvance,
                                yAdvance = first.yAdvance,
                            },
                            Second = new KerningRecord
                            {
                                xPlacement = second.xPlacement,
                                yPlacement = second.yPlacement,
                                xAdvance = second.xAdvance,
                                yAdvance = second.yAdvance,
                            }
                        }
                    });
            }
            return result;
        }
    }
}
