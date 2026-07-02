namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class FaceAccessoriesStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.FaceAccessories;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Glasses,
            GroupType.Shoes,
            GroupType.Hairstyle,
            GroupType.Gloves,
            GroupType.Ears,
        };
    }
}