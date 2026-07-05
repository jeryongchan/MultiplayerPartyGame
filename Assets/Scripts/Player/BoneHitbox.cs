using UnityEngine;

namespace FriendSlop.Player
{
    // which damage zone a bone collider belongs to. drives the hit rule: a Head hit is an
    // instant kill; a Body hit costs one of several hits to down the target.
    public enum HitZone
    {
        Body,
        Head,
    }

    // marker on a single per-bone hitbox collider (baked by BoneHitboxBuilder, or authored by
    // hand). its presence lets other systems (history, gizmos) find the narrow-phase colliders without
    // matching by name or layer, and it carries the Zone the shooter reads to decide
    // headshot-vs-body. the collider itself does the physics work.
    public class BoneHitbox : MonoBehaviour
    {
        // which skeleton bone this collider is glued to. informational (the collider is already a child
        // of the bone, so it follows automatically); handy for debugging and for the builder to log against.
        public string BoneName;

        // damage zone for this collider. set by the builder from its definition table; Head = lethal.
        public HitZone Zone;
    }
}
