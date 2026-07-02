namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class OutfitStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Outfit;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.HatSingle,
            GroupType.Hat,
            GroupType.HatsWithHair,
            GroupType.FaceAccessories,
            GroupType.Glasses,
            GroupType.Shoes,
            GroupType.Hairstyle,
            GroupType.Gloves,
            GroupType.Ears,
        };
    }
}