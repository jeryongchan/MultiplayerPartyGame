using UnityEngine;

namespace FriendSlop.Player
{
    // owns the two sets of spawn points (sniper rooftop, criminal street) and hands them out round-robin so
    // players never stack. server-only logic: only the server calls GetSpawnPoint (in OnNetworkSpawn); clients
    // just teleport to wherever the server told them via authoritative state.
    public class SpawnPointManager : MonoBehaviour
    {
        public static SpawnPointManager Instance { get; private set; }

        [SerializeField]
        private Transform[] sniperPoints = new Transform[5]; // rooftop; place 5 child transforms.

        [SerializeField]
        private Transform[] criminalPoints = new Transform[5]; // street level; place 5.

        private int _nextSniper;
        private int _nextCriminal;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // next spawn position for this role, cycling the 5 slots. falls back to origin+up if a slot is unassigned.
        public Vector3 GetSpawnPoint(PlayerRole role)
        {
            // witness spawns with the hunters on the rooftop (sniper + sketch duty for now).
            if (role.IsHunter())
            {
                var t = sniperPoints[_nextSniper++ % sniperPoints.Length];
                return t != null ? t.position : Vector3.up;
            }

            var c = criminalPoints[_nextCriminal++ % criminalPoints.Length];
            return c != null ? c.position : Vector3.up;
        }
    }
}
