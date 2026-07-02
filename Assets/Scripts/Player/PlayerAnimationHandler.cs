using UnityEngine;
using FriendSlop.Player;
using FriendSlop.Crowd;

namespace FriendSlop.Characters
{
    // syncs the character's Animator with the movement state (NetworkPlayerController or Npc).
    // drives the "Speed" parameter for Idle (0), Walk (1), and Sprint (2) blending.
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimationHandler : MonoBehaviour
    {
        [SerializeField]
        private float dampTime = 0.1f;

        [SerializeField]
        private float aimWeightLerpSpeed = 10f;

        private Animator _animator;
        private NetworkPlayerController _playerController;
        private Npc _npc;

        private static readonly int SpeedHash = Animator.StringToHash("Speed");

        // set once in Awake: the Aim layer's index in the Animator, looked up by name so the layer can
        // be reordered without breaking this reference.
        private int _aimLayerIndex = -1;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _playerController = GetComponentInParent<NetworkPlayerController>();
            _npc = GetComponentInParent<Npc>();

            if (_animator != null)
                _aimLayerIndex = _animator.GetLayerIndex("Aim");
        }

        private void Update()
        {
            if (_animator == null) return;

            float targetSpeed = 0f;

            if (_playerController != null)
            {
                float speed = _playerController.CurrentSpeed;
                if (speed > 0.1f)
                {
                    targetSpeed = _playerController.IsSprinting ? 2f : 1f;
                }
            }
            else if (_npc != null)
            {
                // NPCs currently only walk. if they aren't finished/dying, they are walking.
                // we check the public Speed property for a baseline.
                if (!_npc.Finished)
                {
                    targetSpeed = 1f;
                }
            }

            _animator.SetFloat(SpeedHash, targetSpeed, dampTime, Time.deltaTime);

            if (_aimLayerIndex >= 0 && _playerController != null)
            {
                float targetWeight = _playerController.IsScoped ? 1f : 0f;
                float currentWeight = _animator.GetLayerWeight(_aimLayerIndex);
                _animator.SetLayerWeight(
                    _aimLayerIndex,
                    Mathf.MoveTowards(currentWeight, targetWeight, aimWeightLerpSpeed * Time.deltaTime)
                );
            }
        }
    }
}
