using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative source of truth for "which role does each connected client play." roles enter
    // through exactly one write path, SetRoleRpc, no matter who calls it. today that caller is a debug
    // button; later it's the matchmaking/lobby system. same door.
    //
    // put this on a single scene NetworkObject (not the player prefab, it's session-global, one per
    // match). server-only state: clients never read this dictionary, they just get teleported to the
    // spawn the server picked from it (via NetworkPlayerController.OnNetworkSpawn).
    //
    // current flow (approach B, "re-spawn on role change", debug-friendly): client's player object spawns
    // and defaults to Sniper (registry empty for that client); client picks a role, calling SetRoleRpc,
    // and the server records clientId to role; server re-teleports that client's player to the new role's
    // spawn point. you can flip a player's role live to test both spawn sets. the visible "pop" to the new
    // spot is acceptable for a debug tool.
    //
    // future direction (approach A, "pick before spawn", the real lobby): role is chosen in a pre-game
    // screen before the player object exists, so there is no pop, the player simply spawns at the right
    // place the first time. to get there: gate player-object spawn behind connection approval (or a
    // "ready"/start-match step) so the server already holds the client's role in this registry before it
    // spawns their NetworkObject; the lobby UI calls the same SetRoleRpc, only the timing and the caller
    // change, not the data path. OnNetworkSpawn already reads GetRole(), so once the role is registered
    // pre-spawn it just works with no re-teleport. because every role write already funnels through
    // SetRoleRpc and every spawn already reads GetRole, swapping the debug button for a lobby is additive,
    // this class doesn't change.
    public class RoleRegistry : NetworkBehaviour
    {
        public static RoleRegistry Instance { get; private set; }

        // server-only. clients never touch this, they're told where to spawn via authoritative state.
        private readonly Dictionary<ulong, PlayerRole> _roles = new();

        private void Awake()
        {
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // the single write path. a client calls this to claim a role; the server stamps it against the
        // caller's clientId. using RpcParams to read the sender server-side means a client can only set
        // its own role, it can't spoof another client's. on change, re-spawn that player (approach B).
        [Rpc(SendTo.Server)]
        public void SetRoleRpc(PlayerRole role, RpcParams rpcParams = default)
        {
            var clientId = rpcParams.Receive.SenderClientId;
            _roles[clientId] = role;

            // approach B: re-teleport the already-spawned player to the new role's spawn point
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)
                && client.PlayerObject != null
                && client.PlayerObject.TryGetComponent(out NetworkPlayerController controller))
            {
                controller.RespawnForRole(role);
            }
        }

        // server-only lookup used by OnNetworkSpawn. defaults to Sniper for an unregistered client so a
        // player that spawns before picking still lands on a valid spot (approach B's step 1).
        public PlayerRole GetRole(ulong clientId)
        {
            return _roles.TryGetValue(clientId, out var role) ? role : PlayerRole.Sniper;
        }
    }
}
