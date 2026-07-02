using System.Collections.Generic;
using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor
{
    public static class SlotSorter
    {
        private static readonly List<SlotType> SlotTypesInOrder = new()
        {
            SlotType.Body,
            SlotType.Faces,
            SlotType.Costumes,
            SlotType.Hat,
            SlotType.Hairstyle,
            SlotType.Ears,
            SlotType.Glasses,
            SlotType.FaceAccessories,
            SlotType.Outfit,
            SlotType.Outwear,
            SlotType.Gloves,
            SlotType.Pants,
            SlotType.Shorts,
            SlotType.Socks,
            SlotType.Shoes,
        };

        public static IEnumerable<SlotBase> Sort(IEnumerable<SlotBase> slots)
        {
            var sortedSlots = SlotTypesInOrder
                .Select(type => slots.FirstOrDefault(p => p.IsOfType(type)))
                .Where(part => part != null)
                .ToList();

            return sortedSlots;
        }
    }
}