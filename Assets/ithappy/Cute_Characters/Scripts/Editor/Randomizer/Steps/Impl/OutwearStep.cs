namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Randomizer.Steps.Impl
{
    public class OutwearStep : SlotStepBase, IRandomizerStep
    {
        public override GroupType GroupType => GroupType.Outwear;

        protected override GroupType[] CompatibleGroups => new[]
        {
            GroupType.Pants,
            GroupType.Shorts,
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