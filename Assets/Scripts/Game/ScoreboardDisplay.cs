using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // quick IMGUI scoreboard for early testing: draws ScoreManager's replicated per-player board on
    // screen during the Resolution phase (and optionally always, for debugging). zero setup: drop
    // this on any scene GameObject. replace with a proper Canvas later (the real match-end screen).
    // read-only: it just renders the replicated NetworkList, so it works on every client.
    public class ScoreboardDisplay : MonoBehaviour
    {
        // if true, the board is always visible (debug). if false, only during Resolution.
        [SerializeField]
        private bool alwaysShow;

        private GUIStyle _titleStyle;
        private GUIStyle _rowStyle;

        private void OnGUI()
        {
            if (ScoreManager.Instance == null)
                return;
            if (!alwaysShow &&
                (GameFlowManager.Instance == null || GameFlowManager.Instance.CurrentPhase.Value != GamePhase.Resolution))
                return;

            EnsureStyles();

            var scores = ScoreManager.Instance.Scores;
            const float w = 360f;
            float h = 44f + scores.Count * 24f;
            float x = (Screen.width - w) * 0.5f;
            float y = 80f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            GUI.Label(new Rect(x + 12, y + 8, w - 24, 24), WinnerText(), _titleStyle);

            float rowY = y + 40f;
            foreach (var s in scores)
            {
                string name = ResolveName(s.ClientId);
                string line = $"{name}   kills {s.Kills}   survived {s.Survivals}   -   {s.Total:0.#} pts";
                GUI.Label(new Rect(x + 12, rowY, w - 24, 22), line, _rowStyle);
                rowY += 24f;
            }
        }

        // headline: the team that won this round (falls back to a neutral title before any verdict)
        private static string WinnerText() => ScoreManager.Instance.LastRoundWinner.Value switch
        {
            RoundOutcome.Hunters => "HUNTERS WIN",
            RoundOutcome.Criminals => "CRIMINALS WIN",
            RoundOutcome.Draw => "DRAW",
            _ => "ROUND RESULTS",
        };

        // best-effort display name: mark the local player, else show the client id
        private static string ResolveName(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            bool isLocal = nm != null && nm.LocalClientId == clientId;
            return isLocal ? $"You (#{clientId})" : $"Player #{clientId}";
        }

        private void EnsureStyles()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
            }
            if (_rowStyle == null)
            {
                _rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            }
        }
    }
}
