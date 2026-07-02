using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // small top-center readout of the current GamePhase and the seconds left in it, so you
    // can see what the match state machine is doing. reads the replicated phase + PhaseEndTime from
    // GameFlowManager and derives the countdown from NetworkManager.ServerTime (the same
    // clock the server stamps against), so it stays in sync without replicating a per-tick timer.
    //
    // drop on any scene object. IMGUI (debug-grade); swap for a Canvas later if it graduates to real UI.
    public class PhaseHud : MonoBehaviour
    {
        private GUIStyle _style;

        private void OnGUI()
        {
            var flow = GameFlowManager.Instance;
            if (flow == null || !flow.IsSpawned)
                return;

            EnsureStyle();

            GamePhase phase = flow.CurrentPhase.Value;
            string text = phase == GamePhase.Lobby
                ? "Phase: Lobby"
                : $"Phase: {phase}   {Remaining(flow):0}s";

            const float w = 360f, h = 30f;
            float x = (Screen.width - w) * 0.5f;
            GUI.Box(new Rect(x, 8f, w, h), GUIContent.none);
            GUI.Label(new Rect(x, 8f, w, h), text, _style);
        }

        // seconds left in the current (timed) phase, from the shared server clock. clamped at 0.
        private static float Remaining(GameFlowManager flow)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
                return 0f;
            return Mathf.Max(0f, (float)(flow.PhaseEndTime.Value - nm.ServerTime.Time));
        }

        private void EnsureStyle()
        {
            if (_style != null)
                return;
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _style.normal.textColor = Color.white;
        }
    }
}
