using FriendSlop.Game;
using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Sketch
{
    // shows/hides a sketch UI element based on the current GamePhase and (optionally) the local player's
    // role. drives the canvas and the reveal display: Canvas is active during Sketch, witness only (only
    // the witness draws); Display is active during SketchReveal, everyone (all players see the reveal).
    //
    // local and reactive: it reads the replicated phase and the local player's replicated Role, so it
    // needs no networking of its own. toggles child content via SetActive so the drawing/display
    // components stop running when hidden.
    //
    // setup: put this component on a parent wrapper that stays active, and point content at the child
    // (the SketchCanvas / SketchDisplay object). do not point content at this same GameObject, disabling
    // itself would stop the gate from ever re-enabling it.
    public class SketchUIGate : MonoBehaviour
    {
        // the UI object to show/hide (e.g. the SketchCanvas or SketchDisplay GameObject)
        [SerializeField]
        private GameObject content;

        // phases during which the content may be shown
        [SerializeField]
        private GamePhase[] activeDuring;

        // if true, only the local witness sees this (the drawing canvas). if false, everyone (the reveal).
        [SerializeField]
        private bool witnessOnly;

        private void OnEnable()
        {
            TrySubscribe();
            Refresh();
        }

        private void OnDisable()
        {
            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.PhaseChanged -= OnPhaseChanged;
            if (_watchedController != null)
            {
                _watchedController.Role.OnValueChanged -= OnRoleChanged;
                _watchedController = null;
            }
        }

        private void Start()
        {
            TrySubscribe();
            HookLocalRole();
            Refresh();
        }

        private void TrySubscribe()
        {
            if (GameFlowManager.Instance == null)
                return;
            GameFlowManager.Instance.PhaseChanged -= OnPhaseChanged; // idempotent
            GameFlowManager.Instance.PhaseChanged += OnPhaseChanged;
        }

        private void OnPhaseChanged(GamePhase newPhase)
        {
            // auto-submit the witness's canvas the moment the Sketch phase ends (we're the witness-only
            // gate and the canvas is about to be hidden below). done from the gate, not the canvas,
            // because the canvas GameObject gets SetActive(false) in Refresh() and a disabled component
            // wouldn't receive the phase event itself. leaving Sketch means entering SketchReveal.
            if (witnessOnly && newPhase == GamePhase.SketchReveal && content != null && content.activeSelf)
            {
                if (content.TryGetComponent(out SketchCanvas canvas))
                    canvas.Submit();
            }

            // role is assigned during RoleAssign (a phase change); make sure we're watching the local
            // player's Role so a witness-only gate flips correctly even if the player spawned after us.
            HookLocalRole();
            Refresh();
        }

        private NetworkPlayerController _watchedController;

        // subscribe to the local player's Role changes (once we can find it), so witnessOnly gates update
        // the instant the server assigns/rotates a role, not only on the next phase tick.
        private void HookLocalRole()
        {
            if (!witnessOnly || _watchedController != null)
                return;
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
                return;
            if (nm.LocalClient.PlayerObject.TryGetComponent(out NetworkPlayerController c))
            {
                _watchedController = c;
                _watchedController.Role.OnValueChanged += OnRoleChanged;
            }
        }

        private void OnRoleChanged(PlayerRole _, PlayerRole __) => Refresh();

        private void Refresh()
        {
            if (content == null)
                return;

            bool phaseOk = GameFlowManager.Instance != null
                && System.Array.IndexOf(activeDuring, GameFlowManager.Instance.CurrentPhase.Value) >= 0;

            bool roleOk = !witnessOnly || LocalPlayerIsWitness();

            content.SetActive(phaseOk && roleOk);
        }

        // the local player's replicated role. on each machine the owner's player object carries the Role
        // NetworkVariable the server assigned; we read it to decide if this client should see the canvas.
        private static bool LocalPlayerIsWitness()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
                return false;
            return nm.LocalClient.PlayerObject.TryGetComponent(out NetworkPlayerController controller)
                && controller.Role.Value == PlayerRole.Witness;
        }
    }
}
