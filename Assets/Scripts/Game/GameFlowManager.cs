using System;
using System.Collections.Generic;
using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // the match conductor: the single server-authoritative owner of GamePhase. it flips the phase on a
    // timer (or early win condition, later) and lets every other system react; it holds no gameplay logic
    // of its own. put one of these on a scene NetworkObject (like RoleRegistry), not on the player prefab.
    //
    // authority: only the server writes CurrentPhase/PhaseEndTime. clients never request a phase directly,
    // they send intents (e.g. "criminal reached exit") and the server decides. this matches the locked
    // server-authoritative model and keeps a future headless-server swap clean (this class is pure server
    // logic; subsystems read a replicated enum).
    //
    // timer sync: the server stamps PhaseEndTime = ServerTime + duration once on entering a phase; clients
    // derive the countdown as (PhaseEndTime - ServerTime.Time). no per-tick timer replication, and it
    // self-corrects against latency.
    public class GameFlowManager : NetworkBehaviour
    {
        public static GameFlowManager Instance { get; private set; }

        [Header("Phase durations (seconds); Lobby waits for the host's Start instead")]
        [SerializeField]
        private float roleAssignDuration = 2f;

        [SerializeField]
        private float sketchDuration = 20f;

        [SerializeField]
        private float sketchRevealDuration = 12f;

        [SerializeField]
        private float huntDuration = 150f;

        [SerializeField]
        private float resolutionDuration = 6f;

        // the one source of truth every system reads. server-write; replicated to all.
        public readonly NetworkVariable<GamePhase> CurrentPhase =
            new NetworkVariable<GamePhase>(GamePhase.Lobby, writePerm: NetworkVariableWritePermission.Server);

        // ServerTime at which the current (timed) phase ends. Lobby uses 0 (it isn't timed, the host
        // ends it with StartMatch). clients compute their countdown from this; they never tick a timer.
        public readonly NetworkVariable<double> PhaseEndTime =
            new NetworkVariable<double>(0d, writePerm: NetworkVariableWritePermission.Server);

        // fires on every machine whenever the phase changes (new phase passed). subsystems (gate,
        // spawn-area walls, scoring, cameras) subscribe here to react instead of polling.
        public event Action<GamePhase> PhaseChanged;

        // true while player input should be hard-frozen (the reporter cutscene). sketch-phase
        // containment is physical (invisible walls), not an input freeze, so only SketchReveal freezes.
        // read by NetworkPlayerController to zero the owner's input for the frame.
        public bool InputFrozen => CurrentPhase.Value == GamePhase.SketchReveal;

        // true during the main gameplay phase; gates movement-relevant systems and firing
        public bool IsHunt => CurrentPhase.Value == GamePhase.Hunt;

        // which round we're on, used to rotate role assignment so a given player cycles through
        // witness/sniper/criminal across a session (GDD: "rotate teams and roles, play again")
        private int _round;

        private void Awake() => Instance = this;

        public override void OnNetworkSpawn()
        {
            CurrentPhase.OnValueChanged += OnPhaseChanged;
            // emit the initial phase so late-subscribers/clients sync their reactors to the current state
            PhaseChanged?.Invoke(CurrentPhase.Value);
        }

        public override void OnNetworkDespawn()
        {
            CurrentPhase.OnValueChanged -= OnPhaseChanged;
        }

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // re-broadcast the NetworkVariable change as the friendlier C# event (fires on every machine)
        private void OnPhaseChanged(GamePhase previous, GamePhase current) => PhaseChanged?.Invoke(current);

        // host-only gate out of Lobby. wired to the host's "Start" button. a client calling this is
        // ignored (RPC is SendTo.Server, and we re-check we're actually in Lobby). begins the loop.
        [Rpc(SendTo.Server)]
        public void StartMatchRpc()
        {
            if (CurrentPhase.Value != GamePhase.Lobby)
                return;
            ScoreManager.Instance?.ResetMatch(); // fresh board for the new match.
            RoleRegistry.Instance?.ResetTeams(); // re-split fixed teams for the new match.
            _round = 0; // restart round rotation.
            EnterPhase(GamePhase.RoleAssign);
        }

        private void Update()
        {
            // only the server advances the state machine; everyone else just reacts to the replicated
            // phase. Lobby has no timer (PhaseEndTime 0), it's ended by StartMatchRpc, not the clock.
            if (!IsServer || CurrentPhase.Value == GamePhase.Lobby)
                return;

            if (NetworkManager.ServerTime.Time >= PhaseEndTime.Value)
                EnterPhase(NextPhase(CurrentPhase.Value));
        }

        // server-only. end Hunt early once no criminals are left in play; every one has been eliminated or
        // has escaped (GDD Resolution: "round ends when all criminals are out, eliminated, or time expires").
        // a no-op outside Hunt or while a live criminal remains. call after any event that removes a criminal
        // (a confirmed kill, an exit escape); the normal timer still ends Hunt if criminals are still around.
        public void EndHuntIfNoCriminalsLeft()
        {
            if (!IsServer || CurrentPhase.Value != GamePhase.Hunt)
                return;
            if (AnyCriminalStillInPlay())
                return;
            EnterPhase(GamePhase.Resolution);
        }

        // server-side truth: is any criminal still alive (not killed, not escaped)? escapees are SetAlive(false)
        // just like the dead, so both drop out of this scan and the last one leaving ends the round.
        private bool AnyCriminalStillInPlay()
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                if (kv.Value.TryGetComponent(out NetworkPlayerController pc)
                    && pc.Role.Value == PlayerRole.Criminal && pc.IsAlive.Value)
                    return true;
            return false;
        }

        // the linear progression. Resolution loops back to RoleAssign (with a rotated round) rather than
        // returning to Lobby, so the match keeps cycling rounds until we add a match-end condition.
        private static GamePhase NextPhase(GamePhase phase) => phase switch
        {
            GamePhase.RoleAssign => GamePhase.Sketch,
            GamePhase.Sketch => GamePhase.SketchReveal,
            GamePhase.SketchReveal => GamePhase.Hunt,
            GamePhase.Hunt => GamePhase.Resolution,
            GamePhase.Resolution => GamePhase.RoleAssign,
            _ => GamePhase.RoleAssign,
        };

        // server-only. set the phase, stamp its end time, and run its one-shot entry action.
        private void EnterPhase(GamePhase phase)
        {
            CurrentPhase.Value = phase;
            PhaseEndTime.Value = NetworkManager.ServerTime.Time + DurationOf(phase);

            switch (phase)
            {
                case GamePhase.RoleAssign:
                    // rotate + assign roles and teleport everyone to their spawn area for this round
                    AssignRolesForRound();
                    break;
                case GamePhase.Resolution:
                    // Hunt just ended: award every criminal who's still alive their survival points. kills
                    // were already banked as they happened (NetworkShooter). then advance the round counter
                    // so the next RoleAssign rotates roles.
                    ScoreManager.Instance?.AwardSurvivors();
                    ScoreManager.Instance?.DecideRoundWinner(); // publish the team verdict for the scoreboard
                    _round++;
                    break;
            }
        }

        private float DurationOf(GamePhase phase) => phase switch
        {
            GamePhase.RoleAssign => roleAssignDuration,
            GamePhase.Sketch => sketchDuration,
            GamePhase.SketchReveal => sketchRevealDuration,
            GamePhase.Hunt => huntDuration,
            GamePhase.Resolution => resolutionDuration,
            _ => 0f, // Lobby: untimed
        };

        // server-only. snapshot the connected clients, hand them to RoleRegistry to assign+rotate roles
        // for this round; RoleRegistry re-teleports each player to their new role's spawn point.
        private void AssignRolesForRound()
        {
            if (RoleRegistry.Instance == null)
                return;

            var clients = new List<ulong>(NetworkManager.ConnectedClientsIds);
            RoleRegistry.Instance.AssignRolesForRound(clients, _round);
        }
    }
}
