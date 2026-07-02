using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public abstract class SlotStepBase : StepBase, IRandomizerStep
    {
        protected abstract GroupType[] CompatibleGroups { get; }

        public override StepResult Process(int count, GroupType[] groups, CustomizableCharacter character)
        {
            var cannotProcess = !groups.Contains(GroupType);
            groups = RemoveSelf(groups);

            if (cannotProcess || Random.value > Probability)
            {
                return new StepResult(0, false, groups);
            }

            var newGroups = groups.Where(g => CompatibleGroups.Contains(g)).ToArray();

            return new StepResult(Random.Range(0, count), true, newGroups);
        }
    }
}