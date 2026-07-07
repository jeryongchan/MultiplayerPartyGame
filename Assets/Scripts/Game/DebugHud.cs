using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // temp IMGUI debug HUD: phase/countdown top-center, scoreboard below it during Resolution (or always,
    // for debugging). Replace with a proper Canvas once the UI pass happens.
    public class DebugHud : MonoBehaviour
    {
        [SerializeField]
        private bool alwaysShowScoreboard; // if false, scoreboard only draws during Resolution

        private GUIStyle _titleStyle;
        private GUIStyle _rowStyle;

        private void OnGUI()
        {
            DrawPhase();
            DrawScoreboard();
        }

        private void DrawPhase()
        {
            var flow = GameFlowManager.Instance;
            if (flow == null || !flow.IsSpawned)
                return;

            EnsureStyles();

            GamePhase phase = flow.CurrentPhase.Value;
            string text =
                phase == GamePhase.Lobby
                    ? "Phase: Lobby"
                    : $"Phase: {phase}   {Remaining(flow):0}s";

            const float w = 360f,
                h = 30f;
            float x = (Screen.width - w) * 0.5f;
            GUI.Box(new Rect(x, 8f, w, h), GUIContent.none);
            GUI.Label(new Rect(x, 8f, w, h), text, _titleStyle);
        }

        // seconds left in the current timed phase, from the shared server clock.
        private static float Remaining(GameFlowManager flow)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
                return 0f;
            return Mathf.Max(0f, (float)(flow.PhaseEndTime.Value - nm.ServerTime.Time));
        }

        private void DrawScoreboard()
        {
            if (ScoreManager.Instance == null)
                return;
            if (
                !alwaysShowScoreboard
                && (
                    GameFlowManager.Instance == null
                    || GameFlowManager.Instance.CurrentPhase.Value != GamePhase.Resolution
                )
            )
                return;

            var scores = ScoreManager.Instance.Scores;
            const float w = 360f;
            float h = 44f + scores.Count * 24f;
            float x = (Screen.width - w) * 0.5f;
            float y = 44f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            GUI.Label(new Rect(x + 12, y + 8, w - 24, 24), WinnerText(), _titleStyle);

            float rowY = y + 40f;
            foreach (var s in scores)
            {
                string name = ResolveName(s.ClientId);
                string line =
                    $"{name}   kills {s.Kills}   survived {s.Survivals}   -   {s.Total:0.#} pts";
                GUI.Label(new Rect(x + 12, rowY, w - 24, 22), line, _rowStyle);
                rowY += 24f;
            }
        }

        // headline: the team that won this round, or a neutral title before any verdict.
        private static string WinnerText() =>
            ScoreManager.Instance.LastRoundWinner.Value switch
            {
                RoundOutcome.Hunters => "HUNTERS WIN",
                RoundOutcome.Criminals => "CRIMINALS WIN",
                RoundOutcome.Draw => "DRAW",
                _ => "ROUND RESULTS",
            };

        // best-effort display name: mark the local player, else show the client id.
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
                _rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        }
    }
}
