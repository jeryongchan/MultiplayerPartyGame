using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public class SlotVariant
    {
        public Mesh Mesh { get; }
        public GameObject PreviewObject { get; }
        public Material[] Materials { get; }
        public string Name => Mesh.name;

        public SlotVariant(Mesh mesh, Material[] materials)
        {
            Mesh = mesh;
            Materials = materials;
            PreviewObject = PreviewCreator.CreateVariantPreview(mesh, materials);
        }
    }
}