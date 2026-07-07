using UnityEngine;

namespace FriendSlop.Game
{
    // pure phase-reactor: enables a set of GameObjects only during the phases you tick, disabling them
    // otherwise. drop on any scene object to drive things like spawn-area walls (active until Hunt) or
    // the exit gate (switches off on Hunt). local + cosmetic-ish, no networking of its own: it toggles
    // GameObjects on every machine off the replicated phase.
    public class PhaseActivatedObject : MonoBehaviour
    {
        [SerializeField]
        private GameObject[] targets; // objects active only during the selected phases

        [SerializeField]
        private GamePhase[] activeDuring; // in every other phase they're disabled

        private void OnEnable() => TrySubscribe();

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
            // idempotent: unsubscribe first so we never double-register across OnEnable/Start.
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
