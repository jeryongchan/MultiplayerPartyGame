using System.Collections.Generic;
using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // server-authoritative, per-player scoreboard (CS-style: individual stats are tallied, but who wins
    // is a team question layered on later). put one on a scene NetworkObject (the flow manager /
    // role registry object).
    //
    // scoring: sniper eliminates a criminal gives +pointsPerKill; criminal alive at Hunt's end gives
    // +survivalPoints; sniper hits an NPC gives npcPenalty (a small negative). scores are a running match
    // total (accumulate every round); ResetMatch zeroes them for a fresh match. the board is a
    // replicated NetworkList so every client renders the same numbers.
    public class ScoreManager : NetworkBehaviour
    {
        public static ScoreManager Instance { get; private set; }

        // points a sniper earns per criminal eliminated
        [SerializeField]
        private float pointsPerKill = 1f;

        // points a criminal earns for surviving to the end of Hunt
        [SerializeField]
        private float survivalPoints = 1f;

        // points a sniper loses for hitting an innocent NPC (negative)
        [SerializeField]
        private float npcPenalty = -0.5f;

        // replicated per-player board. server-write; clients read it to render the scoreboard.
        public readonly NetworkList<PlayerScore> Scores = new NetworkList<PlayerScore>(
            writePerm: NetworkVariableWritePermission.Server);

        // which team won the last resolved round (or None before the first Resolution). server-write,
        // replicated so every client's scoreboard shows the same verdict. team is by role this round:
        // hunters (sniper+witness) vs criminals, which equals the fixed teams since membership never crosses.
        public readonly NetworkVariable<RoundOutcome> LastRoundWinner =
            new NetworkVariable<RoundOutcome>(RoundOutcome.None, writePerm: NetworkVariableWritePermission.Server);

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // server-only score events

        // a sniper eliminated a criminal: +pointsPerKill to that sniper
        public void RecordCriminalKill(ulong sniperClientId)
        {
            if (!IsServer)
                return;
            Mutate(sniperClientId, s =>
            {
                s.Kills += 1;
                s.Total += pointsPerKill;
                return s;
            });
        }

        // a sniper hit an innocent NPC: apply the penalty
        public void RecordNpcHit(ulong sniperClientId)
        {
            if (!IsServer)
                return;
            Mutate(sniperClientId, s =>
            {
                s.Total += npcPenalty;
                return s;
            });
        }

        // a criminal reached the Exit and escaped the street (GDD Resolution: "criminal reaches exit
        // gives +1 criminal team"). scored the same as surviving to the end, the criminal got away, so it
        // reuses the survival counter/points. the caller removes the escapee (SetAlive(false)) right after,
        // which also stops AwardSurvivors from awarding them a second time at Hunt's end.
        public void RecordCriminalEscape(ulong criminalClientId)
        {
            if (!IsServer)
                return;
            Mutate(criminalClientId, s =>
            {
                s.Survivals += 1;
                s.Total += survivalPoints;
                return s;
            });
        }

        // end-of-Hunt survival award. called on entering Resolution: every criminal still alive gets
        // +survivalPoints. reads live players so it needs no per-round bookkeeping here.
        public void AwardSurvivors()
        {
            if (!IsServer || NetworkManager.Singleton == null)
                return;

            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                if (!kv.Value.TryGetComponent(out NetworkPlayerController pc))
                    continue;
                if (pc.Role.Value != PlayerRole.Criminal || !pc.IsAlive.Value)
                    continue;
                Mutate(pc.OwnerClientId, s =>
                {
                    s.Survivals += 1;
                    s.Total += survivalPoints;
                    return s;
                });
            }
        }

        // server-only. decide which team won the round and publish it. sums each side's points by the
        // player's role this round (hunter = sniper/witness, criminal = criminal) and picks the higher;
        // equal totals are a Draw. call on entering Resolution, after AwardSurvivors.
        public void DecideRoundWinner()
        {
            if (!IsServer)
                return;

            float hunter = 0f, criminal = 0f;
            foreach (var s in RolesByClient())
            {
                float pts = TotalFor(s.Key);
                if (s.Value.IsHunter())
                    hunter += pts;
                else if (s.Value == PlayerRole.Criminal)
                    criminal += pts;
            }

            LastRoundWinner.Value = hunter > criminal ? RoundOutcome.Hunters
                : criminal > hunter ? RoundOutcome.Criminals
                : RoundOutcome.Draw;
        }

        // server-only. wipe the board and verdict for a brand-new match.
        public void ResetMatch()
        {
            if (!IsServer)
                return;
            Scores.Clear();
            LastRoundWinner.Value = RoundOutcome.None;
        }

        // this match's per-player point total (0 if the player has no row yet)
        private float TotalFor(ulong clientId)
        {
            for (int i = 0; i < Scores.Count; i++)
                if (Scores[i].ClientId == clientId)
                    return Scores[i].Total;
            return 0f;
        }

        // live clientId to current role, read off the spawned player objects (server-side truth)
        private IEnumerable<KeyValuePair<ulong, PlayerRole>> RolesByClient()
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                if (kv.Value.TryGetComponent(out NetworkPlayerController pc))
                    yield return new KeyValuePair<ulong, PlayerRole>(pc.OwnerClientId, pc.Role.Value);
        }

        // find (or create) a player's row and apply a change, writing it back into the NetworkList
        private void Mutate(ulong clientId, System.Func<PlayerScore, PlayerScore> change)
        {
            for (int i = 0; i < Scores.Count; i++)
            {
                if (Scores[i].ClientId == clientId)
                {
                    Scores[i] = change(Scores[i]);
                    return;
                }
            }
            // no row yet, start one at zero and apply the change
            Scores.Add(change(new PlayerScore { ClientId = clientId }));
        }
    }
}
