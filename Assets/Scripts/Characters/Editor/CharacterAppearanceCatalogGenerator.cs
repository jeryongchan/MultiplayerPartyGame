using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FriendSlop.Characters.Editor
{
    // one-time (re-runnable) tool that fills a CharacterAppearanceCatalog from an ithappy SlotLibrary
    // asset. reads the pack's editor data, flattens each slot's variant meshes into a flat array, and
    // records the child-renderer name each slot drives.
    //
    // this is the only point that touches the ithappy pack's format. swapping to a different modular pack
    // means pointing this at the new library (or writing a sibling generator) and hitting the menu again;
    // the runtime code consumes only the generated catalog and never the pack tooling.
    //
    // it reads the library via SerializedObject (not a compile-time reference) so this stays decoupled
    // from the ithappy editor assembly names.
    public static class CharacterAppearanceCatalogGenerator
    {
        private const string SlotLibraryPath =
            "Assets/ithappy/Creative_Characters_FREE/Configs/SlotLibrary.asset";

        private const string CatalogPath =
            "Assets/Settings/CharacterAppearanceCatalog.asset";

        // slots the pack marks as always-present (never hidden by the randomizer). matched by SlotType name.
        private static readonly HashSet<string> AlwaysOnSlotTypes = new() { "Body", "Faces" };

        // ithappy's SlotType enum names to the child GameObject name on the saved character prefab.
        // they match 1:1 except "Outerwear" (SlotType) to "Outerwear" child (confirmed). kept as an
        // explicit map so a pack with different child naming is a one-line change here, not a code hunt.
        private static readonly Dictionary<string, string> SlotTypeToChildName = new()
        {
            { "Body", "Body" },
            { "Faces", "Faces" },
            { "Outerwear", "Outerwear" },
            { "Pants", "Pants" },
            { "Accessories", "Accessories" },
            { "Glasses", "Glasses" },
            { "Gloves", "Gloves" },
            { "Hairstyle", "Hairstyle" },
            { "Hat", "Hat" },
            { "Shoes", "Shoes" },
        };

        [MenuItem("Tools/FriendSlop/Generate Character Appearance Catalog")]
        public static void Generate()
        {
            var library = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SlotLibraryPath);
            if (library == null)
            {
                Debug.LogError($"[CatalogGenerator] SlotLibrary not found at {SlotLibraryPath}");
                return;
            }

            var so = new SerializedObject(library);
            var slotsProp = so.FindProperty("Slots");
            if (slotsProp == null)
            {
                Debug.LogError("[CatalogGenerator] SlotLibrary has no 'Slots' property.");
                return;
            }

            var definitions = new List<CharacterAppearanceCatalog.SlotDefinition>();

            for (int i = 0; i < slotsProp.arraySize; i++)
            {
                var entry = slotsProp.GetArrayElementAtIndex(i);
                var typeProp = entry.FindPropertyRelative("Type");
                string slotTypeName = typeProp.enumNames[typeProp.enumValueIndex];

                if (!SlotTypeToChildName.TryGetValue(slotTypeName, out string childName))
                {
                    Debug.LogWarning($"[CatalogGenerator] SlotType '{slotTypeName}' has no child-name mapping; skipped.");
                    continue;
                }

                // flatten every variant mesh across all sub-groups of this slot, in group-then-variant order
                var meshes = new List<Mesh>();
                var groupsProp = entry.FindPropertyRelative("Groups");
                for (int g = 0; g < groupsProp.arraySize; g++)
                {
                    var variantsProp = groupsProp.GetArrayElementAtIndex(g).FindPropertyRelative("Variants");
                    for (int v = 0; v < variantsProp.arraySize; v++)
                    {
                        var go = variantsProp.GetArrayElementAtIndex(v).objectReferenceValue as GameObject;
                        Mesh mesh = ExtractMesh(go);
                        if (mesh != null)
                        {
                            meshes.Add(mesh);
                        }
                        else if (go != null)
                        {
                            Debug.LogWarning($"[CatalogGenerator] Variant '{go.name}' in slot '{slotTypeName}' has no SkinnedMeshRenderer mesh; skipped.");
                        }
                    }
                }

                bool alwaysOn = AlwaysOnSlotTypes.Contains(slotTypeName);
                definitions.Add(new CharacterAppearanceCatalog.SlotDefinition
                {
                    childName = childName,
                    variants = meshes.ToArray(),
                    alwaysOn = alwaysOn,
                    enableChance = alwaysOn ? 1f : 0.35f,
                });
            }

            var catalog = AssetDatabase.LoadAssetAtPath<CharacterAppearanceCatalog>(CatalogPath);
            bool created = catalog == null;
            if (created)
            {
                catalog = ScriptableObject.CreateInstance<CharacterAppearanceCatalog>();
            }

            catalog.SetSlots(definitions.ToArray());
            EditorUtility.SetDirty(catalog);

            if (created)
            {
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var summary = string.Join("\n", definitions.Select(d =>
                $"  {d.childName}: {d.variants.Length} variants{(d.alwaysOn ? " (always on)" : "")}"));
            Debug.Log($"[CatalogGenerator] {(created ? "Created" : "Updated")} catalog at {CatalogPath} with {definitions.Count} slots:\n{summary}");
        }

        // pulls the skinned mesh out of a variant prefab (mirrors how ithappy's Slot.TranslateGroup does it)
        private static Mesh ExtractMesh(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            return smr != null ? smr.sharedMesh : null;
        }
    }
}
