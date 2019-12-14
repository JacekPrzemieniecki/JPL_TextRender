using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TextCore;

namespace JPL.TextRender
{
    public class TextToRender : IComponentData
    {
        public string Text;
        public float Size = 12;
        public SerializedFontData Font;
        public Color32 Color = UnityEngine.Color.white;
    }

    public struct HorizontalAlignment : IComponentData
    {
        public enum Kind
        {
            Left,
            Center,
            Right,
        }
        public Kind Value;

        public static HorizontalAlignment Left => new HorizontalAlignment(Kind.Left);
        public static HorizontalAlignment Center => new HorizontalAlignment(Kind.Center);
        public static HorizontalAlignment Right => new HorizontalAlignment(Kind.Right);
        public HorizontalAlignment(Kind val) => Value = val;
    }

    [Serializable]
    public struct GlyphInfo
    {
        public GlyphMetrics Metrics;
        public GlyphRect Rect;
    }

    [Serializable]
    public struct KerningRecord
    {
        public float xPlacement;
        public float yPlacement;
        public float xAdvance;
        public float yAdvance;

        public static KerningRecord operator +(KerningRecord lhs, KerningRecord rhs)
        {
            return new KerningRecord
            {
                xPlacement = lhs.xPlacement + rhs.xPlacement,
                yPlacement = lhs.yPlacement + rhs.yPlacement,
                xAdvance = lhs.xAdvance + rhs.xAdvance,
                yAdvance = lhs.yAdvance + rhs.yAdvance,
            };
        }
    }

    [Serializable]
    public struct KerningPair
    {
        public KerningRecord First;
        public KerningRecord Second;
    }
    public struct FontData : IDisposable
    {
        public NativeHashMap<uint, uint> CharacterLookupTable;
        public NativeHashMap<uint, GlyphInfo> GlyphLookupTable;
        public NativeHashMap<long, KerningPair> Kerning;
        public float AtlasWidth;
        public float AtlasHeight;
        public float PointSize;
        public float Scale;

        public void Dispose()
        {
            CharacterLookupTable.Dispose();
            GlyphLookupTable.Dispose();
            Kerning.Dispose();
        }
    }

    [ExecuteAlways]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public class TextRenderingSystemGroup : ComponentSystemGroup { }

    [ExecuteAlways]
    [AlwaysSynchronizeSystem]
    [UpdateInGroup(typeof(TextRenderingSystemGroup))]
    public class TextRenderingSystem : JobComponentSystem
    {
        EntityQuery _noStateQuery;
        EntityQuery _destroyedQuery;
        EntityQuery _noHorizontalAlignmentQuery;

        public WordStorage WordStorage => WordStorage.Instance;

        List<FontData> _fonts = new List<FontData> { default };
        List<Material> _fontMaterials = new List<Material> { default };
        Dictionary<SerializedFontData, int> _fontDataLookup = new Dictionary<SerializedFontData, int>();


        // All the slots have an invalid element at [0]
        List<string> _displayedStrings = new List<string> { default };
        List<int> _usedFontAssets = new List<int> { default };
        List<Mesh> _meshes = new List<Mesh> { default };

        Stack<int> _unusedSlots = new Stack<int>();

        struct RebuildCmd
        {
            public int Slot;
            public int FontIdx;

            public RebuildCmd(int slot, int font)
            {
                Slot = slot;
                FontIdx = font;
            }
        }

        int GetFreeSlot()
        {
            if (_unusedSlots.Count > 0)
            {
                return _unusedSlots.Pop();
            }
            var res = _meshes.Count;

            // Resize the slots
            _displayedStrings.Add(null);
            _usedFontAssets.Add(0);
            _meshes.Add(new Mesh());

            return res;
        }

        FontData DeserializeFontData(SerializedFontData serialized)
        {
            if (Application.isPlaying)
            {
                Debug.Log("Deserializing font");
            }
            var data = new FontData()
            {
                AtlasHeight = serialized.AtlasHeight,
                AtlasWidth = serialized.AtlasWidth,
                Scale = serialized.Scale,
                PointSize = serialized.PointSize,
                CharacterLookupTable = new NativeHashMap<uint, uint>(serialized.CharacterLookupTable.Count, Allocator.Persistent),
                GlyphLookupTable = new NativeHashMap<uint, GlyphInfo>(serialized.GlyphLookupTable.Count, Allocator.Persistent),
                Kerning = new NativeHashMap<long, KerningPair>(serialized.Kerning.Count, Allocator.Persistent),
            };
            foreach (var pair in serialized.CharacterLookupTable)
            {
                data.CharacterLookupTable.Add(pair.From, pair.To);
            }
            foreach (var pair in serialized.GlyphLookupTable)
            {
                data.GlyphLookupTable.Add(pair.Glyph, pair.Info);
            }
            foreach (var pair in serialized.Kerning)
            {
                data.Kerning.Add(pair.Key, pair.Pair);
            }
            return data;
        }

        int GetFontDataIdx(SerializedFontData asset)
        {
            if (_fontDataLookup.TryGetValue(asset, out var res))
            {
                return res;
            }
            var idx = _fonts.Count;
            _fonts.Add(DeserializeFontData(asset));
            _fontMaterials.Add(asset.Material);
            _fontDataLookup[asset] = idx;
            return idx;
        }

