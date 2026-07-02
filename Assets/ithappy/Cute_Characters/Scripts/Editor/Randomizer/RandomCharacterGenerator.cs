using System;
using System.Linq;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps;
using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer
{
    public class RandomCharacterGenerator
    {
        private readonly IRandomizerStep[] _randomizerSteps =
        {
            new FacesStep(),
            new BodyStep(),
            new CostumeStep(),
            new OutfitStep(),
            new OutwearStep(),
            new PantsStep(),
            new ShortsStep(),
            new SocksStep(),
            new HatSingleStep(),
            new HatStep(),
            new HatsWithHairStep(),
            new FaceAccessoriesStep(),
            new GlassesStep(),
            new ShoesStep(),
            new HairstyleStep(),
            new GlovesStep(),
            new HairWithHatsStep(),
            new EarsStep(),
        };

        public void Randomize(CustomizableCharacter character)
        {
            character.ToDefault();

            var groups = Enum.GetValues(typeof(GroupType)).Cast<GroupType>().ToArray();

            foreach (var step in _randomizerSteps)
            {
                var variantsCount = character.GetVariantsCountInGroup(step.GroupType);

                var stepResult = step.Process(variantsCount, groups, character);

                groups = stepResult.AvailableGroups;
                character.PickGroup(step.GroupType, stepResult.Index, stepResult.IsActive);
            }
        }
    }
}