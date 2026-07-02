namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class GlassesStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Glasses;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Shoes,
            GroupType.Hairstyle,
            GroupType.Gloves,
            GroupType.Ears,
        };
    }
}