using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.SlotValidation
{
    public class SlotToggledRule : ISlotValidationRules
    {
        private readonly SlotType[] _slotExceptions =
        {
            SlotType.Costumes,
            SlotType.Body,
            SlotType.Faces,
        };

        public void Validate(CustomizableCharacter character, SlotType type, bool isToggled)
        {
            if (_slotExceptions.Contains(type) || !isToggled)
            {
                return;
            }

            character.Toggle(SlotType.Costumes, false);
        }
    }
}