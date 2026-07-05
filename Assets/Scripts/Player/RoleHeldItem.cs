using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // shows a held item (the gun) only for the hunter roles and hides it for Criminals, a criminal already
    // can't scope or shoot, this stops them visibly carrying a rifle while blending into the crowd.
    //
    // lives on the player root (where NetworkPlayerController.Role is), not on the item itself:
    // a component that disables its own GameObject stops getting callbacks, so it could never re-show the item
    // when the role rotates back. instead it toggles a referenced child (item).
    //
    // runs on every copy (it reads the replicated Role), and re-evaluates on Role.OnValueChanged so it
    // flips correctly each round as roles rotate, and for a client whose Role arrives after spawn.
    public class RoleHeldItem : NetworkBehaviour
    {
        // the held item to show for hunters / hide for criminals (e.g. the Gun under RightHand)
        [SerializeField]
        private GameObject item;

        private NetworkPlayerController _controller;

        private void Awake() => _controller = GetComponent<NetworkPlayerController>();

        public override void OnNetworkSpawn()
        {
            if (_controller != null)
                _controller.Role.OnValueChanged += OnRoleChanged;
            Apply();
        }

        public override void OnNetworkDespawn()
        {
            if (_controller != null)
                _controller.Role.OnValueChanged -= OnRoleChanged;
        }

        private void OnRoleChanged(PlayerRole _, PlayerRole __) => Apply();

        // hunters (sniper/witness) carry the item; criminals don't
        private void Apply()
        {
            if (item != null && _controller != null)
                item.SetActive(_controller.Role.Value.IsHunter());
        }
    }
}
