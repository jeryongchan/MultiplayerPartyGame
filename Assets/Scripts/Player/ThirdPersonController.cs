using UnityEngine;
using UnityEngine.InputSystem;

namespace FriendSlop.Player
{
    // local-only WASD movement relative to camera. throwaway until netcode movement lands.
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float rotationSmoothTime = 0.1f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float jumpHeight = 1.5f;

        [Header("Camera")]
        [SerializeField] private Transform cameraTransform; // defaults to Camera.main

        private CharacterController _controller;
        private float _verticalVelocity;
        private float _rotationVelocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        private void Update()
        {
            Vector2 input = ReadMoveInput();
            Vector3 move = Vector3.zero;

            if (input.sqrMagnitude > 0.01f)
            {
                float targetAngle = Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg; // relative to camera yaw
                if (cameraTransform != null)
                    targetAngle += cameraTransform.eulerAngles.y;

                float smoothedAngle = Mathf.SmoothDampAngle(
                    transform.eulerAngles.y, targetAngle, ref _rotationVelocity, rotationSmoothTime);
                transform.rotation = Quaternion.Euler(0f, smoothedAngle, 0f);

                move = Quaternion.Euler(0f, targetAngle, 0f) * Vector3.forward;
            }

            // keep grounded controller pinned to the plane
            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;

            // v = sqrt(2 * g * h) for the target jump height
            if (_controller.isGrounded && JumpPressed())
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);

            _verticalVelocity += gravity * Time.deltaTime;

            Vector3 velocity = move * moveSpeed + Vector3.up * _verticalVelocity;
            _controller.Move(velocity * Time.deltaTime);
        }

        private static Vector2 ReadMoveInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return Vector2.zero;

            float x = 0f, y = 0f;
            if (keyboard.wKey.isPressed) y += 1f;
            if (keyboard.sKey.isPressed) y -= 1f;
            if (keyboard.dKey.isPressed) x += 1f;
            if (keyboard.aKey.isPressed) x -= 1f;
            return new Vector2(x, y).normalized;
        }

        private static bool JumpPressed()
        {
            // wasPressedThisFrame = one jump per press, no auto-repeat while held
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
        }
    }
}
