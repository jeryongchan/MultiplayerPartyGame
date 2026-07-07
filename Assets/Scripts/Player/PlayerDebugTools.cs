using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FriendSlop.Player
{
    // THROWAWAY owner-only keypress debug hooks, merged from AutoStrafe + RolePickerDebug. Delete once the
    // lobby UI + real test harness exist. Two independent tools:
    //   F1/F2  pick Sniper/Criminal before any lobby exists, to test the RoleRegistry -> spawn flow. Routes
    //          through the same SetRoleRpc the real lobby will use. Function keys leave the number row for poses.
    //   G      toggle auto-strafe: this player strafes left<->right on its own so you can practice tracking a
    //          moving target (feel-testing lag comp) solo. Feeds a synthetic A/D into MoveInputOverride, so
    //          prediction, server sim, interpolation, and hitbox history all behave as if you held the key.
    [RequireComponent(typeof(NetworkPlayerController))]
    public class PlayerDebugTools : NetworkBehaviour
    {
        [SerializeField]
        private float halfPeriod = 0.8f; // hold one strafe direction this long before flipping.

        [SerializeField]
        private Key strafeToggleKey = Key.G;

        private NetworkPlayerController _controller;
        private bool _strafing;
        private float _flipTime;
        private float _dir = 1f;

        private void Awake() => _controller = GetComponent<NetworkPlayerController>();

        private void Update()
        {
            if (!IsOwner)
                return;

            var kb = Keyboard.current;
            if (kb == null)
                return;

            if (RoleRegistry.Instance != null)
            {
                if (kb.f1Key.wasPressedThisFrame)
                    RoleRegistry.Instance.SetRoleRpc(PlayerRole.Sniper);
                else if (kb.f2Key.wasPressedThisFrame)
                    RoleRegistry.Instance.SetRoleRpc(PlayerRole.Criminal);
            }

            if (kb[strafeToggleKey].wasPressedThisFrame)
            {
                _strafing = !_strafing;
                _controller.MoveInputOverride = _strafing ? new Vector2(_dir, 0f) : (Vector2?)null;
            }
            if (!_strafing)
                return;

            if (Time.time >= _flipTime) // flip direction every halfPeriod for a steady sweep.
            {
                _dir = -_dir;
                _flipTime = Time.time + halfPeriod;
            }
            _controller.MoveInputOverride = new Vector2(_dir, 0f);
        }

        public override void OnNetworkDespawn()
        {
            if (_controller != null)
                _controller.MoveInputOverride = null; // never leave the override stuck on.
        }
    }
}
