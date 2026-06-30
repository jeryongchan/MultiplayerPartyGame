using UnityEngine;

namespace FriendSlop.Crowd
{
    // an authored pedestrian centerline: an ordered list of in-scene waypoint Transforms the crowd walks.
    // pure level-design data, no runtime logic. the CrowdManager reads Points (world positions) once and
    // hands them to each spawned NPC. the path is treated as a loop (last point connects back to first);
    // for a one-way street look, author the visible street leg plus an off-screen return leg.
    // each NPC walks this same centerline but with its own lateral offset (to fill the sidewalk width),
    // so one CrowdPath drives a whole crowd.
    public class CrowdPath : MonoBehaviour
    {
        // ordered waypoints. author as child empties. need at least 2.
        [SerializeField]
        private Transform[] waypoints;

        public int Count => waypoints != null ? waypoints.Length : 0;

        // total open-path length (sum of segment distances; no closing loop). used by the manager to
        // compute how long an NPC takes to walk the path.
        public float GetTotalLength()
        {
            if (waypoints == null || waypoints.Length < 2)
                return 0f;
            float total = 0f;
            for (int i = 1; i < waypoints.Length; i++)
                if (waypoints[i] != null && waypoints[i - 1] != null)
                    total += Vector3.Distance(waypoints[i - 1].position, waypoints[i].position);
            return total;
        }

        // world-space snapshot of the path, for the manager to inject into NPCs
        public Vector3[] GetWorldPoints()
        {
            if (waypoints == null)
                return System.Array.Empty<Vector3>();
            var pts = new Vector3[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
                pts[i] = waypoints[i] != null ? waypoints[i].position : Vector3.zero;
            return pts;
        }

        // draw the looping path so you can author it without pressing Play
        private void OnDrawGizmos()
        {
            if (waypoints == null || waypoints.Length < 2)
                return;

            Gizmos.color = Color.yellow;
            int n = waypoints.Length;
            for (int i = 0; i < n; i++)
            {
                if (waypoints[i] == null || waypoints[(i + 1) % n] == null)
                    continue;
                Gizmos.DrawLine(waypoints[i].position, waypoints[(i + 1) % n].position);
                Gizmos.DrawWireSphere(waypoints[i].position, 0.2f);
            }
        }
    }
}
