using FriendSlop.Player;
using UnityEngine;

namespace FriendSlop.Characters
{
    // blends the gun's local position/rotation (relative to RightHand) between a hip-hold and an aim-hold
    // offset. the two poses (locomotion vs the Aim layer's firing clip) hold the hand differently, so one
    // fixed offset looks wrong in at least one of them.
    [RequireComponent(typeof(Transform))]
    public class GunGripOffset : MonoBehaviour
    {
        [SerializeField]
        private NetworkPlayerController playerController;

        [SerializeField]
        private float blendSpeed = 10f;

        [SerializeField]
        private Vector3 hipLocalPosition; // hip-fire hold (idle/walk/run).

        [SerializeField]
        private Vector3 hipLocalEulerAngles;

        [SerializeField]
        private Vector3 aimLocalPosition; // aim hold (scoped).

        [SerializeField]
        private Vector3 aimLocalEulerAngles;

        private float _blend;

        // seed both offsets from the current transform so a fresh add starts sane.
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

        private void LateUpdate() // after the Animator poses the arm for the frame.
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
