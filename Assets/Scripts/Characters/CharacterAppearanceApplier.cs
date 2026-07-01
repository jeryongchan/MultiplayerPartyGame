using System;
using System.Collections.Generic;
using UnityEngine;

namespace FriendSlop.Characters
{
    // runtime component that paints a modular character to match a PlayerAppearance. lives on the
    // character root (the GameObject whose direct children are the per-slot SkinnedMeshRenderers).
    // fully build-safe, no editor/pack dependencies.
    //
    // for each catalog slot it finds the matching child renderer by name (cached on first use) and either
    // swaps in the chosen variant mesh and enables it, or disables the renderer for a hidden slot. because
    // both players and NPCs use this same component driven by the same catalog, appearance is identical
    // everywhere a given PlayerAppearance is applied; the network layer only ships the tiny index struct.
    //
    // Roll generates a random appearance from an injected System.Random, so callers control determinism:
    // the server seeds it per player; the crowd seeds it per (crowdSeed, npcIndex) so every machine
    // rebuilds the identical NPC without networking (matches the deterministic-crowd design).
    public class CharacterAppearanceApplier : MonoBehaviour
    {
        // appearance catalog to resolve slot indices against. must match on every machine.
        [SerializeField]
        private CharacterAppearanceCatalog catalog;

        // childName to renderer, resolved lazily so it survives the character being re-parented/instantiated
        private readonly Dictionary<string, SkinnedMeshRenderer> _rendererCache = new();
        // set of catalog-managed child names, so Apply can hide everything else
        private readonly HashSet<string> _managedChildNames = new();
        private bool _cacheBuilt;

        public CharacterAppearanceCatalog Catalog => catalog;

        // allows the catalog to be injected at runtime (e.g. by a spawner) before applying
        public void SetCatalog(CharacterAppearanceCatalog value)
        {
            catalog = value;
        }

        // applies an appearance: sets each slot's mesh or hides it. silently no-ops on a slot whose index
        // is out of range or whose child renderer is missing (logged once), so a partially-authored
        // catalog or a stale appearance never throws mid-game.
        public void Apply(PlayerAppearance appearance)
        {
            if (catalog == null)
            {
                Debug.LogError($"[{name}] CharacterAppearanceApplier has no catalog assigned.", this);
                return;
            }

            if (!appearance.IsValid)
            {
                return;
            }

            EnsureCache();

            CharacterAppearanceCatalog.SlotDefinition[] slots = catalog.Slots;

            // hide any child renderer the catalog doesn't manage (leftover slots baked into the source
            // prefab, e.g. a placeholder T_Shirt/Full_body). otherwise they'd stay permanently visible
            // under rolled clothing. managed slots are re-enabled below as appropriate.
            foreach (KeyValuePair<string, SkinnedMeshRenderer> pair in _rendererCache)
            {
                if (pair.Value != null && !_managedChildNames.Contains(pair.Key))
                {
                    pair.Value.enabled = false;
                }
            }

            int count = Mathf.Min(slots.Length, appearance.slots.Length);

            for (int i = 0; i < count; i++)
            {
                CharacterAppearanceCatalog.SlotDefinition slot = slots[i];
                if (!_rendererCache.TryGetValue(slot.childName, out SkinnedMeshRenderer renderer) || renderer == null)
                {
                    continue; // missing child logged in EnsureCache; skip cleanly
                }

                sbyte index = appearance.slots[i];
                if (index < 0 || index >= slot.VariantCount)
                {
                    renderer.enabled = false; // hidden slot (or invalid index, treat as hidden)
                    continue;
                }

                renderer.sharedMesh = slot.variants[index];
                renderer.localBounds = renderer.sharedMesh.bounds;
                renderer.enabled = true;
            }
        }

        // builds a random appearance for this catalog using the supplied RNG. always-on slots pick a
        // uniform variant; optional slots roll their enableChance first, then a uniform variant if
        // enabled. deterministic in the RNG: same seed gives the same look on every machine.
        public static PlayerAppearance Roll(CharacterAppearanceCatalog catalog, System.Random rng)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            CharacterAppearanceCatalog.SlotDefinition[] slots = catalog.Slots;
            var indices = new sbyte[slots.Length];

            for (int i = 0; i < slots.Length; i++)
            {
                CharacterAppearanceCatalog.SlotDefinition slot = slots[i];
                int variantCount = slot.VariantCount;

                if (variantCount == 0)
                {
                    indices[i] = PlayerAppearance.Hidden;
                    continue;
                }

                bool enabled = slot.alwaysOn || rng.NextDouble() < slot.enableChance;
                indices[i] = enabled ? (sbyte)rng.Next(variantCount) : PlayerAppearance.Hidden;
            }

            return new PlayerAppearance(indices);
        }

        // convenience overload using this instance's catalog
        public PlayerAppearance Roll(System.Random rng) => Roll(catalog, rng);

        private void EnsureCache()
        {
            if (_cacheBuilt)
            {
                return;
            }

            _rendererCache.Clear();
            foreach (Transform child in transform)
            {
                var smr = child.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && !_rendererCache.ContainsKey(child.name))
                {
                    _rendererCache.Add(child.name, smr);
                }
            }

            // record which children the catalog manages, and warn once about any slot with no matching child
            _managedChildNames.Clear();
            foreach (CharacterAppearanceCatalog.SlotDefinition slot in catalog.Slots)
            {
                _managedChildNames.Add(slot.childName);
                if (!_rendererCache.ContainsKey(slot.childName))
                {
                    Debug.LogWarning($"[{name}] No child renderer named '{slot.childName}' for appearance slot; it will be skipped.", this);
                }
            }

            _cacheBuilt = true;
        }
    }
}