        protected override void OnCreate()
        {
            _noStateQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TextToRender>() },
                None = new[] { ComponentType.ReadWrite<TextToRenderState>() },
            });
            _destroyedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TextToRenderState>() },
                None = new[] { ComponentType.ReadWrite<TextToRender>() },
            });
            _noHorizontalAlignmentQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TextToRender>() },
                None = new[] { ComponentType.ReadWrite<HorizontalAlignment>() },
            });
        }

        protected override void OnDestroy()
        {
            for (int i = 1; i < _fonts.Count; i++)
            {
                var font = _fonts[i];
                font.Dispose();
            }
        }

        struct TextToRenderState : ISystemStateComponentData
        {
            public int Slot;
            public int FontIdx;
            public float Size;
            public HorizontalAlignment.Kind Alignment;
            public Color32 Color;
        }

        List<MeshBuilder> _builders = new List<MeshBuilder>();
        protected unsafe override JobHandle OnUpdate(JobHandle inputDeps)
        {
            EntityManager.AddComponent<TextToRenderState>(_noStateQuery);
            EntityManager.AddComponent<HorizontalAlignment>(_noHorizontalAlignmentQuery);
            if (!_destroyedQuery.IsEmptyIgnoreFilter)
            {
                using (var states = _destroyedQuery.ToComponentDataArray<TextToRenderState>(Allocator.TempJob))
                {
                    foreach (var state in states)
                    {
                        _unusedSlots.Push(state.Slot);
                    }
                }
                EntityManager.RemoveComponent<TextToRenderState>(_destroyedQuery);
            }

            var toRebuild = new NativeList<TextToRenderState>(Allocator.Temp);

            // Find meshes that need rebuilding
            {
                Entities
                    .WithoutBurst()
                    .ForEach((TextToRender src, HorizontalAlignment alignment, ref TextToRenderState dst) =>
                {
                    {
                        var font = src.Font;
                        var text = src.Text;
                        // TODO: warn about null texts and fonts
                        if (text == null) return;
                        if (font == null) return;

                        void UpdateAndQueueRebuild(ref TextToRenderState state, int fontHash)
                        {
                            int slot = state.Slot;
                            _usedFontAssets[slot] = fontHash;
                            state.FontIdx = GetFontDataIdx(font);
                            state.Color = src.Color;
                            state.Alignment = alignment.Value;
                            _displayedStrings[slot] = text;
                            state.Size = src.Size;
                            toRebuild.Add(state);
                        }
                        if (dst.Slot == 0) // new text
                        {
                            dst.Slot = GetFreeSlot();
                            UpdateAndQueueRebuild(ref dst, font.GetInstanceID());
                        }
                        else
                        {
                            bool ColorsEqual(Color32 lhs, Color32 rhs)
                            {
                                return lhs.r == rhs.r && lhs.g == rhs.g && lhs.b == rhs.b && lhs.a == rhs.a;
                            }
                            var fontHash = font.GetInstanceID();
                            if (fontHash != _usedFontAssets[dst.Slot] ||
                                !ColorsEqual(src.Color, dst.Color) ||
                                alignment.Value != dst.Alignment ||
                                !ReferenceEquals(text, _displayedStrings[dst.Slot]) ||
                                src.Size != dst.Size)
                            {
                                UpdateAndQueueRebuild(ref dst, fontHash);
                            }
                        }
                    }
                }).Run();
            }

            // Rebuild the meshes 
            if (toRebuild.Length > 0)
            {
                var jobs = new NativeList<JobHandle>(Allocator.Temp);

                Profiler.BeginSample("Prepare data");
                for (int i = 0; i < toRebuild.Length; i++)
                {
                    var cmd = toRebuild[i];
                    var textString = _displayedStrings[cmd.Slot];
                    var textBuffer = new NativeArray<uint>(textString.Length, Allocator.TempJob);
                    for (int j = 0; j < textString.Length; j++)
                    {
                        textBuffer[j] = textString[j];
                    }

                    var builder = new MeshBuilder(_fonts[cmd.FontIdx], cmd.Size, textBuffer, cmd.Alignment, cmd.Color);
                    _builders.Add(builder);
                }
                Profiler.EndSample();
                Profiler.BeginSample("Build meshes");
                for (int i = 0; i < _builders.Count; i++)
                {
                    jobs.Add(_builders[i].Schedule(default));
                }
                JobHandle.CompleteAll(jobs);
                Profiler.EndSample();

                Profiler.BeginSample("Assign meshes");
                for (int i = 0; i < _builders.Count; i++)
                {
                    var b = _builders[i];
                    b.WriteToMesh(_meshes[toRebuild[i].Slot]);
                    b.Dispose();
                }
                Profiler.EndSample();
                _builders.Clear();
            }

            Draw();
            return default;
        }

        void Draw()
        {
            Profiler.BeginSample("Queue drawing");
            var meshes = _meshes;
            var mats = _fontMaterials;
            Entities
                .WithoutBurst()
                .ForEach((ref TextToRenderState txt, ref LocalToWorld l2w) =>
            {
                Graphics.DrawMesh(mesh: meshes[txt.Slot], matrix: l2w.Value, material: mats[txt.FontIdx], layer: 0);
            }).Run();
            Profiler.EndSample();
        }
    }
}