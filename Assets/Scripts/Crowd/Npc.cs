using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // an ambient crowd NPC whose position is a pure function of time: P(t). no mutable state is carried
    // between frames, so feeding the same time on two machines yields the same position; the crowd needs
    // no per-NPC replication, only the shared network clock (NetworkManager.ServerTime). the manager
    // instantiates one of these locally on every machine; determinism keeps them aligned.
    //
    // motion model (basic, straight pathing): arc-length traversal of a looping waypoint path at constant
    // ground speed (so uneven waypoint spacing doesn't make the NPC sprint long legs and crawl short
    // ones); a constant per-NPC lateral offset shifts the whole path sideways, so a crowd sharing one
    // centerline spreads across the sidewalk's width (a crowd, not a single-file queue); facing derived
    // from the path tangent (direction of travel), no stored rotation to desync.
    //
    // the path can be supplied two ways: manager-driven (call Initialize(points, lateralOffset) before/at
    // spawn, the crowd use case), or standalone (assign waypoints in the Inspector and drop one in a
    // scene, for solo testing). Initialize() wins if called; otherwise Awake falls back to the serialized
    // waypoints.
    //
    // not handled yet: dwell (NPC pauses mid-path for N seconds, seeded random chance + duration, pure
    // function of (seed, index) so every machine agrees; dwell position should step laterally off the
    // sidewalk centerline into a shop doorway so it looks natural, not frozen mid-street; implement as a
    // time-offset in the arc-length traversal, holding effectiveProgress at dwellDist for dwellDuration);
    // a path-node system where each waypoint carries an optional NodeBehaviour ScriptableObject (Dwell,
    // Strafe, LookAt, etc.) for criminals to mimic specific behaviours at authored locations; jitter/sway,
    // subtle per-frame lateral noise so NPCs don't walk robotically straight.
    public class Npc : MonoBehaviour
    {
        [Header("Path (standalone testing only, manager injects via Initialize)")]
        // world-space waypoints, looped (last connects back to first). optional; used only when no path is
        // injected. need at least 2 to move.
        [SerializeField]
        private Transform[] waypoints;

        // ground speed in metres/second along the path. constant regardless of segment lengths.
        // the single source of truth; CrowdManager reads this from the prefab so they never drift.
        [SerializeField]
        private float speed = 1.4f; // ~human walking pace
        public float Speed => speed;

        [Header("Facing")]
        // how fast the NPC turns to face its travel direction, deg/sec
        [SerializeField]
        private float turnSpeed = 360f;

        // the path centerline, snapshotted as world positions. we deliberately do not read live Transforms
        // each frame: the NPC moves itself, and a parented waypoint would be dragged along into a feedback
        // loop. the path is fixed level geometry, so capturing it once is correct and safe.
        private Vector3[] _points;

        // constant sideways shift (metres) applied to the whole path, along each segment's right vector.
        // spreads a shared-centerline crowd across the sidewalk width. set per NPC by the manager.
        private float _lateralOffset;

        // +1 walks the path start to end, -1 walks it end to start (a reversed pedestrian)
        private float _direction = 1f;

        // shared-clock time at which this NPC enters the path. its progress is (now - birth) * speed, so
        // it walks the path once and is done, no looping (which would recycle a recognizable face back to
        // the start). the manager derives birth times from the seed+clock so every machine agrees.
        private double _birthTime;

        // true once this NPC has walked the whole path. the manager polls this to despawn it. pure function
        // of the clock, so all machines flip it at the same instant.
        public bool Finished { get; private set; }

        // this NPC's stream index, the one identity every machine agrees on (births are pure functions of
        // (seed, index)). a confirmed shot is replicated by index, so each machine kills its own NPC #index.
        public int Index { get; private set; }

        // once shot, the NPC stops walking and fades out, then reports Finished so the manager despawns it.
        // set on every machine via the kill RPC, not derived from the clock; a kill is a live reactive
        // event layered on top of the deterministic crowd, so it must be explicitly replicated.
        private bool _dying;

        // precomputed cumulative arc-length at each waypoint (index i = distance from start to waypoint i),
        // plus the total loop length. built once so P(t) maps time -> distance -> segment in O(segments).
        private float[] _cumulative;
        private float _totalLength;

        private bool _initialized;

        // manager entry point: hand this NPC its path (world-space points), lateral offset, walk direction,
        // and the shared-clock time it enters the path. call once at/just after Instantiate, on every
        // machine. overrides any serialized waypoints.
        public void Initialize(
            int index,
            Vector3[] points,
            float lateralOffset,
            bool forward,
            double birthTime
        )
        {
            Index = index;
            _lateralOffset = lateralOffset;
            _direction = forward ? 1f : -1f;
            _birthTime = birthTime;
            BuildPath(points);
            _initialized = true;

            // place at the path start immediately, so the NPC never renders for one frame at the prefab's
            // origin (center of the map) before its first Update moves it onto the path.
            if (_totalLength > 0f)
                transform.position = EvaluatePosition(0f);
        }

        // standalone fallback: if no manager injected a path, build from the serialized waypoints so a
        // lone NPC dropped in a scene still moves.
        private void Awake()
        {
            // only build from serialized waypoints if they're actually assigned (standalone test scene).
            // when the manager spawns this prefab, Awake runs during Instantiate, before Initialize, and
            // the prefab has no waypoints assigned, so we must skip rather than dereference nulls.
            if (_initialized || waypoints == null || waypoints.Length < 2)
                return;
            foreach (var w in waypoints)
                if (w == null)
                    return; // unassigned slot, not a real standalone path; wait for Initialize

            var pts = new Vector3[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
                pts[i] = waypoints[i].position;
            BuildPath(pts);
        }

        // snapshot the path and precompute cumulative distances so traversal is by distance travelled,
        // not raw waypoint index; this is what gives constant speed across unevenly spaced waypoints.
        private void BuildPath(Vector3[] points)
        {
            if (points == null || points.Length < 2)
            {
                _totalLength = 0f;
                return;
            }

            int n = points.Length;
            _points = (Vector3[])points.Clone();

            // open path (not a loop): cumulative distance from the first point to each subsequent point.
            // _cumulative[n-1] is the total walk length; there is no closing segment back to the start.
            _cumulative = new float[n];
            _cumulative[0] = 0f;
            for (int i = 1; i < n; i++)
                _cumulative[i] = _cumulative[i - 1] + Vector3.Distance(_points[i - 1], _points[i]);
            _totalLength = _cumulative[n - 1];
        }

        private void Update()
        {
            // while dying, the fade coroutine owns this NPC: it's frozen in place (a shot pedestrian
            // doesn't keep strolling), so skip all path motion until it despawns.
            if (_dying || _totalLength <= 0f)
                return;

            // the shared clock. NetworkManager.ServerTime.Time is synchronized across host and clients
            // (unlike Time.time, which is each machine's own play-time since load), so every machine
            // evaluates P(t) at the same t and the crowd lands in the same place. fall back to Time.time
            // before the network is up (e.g. a non-networked test scene) so the NPC still moves.
            double now =
                NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
                    ? NetworkManager.Singleton.ServerTime.Time
                    : Time.timeAsDouble;

            // progress is birth-relative, so the NPC walks the path once then is done (no looping). once
            // finished, freeze: stop recomputing position so we never render the degenerate end-of-path
            // frame (the tangent vanishes and a reversed NPC's clamp would snap to the far terminus). the
            // manager despawns it next frame.
            float travelled = (float)(now - _birthTime) * speed;
            if (travelled >= _totalLength)
            {
                Finished = true;
                return;
            }

            float clamped = Mathf.Clamp(travelled, 0f, _totalLength);
            Vector3 pos = EvaluatePosition(clamped);
            transform.position = pos;

            // facing from the path tangent: sample a small step in the direction of travel so we point
            // along the walk. distanceAlong increases as the NPC advances (for both forward and reversed,
            // since EvaluatePosition already maps the reversal), so +step is always "ahead".
            Vector3 dir = EvaluatePosition(Mathf.Min(clamped + 0.1f, _totalLength)) - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f)
            {
                Quaternion target = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    target,
                    turnSpeed * Time.deltaTime
                );
            }
        }

        // time (seconds) the death fade takes. placeholder until a real death animation exists.
        [SerializeField]
        private float fadeOutSeconds = 1f;

        // react to a confirmed shot: freeze in place and fade out, then report Finished so the manager
        // despawns this NPC. called on every machine (via the kill RPC) on its own NPC #index, so the same
        // pedestrian dies everywhere. idempotent, a second hit on an already-dying NPC is ignored.
        public void Kill()
        {
            if (_dying)
                return;
            _dying = true;
            StartCoroutine(FadeAndFinish());
        }

        private System.Collections.IEnumerator FadeAndFinish()
        {
            // scale to zero over fadeOutSeconds, works with any material Surface Type (Opaque included).
            // placeholder until a real death animation exists.
            Vector3 startScale = transform.localScale;
            float t = 0f;
            while (t < fadeOutSeconds)
            {
                t += Time.deltaTime;
                float frac = Mathf.Clamp01(t / fadeOutSeconds);
                transform.localScale = Vector3.Lerp(startScale, Vector3.zero, frac);
                yield return null;
            }

            Finished = true;
        }

        // the pure position function: distance-along-path (arc-length, in metres from this NPC's start) to
        // a point on the path, shifted sideways by the constant lateral offset. same distance in gives the
        // same point out, on any machine. a reversed NPC measures distance from the other end of the path.
        private Vector3 EvaluatePosition(float distanceAlong)
        {
            // reversed pedestrians walk end to start: flip the distance so 0 means the far end of the path
            float dist = _direction > 0f ? distanceAlong : _totalLength - distanceAlong;

            // find the segment this distance falls in (linear scan; paths are short). open path, so the
            // last valid segment is between points[n-2] and points[n-1].
            int n = _points.Length;
            int seg = 0;
            while (seg < n - 2 && _cumulative[seg + 1] <= dist)
                seg++;

            Vector3 a = _points[seg];
            Vector3 b = _points[seg + 1];

            float segLen = _cumulative[seg + 1] - _cumulative[seg];
            float frac = segLen > 1e-6f ? (dist - _cumulative[seg]) / segLen : 0f;
            Vector3 pos = Vector3.Lerp(a, b, frac);

            // constant lateral offset along the segment's flat right vector, so the whole path is shifted
            // sideways and a shared-centerline crowd fills the sidewalk width instead of single-filing.
            if (_lateralOffset != 0f)
            {
                Vector3 forward = b - a;
                forward.y = 0f;
                if (forward.sqrMagnitude > 1e-6f)
                {
                    forward.Normalize();
                    Vector3 right = new Vector3(forward.z, 0f, -forward.x); // 90 degree flat rotation
                    pos += right * _lateralOffset;
                }
            }

            return pos;
        }

        // draw the path + waypoints in the editor so you can author and eyeball it without pressing Play.
        // only meaningful for the standalone (serialized-waypoint) case.
        private void OnDrawGizmos()
        {
            if (waypoints == null || waypoints.Length < 2)
                return;

            Gizmos.color = Color.cyan;
            int n = waypoints.Length;
            for (int i = 0; i < n; i++)
            {
                if (waypoints[i] == null || waypoints[(i + 1) % n] == null)
                    continue;
                Gizmos.DrawLine(waypoints[i].position, waypoints[(i + 1) % n].position);
                Gizmos.DrawWireSphere(waypoints[i].position, 0.15f);
            }
        }
    }
}
