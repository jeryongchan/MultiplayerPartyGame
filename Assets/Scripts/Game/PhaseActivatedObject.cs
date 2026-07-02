using UnityEngine;

namespace FriendSlop.Game
{
    // a pure phase-reactor: enables a set of GameObjects only during the phases you tick, disabling them
    // otherwise. drop it on any scene object and let it drive things like the spawn-area invisible walls
    // (active until Hunt) or the exit gate (a "gate closed" object that switches off on Hunt).
    //
    // this is the "conductor + reactors" pattern in action: GameFlowManager flips the phase, this
    // component reacts, and neither knows the other's internals beyond the phase enum.
    //
    // local and cosmetic-ish: it toggles GameObjects on every machine off the replicated phase, so no
    // networking of its own. use it for scene geometry/visuals whose state is fully implied by the phase.
    public class PhaseActivatedObject : MonoBehaviour
    {
        // objects that should be active only during the selected phases (e.g. spawn-area walls)
        [SerializeField]
        private GameObject[] targets;

        // phases during which the targets are active. in every other phase they're disabled.
        [SerializeField]
        private GamePhase[] activeDuring;

        private void OnEnable()
        {
            // GameFlowManager may spawn after this object; retry-subscribe is handled in Update-free
            // fashion by hooking as soon as an instance exists. simplest robust approach: subscribe now
            // if present, and also apply the current phase immediately.
            TrySubscribe();
        }

        private void OnDisable()
        {
            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.PhaseChanged -= Apply;
        }

        private void Start() => TrySubscribe();

        private void TrySubscribe()
        {
            if (GameFlowManager.Instance == null)
                return;
            // idempotent: unsubscribe first so we never double-register across OnEnable/Start
            GameFlowManager.Instance.PhaseChanged -= Apply;
            GameFlowManager.Instance.PhaseChanged += Apply;
            Apply(GameFlowManager.Instance.CurrentPhase.Value);
        }

        private void Apply(GamePhase phase)
        {
            bool active = System.Array.IndexOf(activeDuring, phase) >= 0;
            foreach (var go in targets)
                if (go != null)
                    go.SetActive(active);
        }
    }
}
