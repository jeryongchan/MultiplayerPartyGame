using UnityEditor;
using UnityEditor.SceneManagement;

namespace FriendSlop.EditorTools
{
    // forces the editor's Play button to always boot the start scene (MainMenu) instead of whatever scene
    // happens to be open in the Hierarchy, so a designer hitting Play from GameScene still enters the game
    // through the real menu/lobby flow, matching a build (which starts at build index 0).
    //
    // Unity has no Project Settings UI for this; the only hook is EditorSceneManager.playModeStartScene,
    // which is a per-session editor value that resets on restart. [InitializeOnLoad] re-applies it on every
    // editor load so the behaviour is permanent and checked into the repo.
    //
    // to disable temporarily: comment out the assignment, since "Enter Play Mode Start Scene" is not
    // exposed in the UI, so just toggle it here.
    [InitializeOnLoad]
    internal static class PlayModeStartScene
    {
        // the scene the Play button should always start from. keep in sync with build index 0.
        private const string StartScenePath = "Assets/Scenes/MainMenu.unity";

        static PlayModeStartScene()
        {
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(StartScenePath);
            if (scene != null)
                EditorSceneManager.playModeStartScene = scene;
        }
    }
}
