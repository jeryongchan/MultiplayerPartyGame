using System;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using Random = UnityEngine.Random;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class CostumeStep : StepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Costume;

        protected override float Probability => .2f;

        public override StepResult Process(int count, GroupType[] groups, CustomizableCharacter character)
        {
            if (Random.value > Probability)
            {
                return new StepResult(0, false, RemoveSelf(groups));
            }

            return new StepResult(Random.Range(0, count), true, Array.Empty<GroupType>());
        }
    }
}