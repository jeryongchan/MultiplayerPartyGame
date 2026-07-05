using System.Collections.Generic;
using UnityEngine;

namespace FriendSlop.Player
{
    // builds per-bone hitbox colliders at runtime so the shooter can tell a real limb hit from a shot
    // through a gap between the arms/legs (which the old single body-capsule wrongly counted as a hit).
    //
    // on Awake it walks _defaults, finds each named bone in this character's skeleton, and
    // parents a child GameObject carrying a sized primitive collider on the shootable hitbox layer.
    // because each collider is a child of its bone, it follows the animation for free, no per-frame code.
    // the created colliders (in a stable order) are exposed via Hitboxes for lag-comp history.
    //
    // one builder works for both the Player and NPC prefabs (same ithappy rig). NPCs can pass a coarser
    // subset via overrideDefinitions if we want fewer colliders on the crowd later.
    public class BoneHitboxBuilder : MonoBehaviour
    {
        // shape of a bone collider. capsule for limbs/torso (a swept sphere along the bone), sphere for
        // the head (roughly ball-shaped, no meaningful long axis).
        public enum Shape
        {
            Capsule,
            Sphere,
        }

        // one collider recipe: which bone to glue to, what shape, and its dimensions in the bone's local
        // space. length = capsule height along its local axis; radius = capsule/sphere radius; offset nudges
        // the collider along the bone so it spans the limb segment rather than sitting at the joint pivot.
        [System.Serializable]
        public struct Definition
        {
            public string boneName;
            public Shape shape;
            public float radius;
            public float length; // capsule only; ignored for spheres
            public Vector3 offset; // local-space centre relative to the bone pivot
            public CapsuleAxis axis; // capsule only; which local axis the length runs along
        }

        // Unity's CapsuleCollider.direction encoding: 0 = X, 1 = Y, 2 = Z. ithappy bones point "down the
        // bone" along local Y (standard Mixamo-style), so limbs use Y.
        public enum CapsuleAxis
        {
            X = 0,
            Y = 1,
            Z = 2,
        }

        // layer for the created hitbox colliders. must match NetworkShooter.shootableMask (the
        // project's player-hitbox layer, currently 7).
        [SerializeField]
        private int hitboxLayer = 7;

        // leave empty to use the built-in ithappy-rig table. fill in to override per prefab (e.g. a
        // coarser 3-collider set for NPCs).
        [SerializeField]
        private Definition[] overrideDefinitions;

        // draw wire gizmos for each built collider when this object is selected (editor only)
        [SerializeField]
        private bool drawGizmos = true;

        // the colliders we created, in definition order. stable across a session, so history can index them
        // positionally without matching by name every frame. populated in Awake.
        public IReadOnlyList<Transform> Hitboxes => _hitboxes;

        private readonly List<Transform> _hitboxes = new();

        // built-in table tuned for the ithappy Cute_Characters rig (Base_Model.fbx). sizes are deliberately
        // a touch fat: a hit that grazes a limb should register, and fat colliders cover the swept motion
        // between ticks (the same reason Overwatch/Valorant inflate their bone volumes). tune in play mode
        // via the gizmos. note: this rig has a single "Spine" (no Spine1) and no separate hand/foot hitboxes;
        // fingers/toes are cosmetic and not worth a collider.
        private static readonly Definition[] _defaults =
        {
            new() { boneName = "Head",         shape = Shape.Sphere,  radius = 0.13f },
            new() { boneName = "Spine",        shape = Shape.Capsule, radius = 0.16f, length = 0.42f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.18f, 0) },
            new() { boneName = "Hips",         shape = Shape.Capsule, radius = 0.15f, length = 0.16f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.02f, 0) },
            new() { boneName = "LeftArm",      shape = Shape.Capsule, radius = 0.06f, length = 0.24f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.10f, 0) },
            new() { boneName = "RightArm",     shape = Shape.Capsule, radius = 0.06f, length = 0.24f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.10f, 0) },
            new() { boneName = "LeftForeArm",  shape = Shape.Capsule, radius = 0.05f, length = 0.22f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.10f, 0) },
            new() { boneName = "RightForeArm", shape = Shape.Capsule, radius = 0.05f, length = 0.22f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.10f, 0) },
            new() { boneName = "LeftUpLeg",    shape = Shape.Capsule, radius = 0.08f, length = 0.30f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.15f, 0) },
            new() { boneName = "RightUpLeg",   shape = Shape.Capsule, radius = 0.08f, length = 0.30f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.15f, 0) },
            new() { boneName = "LeftLeg",      shape = Shape.Capsule, radius = 0.06f, length = 0.30f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.15f, 0) },
            new() { boneName = "RightLeg",     shape = Shape.Capsule, radius = 0.06f, length = 0.30f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.15f, 0) },
        };

        private void Awake()
        {
            var defs = (overrideDefinitions != null && overrideDefinitions.Length > 0)
                ? overrideDefinitions
                : _defaults;

            // index every Transform in the skeleton once by name, so multiple defs targeting the same rig
            // don't each pay a full-tree walk. first match wins (the rig has no duplicate bone names).
            var bones = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                bones.TryAdd(t.name, t);

            foreach (var def in defs)
            {
                if (string.IsNullOrEmpty(def.boneName))
                    continue;
                if (!bones.TryGetValue(def.boneName, out Transform bone))
                {
                    Debug.LogWarning($"{name}: BoneHitboxBuilder found no bone '{def.boneName}', skipping.", this);
                    continue;
                }

                _hitboxes.Add(CreateCollider(def, bone));
            }

            if (_hitboxes.Count == 0)
                Debug.LogError($"{name}: BoneHitboxBuilder built zero hitboxes, shots can't hit this character.", this);
        }

        // create one child-of-bone GameObject with a sized collider on the hitbox layer. local scale is
        // forced to 1 so the authored radius/length are honoured regardless of any bone scaling.
        private Transform CreateCollider(Definition def, Transform bone)
        {
            var go = new GameObject($"Hitbox_{def.boneName}") { layer = hitboxLayer };
            Transform t = go.transform;
            t.SetParent(bone, worldPositionStays: false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            if (def.shape == Shape.Sphere)
            {
                var col = go.AddComponent<SphereCollider>();
                col.center = def.offset;
                col.radius = def.radius;
            }
            else
            {
                var col = go.AddComponent<CapsuleCollider>();
                col.center = def.offset;
                col.radius = def.radius;
                col.height = def.length;
                col.direction = (int)def.axis;
            }

            go.AddComponent<BoneHitbox>().BoneName = def.boneName;
            return t;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;
            Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
            foreach (var t in _hitboxes)
            {
                if (t == null)
                    continue;
                var col = t.GetComponent<Collider>();
                if (col == null)
                    continue;
                Gizmos.matrix = t.localToWorldMatrix;
                if (col is SphereCollider s)
                    Gizmos.DrawWireSphere(s.center, s.radius);
                else if (col is CapsuleCollider c)
                    Gizmos.DrawWireSphere(c.center, c.radius); // rough marker; full capsule gizmo is verbose
            }
        }
    }
}
