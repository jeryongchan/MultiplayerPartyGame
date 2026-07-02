namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class GlovesStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Gloves;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Ears,
        };
    }
}