using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor
{
    [CreateAssetMenu(menuName = "Character Customization Tool/Slot Group Entry", fileName = "SlotGroupEntry")]
    public class SlotGroupEntry : ScriptableObject
    {
        public GroupType Type;
        public GameObject[] Variants;
    }
}