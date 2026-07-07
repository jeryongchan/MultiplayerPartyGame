using System.Collections.Generic;
using FriendSlop.Crowd;
using UnityEngine;

namespace FriendSlop.Player
{
    // a trigger volume tracking which crowd NPCs are within the criminal's melee reach. lives on a child with
    // a trigger collider, so the reach is a real editor-visible, drag-to-resize collider instead of a runtime
    // OverlapSphere. it only maintains the in-range set (enter/exit); it never decides a hit. at the punch's
    // contact frame CriminalMelee asks for the nearest in-range NPC and runs the authoritative server hit.
    public class MeleeRangeTracker : MonoBehaviour
    {
        // NPCs whose hitbox overlaps the trigger. a set so duplicate bone colliders of one NPC don't double it.
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

        // nearest live NPC in reach that's also within `coneHalfAngle` of `forward` from `origin`, so you punch
        // what you face, not who's beside you. prunes destroyed NPCs. null if nothing qualifies.
        public Npc GetBestTarget(Vector3 origin, Vector3 forward, float coneHalfAngle)
        {
            float cosLimit = Mathf.Cos(coneHalfAngle * Mathf.Deg2Rad);
            Npc best = null;
            float bestDist = float.MaxValue;

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
                    continue; // outside the cone.

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
