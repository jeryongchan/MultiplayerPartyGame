using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // the sniper's red laser sight (visible to all players when scoped in). continuous data, not a one-shot
    // event, and no lag comp: nobody's hit outcome depends on it.
    //
    // the camera (not the gun mesh) is the aim source, same ray NetworkShooter fires. the barrel and the eye
    // are never at the same point, so aiming the mesh's forward would diverge from the crosshair. only the
    // owner has that camera, so the owner raycasts locally and replicates the resulting world hit point; every
    // machine then draws a line from its own MuzzleTip to that point, always converging on the same spot.
    [RequireComponent(typeof(LineRenderer))]
    public class SniperLaser : NetworkBehaviour
    {
        [SerializeField]
        private Transform muzzleTip;

        [SerializeField]
        private float maxRange = 200f;

        [SerializeField]
        private LayerMask hitMask = ~0;

        private NetworkPlayerController _controller;
        private LineRenderer _line;

        // world point the beam reaches, written by the owner each tick from its camera ray. everyone (owner
        // included) draws MuzzleTip -> here.
        private readonly NetworkVariable<Vector3> _aimPoint =
            new NetworkVariable<Vector3>(writePerm: NetworkVariableWritePermission.Owner);

        // whether the beam shows at all, replicated alongside the point (can't infer "off" from a zero point:
        // the origin itself is a valid world position).
        private readonly NetworkVariable<bool> _visible =
            new NetworkVariable<bool>(writePerm: NetworkVariableWritePermission.Owner);

        private void Awake()
        {
            _controller = GetComponentInParent<NetworkPlayerController>();
            _line = GetComponent<LineRenderer>();
            _line.enabled = false;
        }

        public override void OnNetworkSpawn() => NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= OnNetworkTick;
        }

        // owner-only: resolve where the scope points (screen-center camera ray, same as the shot) and replicate it.
        private void OnNetworkTick()
        {
            if (!IsOwner || _controller == null)
                return;

            bool visible = _controller.Role.Value.IsHunter() && _controller.IsScoped;
            _visible.Value = visible;
            if (!visible)
                return;

            Camera cam = Camera.main;
            if (cam == null)
                return;

            Ray aim = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            _aimPoint.Value = Physics.Raycast(aim.origin, aim.direction, out RaycastHit hit, maxRange, hitMask)
                ? hit.point
                : aim.origin + aim.direction * maxRange;
        }

        // every frame, every copy: draw MuzzleTip -> the replicated aim point (the owner already resolved the end).
        private void Update()
        {
            if (_line == null)
                return;

            // the owner never sees its own beam: it already knows where it's aiming (the crosshair), and the
            // line just streaks across the scope. only other players read it as the "sniper is watching" tell.
            if (IsOwner || !_visible.Value || muzzleTip == null)
            {
                _line.enabled = false;
                return;
            }

            _line.enabled = true;
            _line.SetPosition(0, muzzleTip.position);
            _line.SetPosition(1, _aimPoint.Value);
        }
    }
}
