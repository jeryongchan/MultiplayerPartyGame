using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // drives occasional buses down the road so trucks temporarily block sniper sightlines (GDD: "occasional
    // large trucks temporarily block sniper sightlines" / "trucks on a fixed loop driven by a shared network
    // clock for identical sightline blocking across clients").
    //
    // same zero-bandwidth model as CrowdManager: buses are not replicated. the server writes one
    // seed + one stream-start time; every machine reconstructs the identical bus schedule from that seed and
    // the shared NetworkManager.ServerTime. a bus's arrival, whether it stops, which stop it
    // picks, and its whole drive are pure functions of (seed, lane, busIndex), so a bus blocks the sniper on
    // the host and on every client at the exact same instant, which is the entire point.
    //
    // one numbered stream per lane. bus #k in a lane is born at k*busInterval (from the stream start), drives
    // the lane once, then despawns, never recycled. each lane gets its own seed salt so the two lanes don't
    // arrive in lockstep.
    public class BusManager : NetworkBehaviour
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

        [Tooltip("The road's lanes (usually two, opposite directions). Each runs its own bus stream.")]
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

        // shared seed: host writes once, clients read. everything per-bus derives from this so the whole
        // schedule is reproducible on every machine. server-writable only; forced non-zero (0 = "not set").
        private readonly NetworkVariable<ulong> _seed =
            new(writePerm: NetworkVariableWritePermission.Server);

        // shared-clock time the streams start counting from (each lane's bus #0 birth time). the host writes
        // it so every machine agrees; capturing it locally would differ per machine (clients see it later).
        private readonly NetworkVariable<double> _streamStartTime =
            new(writePerm: NetworkVariableWritePermission.Server);

        // next bus index to instantiate, per lane. same on every machine since births are clock-derived.
        private int[] _nextIndex;

        private bool _streaming;

        // live buses (for Finished polling + despawn). local bookkeeping only.
        private readonly List<Bus> _active = new();

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _seed.Value =
                    (
                        (ulong)Random.Range(int.MinValue, int.MaxValue)
                        ^ (ulong)System.DateTime.Now.Ticks
                    ) | 1UL; // force non-zero

                // start the schedule counting from now. (no steady-state back-dating like the crowd: buses are
                // sparse, so a road that fills over the first busInterval reads fine and avoids a bus popping
                // in already mid-lane at match start.)
                _streamStartTime.Value = NetworkManager.ServerTime.Time;
            }

            _seed.OnValueChanged += OnSeedChanged;
            _streamStartTime.OnValueChanged += OnStreamStartChanged;
            TryBeginStream();
        }

        public override void OnNetworkDespawn()
        {
            _seed.OnValueChanged -= OnSeedChanged;
            _streamStartTime.OnValueChanged -= OnStreamStartChanged;
        }

        private void OnSeedChanged(ulong _, ulong __) => TryBeginStream();
        private void OnStreamStartChanged(double _, double __) => TryBeginStream();

        // latch once both NetworkVariables have arrived (they sync independently on clients). idempotent.
        private void TryBeginStream()
        {
            if (_streaming || _seed.Value == 0 || _streamStartTime.Value == 0)
                return;
            if (busPrefab == null || lanes == null || lanes.Length == 0)
            {
                Debug.LogError("BusManager: busPrefab/lanes not set up.", this);
                return;
            }
            _nextIndex = new int[lanes.Length];
            _streaming = true;
        }

        private void Update()
        {
            if (!_streaming)
                return;

            double now = NetworkManager.ServerTime.Time;

            // spawn every bus (per lane) whose birth time has arrived and that hasn't already driven off
            for (int lane = 0; lane < lanes.Length; lane++)
            {
                while (_streamStartTime.Value + _nextIndex[lane] * busInterval <= now)
                {
                    double birth = _streamStartTime.Value + _nextIndex[lane] * busInterval;
                    if (WouldStillBeDriving(lane, _nextIndex[lane], birth, now))
                        SpawnBus(lane, _nextIndex[lane], birth);
                    _nextIndex[lane]++;
                }
            }

            // despawn any that finished their drive
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i] == null || _active[i].Finished)
                {
                    if (_active[i] != null)
                        Destroy(_active[i].gameObject);
                    _active.RemoveAt(i);
                }
            }
        }

        // quick guard so a late joiner doesn't instantiate buses that already left. a stopping bus is on the
        // road at most (lane length / cruise + stopDuration + a little ramp); this upper-bounds that cheaply.
        private bool WouldStillBeDriving(int lane, int index, double birth, double now)
        {
            var l = lanes[lane];
            if (l.start == null || l.end == null)
                return false;
            float length = Vector3.Distance(l.start.position, l.end.position);
            double maxLifetime = length / Mathf.Max(0.01f, cruiseSpeed) + stopDuration + accelTime * 2f;
            return now - birth < maxLifetime;
        }

        // instantiate bus #index in a lane locally, with its seed-derived stop behaviour and clock-derived
        // birth time. every choice is a pure function of (seed, lane, index), no runtime history, so a late
        // joiner computes the identical bus.
        private void SpawnBus(int lane, int index, double birth)
        {
            var l = lanes[lane];
            if (l.start == null || l.end == null)
                return;

            var rng = RngFor(lane, index);

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
            go.name = $"Bus_L{lane}_{index}";

            var bus = go.GetComponent<Bus>();
            if (bus == null)
                bus = go.AddComponent<Bus>();
            bus.Initialize(l.start.position, l.end.position, birth, stops, stopDist, stopDuration,
                cruiseSpeed, accelTime);

            _active.Add(bus);
        }

        // a fresh deterministic PRNG for one bus: same (seed, lane, index) gives the same stream on every
        // machine, at any join time. lane is salted in so the two lanes' schedules are independent, not
        // mirror images.
        private System.Random RngFor(int lane, int index) =>
            new System.Random((int)(_seed.Value ^ (ulong)((index + 1) * 2654435761) ^ (ulong)((lane + 1) * 40503)));
    }
}
