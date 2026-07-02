using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor
{
    [CreateAssetMenu(menuName = "Character Customization Tool/Slot Library", fileName = "SlotLibrary")]
    public class SlotLibrary : ScriptableObject
    {
        public FullBodyEntry[] FullBodyCostumes;
        public SlotEntry[] Slots;
    }
}