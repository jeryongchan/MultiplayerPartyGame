using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor
{
    [CreateAssetMenu(menuName = "Character Customization Tool/Slot Entry", fileName = "SlotEntry")]
    public class SlotEntry : ScriptableObject
    {
        public SlotType Type;
        public SlotGroupEntry[] Groups;
    }
}