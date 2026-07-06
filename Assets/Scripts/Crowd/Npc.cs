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

        // Actual ground speed this frame (metres/sec), measured from real movement. Drops to ~0 while
        // loitering, so the Animator can blend to idle instead of moonwalking in place. Mirrors the
        // player's CurrentSpeed so PlayerAnimationHandler can treat NPCs the same as players.
        public float CurrentSpeed { get; private set; }

        [Header("Facing")]
        // how fast the NPC turns to face its travel direction, deg/sec
        [SerializeField]
        private float turnSpeed = 360f;

        [Header("Sway (organic walk)")]
        // chance [0,1] this NPC sways at all during its walk. rolled from (seed, index) so it's the same
        // on every machine; some NPCs weave, others walk straight. 0 disables sway entirely.
        [Range(0f, 1f)]
        [SerializeField]
        private float swayChance = 0.6f;

        // smooth side-to-side weave around the path, so the NPC doesn't walk a robotic straight line.
        // a deterministic sine of (time, phase), not per-frame randomness, so every machine agrees.
        // amplitude in metres; 0 disables.
        [SerializeField]
        private float swayAmplitude = 0.12f;

        // sway oscillations per second. slow = a gentle amble; fast = a nervous weave.
        [SerializeField]
        private float swayFrequency = 0.6f;

        [Header("Loiter (mid-path pause)")]
        // chance [0,1] this NPC loiters once during its walk. rolled from (seed, index) so it's the same
        // on every machine. 0 disables loitering entirely.
        [Range(0f, 1f)]
        [SerializeField]
        private float loiterChance = 0.3f;

        // how long a loitering NPC stands still, seconds. it freezes its path progress for this long, then
        // resumes, so total walk time is longer but the route is unchanged.
        [SerializeField]
        private float loiterDuration = 3f;

        [Header("Drift (diagonal weave)")]
        // chance [0,1] this NPC drifts diagonally, in segments: walk straight a while, then angle off to one
        // side (like a player holding W+A), then straighten, repeat. rolled from (seed, index). 0 disables.
        [Range(0f, 1f)]
        [SerializeField]
        private float driftChance = 0.4f;

        // how far to one side a full diagonal reaches, metres. the lateral offset ramps toward this.
        [SerializeField]
        private float driftAmplitude = 0.5f;

        // seconds of each straight/diagonal segment. one drift cycle = straight for this long, then diagonal
        // for this long. kept simple: one knob for both segments.
        [SerializeField]
        private float driftSegmentDuration = 2.5f;

        // per-NPC sway/loiter/drift plans, derived once from the stream index so they're identical on every
        // machine. _sways/_drifts = won that roll; -1 loiterStart = "doesn't loiter this run".
        private bool _sways;
        private float _swayPhase;
        private float _loiterStartDist = -1f;
        private bool _drifts;
        private float _driftSign = 1f; // which side this NPC drifts toward first (+right / -left)

        // last frame's final position (incl. drift/sway), so facing can follow actual motion rather than the
        // raw path tangent. _hasLastPos guards the first frame where there's no previous position yet.
        private Vector3 _lastPos;
        private bool _hasLastPos;

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

        // once robbed by a criminal, the NPC stops walking and lies down where it fell, staying there for the
        // rest of the round (evidence + cover). like _dying it's a live reactive override on the pure-function
        // crowd, set on every machine via the downed RPC, but unlike a kill it does not despawn.
        private bool _downed;
        public bool Downed => _downed;

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

            // derive sway phase + loiter plan from the index so they're deterministic (same on every
            // machine, at any join time), never from per-frame Random, which would desync the crowd.
            var rng = new System.Random(index * 73856093);
            _sways = rng.NextDouble() < swayChance;
            _swayPhase = (float)rng.NextDouble() * Mathf.PI * 2f;
            if (rng.NextDouble() < loiterChance && _totalLength > 0f)
                // loiter somewhere in the middle 60% of the walk (not right at the ends)
                _loiterStartDist = _totalLength * (0.2f + (float)rng.NextDouble() * 0.6f);
            _drifts = rng.NextDouble() < driftChance;
            _driftSign = rng.NextDouble() < 0.5 ? 1f : -1f;

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
            // while dying (shot, fade) or downed (robbed, lying on the pavement) the NPC is frozen: it no
            // longer strolls the path, so skip all path motion. _dying despawns; _downed stays for the round.
            if (_dying || _downed || _totalLength <= 0f)
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

            // loiter: once the NPC reaches its loiter point, hold there for loiterDuration by subtracting
            // the paused distance from travelled. purely a function of the clock + the seeded plan, so
            // every machine pauses the same NPC at the same spot for the same time.
            bool loitering = false;
            if (_loiterStartDist >= 0f && travelled > _loiterStartDist)
            {
                float pastLoiter = travelled - _loiterStartDist;
                float loiterDist = loiterDuration * speed; // distance "spent" standing still
                // still inside the pause window if we haven't yet "spent" the full loiter distance
                loitering = pastLoiter < loiterDist;
                travelled = _loiterStartDist + Mathf.Max(0f, pastLoiter - loiterDist);
            }

            if (travelled >= _totalLength)
            {
                Finished = true;
                return;
            }

            float clamped = Mathf.Clamp(travelled, 0f, _totalLength);
            Vector3 pos = EvaluatePosition(clamped);

            // sway and drift both nudge sideways along the path's right vector, so compute it once from the
            // travel direction. both are deterministic functions of the shared clock (+ seeded plan), never
            // per-frame Random, so they stay identical on every machine.
            Vector3 ahead = EvaluatePosition(Mathf.Min(clamped + 0.1f, _totalLength)) - pos;
            ahead.y = 0f;
            // skip sway/drift entirely while loitering, otherwise the clock-driven sideways nudge keeps
            // the NPC micro-moving (CurrentSpeed > 0), so the Animator never blends to idle.
            if (!loitering && ahead.sqrMagnitude > 1e-6f)
            {
                ahead.Normalize();
                Vector3 right = new Vector3(ahead.z, 0f, -ahead.x);

                // sway: a smooth continuous weave around the path centerline
                if (_sways && swayAmplitude > 0f)
                {
                    float s = Mathf.Sin((float)now * swayFrequency * Mathf.PI * 2f + _swayPhase);
                    pos += right * (s * swayAmplitude);
                }

                // drift: segmented diagonal. each cycle = one straight leg then one diagonal leg, the
                // diagonal side flipping every cycle. a triangle ramp over the diagonal leg makes the NPC
                // angle off to the side (like holding W+A) and back, rather than snapping.
                if (_drifts && driftAmplitude > 0f && driftSegmentDuration > 0f)
                {
                    float cycle = driftSegmentDuration * 2f;
                    float t = Mathf.Repeat((float)now, cycle); // 0..cycle
                    float side = (Mathf.Floor((float)now / cycle) % 2f == 0f) ? 1f : -1f;
                    float ramp = t < driftSegmentDuration
                        ? 0f // straight segment
                        : Mathf.Sin((t - driftSegmentDuration) / driftSegmentDuration * Mathf.PI); // 0->1->0 diagonal
                    pos += right * (ramp * side * _driftSign * driftAmplitude);
                }
            }

            // face where the NPC actually moves this frame (final pos incl. drift/sway), not the raw path
            // tangent, otherwise a drifting NPC faces the clean centerline while sliding sideways, which
            // reads as facing the wrong way. delta of consecutive final positions captures the true heading.
            Vector3 dir = _hasLastPos ? pos - _lastPos : Vector3.zero;
            _lastPos = pos;
            _hasLastPos = true;

            // real speed this frame, so the Animator idles during loiter instead of walking on the spot
            CurrentSpeed = Time.deltaTime > 0f ? dir.magnitude / Time.deltaTime : 0f;

            transform.position = pos;

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

        // react to being robbed by a criminal: stop walking and lie down where you fell, staying put for the
        // rest of the round (a body on the pavement is evidence + cover). called on every machine via the
        // downed RPC on its own NPC #index, so the same pedestrian goes down everywhere. idempotent, and a
        // no-op if the NPC is already dying (a shot wins). does not set Finished, a downed NPC is not despawned.
        //
        // the actual fall/lay animation is driven by PlayerAnimationHandler reading Downed; here we just
        // flip the flag and zero the speed so it blends out of walking.
        public void Down(int stolenSlot)
        {
            if (_dying || _downed)
                return;
            _downed = true;
            CurrentSpeed = 0f;

            // visibly remove the garment the criminal stole (same slot index on every machine, so the crowd
            // stays consistent). -1 means nothing was taken. the applier lives on the Character child.
            if (stolenSlot >= 0)
            {
                var applier = GetComponentInChildren<FriendSlop.Characters.CharacterAppearanceApplier>();
                if (applier != null)
                    applier.HideSlot(stolenSlot);
            }
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
