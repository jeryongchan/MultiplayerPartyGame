using System;
using ithappy.Creative_Characters_FREE.CharacterCustomizationTool.Extensions;
using UnityEngine;

namespace ithappy.Creative_Characters_FREE.CharacterCustomizationTool.FaceManagement
{
    [Serializable]
    public class FaceMesh
    {
        public FaceType Type;
        public Mesh Mesh;

        public FaceMesh(Mesh mesh)
        {
            Type = Enum.Parse<FaceType>(mesh.name.Split("_")[2].ToCapital());
            Mesh = mesh;
        }
    }
}