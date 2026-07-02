namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class SocksStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Socks;

        protected override float Probability => .5f;

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