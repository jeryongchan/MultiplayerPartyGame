using System;
using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using Random = UnityEngine.Random;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class EarsStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Ears;

        protected override float Probability => 1f;

        protected override GroupType[] CompatibleGroups => Array.Empty<GroupType>();

        public override StepResult Process(int count, GroupType[] groups, CustomizableCharacter character)
        {
            var cannotProcess = !groups.Contains(GroupType);
            groups = RemoveSelf(groups);

            if (cannotProcess || Random.value > Probability)
            {
                return new StepResult(0, false, groups);
            }

            var newGroups = groups.Where(g => CompatibleGroups.Contains(g)).ToArray();
            var index = character.GetSlotBy(SlotType.Body).SelectedIndex;

            return new StepResult(index, true, newGroups);
        }
    }
}