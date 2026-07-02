using ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor
{
    public static class AssetsPath
    {
        public const string PackageName = "Cute_Characters";

        public static string AnimationController => BaseMeshAccessor.RootPath + "Animations/Animation_Controllers/Character_Movement.controller";
        public static string SavedCharacters => BaseMeshAccessor.RootPath + "Saved_Characters/";
        public static string SlotLibrary => BaseMeshAccessor.RootPath + "Configs/SlotLibrary.asset";

        public static class Folder
        {
            public static string Materials => BaseMeshAccessor.RootPath + "Materials/";
            public static string Meshes => BaseMeshAccessor.RootPath + "Meshes";
            public static string Faces => BaseMeshAccessor.RootPath + "Meshes/Faces/";
        }
    }
}