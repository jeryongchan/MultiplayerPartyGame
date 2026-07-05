using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FriendSlop.Player
{
    // editor tool that bakes per-bone hitbox colliders into the prefab so the shooter can tell a real
    // limb hit from a shot through a gap between the arms/legs (which the old single body-capsule
    // wrongly counted as a hit).
    //
    // right-click the component -> "Bake Bone Hitboxes" to walk _defaults, find each named bone in this
    // character's skeleton, and parent a persistent child GameObject carrying a sized primitive collider
    // on the shootable hitbox layer. because each collider is a child of its bone, it follows the
    // animation for free, no per-frame code. baking (vs building at runtime in Awake) means the colliders
    // live in the prefab asset: visible and tunable in the editor, gizmo-drawable without Play mode, and
    // no per-spawn allocation. re-baking is idempotent (it clears the previous bake first).
    //
    // HitboxHistory reads the baked colliders back via Hitboxes (it collects the BoneHitbox-tagged
    // children) for lag-comp rewind.
    //
    // one builder works for both the Player and NPC prefabs (same ithappy rig). NPCs can bake a coarser
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
            public HitZone zone; // Head = instant kill; Body = costs several hits (default)
        }

        // Unity's CapsuleCollider.direction encoding: 0 = X, 1 = Y, 2 = Z. ithappy bones point "down the
        // bone" along local Y (standard Mixamo-style), so limbs use Y.
        public enum CapsuleAxis
        {
            X = 0,
            Y = 1,
            Z = 2,
        }

        // layer for the baked hitbox colliders. must match NetworkShooter.shootableMask (the
        // project's player-hitbox layer, currently 7).
        [SerializeField]
        private int hitboxLayer = 7;

        // draw wire gizmos for each baked collider when this object is selected
        [SerializeField]
        private bool drawGizmos = true;

        // name prefix on every baked child, so the collector and the clear step can find them unambiguously
        private const string HitboxNamePrefix = "Hitbox_";

        // the baked colliders, collected from the BoneHitbox children under the skeleton. order follows the
        // hierarchy walk (stable for a given prefab), so history can index them positionally.
        public IReadOnlyList<Transform> Hitboxes
        {
            get
            {
                var list = new List<Transform>();
                foreach (var hb in GetComponentsInChildren<BoneHitbox>(true))
                    list.Add(hb.transform);
                return list;
            }
        }

        // the collider table, exposed in the Inspector so you can tune each bone's radius/length/offset and
        // re-bake without touching code. pre-filled with values derived from the actual ithappy Cute_Characters
        // rig bone distances (measured joint-to-child-joint segment lengths), not eyeballed: length = segment +
        // a small cap overhang; offset.y = segment/2 so the capsule spans this joint to the next one instead
        // of sitting at the pivot. measured segments: arm 0.141, forearm 0.116, thigh 0.150, shin 0.165,
        // torso(Spine->Neck) 0.160, hips->spine 0.093. radii are proportioned to this small "cute" rig (thin
        // limbs, chunky torso). note: no separate hand/foot hitboxes, the forearm/shin capsules extend to
        // cover them; fingers/toes are cosmetic. for a coarser NPC set, just delete rows here on that prefab.
        [Tooltip("Per-bone collider recipes. Edit and re-bake (right-click header -> Bake Bone Hitboxes). " +
                 "Reset via right-click header -> Reset To Rig Defaults.")]
        [SerializeField]
        private Definition[] definitions = DefaultDefinitions();

        // the rig-measured defaults, as a factory so both the field initializer and the reset menu share one
        // source. (a field initializer can't reference another instance field, hence a static method.)
        private static Definition[] DefaultDefinitions() => new Definition[]
        {
            new() { boneName = "Head",         shape = Shape.Sphere,  radius = 0.075f, offset = new Vector3(0, 0.03f,  0), zone = HitZone.Head },
            new() { boneName = "Spine",        shape = Shape.Capsule, radius = 0.11f,  length = 0.200f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.080f, 0) },
            new() { boneName = "LeftArm",      shape = Shape.Capsule, radius = 0.045f, length = 0.161f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.071f, 0) },
            new() { boneName = "RightArm",     shape = Shape.Capsule, radius = 0.045f, length = 0.161f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.071f, 0) },
            new() { boneName = "LeftForeArm",  shape = Shape.Capsule, radius = 0.04f,  length = 0.156f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.058f, 0) },
            new() { boneName = "RightForeArm", shape = Shape.Capsule, radius = 0.04f,  length = 0.156f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.058f, 0) },
            new() { boneName = "LeftUpLeg",    shape = Shape.Capsule, radius = 0.06f,  length = 0.170f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.075f, 0) },
            new() { boneName = "RightUpLeg",   shape = Shape.Capsule, radius = 0.06f,  length = 0.170f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.075f, 0) },
            new() { boneName = "LeftLeg",      shape = Shape.Capsule, radius = 0.05f,  length = 0.205f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.082f, 0) },
            new() { boneName = "RightLeg",     shape = Shape.Capsule, radius = 0.05f,  length = 0.205f, axis = CapsuleAxis.Y, offset = new Vector3(0, 0.082f, 0) },
        };

