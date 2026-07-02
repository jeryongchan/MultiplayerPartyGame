using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // stub: server-authoritative team scoreboard. wired in but not yet driving win/lose; it exists so
    // NetworkShooter's scoring TODOs and GameFlowManager's Resolution tally have a real home to call
    // into now, rather than being scattered later. put one on a scene NetworkObject (like the flow
    // manager / role registry).
    //
    // GDD scoring (to implement): criminal reaches exit gives +1 criminal; sniper hits criminal gives +1
    // sniper; sniper hits NPC gives -0.5 sniper. round ends when all criminals are out/eliminated or time
    // expires; higher score wins.
    public class ScoreManager : NetworkBehaviour
    {
        public static ScoreManager Instance { get; private set; }

        // replicated team scores so every client can show the scoreboard. server-write. float because the
        // NPC-hit penalty is -0.5.
        public readonly NetworkVariable<float> HunterScore =
            new NetworkVariable<float>(0f, writePerm: NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CriminalScore =
            new NetworkVariable<float>(0f, writePerm: NetworkVariableWritePermission.Server);

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // server-only score events (called from NetworkShooter / exit trigger later)

        public void AddSniperHitCriminal() { if (IsServer) HunterScore.Value += 1f; }

        public void AddSniperHitNpc() { if (IsServer) HunterScore.Value -= 0.5f; }

        public void AddCriminalReachedExit() { if (IsServer) CriminalScore.Value += 1f; }

        // server-only. zero the board for a fresh round (call on RoleAssign entry later).
        public void ResetRound()
        {
            if (!IsServer)
                return;
            HunterScore.Value = 0f;
            CriminalScore.Value = 0f;
        }
    }
}
