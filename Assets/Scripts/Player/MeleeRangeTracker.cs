using System.Collections.Generic;
using FriendSlop.Crowd;
using UnityEngine;

namespace FriendSlop.Player
{
    // a trigger volume that tracks which crowd NPCs are currently within the criminal's melee reach. lives on
    // a child of the player with a Sphere/Capsule collider set to Is Trigger, so the reach is a real,
    // editor-visible, drag-to-resize collider instead of a runtime OverlapSphere.
    //
    // it only maintains the in-range set (enter/exit); it never decides a hit. at the punch's contact frame
    // CriminalMelee asks for the nearest in-range NPC and runs the authoritative server hit, so
    // timing (contact frame) and authority (server) stay exactly where they were.
    public class MeleeRangeTracker : MonoBehaviour
    {
        // NPCs whose hitbox is currently overlapping the trigger. a set (not a list) so duplicate bone
        // colliders of the same NPC don't add it twice, and exit cleanly removes it.
        private readonly HashSet<Npc> _inRange = new();

        private void OnTriggerEnter(Collider other)
        {
            var hitbox = other.GetComponentInParent<CrowdNpcHitbox>();
            if (hitbox != null && hitbox.Npc != null)
                _inRange.Add(hitbox.Npc);
        }

        private void OnTriggerExit(Collider other)
        {
            var hitbox = other.GetComponentInParent<CrowdNpcHitbox>();
            if (hitbox != null && hitbox.Npc != null)
                _inRange.Remove(hitbox.Npc);
        }

        // the nearest live (not downed) NPC currently in reach that is also within coneHalfAngle
        // degrees of forward from origin, so you punch what you face, not
        // someone beside you. prunes any destroyed/despawned NPCs it encounters. null if nothing qualifies.
        public Npc GetBestTarget(Vector3 origin, Vector3 forward, float coneHalfAngle)
        {
            float cosLimit = Mathf.Cos(coneHalfAngle * Mathf.Deg2Rad);
            Npc best = null;
            float bestDist = float.MaxValue;

            // iterate a snapshot so we can remove stale entries while scanning
            _inRange.RemoveWhere(n => n == null);
            foreach (var npc in _inRange)
            {
                if (npc.Downed)
                    continue;

                Vector3 to = npc.transform.position - origin;
                to.y = 0f;
                float dist = to.magnitude;
                if (dist < 1e-3f)
                    continue;

                if (Vector3.Dot(forward.normalized, to / dist) < cosLimit)
                    continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = npc;
                }
            }
            return best;
        }
    }
}
