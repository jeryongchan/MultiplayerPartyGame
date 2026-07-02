using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class ShoesStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Shoes;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Hairstyle,
            GroupType.Gloves,
            GroupType.Ears,
        };

        public override StepResult Process(int count, GroupType[] groups, CustomizableCharacter character)
        {
            var cannotProcess = !groups.Contains(GroupType);
            groups = RemoveSelf(groups);

            if (cannotProcess || Random.value > Probability)
            {
                return new StepResult(0, false, groups);
            }

            var newGroups = groups.Where(g => CompatibleGroups.Contains(g)).ToArray();
            var finalGroups = newGroups.ToList();

            if (Random.value > .5f)
            {
                finalGroups.Add(GroupType.HairWithHats);
            }

            return new StepResult(Random.Range(0, count), true, finalGroups.ToArray());
        }
    }
}