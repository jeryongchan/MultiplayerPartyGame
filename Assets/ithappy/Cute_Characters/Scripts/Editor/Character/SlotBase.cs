using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public abstract class SlotBase
    {
        public abstract string Name { get; }
        public abstract GameObject Preview { get; }
        public abstract int SelectedIndex { get; }
        public abstract int VariantsCount { get; }
        public abstract (SlotType, Mesh, Material[])[] Meshes { get; }

        public SlotType Type { get; }
        public bool IsEnabled { get; private set; } = true;

        protected SlotBase(SlotType type)
        {
            Type = type;
        }

        public abstract void SelectNext();
        public abstract void SelectPrevious();
        public abstract void Select(int index);
        public abstract bool TryGetVariantsCountInGroup(GroupType groupType, out int count);
        public abstract bool TryPickInGroup(GroupType groupType, int index, bool isEnabled);

        public void Draw(int previewLayer, Camera camera)
        {
            if (IsEnabled)
            {
                DrawSlot(previewLayer, camera);
            }
        }

        public bool IsOfType(SlotType type)
        {
            return Type == type;
        }

        public void Toggle(bool isToggled)
        {
            IsEnabled = isToggled;
        }

        protected abstract void DrawSlot(int previewLayer, Camera camera);

        protected int GetNextIndex()
        {
            var targetIndex = SelectedIndex + 1;
            if (targetIndex >= VariantsCount)
            {
                targetIndex = 0;
            }

            return targetIndex;
        }

        protected int GetPreviousIndex()
        {
            var targetIndex = SelectedIndex - 1;
            if (targetIndex < 0)
            {
                targetIndex = VariantsCount - 1;
            }
            return targetIndex;
        }

        protected static void DrawMesh(Mesh mesh, Material[] materials, int previewLayer, Camera camera)
        {
            for (var i = 0; i < materials.Length; i++)
            {
                Graphics.DrawMesh(mesh, new Vector3(0, -.01f, 0), Quaternion.identity, materials[i], previewLayer, camera, i);
            }
        }
    }
}