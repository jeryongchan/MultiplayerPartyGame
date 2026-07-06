using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // one bus that drives a straight lane once, optionally halting at a stop, then leaves. like the crowd
    // NPC, its position is a pure function of time: feed the same shared clock on two machines and you get
    // the same pose, so a bus blocks the sniper's sightline at the exact same instant everywhere, with no
    // per-bus replication (matches the GDD's "trucks on a shared clock for identical sightline blocking").
    //
    // motion model: a symmetric speed profile along the lane's forward axis, sampled at elapsed = now-birth.
    // approach: accelerate from 0 to cruiseSpeed over accelTime, then cruise to the stop point.
    // stop: (only if this bus stops) sit still at stopDist for stopDuration. this is the useful window:
    // the big solid BoxCollider on the prefab blocks the sniper's LOS raycast.
    // depart: accelerate away and cruise off the far end; report Finished so the manager despawns it.
    // a non-stopping bus just cruises straight through (approach+depart with no dwell).
    //
    // distances are integrated analytically from the profile (no per-frame accumulation of state), so a late
    // joiner that starts sampling mid-drive lands on the exact same spot as everyone else.
    public class Bus : MonoBehaviour, IStreamItem
    {
        // lane geometry, injected by the manager at spawn. world space; the bus drives start -> end.
        private Vector3 _start;
        private Vector3 _end;
        private Vector3 _dir;      // normalized start->end
        private float _laneLength; // |end - start|

        // vertical lift so the bus rests on the lane line instead of straddling it: the lane is authored at
        // ground height (Y=0), but the prefab's pivot is at its center, so we raise every position by half the
        // bus's world height. computed from the renderer bounds, so it auto-adapts to any prefab scale.
        private Vector3 _groundOffset;

        private double _birthTime; // shared-clock time this bus entered the lane

        // speed profile
        private float _cruiseSpeed;
        private float _accelTime;

        // stop behaviour (all fixed at spawn, seed-derived by the manager)
        private bool _stops;
        private float _stopDist;      // arc-distance along the lane where the bus halts
        private float _stopDuration;  // seconds the bus sits at _stopDist

        // cached profile timings (computed once in Initialize from the above)
        private float _accelDist;     // distance covered while ramping 0->cruise (= depart ramp too)
        private double _approachTime; // elapsed at which the bus reaches _stopDist (end of phase 1)
        private double _departStart;  // elapsed at which the bus starts moving again (end of stop)
        private double _totalTime;    // elapsed at which the bus clears the far end -> Finished

        public bool Finished { get; private set; }

        // configure this bus for its one drive. called locally on every machine right after Instantiate with
        // values the manager derived deterministically from (seed, index), so every machine builds the same
        // bus. stopDist is ignored when stops is false.
        public void Initialize(Vector3 start, Vector3 end, double birthTime, bool stops, float stopDist,
            float stopDuration, float cruiseSpeed, float accelTime)
        {
            _start = start;
            _end = end;
            _dir = (end - start).normalized;
            _laneLength = Vector3.Distance(start, end);

            // lift by half the bus's world height so its bottom sits at the lane's height (pivot is centered)
            var rend = GetComponentInChildren<Renderer>();
            _groundOffset = rend != null ? Vector3.up * rend.bounds.extents.y : Vector3.zero;

            _birthTime = birthTime;
            _cruiseSpeed = Mathf.Max(0.01f, cruiseSpeed);
            _accelTime = Mathf.Max(0f, accelTime);

            _stops = stops;
            _stopDist = Mathf.Clamp(stopDist, 0f, _laneLength);
            _stopDuration = stops ? Mathf.Max(0f, stopDuration) : 0f;

            // distance covered during a single 0->cruise ramp (0.5*v*t). the depart ramp mirrors it.
            _accelDist = 0.5f * _cruiseSpeed * _accelTime;

            // phase boundaries in elapsed-time. approach = ramp up + cruise the remaining approach distance.
            float approachCruiseDist = Mathf.Max(0f, _stopDist - _accelDist);
            _approachTime = _accelTime + approachCruiseDist / _cruiseSpeed;
            _departStart = _approachTime + _stopDuration;

            // depart = ramp up again + cruise the rest of the lane. (if the bus doesn't stop, _stopDuration
            // is 0 so approach and depart are continuous; the double-ramp at _stopDist is a negligible,
            // invisible hitch we accept for one simple analytic profile.)
            float departCruiseDist = Mathf.Max(0f, (_laneLength - _stopDist) - _accelDist);
            _totalTime = _departStart + _accelTime + departCruiseDist / _cruiseSpeed;

            // snap to the correct starting pose immediately so the first rendered frame is right (important
            // for a late joiner that spawns the bus already mid-lane)
            UpdatePose();
        }

        private void Update() => UpdatePose();

        private void UpdatePose()
        {
            if (Finished)
                return;

            double now = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Time
                : Time.timeAsDouble;
            double t = now - _birthTime;

            if (t >= _totalTime)
            {
                Finished = true;
                return;
            }

            float dist = DistanceAt(t);
            transform.position = _start + _dir * dist + _groundOffset;
            if (_dir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(_dir, Vector3.up);
        }

        // arc-distance along the lane at elapsed time t, integrating the approach->stop->depart profile
        private float DistanceAt(double t)
        {
            if (t <= 0d)
                return 0f;

            // phase 1: approach to the stop point
            if (t < _approachTime)
                return RampCruiseDistance((float)t, _stopDist);

            // phase 2: halted at the stop point
            if (t < _departStart)
                return _stopDist;

            // phase 3: depart from the stop point to the far end
            float td = (float)(t - _departStart);
            return _stopDist + RampCruiseDistance(td, _laneLength - _stopDist);
        }

        // distance travelled after `elapsed` seconds of a "ramp 0->cruise over accelTime, then cruise" motion,
        // clamped so it never overshoots `segmentLength` (the remaining distance in this phase)
        private float RampCruiseDistance(float elapsed, float segmentLength)
        {
            float d;
            if (elapsed < _accelTime && _accelTime > 0f)
            {
                // 0.5*a*t^2 with a = cruise/accelTime
                float a = _cruiseSpeed / _accelTime;
                d = 0.5f * a * elapsed * elapsed;
            }
            else
            {
                d = _accelDist + (elapsed - _accelTime) * _cruiseSpeed;
            }
            return Mathf.Min(d, segmentLength);
        }
    }
}
