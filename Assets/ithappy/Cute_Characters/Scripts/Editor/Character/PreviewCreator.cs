using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public static class PreviewCreator
    {
        public static GameObject CreateVariantPreview(Mesh mesh, Material[] materials)
        {
            var variant = new GameObject(mesh.name);

            variant.AddComponent<MeshFilter>().sharedMesh = mesh;
            variant.transform.position = Vector3.one * int.MaxValue;
            variant.hideFlags = HideFlags.HideAndDontSave;

            var renderer = variant.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;

            return variant;
        }
    }
}