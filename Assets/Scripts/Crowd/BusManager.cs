using UnityEngine;

namespace FriendSlop.Crowd
{
    // drives occasional buses down the road so they temporarily block sniper sightlines (GDD: "occasional
    // large trucks temporarily block sniper sightlines" / "trucks on a fixed loop driven by a shared network
    // clock for identical sightline blocking across clients").
    //
    // a DeterministicStream with one numbered stream per lane: buses are not replicated, every
    // machine reconstructs the identical schedule from the shared seed + clock, so a bus blocks the sniper on
    // the host and every client at the exact same instant. a bus's arrival, whether it stops, which stop it
    // picks, and its whole drive are pure functions of (seed, lane, busIndex). each lane is salted separately
    // (via the base's stream arg) so the two lanes don't arrive in lockstep.
    public class BusManager : DeterministicStream
    {
        [System.Serializable]
        public class Lane
        {
            // ordered path this lane's buses drive, start to end (index 0 -> last). straight segments between
            // consecutive points, so corners (e.g. around a block) just need an extra waypoint at the turn.
            // needs at least 2.
            public Transform[] waypoints;

            // possible stop points along the lane. each stopping bus picks one (seeded). leave empty
            // for a lane whose buses never stop (drive straight through).
            public Transform[] stops;
        }

        [Header("Setup")]
        [SerializeField]
        private GameObject busPrefab;

        // the road's lanes (usually two, opposite directions). each runs its own bus stream.
        [SerializeField]
        private Lane[] lanes;

        [Header("Timing")]
        // seconds between consecutive buses entering a lane. higher = rarer buses.
        [SerializeField]
        private float busInterval = 12f;

        // chance [0,1] a given bus actually stops (vs. driving straight through). seeded per bus.
        [Range(0f, 1f)]
        [SerializeField]
        private float stopChance = 0.6f;

        // how long a stopping bus sits at its stop point, blocking the sightline (seconds)
        [SerializeField]
        private float stopDuration = 5f;

        // on: stopping buses cycle through the stop markers in order (0,1,2,... then loop). off: each
        // stopping bus picks a stop at random (seeded). buses that roll "drive through" (stopChance)
        // still advance the cycle by index, so set stopChance = 1 for a strict, gap-free 1->2->3 rotation.
        [SerializeField]
        private bool cycleStops = true;

        [Header("Motion")]
        // cruising speed of a bus (metres/second)
        [SerializeField]
        private float cruiseSpeed = 8f;

        // seconds to accelerate from a standstill up to cruise speed (and to brake into a stop)
        [SerializeField]
        private float accelTime = 1f;

        // one stream per lane. (no steady-state back-dating like the crowd: buses are sparse, so a road that
        // fills over the first busInterval reads fine and avoids a bus popping in already mid-lane at match start.)
        protected override int StreamCount => lanes != null ? lanes.Length : 0;

        protected override float Interval(int stream) => busInterval;

        protected override void OnStreamReady()
        {
            if (busPrefab == null || lanes == null || lanes.Length == 0)
                Debug.LogError("BusManager: busPrefab/lanes not set up.", this);
        }

        // quick guard so a late joiner doesn't instantiate buses that already left. a stopping bus is on the
        // road at most (lane length / cruise + stopDuration + a little ramp); this upper-bounds that cheaply.
        protected override bool IsStillAlive(int stream, int index, double birth, double now)
        {
            var l = lanes[stream];
            if (!HasValidPath(l, out float length))
                return false;
            double maxLifetime = length / Mathf.Max(0.01f, cruiseSpeed) + stopDuration + accelTime * 2f;
            return now - birth < maxLifetime;
        }

        // a usable lane needs at least 2 waypoints, all assigned. also returns the total polyline length.
        private static bool HasValidPath(Lane l, out float length)
        {
            length = 0f;
            if (l.waypoints == null || l.waypoints.Length < 2)
                return false;
            for (int i = 0; i < l.waypoints.Length; i++)
            {
                if (l.waypoints[i] == null)
                    return false;
            }
            for (int i = 1; i < l.waypoints.Length; i++)
                length += Vector3.Distance(l.waypoints[i - 1].position, l.waypoints[i].position);
            return true;
        }

        // arc-distance along the polyline (from waypoints[0]) of the point on the path nearest to `marker`.
        // used to place a stop at the waypoint closest to it, projected onto the path the bus actually drives.
        private static float ArcDistanceOf(Transform[] waypoints, Vector3 marker)
        {
            float best = float.MaxValue;
            float bestArc = 0f;
            float arc = 0f;
            for (int i = 1; i < waypoints.Length; i++)
            {
                Vector3 a = waypoints[i - 1].position;
                Vector3 b = waypoints[i].position;
                float segLength = Vector3.Distance(a, b);
                Vector3 segDir = segLength > 1e-6f ? (b - a) / segLength : Vector3.zero;
                float proj = Mathf.Clamp(Vector3.Dot(marker - a, segDir), 0f, segLength);
                float dist = Vector3.Distance(marker, a + segDir * proj);
                if (dist < best)
                {
                    best = dist;
                    bestArc = arc + proj;
                }
                arc += segLength;
            }
            return bestArc;
        }

        // instantiate bus #index in a lane locally, with its seed-derived stop behaviour and clock-derived birth
        // time. every choice is a pure function of (seed, lane, index), no runtime history, so a late joiner
        // computes the identical bus.
        protected override IStreamItem SpawnItem(int stream, int index, double birth)
        {
            var l = lanes[stream];
            if (!HasValidPath(l, out float laneLength))
                return null;

            var rng = RngFor(stream, index);

            bool hasStops = l.stops != null && l.stops.Length > 0;
            bool stops = hasStops && rng.NextDouble() < stopChance;

            float stopDist = 0f;
            if (stops)
            {
                // choose which stop marker this bus halts at. cycle = 0,1,2,...,0,1,... by bus index
                // (predictable, deterministic); otherwise a seeded random pick. either way it's a pure
                // function of (seed, lane, index) so every machine agrees.
                int pick = cycleStops ? index % l.stops.Length : rng.Next(l.stops.Length);
                Transform marker = l.stops[pick];
                stopDist = marker != null
                    ? ArcDistanceOf(l.waypoints, marker.position)
                    : laneLength * 0.5f;
            }

            Vector3[] path = new Vector3[l.waypoints.Length];
            for (int i = 0; i < path.Length; i++)
                path[i] = l.waypoints[i].position;

            GameObject go = Instantiate(busPrefab);
            go.name = $"Bus_L{stream}_{index}";

            var bus = go.GetComponent<Bus>();
            if (bus == null)
                bus = go.AddComponent<Bus>();
            bus.Initialize(path, birth, stops, stopDist, stopDuration, cruiseSpeed, accelTime);

            return bus;
        }
    }
}
