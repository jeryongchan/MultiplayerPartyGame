using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class BodyStep : IRandomizerStep
    {
        public GroupType GroupType => GroupType.Body;

        public StepResult Process(int count, GroupType[] groups, CustomizableCharacter character)
        {
            var newGroups = groups.Where(g => g != GroupType.Body).ToArray();

            return new StepResult(Random.Range(0, count), true, newGroups);
        }
    }
}