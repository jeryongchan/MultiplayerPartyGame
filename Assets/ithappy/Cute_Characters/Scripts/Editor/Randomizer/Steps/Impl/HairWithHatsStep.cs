namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class HairWithHatsStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.HairWithHats;

        protected override float Probability => 1f;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Ears,
        };
    }
}