using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.SlotValidation
{
    public class SlotValidator
    {
        private readonly ISlotValidationRules[] _slotValidationRules =
        {
            new FullBodyToggledRule(),
            new SlotToggledRule(),
        };

        public void Validate(CustomizableCharacter character, SlotType type, bool isToggled)
        {
            foreach (var rule in _slotValidationRules)
            {
                rule.Validate(character, type, isToggled);
            }
        }
    }
}