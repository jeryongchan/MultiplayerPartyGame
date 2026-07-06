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
            // where buses enter this lane
            public Transform start;

            // where buses exit this lane. buses drive start -> end.
            public Transform end;

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
            if (l.start == null || l.end == null)
                return false;
            float length = Vector3.Distance(l.start.position, l.end.position);
            double maxLifetime = length / Mathf.Max(0.01f, cruiseSpeed) + stopDuration + accelTime * 2f;
            return now - birth < maxLifetime;
        }

        // instantiate bus #index in a lane locally, with its seed-derived stop behaviour and clock-derived birth
        // time. every choice is a pure function of (seed, lane, index), no runtime history, so a late joiner
        // computes the identical bus.
        protected override IStreamItem SpawnItem(int stream, int index, double birth)
        {
            var l = lanes[stream];
            if (l.start == null || l.end == null)
                return null;

            var rng = RngFor(stream, index);

            float laneLength = Vector3.Distance(l.start.position, l.end.position);
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
                    ? Vector3.Dot(marker.position - l.start.position, (l.end.position - l.start.position).normalized)
                    : laneLength * 0.5f;
            }

            GameObject go = Instantiate(busPrefab);
            go.name = $"Bus_L{stream}_{index}";

            var bus = go.GetComponent<Bus>();
            if (bus == null)
                bus = go.AddComponent<Bus>();
            bus.Initialize(l.start.position, l.end.position, birth, stops, stopDist, stopDuration,
                cruiseSpeed, accelTime);

            return bus;
        }
    }
}
