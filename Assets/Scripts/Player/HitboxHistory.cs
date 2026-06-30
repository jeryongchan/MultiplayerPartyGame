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
        // the Hitbox child whose CapsuleCollider is the shootable body. assign in the Inspector.
        [SerializeField]
        private Transform hitbox;

        // how long to keep history. Unity DOTS / Valve recommend ~250-500ms; peer-hosted games keep it
        // tighter to limit host advantage. keep at least the shooter's maxRewindSeconds so its rewinds
        // land inside the recorded window.
        [SerializeField]
        private float historySeconds = 0.5f;

        private Vector3[] _buffer; // ring of hitbox positions, indexed by tick % length
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
            {
                Debug.LogError($"{name}: HitboxHistory has no hitbox assigned, lag comp will not work.", this);
                return;
            }

            int size = Mathf.CeilToInt(historySeconds * TickRate) + 2;
            _buffer = new Vector3[size];
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
            _buffer[Mod(tick, _buffer.Length)] = hitbox.position;
            _newestTick = tick;
            if (_count < _buffer.Length)
                _count++;
        }

        // move the hitbox to where it was at tick, clamped into whatever history we actually have (the
        // buffer may not be full yet early in the match). stores the present position so Restore() can
        // put it back. no-ops if there's no history yet.
        public void Rewind(int tick)
        {
            if (_buffer == null || _count == 0 || hitbox == null)
                return;

            int oldest = _newestTick - (_count - 1);
            int clamped = Mathf.Clamp(tick, oldest, _newestTick);

            _restorePos = hitbox.position;
            hitbox.position = _buffer[Mod(clamped, _buffer.Length)];
            _rewound = true;
        }

        // put the hitbox back where it was before Rewind(). safe to call even if Rewind() was skipped.
        public void Restore()
        {
            if (!_rewound)
                return;
            hitbox.position = _restorePos;
            _rewound = false;
        }

        // Positive modulo, so a negative tick still maps to a valid ring index.
        private static int Mod(int a, int n) => ((a % n) + n) % n;
    }
}
