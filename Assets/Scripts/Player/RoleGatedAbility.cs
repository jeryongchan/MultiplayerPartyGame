using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // base for a player ability the owner triggers but the server authorizes: the shooter, the criminal's
    // melee. composition over inheritance: an ability is a component gated on the player's replicated
    // PlayerRole (data), not a subclass of the player. the base folds together the parts every
    // such ability shares: caching the sibling NetworkPlayerController, the role/alive/phase
    // gate (readable on the owner and re-checkable on the server so it's authoritative), and an owner-side
    // cooldown. subclasses supply their role and their actual behaviour (the raycast, the punch, the RPCs).
    public abstract class RoleGatedAbility : NetworkBehaviour
    {
        // sibling controller on the same player object; holds the replicated Role / IsAlive we gate on
        protected NetworkPlayerController Controller { get; private set; }

        // Time.time of the next allowed use. owner-only state (the server doesn't enforce fire rate).
        private float _nextUseTime;

        protected virtual void Awake() => Controller = GetComponent<NetworkPlayerController>();

        // true when this ability is currently usable: the player's role allows it, they're alive, and it's the
        // Hunt phase. read on the owner to gate input cheaply, and re-checked on the server in the ability's RPC
        // so a modified client can't bypass it, that server re-check is what makes the gate authoritative.
        protected bool CanUse =>
            Controller != null
            && RolePredicate(Controller.Role.Value)
            && Controller.Health.IsAlive.Value
            && (GameFlowManager.Instance == null || GameFlowManager.Instance.IsHunt);

        // which roles may use this ability (e.g. role.IsHunter() or role == Criminal)
        protected abstract bool RolePredicate(PlayerRole role);

        // owner-side cooldown gate: true and starts the cooldown if cooldown seconds have
        // passed since the last successful use; false (no state change) while still cooling down. call it at the
        // point the ability actually fires, e.g. if (!TryConsumeCooldown(fireCooldown)) return;
        protected bool TryConsumeCooldown(float cooldown)
        {
            if (Time.time < _nextUseTime)
                return false;
            _nextUseTime = Time.time + cooldown;
            return true;
        }
    }
}
