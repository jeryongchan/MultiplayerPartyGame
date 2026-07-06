using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative round health for a player. owns the replicated alive/down state and the server-only
    // body-HP count, and hides/shows the mesh + shootable hitbox on every copy when the player is downed. split
    // out of NetworkPlayerController so the controller is pure movement netcode: this component is
    // the single home for "is this player alive, and what happens when they're hit."
    public class PlayerHealth : NetworkBehaviour
    {
        // the visual mesh root, hidden when the player is downed so a corpse doesn't linger. same GameObject the
        // controller render-interpolates; referenced here separately so health stays self-contained.
        [SerializeField]
        private Transform visual;

        // the player's shootable body hitbox (the dedicated CapsuleCollider NetworkShooter raycasts). hidden
        // together with the mesh when the player is downed, so a corpse can't be shot again. optional; if
        // unassigned, only the mesh hides.
        [SerializeField]
        private Collider hitCollider;

        // how many body hits it takes to down this player. a head hit is always an instant kill regardless.
        // server-only tuning; exposed so you can tweak survivability in the Inspector while testing.
        [SerializeField]
        private int bodyHitsToKill = 3;

        // remaining body hits before this player goes down, server-side only (never networked; clients don't
        // need the number, only the resulting IsAlive flip). refilled to bodyHitsToKill on spawn/respawn.
        private int _hp;

        // alive/down state for the round. server-write, read everywhere. a criminal shot during Hunt is
        // set false: its mesh + hitbox hide on every copy (OnValueChanged) and, on the owner, its input is
        // frozen so it spectates in place until the next round. Revive() brings it back. defaults true so
        // nobody spawns dead (and non-round systems that never touch it see a live player).
        public readonly NetworkVariable<bool> IsAlive =
            new NetworkVariable<bool>(true, writePerm: NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                _hp = bodyHitsToKill; // start the round at full body HP

            // down/alive visuals: hide the mesh + hitbox on every copy when a player is downed. apply the
            // current value now (covers late joiners who arrive mid-round) and react to changes after.
            IsAlive.OnValueChanged += OnAliveChanged;
            ApplyAliveVisual(IsAlive.Value);
        }

        public override void OnNetworkDespawn()
        {
            IsAlive.OnValueChanged -= OnAliveChanged;
        }

        // server-only. apply one hit in the given zone. a head hit downs instantly; a body hit decrements
        // HP and only downs at zero. returns true if this hit was the killing blow (so the caller can score
        // the kill exactly once). no-ops if already down. body HP refills on respawn/SetAlive(true).
        public bool TakeHit(HitZone zone)
        {
            if (!IsServer || !IsAlive.Value)
                return false;

            if (zone == HitZone.Head)
            {
                IsAlive.Value = false;
                return true;
            }

            _hp--;
            if (_hp <= 0)
            {
                IsAlive.Value = false;
                return true;
            }
            return false; // survived, a body hit that didn't kill
        }

        // server-only. mark this player down (or revive). down players hide their mesh + hitbox (so they
        // can't be shot again) on every copy and, on the owner, freeze input to spectate in place. called
        // by ExitZone when a criminal escapes.
        public void SetAlive(bool alive)
        {
            if (!IsServer)
                return;
            IsAlive.Value = alive;
            if (alive)
                _hp = bodyHitsToKill; // reviving refills body HP
        }

        // server-only. revive for a new round: alive again with full body HP.
        public void Revive() => SetAlive(true);

        // runs on every copy when IsAlive flips: show/hide the mesh + shootable hitbox
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
