using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative hitscan. owner left-clicks, builds a ray from its camera, sends origin+dir
    // to the server; the server raycasts, logs the hit, and broadcasts the hit point so every machine
    // drops a debug marker. no cooldown, damage, or lag-compensation yet. same shared-script model as
    // NetworkPlayerController: only the owner reads input, only the server decides the hit.
    public class NetworkShooter : NetworkBehaviour
    {
        // how far the ray reaches; beyond this a shot just misses
        [SerializeField]
        private float maxRange = 200f;

        // sniper must be scoped (right mouse held) to fire, which also avoids the hip-fire facing
        // mismatch (scoped = body faces aim = gun points where you shoot). set false for a hip-firing
        // weapon later.
        [SerializeField]
        private bool requireScopeToFire = true;

        [SerializeField]
        private float markerSize = 0.2f;

        [SerializeField]
        private float markerLifetime = 1f;

        // one shared material for all markers, so we don't leak a Material per shot or render pink
        private static Material _markerMaterial;

        private void Update()
        {
            // only the owner fires, and only on the click frame (an edge event, like jump)
            if (!IsOwner)
                return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            if (requireScopeToFire && !mouse.rightButton.isPressed)
                return;

            // build the shot through screen centre; only the owner has this camera, so compute it here
            // and send the result to the server.
            Camera cam = Camera.main;
            if (cam == null)
                return;

            Ray aim = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            SubmitShootServerRpc(aim.origin, aim.direction);
        }

        // client to server: the raw shot. the server does the authoritative raycast; the client only
        // declares where it aimed, never what it hit.
        [Rpc(SendTo.Server)]
        private void SubmitShootServerRpc(Vector3 origin, Vector3 direction)
        {
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxRange))
                return;

            // resolve the hit to a networked player, ignoring world geometry / non-networked colliders
            var target = hit.collider.GetComponentInParent<NetworkObject>();
            if (target != null && target.OwnerClientId != OwnerClientId)
                Debug.Log($"[Shoot] client {OwnerClientId} hit client {target.OwnerClientId}");

            // broadcast the authoritative hit point so every machine marks the same spot
            ShowHitMarkerRpc(hit.point);
        }

        // server to all machines: spawn a short-lived local marker at the hit point. it's a plain local
        // GameObject, not networked, so each machine makes its own (the usual pattern for cosmetic
        // effects: replicate the event, spawn the visual locally).
        [Rpc(SendTo.ClientsAndHost)]
        private void ShowHitMarkerRpc(Vector3 point)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.transform.position = point;
            marker.transform.localScale = Vector3.one * markerSize;
            Destroy(marker.GetComponent<Collider>()); // must not block future raycasts
            marker.GetComponent<Renderer>().sharedMaterial = MarkerMaterial();
            Destroy(marker, markerLifetime);
        }

        private static Material MarkerMaterial()
        {
            if (_markerMaterial == null)
                _markerMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
                {
                    color = Color.red,
                };
            return _markerMaterial;
        }
    }
}
