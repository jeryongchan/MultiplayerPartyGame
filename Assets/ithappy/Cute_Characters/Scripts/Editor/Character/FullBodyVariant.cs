using System.Linq;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public class FullBodyVariant
    {
        public FullBodyElement[] Elements { get; }
        public GameObject PreviewObject { get; }
        public string Name => Elements.First().Mesh.name;

        public FullBodyVariant(FullBodyEntry fullBodyEntry)
        {
            Elements = fullBodyEntry.Slots.Select(s =>
            {
                var r = s.GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
                return new FullBodyElement(s.Type, r.sharedMesh, r.sharedMaterials);
            }).ToArray();

            var previewElement = GetPreviewElement(Elements);
            PreviewObject = PreviewCreator.CreateVariantPreview(previewElement.Mesh, previewElement.Materials);
        }

        private static FullBodyElement GetPreviewElement(FullBodyElement[] elements)
        {
            var element = elements.FirstOrDefault(e => e.Type == SlotType.Hat)
                          ?? elements.FirstOrDefault(e => e.Type == SlotType.Outfit)
                          ?? elements.First();

            return element;
        }
    }

    public class FullBodyElement
    {
        public SlotType Type { get; }
        public Mesh Mesh { get; }
        public Material[] Materials { get; }

        public FullBodyElement(SlotType type, Mesh mesh, Material[] materials)
        {
            Type = type;
            Mesh = mesh;
            Materials = materials;
        }
    }
}