#if UNITY_EDITOR
        // right-click the component header -> "Bake Bone Hitboxes". idempotent: clears any prior bake first,
        // then creates one persistent child collider per definition under its bone. editor-only, the colliders
        // are authored into the prefab, not spawned at runtime.
        [ContextMenu("Bake Bone Hitboxes")]
        private void Bake()
        {
            ClearBakedInternal();

            // index every Transform in the skeleton once by name, so multiple defs targeting the same rig
            // don't each pay a full-tree walk. first match wins (the rig has no duplicate bone names).
            var bones = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                bones.TryAdd(t.name, t);

            int made = 0;
            foreach (var def in definitions)
            {
                if (string.IsNullOrEmpty(def.boneName))
                    continue;
                if (!bones.TryGetValue(def.boneName, out Transform bone))
                {
                    Debug.LogWarning($"{name}: BoneHitboxBuilder found no bone '{def.boneName}', skipping.", this);
                    continue;
                }

                CreateCollider(def, bone);
                made++;
            }

            if (made == 0)
                Debug.LogError($"{name}: BoneHitboxBuilder baked zero hitboxes, check bone names.", this);
            else
                Debug.Log($"{name}: BoneHitboxBuilder baked {made} bone hitboxes.", this);
        }

        // right-click the component header -> "Clear Bone Hitboxes". removes every baked child.
        [ContextMenu("Clear Bone Hitboxes")]
        private void ClearBaked()
        {
            int removed = ClearBakedInternal();
            Debug.Log($"{name}: BoneHitboxBuilder cleared {removed} baked hitboxes.", this);
        }

        private int ClearBakedInternal()
        {
            var existing = new List<BoneHitbox>(GetComponentsInChildren<BoneHitbox>(true));
            foreach (var hb in existing)
                Undo.DestroyObjectImmediate(hb.gameObject);
            return existing.Count;
        }

        // right-click the component header -> "Reset To Rig Defaults". restores the measured ithappy-rig table
        // if you've edited the definitions in the Inspector and want the tuned baseline back. does not re-bake.
        [ContextMenu("Reset To Rig Defaults")]
        private void ResetToDefaults()
        {
            Undo.RecordObject(this, "Reset Bone Hitbox Definitions");
            definitions = DefaultDefinitions();
            EditorUtility.SetDirty(this);
            Debug.Log($"{name}: BoneHitboxBuilder definitions reset to rig defaults ({definitions.Length} rows).", this);
        }

        // create one child-of-bone GameObject with a sized collider on the hitbox layer. local scale is
        // forced to 1 so the authored radius/length are honoured regardless of any bone scaling. registered
        // with Undo so a mis-bake is reversible, and marked dirty so the prefab persists the new children.
        private void CreateCollider(Definition def, Transform bone)
        {
            var go = new GameObject($"{HitboxNamePrefix}{def.boneName}") { layer = hitboxLayer };
            Undo.RegisterCreatedObjectUndo(go, "Bake Bone Hitbox");
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

            var marker = go.AddComponent<BoneHitbox>();
            marker.BoneName = def.boneName;
            marker.Zone = def.zone;
            EditorUtility.SetDirty(this);
        }
#endif

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;
            Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
            foreach (var t in Hitboxes)
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
