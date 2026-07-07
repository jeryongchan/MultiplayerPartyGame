using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FriendSlop.Player
{
    // editor tool that bakes per-bone hitbox colliders into the prefab, so the shooter can tell a real limb hit
    // from a shot through a gap between the arms/legs (which the old single body-capsule wrongly counted).
    //
    // right-click the component -> Bake Bone Hitboxes to walk `definitions`, find each named bone, and parent a
    // persistent child carrying a sized collider on the hitbox layer. each collider is a child of its bone, so
    // it follows the animation for free. baking (vs building at runtime) means the colliders live in the prefab:
    // visible/tunable in the editor, gizmo-drawable without play mode, no per-spawn alloc. re-baking is
    // idempotent (clears the previous bake first). HitboxHistory reads them back via Hitboxes for lag-comp.
    //
    // one builder works for both the Player and NPC prefabs (same ithappy rig); trim rows on the NPC prefab for
    // a coarser crowd set.
    public class BoneHitboxBuilder : MonoBehaviour
    {
        // capsule for limbs/torso (swept sphere along the bone), sphere for the head (no meaningful long axis).
        public enum Shape
        {
            Capsule,
            Sphere,
        }

        // one collider recipe, dimensions in the bone's local space. length = capsule height along its axis;
        // offset nudges the collider along the bone so it spans the segment rather than sitting at the pivot.
        [System.Serializable]
        public struct Definition
        {
            public string boneName;
            public Shape shape;
            public float radius;
            public float length; // capsule only.
            public Vector3 offset; // local-space centre relative to the bone pivot.
            public CapsuleAxis axis; // capsule only; which local axis the length runs along.
            public HitZone zone; // Head = instant kill; Body = costs several hits (default).
        }

        // CapsuleCollider.direction encoding. ithappy bones point down the bone along local Y (Mixamo-style).
        public enum CapsuleAxis
        {
            X = 0,
            Y = 1,
            Z = 2,
        }

        // layer for the baked colliders. must match SniperShooter.shootableMask (currently 7).
        [SerializeField]
        private int hitboxLayer = 7;

        [SerializeField]
        private bool drawGizmos = true; // wire gizmos for each collider when selected.

        // name prefix on every baked child, so the collector and clear step find them unambiguously.
        private const string HitboxNamePrefix = "Hitbox_";

        // the baked colliders, from the BoneHitbox children. order follows the hierarchy walk (stable per
        // prefab), so history can index them positionally.
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

        // the collider table, exposed in the Inspector so you can tune each bone and re-bake without code.
        // values are derived from the actual ithappy Cute_Characters rig (measured joint->child-joint
        // segments), not eyeballed: length = segment + a small cap overhang; offset.y = segment/2 so the
        // capsule spans joint to joint. radii proportioned to this small "cute" rig (thin limbs, chunky torso).
        // no separate hand/foot hitboxes: the forearm/shin capsules extend to cover them. delete rows on the
        // NPC prefab for a coarser set.
        [SerializeField]
        private Definition[] definitions = DefaultDefinitions();

        // the rig-measured defaults, as a factory so the field initializer and the reset menu share one source
        // (a field initializer can't reference another instance field, hence a static method).
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
        // idempotent: clears any prior bake, then creates one persistent child collider per definition under
        // its bone. editor-only; the colliders are authored into the prefab, not spawned at runtime.
        [ContextMenu("Bake Bone Hitboxes")]
        private void Bake()
        {
            ClearBakedInternal();

            // index every Transform once by name so multiple defs don't each pay a full-tree walk. first match
            // wins (the rig has no duplicate bone names).
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

        // removes every baked child.
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

        // restores the measured ithappy-rig table if you've edited the definitions and want the baseline back.
        // does not re-bake.
        [ContextMenu("Reset To Rig Defaults")]
        private void ResetToDefaults()
        {
            Undo.RecordObject(this, "Reset Bone Hitbox Definitions");
            definitions = DefaultDefinitions();
            EditorUtility.SetDirty(this);
            Debug.Log($"{name}: BoneHitboxBuilder definitions reset to rig defaults ({definitions.Length} rows).", this);
        }

        // one child-of-bone with a sized collider on the hitbox layer. local scale forced to 1 so the authored
        // radius/length hold regardless of bone scaling. undo-registered and dirtied so the prefab persists it.
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
