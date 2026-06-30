using UnityEngine;

namespace FriendSlop.Crowd
{
    // marks a collider as belonging to an ambient crowd NPC, so the shooter can tell an NPC hit apart from
    // a player hit (NetworkObject in parent) or world/wall (neither). lives on the NPC's Hitbox child.
    // also a handle back to the owning Npc, so a confirmed NPC hit can drive reactions later
    // (despawn the NPC, trigger crowd panic, etc.). carries no networking, like the rest of the crowd;
    // the consequence of hitting an NPC (the score penalty) is decided server-side in the shooter.
    public class CrowdNpcHitbox : MonoBehaviour
    {
        [SerializeField]
        private Npc npc;

        public Npc Npc => npc;

        private void Reset() => npc = GetComponentInParent<Npc>();
    }
}
