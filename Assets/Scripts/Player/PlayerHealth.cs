using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative round health. owns the replicated alive/down state and the server-only body-HP
    // count, and hides/shows the mesh + shootable hitbox on every copy when downed. split out of the
    // controller so it stays pure movement netcode.
    public class PlayerHealth : NetworkBehaviour
    {
        [SerializeField]
        private Transform visual; // mesh root, hidden when downed so no corpse lingers.

        [SerializeField]
        private Collider hitCollider; // shootable body; hidden with the mesh so a corpse can't be re-shot. optional.

        [SerializeField]
        private int bodyHitsToKill = 3; // body hits to down; a head hit is always an instant kill.

        private int _hp; // remaining body hits, server-only (clients only need the IsAlive flip). refilled on spawn/respawn.

        // alive/down state for the round. server-write, read everywhere. a downed criminal hides its mesh +
        // hitbox on every copy and freezes input on the owner. defaults true so nobody spawns dead.
        public readonly NetworkVariable<bool> IsAlive =
            new NetworkVariable<bool>(true, writePerm: NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                _hp = bodyHitsToKill;

            // apply the current value now (covers late joiners mid-round), then react to changes.
            IsAlive.OnValueChanged += OnAliveChanged;
            ApplyAliveVisual(IsAlive.Value);
        }

        public override void OnNetworkDespawn() => IsAlive.OnValueChanged -= OnAliveChanged;

        // server-only. apply one hit: head downs instantly, body decrements and downs at zero. returns true on
        // the killing blow (so the caller scores the kill once). no-ops if already down.
        public bool TakeHit(HitZone zone)
        {
            if (!IsServer || !IsAlive.Value)
                return false;

            if (zone == HitZone.Head)
            {
                IsAlive.Value = false;
                return true;
            }

            if (--_hp <= 0)
            {
                IsAlive.Value = false;
                return true;
            }
            return false;
        }

        // server-only. mark down or revive; reviving refills body HP. called by ExitZone when a criminal escapes.
        public void SetAlive(bool alive)
        {
            if (!IsServer)
                return;
            IsAlive.Value = alive;
            if (alive)
                _hp = bodyHitsToKill;
        }

        public void Revive() => SetAlive(true); // server-only, for a new round.


        private void OnAliveChanged(bool _, bool alive) => ApplyAliveVisual(alive);

        private void ApplyAliveVisual(bool alive)
        {
            if (visual != null)
                visual.gameObject.SetActive(alive);
            if (hitCollider != null)
                hitCollider.enabled = alive;
        }
    }
}
