using UnityEngine;

namespace FriendSlop.Player
{
    // marker on a single per-bone hitbox collider (created at runtime by BoneHitboxBuilder, or authored
    // by hand). its presence lets other systems (e.g. history, gizmos) find the narrow-phase colliders
    // without matching by name or layer. carries no logic; the collider itself does the work.
    public class BoneHitbox : MonoBehaviour
    {
        // which skeleton bone this collider is glued to. informational (the collider is already a child
        // of the bone, so it follows automatically); handy for debugging and for the builder to log against.
        public string BoneName;
    }
}
