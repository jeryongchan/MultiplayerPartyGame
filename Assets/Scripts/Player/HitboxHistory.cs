using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-only rolling history of this player's hitbox world position, indexed by the shared
    // network tick (NetworkManager.ServerTime.Tick, synchronized across all machines, unlike the
    // per-player movement tick). the shooter rewinds this hitbox to a past tick to validate a
    // lag-compensated shot ("server-side rewind"), then restores it. this is invisible bookkeeping
    // that only ever runs on the server's copies; clients never see the momentary rewind.
    // minimal first pass: snaps to the exact recorded tick (we record every tick, so any tick inside
    // the window exists). a polished version would interpolate between the two bracketing snapshots.
    public class HitboxHistory : NetworkBehaviour
    {
        // the HitCollider child whose CapsuleCollider is the shootable body. auto-found by name if unset.
        [SerializeField]
        private Transform hitbox;

        // how long to keep history. Unity DOTS / Valve recommend ~250-500ms; peer-hosted games keep it
        // tighter to limit host advantage. the shooter's own cheat-guard window should not exceed this.
        [SerializeField]
        private float historySeconds = 0.5f;

        private struct Sample
        {
            public int Tick;
            public Vector3 Position;
        }

        private Sample[] _buffer; // ring indexed by tick % length
        private int _count; // how many ticks recorded so far (caps at length)
        private int _newestTick = int.MinValue;

        private Vector3 _restorePos;
        private bool _rewound;

        private int TickRate =>
            NetworkManager != null ? (int)NetworkManager.NetworkConfig.TickRate : 30;

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
                return; // history is server-only, clients don't rewind anything

            if (hitbox == null)
                hitbox = transform.Find("HitCollider");

            int size = Mathf.CeilToInt(historySeconds * TickRate) + 2;
            _buffer = new Sample[size];
            NetworkManager.NetworkTickSystem.Tick += RecordTick;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= RecordTick;
        }

        // append this tick's hitbox position to the ring (server, once per shared network tick)
        private void RecordTick()
        {
            if (hitbox == null)
                return;
            int tick = NetworkManager.ServerTime.Tick;
            _buffer[((tick % _buffer.Length) + _buffer.Length) % _buffer.Length] = new Sample
            {
                Tick = tick,
                Position = hitbox.position,
            };
            _newestTick = tick;
            if (_count < _buffer.Length)
                _count++;
        }

        // move the hitbox to where it was at tick (clamped into the recorded window, since the shooter
        // already cheat-guards the range). stores the present position so Restore() can put it back.
        // returns false if there's no history yet.
        public bool Rewind(int tick)
        {
            if (_buffer == null || _count == 0 || hitbox == null)
                return false;

            int oldest = _newestTick - (_count - 1);
            int clamped = Mathf.Clamp(tick, oldest, _newestTick);
            Sample s = _buffer[((clamped % _buffer.Length) + _buffer.Length) % _buffer.Length];

            _restorePos = hitbox.position;
            hitbox.position = s.Position;
            _rewound = true;
            return true;
        }

        // put the hitbox back where it was before Rewind(). safe to call even if Rewind() was skipped.
        public void Restore()
        {
            if (!_rewound)
                return;
            hitbox.position = _restorePos;
            _rewound = false;
        }
    }
}
