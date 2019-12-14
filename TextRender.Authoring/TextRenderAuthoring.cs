using UnityEngine;
using Unity.Entities;

namespace JPL.TextRender
{
    public class TextRenderAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public string Text;
        public SerializedFontData Font;
        public float Size;
        public HorizontalAlignment.Kind Alignment;
        public Color32 Color = UnityEngine.Color.white;

        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new TextToRender
            {
                Text = Text,
                Size = Size,
                Font = Font,
                Color = Color,
            });
            dstManager.AddComponentData(entity, new HorizontalAlignment(Alignment));
        }
    }
}
