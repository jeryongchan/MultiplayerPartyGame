using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // the sniper's red laser sight (GDD: "when scoped in, a red laser beam is emitted and visible to
    // all players"). continuous data, not a one-shot event; category 1 in the networking guide
    // (lightweight, no lag comp needed, nobody's hit outcome depends on it).
    //
    // the camera (not the gun mesh) is the real aim source, same ray NetworkShooter fires from,
    // screen-center through the owner's camera, pitch included. the gun barrel and the eye are never
    // at the same point, so aiming the mesh's own forward would diverge from the crosshair (this is
    // the standard "gun-to-crosshair convergence" every third-person shooter handles). only the owner
    // has that camera, so the owner raycasts locally and replicates the resulting world hit point;
    // every machine (owner included) then just draws a line from its own MuzzleTip to that point,
    // no direction math needed on remotes, and the beam always visually converges on the same spot
    // regardless of the gun's current animated pose.
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

        // replicated world-space point the beam should reach, written by the owner each tick from its
        // own camera ray. everyone (including the owner) draws MuzzleTip -> this point.
        private readonly NetworkVariable<Vector3> _aimPoint =
            new NetworkVariable<Vector3>(writePerm: NetworkVariableWritePermission.Owner);

        // whether the beam should be showing at all right now, replicated alongside the point (can't
        // infer "off" from a zero point, the origin itself is a valid, reachable world position).
        private readonly NetworkVariable<bool> _visible =
            new NetworkVariable<bool>(writePerm: NetworkVariableWritePermission.Owner);

        private void Awake()
        {
            _controller = GetComponentInParent<NetworkPlayerController>();
            _line = GetComponent<LineRenderer>();
            _line.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= OnNetworkTick;
        }

        // owner-only: figure out where the scope is actually pointing (screen-center camera ray, same
        // as NetworkShooter's shot) and replicate that world point.
        private void OnNetworkTick()
        {
            if (!IsOwner || _controller == null)
                return;

            bool visible = _controller.Role.Value == PlayerRole.Sniper && _controller.IsScoped;
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

        // runs every frame on every copy: draw a line from this machine's own MuzzleTip to the
        // replicated aim point. no raycast needed here, the owner already resolved where the beam ends.
        private void Update()
        {
            if (_line == null)
                return;

            // the owner never sees their own beam, they already know where they're aiming (scope
            // crosshair), and the line just streaks across their scope view. only other players read it
            // as the "sniper is watching" tell (GDD). the owner still writes the aim point above so
            // everyone else can draw it; it's only the local draw that's suppressed here.
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
