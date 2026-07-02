using FriendSlop.Player;
using UnityEngine;

namespace FriendSlop.Characters
{
    // blends the gun's local position/rotation (relative to RightHand) between a hip-hold offset and
    // an aim-hold offset. the two poses (locomotion vs the Aim layer's Firing Rifle clip) rotate the
    // hand differently, so one fixed offset looks wrong in at least one of them.
    [RequireComponent(typeof(Transform))]
    public class GunGripOffset : MonoBehaviour
    {
        [SerializeField]
        private NetworkPlayerController playerController;

        [SerializeField]
        private float blendSpeed = 10f;

        [Header("Hip-fire hold (idle/walk/run)")]
        [SerializeField]
        private Vector3 hipLocalPosition;

        [SerializeField]
        private Vector3 hipLocalEulerAngles;

        [Header("Aim hold (scoped)")]
        [SerializeField]
        private Vector3 aimLocalPosition;

        [SerializeField]
        private Vector3 aimLocalEulerAngles;

        private float _blend;

        private void Reset()
        {
            hipLocalPosition = transform.localPosition;
            hipLocalEulerAngles = transform.localEulerAngles;
            aimLocalPosition = transform.localPosition;
            aimLocalEulerAngles = transform.localEulerAngles;
        }

        private void Awake()
        {
            if (playerController == null)
                playerController = GetComponentInParent<NetworkPlayerController>();
        }

        private void LateUpdate()
        {
            if (playerController == null)
                return;

            float target = playerController.IsScoped ? 1f : 0f;
            _blend = Mathf.MoveTowards(_blend, target, blendSpeed * Time.deltaTime);

            transform.localPosition = Vector3.Lerp(hipLocalPosition, aimLocalPosition, _blend);
            transform.localRotation = Quaternion.Slerp(
                Quaternion.Euler(hipLocalEulerAngles),
                Quaternion.Euler(aimLocalEulerAngles),
                _blend
            );
        }
    }
}
