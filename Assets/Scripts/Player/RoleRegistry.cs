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

        // fixed teams for the match, frozen once at the first RoleAssign (BuildTeams). membership does not
        // change round-to-round; only which hunter is the Witness rotates within _hunters. cleared by
        // ResetTeams at match start so a new match re-splits. (minimal: split is a simple size rule now;
        // a real lobby will populate these instead.)
        private readonly List<ulong> _hunters = new();
        private readonly List<ulong> _criminals = new();

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

            // approach B: re-teleport the already-spawned player to the new role's spawn point. debug
            // re-spawn (not a round advance), so round 0 for appearance; the match flow re-rolls per round.
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)
                && client.PlayerObject != null
                && client.PlayerObject.TryGetComponent(out NetworkPlayerController controller))
            {
                controller.RespawnForRole(role, 0);
            }
        }

        // server-only lookup used by OnNetworkSpawn. defaults to Sniper for an unregistered client so a
        // player that spawns before picking still lands on a valid spot (approach B's step 1).
        public PlayerRole GetRole(ulong clientId)
        {
            return _roles.TryGetValue(clientId, out var role) ? role : PlayerRole.Sniper;
        }

        // server-only. the match's role-assignment door (called by GameFlowManager each RoleAssign phase).
        // fixed teams: membership (hunter vs criminal) is frozen once at the first assignment and never
        // crosses. each round only rotates which hunter is the Witness; the rest of the hunters are
        // Snipers, and criminals stay criminals. roundOffset picks the Witness so the spotter role cycles
        // among the hunters across rounds.
        //
        // composition split unchanged: this owns "who plays what"; SetRoleRpc / RespawnForRole applies it.
        public void AssignRolesForRound(IReadOnlyList<ulong> clients, int roundOffset)
        {
            if (clients.Count == 0)
                return;

            // freeze the teams the first time (or after a match reset). a real lobby will fill these instead.
            if (_hunters.Count == 0 && _criminals.Count == 0)
                BuildTeams(clients);

            // rotate the Witness within the hunter team; every other hunter is a Sniper
            int witnessIdx = _hunters.Count > 0 ? roundOffset % _hunters.Count : 0;
            for (int i = 0; i < _hunters.Count; i++)
                ApplyRole(_hunters[i], i == witnessIdx ? PlayerRole.Witness : PlayerRole.Sniper, roundOffset);

            // criminals keep their team, but re-roll their look for the new round (pass the round)
            foreach (var c in _criminals)
                ApplyRole(c, PlayerRole.Criminal, roundOffset);
        }

        // minimal one-time split: order by id, first (1 + (N-1)/2) become hunters, the rest criminals.
        // (same size rule as before, but frozen as fixed teams instead of re-rolled each round.)
        private void BuildTeams(IReadOnlyList<ulong> clients)
        {
            var ordered = new List<ulong>(clients);
            ordered.Sort();

            int n = ordered.Count;
            int hunterCount = 1 + (n - 1) / 2; // 1 witness slot + (N-1)/2 snipers

            _hunters.Clear();
            _criminals.Clear();
            for (int i = 0; i < n; i++)
                (i < hunterCount ? _hunters : _criminals).Add(ordered[i]);
        }

        // server-only. forget the fixed teams so the next assignment re-splits (call at match start).
        public void ResetTeams()
        {
            _hunters.Clear();
            _criminals.Clear();
        }

        // is this client on the hunter team this match? (read after teams are built)
        public bool IsHunterTeam(ulong clientId) => _hunters.Contains(clientId);

        // record the role and apply it through the same door SetRoleRpc uses (re-teleport the live player).
        // round threads through to appearance so each round re-rolls a fresh look.
        private void ApplyRole(ulong clientId, PlayerRole role, int round)
        {
            _roles[clientId] = role;
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)
                && client.PlayerObject != null
                && client.PlayerObject.TryGetComponent(out NetworkPlayerController controller))
            {
                controller.RespawnForRole(role, round);
            }
        }
    }
}
