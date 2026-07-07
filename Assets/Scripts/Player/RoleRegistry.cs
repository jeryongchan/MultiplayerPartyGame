using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative source of truth for "which role does each client play." roles enter through exactly
    // one write path (SetRoleRpc) no matter who calls it: today a debug key, later the lobby, same door. put it
    // on a single scene NetworkObject (session-global, one per match). server-only state: clients never read
    // this, they're teleported to the spawn the server picked.
    //
    // current flow (approach B, "re-spawn on role change", debug-friendly): the player spawns defaulting to
    // Sniper, then a role pick re-teleports it. the visible pop is fine for a debug tool.
    //
    // future (approach A, the real lobby): role is chosen pre-game before the player object exists, so there's
    // no pop, the player just spawns in the right place. that only needs a spawn gate so the role is registered
    // before OnNetworkSpawn (which already reads GetRole). the lobby calls the same SetRoleRpc, so the swap is
    // additive and this class doesn't change.
    public class RoleRegistry : NetworkBehaviour
    {
        public static RoleRegistry Instance { get; private set; }

        // server-only. clients never touch this; they're told where to spawn via authoritative state.
        private readonly Dictionary<ulong, PlayerRole> _roles = new();

        // fixed teams, frozen once at the first RoleAssign (BuildTeams). membership doesn't change round-to-
        // round; only which hunter is the Witness rotates. cleared by ResetTeams at match start.
        private readonly List<ulong> _hunters = new();
        private readonly List<ulong> _criminals = new();

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // the single write path. a client claims a role; the server stamps it against the caller's clientId
        // (read from RpcParams server-side, so a client can only set its own role). on change, re-spawn (B).
        [Rpc(SendTo.Server)]
        public void SetRoleRpc(PlayerRole role, RpcParams rpcParams = default)
        {
            var clientId = rpcParams.Receive.SenderClientId;
            _roles[clientId] = role;

            // approach B: re-teleport the live player to the new spawn. debug re-spawn (not a round advance),
            // so round 0 for appearance; the match flow re-rolls per round.
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)
                && client.PlayerObject != null
                && client.PlayerObject.TryGetComponent(out NetworkPlayerController controller))
            {
                controller.RespawnForRole(role, 0);
            }
        }

        // server-only lookup used by OnNetworkSpawn. defaults to Sniper for an unregistered client so a player
        // that spawns before picking still lands somewhere valid.
        public PlayerRole GetRole(ulong clientId) =>
            _roles.TryGetValue(clientId, out var role) ? role : PlayerRole.Sniper;

        // server-only. the match's role-assignment door (GameFlowManager calls it each RoleAssign). fixed
        // teams: hunter-vs-criminal membership is frozen at the first assignment and never crosses; each round
        // only rotates which hunter is the Witness (roundOffset picks it). this owns "who plays what";
        // SetRoleRpc / RespawnForRole applies it.
        public void AssignRolesForRound(IReadOnlyList<ulong> clients, int roundOffset)
        {
            if (clients.Count == 0)
                return;

            // freeze the teams the first time (or after a match reset). a real lobby will fill these instead.
            if (_hunters.Count == 0 && _criminals.Count == 0)
                BuildTeams(clients);

            // rotate the Witness within the hunter team; every other hunter is a Sniper.
            int witnessIdx = _hunters.Count > 0 ? roundOffset % _hunters.Count : 0;
            for (int i = 0; i < _hunters.Count; i++)
                ApplyRole(_hunters[i], i == witnessIdx ? PlayerRole.Witness : PlayerRole.Sniper, roundOffset);

            // criminals keep their team but re-roll their look for the new round.
            foreach (var c in _criminals)
                ApplyRole(c, PlayerRole.Criminal, roundOffset);
        }

        // one-time split: order by id, first (1 + (N-1)/2) become hunters, the rest criminals.
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

        // is this client on the hunter team this match? (read after teams are built.)
        public bool IsHunterTeam(ulong clientId) => _hunters.Contains(clientId);

        // record the role and apply it through the same door SetRoleRpc uses; round threads to appearance.
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
