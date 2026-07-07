using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // Owner + role driven presentation for the hunter's weapon: two small purely-visual jobs on the player
    // root (where the replicated Role and IsScoped live), folded into one component.
    //   1. Held-item visibility: the gun shows only for hunters, hidden for criminals so they don't visibly
    //      carry a rifle in the crowd. Runs on every copy and re-applies on Role change (roles rotate each
    //      round, and a client's Role may arrive after spawn). Toggles a referenced child, not its own object,
    //      since a component that disables itself stops getting callbacks and could never re-show the item.
    //   2. Scoped self-hide: while the owner is scoped, its own body + gun renderers are hidden from its own
    //      view so the barrel doesn't clip into the scope image. Owner-only + local (toggles Renderer.enabled,
    //      not GameObjects), so colliders/animation/network are untouched; every other machine still sees the
    //      full sniper.
    // Merged from the former ScopedSelfHider + RoleHeldItem. The networked laser (SniperLaser) and the gun's
    // grip blend (GunGripOffset, on the Gun bone) stay separate.
    public class SniperWeaponVisuals : NetworkBehaviour
    {
        [SerializeField]
        private GameObject item; // shown for hunters / hidden for criminals (the Gun under RightHand).

        [SerializeField]
        private Transform visualRoot; // renderers hidden while the owner is scoped (Character mesh, Gun, sight).

        private NetworkPlayerController _controller;
        private Renderer[] _renderers;
        private bool _hidden;

        private void Awake() => _controller = GetComponent<NetworkPlayerController>();

        public override void OnNetworkSpawn()
        {
            // held-item visibility runs on every copy (reads the replicated Role).
            if (_controller != null)
                _controller.Role.OnValueChanged += OnRoleChanged;
            ApplyHeldItem();

            // scoped self-hide is owner-only; cache the owner's renderers once.
            if (IsOwner && visualRoot != null)
                _renderers = visualRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        }

        public override void OnNetworkDespawn()
        {
            if (_controller != null)
                _controller.Role.OnValueChanged -= OnRoleChanged;
        }

        private void OnRoleChanged(PlayerRole _, PlayerRole __) => ApplyHeldItem();

        private void ApplyHeldItem()
        {
            if (item != null && _controller != null)
                item.SetActive(_controller.Role.Value.IsHunter()); // hunters carry the item; criminals don't.
        }

        private void Update()
        {
            // only the owner hides its own view; remotes never run this (they must see the full sniper).
            if (!IsOwner || _controller == null || _renderers == null)
                return;

            bool shouldHide = _controller.IsScoped;
            if (shouldHide == _hidden)
                return; // only touch renderers on the edge, not every frame.

            _hidden = shouldHide;
            foreach (var r in _renderers)
                if (r != null)
                    r.enabled = !shouldHide;
        }
    }
}
