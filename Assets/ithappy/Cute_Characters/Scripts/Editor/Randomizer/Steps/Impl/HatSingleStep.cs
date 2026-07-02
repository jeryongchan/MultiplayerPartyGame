namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class HatSingleStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.HatSingle;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Shoes,
            GroupType.Gloves,
        };
    }
}