using UnityEngine;
using FriendSlop.Player;
using FriendSlop.Crowd;

namespace FriendSlop.Characters
{
    // drives the Animator from the movement state (NetworkPlayerController or Npc): the Speed blend
    // (Idle 0 / Walk 1 / Run 2), the Aim layer, and the exotic-pose override layer.
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimationHandler : MonoBehaviour
    {
        [SerializeField]
        private float dampTime = 0.1f;

        [SerializeField]
        private float aimWeightLerpSpeed = 10f;

        [SerializeField]
        private float poseWeightLerpSpeed = 12f;

        private Animator _animator;
        private NetworkPlayerController _playerController;
        private Npc _npc;

        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int DownedHash = Animator.StringToHash("Downed");
        private static readonly int PoseHash = Animator.StringToHash("PoseIndex"); // which exotic pose (the CharacterPose value).

        // set once in Awake: layer indices looked up by name so layers can be reordered without breaking these.
        // Aim = upper-body aim stance (weight driven by IsScoped). Pose = full-body exotic override (weight
        // driven by CurrentPose being an exotic pose; the clip chosen by the PoseIndex int param).
        private int _aimLayerIndex = -1;
        private int _poseLayerIndex = -1;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _playerController = GetComponentInParent<NetworkPlayerController>();
            _npc = GetComponentInParent<Npc>();

            if (_animator != null)
            {
                _aimLayerIndex = _animator.GetLayerIndex("Aim");
                _poseLayerIndex = _animator.GetLayerIndex("Pose");
            }
        }

        // the criminal exotic poses on the full-body Pose layer (everything but None and the aim stance Scoped).
        private static bool IsExoticPose(CharacterPose p) =>
            p != CharacterPose.None && p != CharacterPose.Scoped;

        // guards SetBool/SetInteger against a "parameter does not exist" throw on a controller variant lacking it.
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
                if (_playerController.CurrentSpeed > 0.1f)
                    targetSpeed = _playerController.IsSprinting ? 2f : 1f;
            }
            else if (_npc != null)
            {
                // a robbed NPC holds its fall/lie state (the Downed bool); don't also feed it a walk speed.
                if (_npc.Downed)
                {
                    if (HasParam(DownedHash))
                        _animator.SetBool(DownedHash, true);
                }
                // blend by the NPC's actual speed, so a loitering NPC (~0) idles instead of walking on the spot.
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

            // exotic pose (handstand/shoelace/split): full-body override layer. runs on every copy including the
            // server, so the server's Animator holds the same pose and the per-bone hitboxes are posed when
            // HitboxHistory rewinds them (or lag-comp hits the wrong shape). PoseIndex picks the clip.
            if (_poseLayerIndex >= 0 && _playerController != null)
            {
                CharacterPose pose = _playerController.CurrentPose;
                if (HasParam(PoseHash))
                    _animator.SetInteger(PoseHash, (int)pose);

                float targetWeight = IsExoticPose(pose) ? 1f : 0f;
                float currentWeight = _animator.GetLayerWeight(_poseLayerIndex);
                _animator.SetLayerWeight(
                    _poseLayerIndex,
                    Mathf.MoveTowards(currentWeight, targetWeight, poseWeightLerpSpeed * Time.deltaTime)
                );
            }
        }
    }
}
