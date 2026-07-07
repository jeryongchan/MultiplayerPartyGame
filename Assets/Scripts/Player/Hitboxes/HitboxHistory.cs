using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-only rolling history of this character's per-bone hitbox world transforms, keyed by the shared
    // network tick (ServerTime.Tick, synced across machines unlike the per-player movement tick). the shooter
    // rewinds these to a past tick to validate a lag-compensated shot, then restores them. invisible
    // bookkeeping on the server's copies only; clients never see the momentary rewind.
    //
    // pose-correct: it records world position and rotation of every per-bone collider built by
    // BoneHitboxBuilder, so rewinding restores the whole posed skeleton at that tick (a shot grazing a swinging
    // arm hits the arm; one through the gap between the legs misses). server-local RAM only, ~0 bandwidth.
    //
    // first pass snaps to the exact recorded tick (we record every tick, so any tick in the window exists); a
    // polished version would interpolate between the two bracketing snapshots.
    public class HitboxHistory : NetworkBehaviour
    {
        [SerializeField]
        private BoneHitboxBuilder builder; // source of the colliders to record; found on this object/children if unset.

        // how long to keep history. Valve/DOTS recommend ~250-500ms; peer-hosted keeps it tight to limit host
        // advantage. keep >= the shooter's maxRewindSeconds so its rewinds land inside the window.
        [SerializeField]
        private float historySeconds = 0.5f;

        private Transform[] _bones; // colliders we record/rewind, captured on spawn (order stable across the session).

        // ring of per-tick snapshots. _posBuffer[tickSlot * _bones.Length + boneIndex] is one bone's world pos
        // at that tick; _rotBuffer likewise. flat arrays avoid a jagged-array alloc per tick.
        private Vector3[] _posBuffer;
        private Quaternion[] _rotBuffer;
        private int _slots; // tick slots in the ring.
        private int _count; // ticks recorded so far (caps at _slots).
        private int _newestTick = int.MinValue;

        // present-world transforms saved by Rewind() so Restore() can put everything back.
        private Vector3[] _restorePos;
        private Quaternion[] _restoreRot;
        private bool _rewound;

        private int TickRate =>
            NetworkManager != null ? (int)NetworkManager.NetworkConfig.TickRate : 30;

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
                return; // history is server-only, clients don't rewind anything

            if (builder == null)
                builder = GetComponentInChildren<BoneHitboxBuilder>();
            if (builder == null)
            {
                Debug.LogError($"{name}: HitboxHistory found no BoneHitboxBuilder, lag comp will not work.", this);
                return;
            }

            IReadOnlyList<Transform> boxes = builder.Hitboxes;
            if (boxes == null || boxes.Count == 0)
            {
                Debug.LogError($"{name}: HitboxHistory got zero bone hitboxes, lag comp will not work.", this);
                return;
            }

            _bones = new Transform[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
                _bones[i] = boxes[i];

            _slots = Mathf.CeilToInt(historySeconds * TickRate) + 2;
            _posBuffer = new Vector3[_slots * _bones.Length];
            _rotBuffer = new Quaternion[_slots * _bones.Length];
            _restorePos = new Vector3[_bones.Length];
            _restoreRot = new Quaternion[_bones.Length];

            NetworkManager.NetworkTickSystem.Tick += RecordTick;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= RecordTick;
        }

        // append this tick's posed skeleton (every bone collider's world pos+rot) to the ring, once per shared tick.
        private void RecordTick()
        {
            if (_bones == null)
                return;
            int tick = NetworkManager.ServerTime.Tick;
            int baseIdx = Mod(tick, _slots) * _bones.Length;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null)
                    continue;
                _posBuffer[baseIdx + i] = _bones[i].position;
                _rotBuffer[baseIdx + i] = _bones[i].rotation;
            }
            _newestTick = tick;
            if (_count < _slots)
                _count++;
        }

        // move every bone collider to where it was at `tick`, clamped into whatever history we have (the ring
        // may not be full early in the match). saves the present transforms for Restore(). no-ops if empty.
        public void Rewind(int tick)
        {
            if (_bones == null || _count == 0)
                return;

            int oldest = _newestTick - (_count - 1);
            int clamped = Mathf.Clamp(tick, oldest, _newestTick);
            int baseIdx = Mod(clamped, _slots) * _bones.Length;

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null)
                    continue;
                _restorePos[i] = _bones[i].position;
                _restoreRot[i] = _bones[i].rotation;
                _bones[i].SetPositionAndRotation(_posBuffer[baseIdx + i], _rotBuffer[baseIdx + i]);
            }
            _rewound = true;
        }

        // put every bone collider back where it was before Rewind(). safe even if Rewind() was skipped.
        public void Restore()
        {
            if (!_rewound)
                return;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null)
                    continue;
                _bones[i].SetPositionAndRotation(_restorePos[i], _restoreRot[i]);
            }
            _rewound = false;
        }

        // positive modulo, so a negative tick still maps to a valid ring index.
        private static int Mod(int a, int n) => ((a % n) + n) % n;
    }
}
