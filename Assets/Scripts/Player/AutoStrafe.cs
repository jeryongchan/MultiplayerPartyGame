using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // test aid: makes this player auto-strafe left-right so you can practice tracking a moving target
    // (e.g. to feel-test lag compensation) without a second human. the owner toggles it with a key.
    // it drives the real input path, just feeding a synthetic A/D into NetworkPlayerController's
    // MoveInputOverride, so prediction, server sim, interpolation, and hitbox history all behave exactly
    // as if you were holding the key. owner-only (input is sampled on the owner). throwaway scaffolding.
    [RequireComponent(typeof(NetworkPlayerController))]
    public class AutoStrafe : NetworkBehaviour
    {
        // hold one direction this long before flipping, giving a clear back-and-forth to track
        [SerializeField]
        private float halfPeriod = 0.8f;

        [SerializeField]
        private UnityEngine.InputSystem.Key toggleKey = UnityEngine.InputSystem.Key.G;

        private NetworkPlayerController _controller;
        private bool _active;
        private float _flipTime;
        private float _dir = 1f;

        private void Awake() => _controller = GetComponent<NetworkPlayerController>();

        private void Update()
        {
            if (!IsOwner)
                return;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb[toggleKey].wasPressedThisFrame)
            {
                _active = !_active;
                _controller.MoveInputOverride = _active ? new Vector2(_dir, 0f) : (Vector2?)null;
            }

            if (!_active)
                return;

            // flip strafe direction every halfPeriod for a steady left-right sweep
            if (Time.time >= _flipTime)
            {
                _dir = -_dir;
                _flipTime = Time.time + halfPeriod;
            }
            _controller.MoveInputOverride = new Vector2(_dir, 0f);
        }

        public override void OnNetworkDespawn()
        {
            if (_controller != null)
                _controller.MoveInputOverride = null; // never leave the override stuck on
        }
    }
}
