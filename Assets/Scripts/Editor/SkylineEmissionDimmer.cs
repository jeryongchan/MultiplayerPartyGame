using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FriendSlop.EditorTools
{
    // dims emission on the distant backdrop buildings so they read as further away than the street.
    //
    // the synty city pack shares one material per texture atlas across every building, so the emission
    // colour cannot be lowered on the shared asset without dimming the street too. instead this creates a
    // material variant per emissive source material and reassigns the selected hierarchy's renderers to
    // the matching variant. a variant inherits the parent's albedo and normal map, so uv layouts and
    // colours are untouched; only the emission colour is overridden.
    //
    // materials are matched by the renderer's current material, never by GameObject name. the skyscraper
    // instances share prefab guids across differently-named objects, so names are not reliable keys.
    //
    // only the _A alt materials carry an emission map. _B and _C have an empty emission slot and are left
    // alone, so a hierarchy using those needs no variant and gets skipped.
    //
    // re-runnable. an existing variant at the target path is reused and re-dimmed rather than duplicated.
    public static class SkylineEmissionDimmer
    {
        private const string VariantFolder = "Assets/Settings/Materials/Skyline";
        private const string VariantSuffix = "_Skyline";

        // synty's custom urp shader exposes emission through _Emission_Color, not unity's _EmissionColor.
        // _Enable_Emission sits in the material's locked-properties list so a variant cannot override it,
        // which is why the colour is scaled instead of the toggle being flipped.
        private const string EmissionColorProperty = "_Emission_Color";
        private const string EmissionMapProperty = "_Emission_Map";

        private const float DimFactor = 0.5f;

        [MenuItem("Tools/FriendSlop/Dim Skyline Emission")]
        public static void DimSelection()
        {
            var parent = Selection.activeGameObject;
            if (parent == null)
            {
                Debug.LogError("[SkylineDimmer] select the Skyscrapers parent in the hierarchy first.");
                return;
            }

            var renderers = parent.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"[SkylineDimmer] no MeshRenderers under '{parent.name}'.");
                return;
            }

            Directory.CreateDirectory(VariantFolder);

            // one variant per distinct source material, so two renderers on the same atlas share a variant
            var variantsBySource = new Dictionary<Material, Material>();
            var skipped = new HashSet<string>();
            int reassigned = 0;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || IsVariant(source))
                    {
                        continue;
                    }

                    if (!HasEmission(source))
                    {
                        skipped.Add(source.name);
                        continue;
                    }

                    if (!variantsBySource.TryGetValue(source, out var variant))
                    {
                        variant = GetOrCreateVariant(source);
                        variantsBySource[source] = variant;
                    }

                    materials[i] = variant;
                    changed = true;
                }

                if (changed)
                {
                    Undo.RecordObject(renderer, "Dim Skyline Emission");
                    renderer.sharedMaterials = materials;
                    EditorUtility.SetDirty(renderer);
                    reassigned++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Report(parent.name, renderers.Length, reassigned, variantsBySource, skipped);
        }

        // a material only glows if the shader declares the emission colour and an emission map is bound.
        // the _B and _C alts share the shader but leave the map empty.
        private static bool HasEmission(Material material)
        {
            if (!material.HasProperty(EmissionColorProperty))
            {
                return false;
            }

            return material.HasProperty(EmissionMapProperty)
                   && material.GetTexture(EmissionMapProperty) != null;
        }

        private static bool IsVariant(Material material)
        {
            return material.parent != null;
        }

        private static Material GetOrCreateVariant(Material source)
        {
            string path = $"{VariantFolder}/{source.name}{VariantSuffix}.mat";
            var variant = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (variant == null)
            {
                variant = new Material(source) { parent = source };
                AssetDatabase.CreateAsset(variant, path);
            }
            else if (variant.parent != source)
            {
                variant.parent = source;
            }

            // read the emission off the parent, not the variant, so re-running does not compound the dim
            Color baseEmission = source.GetColor(EmissionColorProperty);
            Color dimmed = baseEmission * DimFactor;
            dimmed.a = baseEmission.a;

            variant.SetColor(EmissionColorProperty, dimmed);
            EditorUtility.SetDirty(variant);

            return variant;
        }

        private static void Report(
            string parentName,
            int rendererCount,
            int reassigned,
            Dictionary<Material, Material> variants,
            HashSet<string> skipped)
        {
            var lines = new List<string>
            {
                $"[SkylineDimmer] '{parentName}': {reassigned}/{rendererCount} renderers reassigned, " +
                $"emission scaled to {DimFactor:P0}.",
            };

            if (variants.Count > 0)
            {
                lines.Add("variants:");
                lines.AddRange(variants.Select(pair => $"  {pair.Key.name} -> {pair.Value.name}"));
            }

            if (skipped.Count > 0)
            {
                lines.Add($"skipped (no emission map): {string.Join(", ", skipped.OrderBy(n => n))}");
            }

            Debug.Log(string.Join("\n", lines));
        }
    }
}
