using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps
{
    public interface IRandomizerStep
    {
        GroupType GroupType { get; }

        StepResult Process(int count, GroupType[] groups, CustomizableCharacter character);
    }
}