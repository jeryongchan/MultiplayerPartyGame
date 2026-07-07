using UnityEngine;

namespace FriendSlop.Player
{
    // HitZone lives in PlayerEnums.cs alongside PlayerRole / CharacterPose.

    // marker on a single per-bone hitbox collider (baked by BoneHitboxBuilder, or authored by hand). lets
    // history/gizmos find the narrow-phase colliders without matching by name or layer, and carries the zone
    // the shooter reads for headshot-vs-body. the collider itself does the physics work.
    public class BoneHitbox : MonoBehaviour
    {
        public string BoneName; // which bone this is glued to; informational, for debugging + builder logs.
        public HitZone Zone;    // set by the builder from its definition table; head = lethal.
    }
}
