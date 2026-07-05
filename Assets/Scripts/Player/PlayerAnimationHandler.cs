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
        private static readonly int DownedHash = Animator.StringToHash("Downed");

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

        // true if the live animator controller actually declares this parameter, guards SetBool/SetTrigger
        // against a "Parameter does not exist" throw when a controller variant lacks it (or a stale runtime).
        private bool HasParam(int hash)
        {
            foreach (var p in _animator.parameters)
                if (p.nameHash == hash)
                    return true;
            return false;
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
                // a robbed NPC plays its fall/lie state (driven by the Downed bool) and holds; don't also
                // feed it a walk speed. set the bool once it's down; it never clears (down for the round).
                if (_npc.Downed)
                {
                    if (HasParam(DownedHash))
                        _animator.SetBool(DownedHash, true);
                }
                // blend by the NPC's actual speed, so a loitering NPC (CurrentSpeed ~0) idles instead of
                // walking on the spot. any real motion reads as a walk.
                else if (!_npc.Finished && _npc.CurrentSpeed > 0.1f)
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
