using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ithappy.Cute_Characters.CharacterCustomizationTool.Editor.Character
{
    public static class BaseMeshAccessor
    {
        public static string RootPath = "Assets/ithappy/" + AssetsPath.PackageName + "/";

        private static readonly string[] Keywords =
        {
            "Base",
            "Basic"
        };

        private static string[] Paths => new[]
        {
            RootPath,
            RootPath + "Meshes/",
        };

        public static void FindRoot()
        {
            var anchorAssetPath = FindBaseMeshPath();
            var pathParts = anchorAssetPath.Split('/');
            var packTitleParts = AssetsPath.PackageName.Split('_');
            var rootFound = false;
            for (var i = pathParts.Length - 1; i >= 0; i--)
            {
                if (rootFound)
                {
                    break;
                }

                foreach (var part in packTitleParts)
                {
                    rootFound = false;

                    if (!pathParts[i].Contains(part))
                    {
                        pathParts[i] = string.Empty;
                        break;
                    }

                    rootFound = true;
                }
            }

            var root = string.Join("/", pathParts.Where(p => !string.IsNullOrEmpty(p)).ToArray()) + "/";
            RootPath = root;
        }

        private static string FindBaseMeshPath()
        {
            foreach (var keyword in Keywords)
            {
                foreach (var guid in AssetDatabase.FindAssets(keyword))
                {
                    var baseMeshPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (baseMeshPath.Contains(".fbx") && baseMeshPath.Contains(AssetsPath.PackageName))
                    {
                        return baseMeshPath;
                    }
                }
            }

            return string.Empty;
        }

        public static GameObject Load()
        {
            var availableBaseMeshes = new List<GameObject>();

            foreach (var path in Paths)
            {
                var meshesInFolder = FindInFolder(path);
                availableBaseMeshes.AddRange(meshesInFolder);
            }

            var baseMesh = availableBaseMeshes.First();

            return baseMesh;
        }

        private static IEnumerable<GameObject> FindInFolder(string path)
        {
            var meshes = new List<GameObject>();

            foreach (var keyword in Keywords)
            {
                var foundMeshes = AssetLoader.LoadAssets<GameObject>(keyword, path);
                meshes.AddRange(foundMeshes);
            }

            return meshes;
        }
    }
}