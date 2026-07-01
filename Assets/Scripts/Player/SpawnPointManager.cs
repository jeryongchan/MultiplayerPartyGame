using UnityEngine;

namespace FriendSlop.Player
{
    // owns the two sets of spawn points (sniper rooftop, criminal street-level) and hands them out
    // round-robin so players never stack on the same spot. max 5 per team, place exactly 5 child
    // Transforms under each array slot in the Inspector.
    // server-only logic: only the server calls GetSpawnPoint (inside OnNetworkSpawn, IsServer guard).
    // clients just teleport to wherever the server told them to be via the authoritative state sync.
    public class SpawnPointManager : MonoBehaviour
    {
        public static SpawnPointManager Instance { get; private set; }

        [Header("Sniper spawn points (rooftop), place 5")]
        [SerializeField]
        private Transform[] sniperPoints = new Transform[5];

        [Header("Criminal spawn points (street level), place 5")]
        [SerializeField]
        private Transform[] criminalPoints = new Transform[5];

        private int _nextSniper;
        private int _nextCriminal;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // returns the next available spawn position for the given role, cycling through the 5 slots.
        // if a slot's Transform is null (not assigned in Inspector), falls back to the origin so the
        // game doesn't hard-crash; assign all 5 points before shipping.
        public Vector3 GetSpawnPoint(PlayerRole role)
        {
            if (role == PlayerRole.Sniper)
            {
                var t = sniperPoints[_nextSniper % sniperPoints.Length];
                _nextSniper++;
                return t != null ? t.position : Vector3.up;
            }
            else
            {
                var t = criminalPoints[_nextCriminal % criminalPoints.Length];
                _nextCriminal++;
                return t != null ? t.position : Vector3.up;
            }
        }
    }
}
