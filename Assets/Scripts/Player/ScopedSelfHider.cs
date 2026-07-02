using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // hides the owner's own character mesh and gun from their view while scoped, so the scope image
    // isn't cluttered by the barrel/shoulder clipping into frame (the camera is at the eye when scoped,
    // so the body is right in front of the lens). purely local and visual: toggles Renderer.enabled, not
    // the GameObjects, so colliders, animation, and network logic are untouched. runs only on the owner;
    // every other machine keeps rendering this player's full model (they must still see the sniper).
    public class ScopedSelfHider : NetworkBehaviour
    {
        // the visual root whose renderers get hidden while scoped (Character mesh, Gun, sight, etc.)
        [SerializeField]
        private Transform visualRoot;

        private NetworkPlayerController _controller;
        private Renderer[] _renderers;
        private bool _hidden;

        public override void OnNetworkSpawn()
        {
            // only the owner's own view is affected; remotes never run the hide logic
            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            _controller = GetComponent<NetworkPlayerController>();
            if (visualRoot != null)
                _renderers = visualRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        }

        private void Update()
        {
            if (_controller == null || _renderers == null)
                return;

            bool shouldHide = _controller.IsScoped;
            if (shouldHide == _hidden)
                return; // only touch renderers on the edge, not every frame

            _hidden = shouldHide;
            foreach (var r in _renderers)
                if (r != null)
                    r.enabled = !shouldHide;
        }
    }
}
