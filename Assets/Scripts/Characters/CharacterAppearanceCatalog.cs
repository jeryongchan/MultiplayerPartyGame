using System;
using UnityEngine;

namespace FriendSlop.Characters
{
    // runtime, pack-agnostic lookup table for modular character appearance.
    //
    // a modular character (e.g. from ithappy) is a base skeleton with one child SkinnedMeshRenderer
    // per body "slot" (Hat, Pants, Shoes, ...). customizing the character means setting each slot
    // renderer's sharedMesh to one of that slot's variant meshes, or hiding the renderer for an empty slot.
    //
    // this catalog is pure data: for each slot it stores the child GameObject name to target plus the flat
    // array of variant meshes available for it. nothing here references the ithappy editor tooling, so it
    // compiles into a build and works for any modular pack that follows the "one child renderer per slot"
    // convention. swapping to a bigger pack means regenerating this asset (see the generator editor tool)
    // and re-pointing the character prefab; no code changes.
    //
    // the appearance itself is transmitted as a small struct of per-slot indices (PlayerAppearance), never
    // mesh data; every machine maps the same indices through its own copy of this catalog. that matches
    // the GDD's "appearance is a struct of indices, synced as integers, mapped to local prefabs per client".
    [CreateAssetMenu(menuName = "FriendSlop/Character Appearance Catalog", fileName = "CharacterAppearanceCatalog")]
    public class CharacterAppearanceCatalog : ScriptableObject
    {
        [SerializeField]
        private SlotDefinition[] slots = Array.Empty<SlotDefinition>();

        // the slots in a fixed order. index in this array is the slot's stable id, matched
        // position-for-position by PlayerAppearance. never reorder after appearances exist.
        public SlotDefinition[] Slots => slots;

        public int SlotCount => slots.Length;

        // replaces the whole slot table. used only by the editor generator.
        public void SetSlots(SlotDefinition[] newSlots) => slots = newSlots ?? Array.Empty<SlotDefinition>();

        // one customizable slot: the child renderer it drives, its variant meshes, and how likely the
        // randomizer is to fill it. alwaysOn slots (Body, Faces) are never hidden.
        [Serializable]
        public class SlotDefinition
        {
            [Tooltip("name of the child GameObject (holding the SkinnedMeshRenderer) this slot drives. " +
                     "matched against the character's direct children at apply time.")]
            public string childName;

            [Tooltip("meshes selectable for this slot, flattened across the pack's sub-groups. " +
                     "index into this array is what the networked appearance stores.")]
            public Mesh[] variants = Array.Empty<Mesh>();

            [Tooltip("if true the slot always shows a variant (never empty), e.g. Body, Faces.")]
            public bool alwaysOn;

            [Range(0f, 1f)]
            [Tooltip("chance the randomizer enables this slot (ignored when Always On).")]
            public float enableChance = 0.35f;

            public int VariantCount => variants != null ? variants.Length : 0;
        }
    }
}
