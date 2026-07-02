using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public class Slot : SlotBase
    {
        private readonly SlotGroup[] _groups;
        private readonly List<SlotVariant> _variants;

        private SlotVariant _selected;

        public override string Name => Type.ToString();
        public override GameObject Preview => _selected.PreviewObject;
        public override int SelectedIndex => _variants.FindIndex(v => v.Name == _selected.Name);
        public override int VariantsCount => _variants.Count;

        public override (SlotType, Mesh, Material[])[] Meshes => new[]
        {
            (Type, _selected.Mesh, _selected.Materials),
        };

        public Slot(SlotType type, SlotGroupEntry[] slotGroupEntries) : base(type)
        {
            _groups = slotGroupEntries.Select(TranslateGroup).ToArray();
            _variants = FlattenVariants(_groups);
            _selected = _variants.First();
        }

        public override void SelectNext()
        {
            _selected = _variants[GetNextIndex()];
        }

        public override void SelectPrevious()
        {
            _selected = _variants[GetPreviousIndex()];
        }

        public override void Select(int index)
        {
            _selected = _variants[index];
        }

        public override bool TryGetVariantsCountInGroup(GroupType stepGroupType, out int count)
        {
            var group = _groups.FirstOrDefault(g => g.Type == stepGroupType);
            if (group != null)
            {
                count = group.Variants.Length;
                return true;
            }

            count = 0;
            return false;
        }

        public override bool TryPickInGroup(GroupType groupType, int index, bool isEnabled)
        {
            if (!isEnabled || _groups.All(g => g.Type != groupType))
            {
                return false;
            }

            _selected = _groups.First(g => g.Type == groupType).Variants[index];
            Toggle(true);

            return true;
        }

        protected override void DrawSlot(int previewLayer, Camera camera)
        {
            DrawMesh(_selected.Mesh, _selected.Materials, previewLayer, camera);
        }

        private static SlotGroup TranslateGroup(SlotGroupEntry entry)
        {
            var variants = entry.Variants
                .Select(v =>
                {
                    var r = v.gameObject.GetComponentInChildren<SkinnedMeshRenderer>();
                    return new SlotVariant(r.sharedMesh, r.sharedMaterials);
                })
                .ToArray();

            return new SlotGroup(entry.Type, variants);
        }

        private static List<SlotVariant> FlattenVariants(SlotGroup[] groups)
        {
            var variants = new List<SlotVariant>();
            foreach (var group in groups)
            {
                variants.AddRange(group.Variants);
            }

            return variants;
        }
    }
}