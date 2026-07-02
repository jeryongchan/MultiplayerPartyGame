using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.SlotValidation
{
    public class FullBodyToggledRule : ISlotValidationRules
    {
        private readonly SlotType[] _slotExceptions =
        {
            SlotType.Costumes,
            SlotType.Body,
            SlotType.Faces,
        };

        public void Validate(CustomizableCharacter character, SlotType type, bool isToggled)
        {
            if (type != SlotType.Costumes || !isToggled)
            {
                return;
            }

            foreach (var slot in character.Slots.Where(s => !_slotExceptions.Contains(s.Type)))
            {
                slot.Toggle(false);
            }
        }
    }
}