using UnityEditor;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public static class SlotLibraryLoader
    {
        public static SlotLibrary LoadSlotLibrary()
        {
            return AssetDatabase.LoadAssetAtPath<SlotLibrary>(AssetsPath.SlotLibrary);
        }
    }
}