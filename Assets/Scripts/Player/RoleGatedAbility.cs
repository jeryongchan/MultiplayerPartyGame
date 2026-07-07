using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // base for a player ability the owner triggers but the server authorizes (the shooter, the criminal's
    // melee). an ability is a component gated on the player's replicated PlayerRole, not a subclass of the
    // player. the base folds together what every such ability shares: caching the sibling controller, the
    // role/alive/phase gate (readable on the owner and re-checkable on the server so it's authoritative), and
    // an owner-side cooldown. subclasses supply their role and their actual behaviour.
    public abstract class RoleGatedAbility : NetworkBehaviour
    {
        // sibling controller on the same player object; holds the replicated Role / IsAlive we gate on.
        protected NetworkPlayerController Controller { get; private set; }

        private float _nextUseTime; // Time.time of the next allowed use. owner-only (server doesn't gate fire rate).

        protected virtual void Awake() => Controller = GetComponent<NetworkPlayerController>();

        // true when this ability is usable: role allows it, alive, and it's Hunt. gates input cheaply on the
        // owner and is re-checked on the server in the ability's RPC, which is what makes the gate authoritative.
        protected bool CanUse =>
            Controller != null
            && RolePredicate(Controller.Role.Value)
            && Controller.Health.IsAlive.Value
            && (GameFlowManager.Instance == null || GameFlowManager.Instance.IsHunt);

        // which roles may use this (e.g. role.IsHunter() or role == Criminal).
        protected abstract bool RolePredicate(PlayerRole role);

        // owner-side cooldown gate: true (and starts the cooldown) once `cooldown` seconds passed since the
        // last use; false while still cooling down. call at the point the ability fires.
        protected bool TryConsumeCooldown(float cooldown)
        {
            if (Time.time < _nextUseTime)
                return false;
            _nextUseTime = Time.time + cooldown;
            return true;
        }
    }
}
