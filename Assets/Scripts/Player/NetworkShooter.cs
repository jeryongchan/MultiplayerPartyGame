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

        // debug: draw a dot + cross at the exact screen centre (where the ray is cast from). compare it
        // against the scope overlay's crosshair while scoped, if they don't line up the overlay cross
        // is lying about where you're aiming.
        private void OnGUI()
        {
            if (!IsOwner)
                return;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            var tex = Texture2D.whiteTexture;
            GUI.color = Color.red;
            GUI.DrawTexture(new Rect(cx - 1f, cy - 12f, 2f, 24f), tex); // vertical
            GUI.DrawTexture(new Rect(cx - 12f, cy - 1f, 24f, 2f), tex); // horizontal
            GUI.color = Color.white;
        }

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

            // DEBUG: compare Screen size vs camera pixel size + viewport rect. A mismatch (letterbox /
            // partial viewport) means screen-centre ray != where the OnGUI cross is drawn.
            Debug.Log($"[Shoot DBG] Screen {Screen.width}x{Screen.height} | cam.pixel {cam.pixelWidth}x{cam.pixelHeight} | cam.rect {cam.rect} | viewportPoint of ray {cam.ScreenToViewportPoint(new Vector3(Screen.width*0.5f, Screen.height*0.5f, 0))}");

            // DEBUG: what does the CLIENT see this ray hit? Compare against the server's result to tell
            // a position desync (client hits player, server hits wall) from a clean miss (both miss).
            if (Physics.Raycast(aim.origin, aim.direction, out RaycastHit clientHit, maxRange))
                Debug.Log($"[Shoot CLIENT] sees hit '{clientHit.collider.name}' at {clientHit.point}, dist {clientHit.distance:F2}");
            else
                Debug.Log("[Shoot CLIENT] sees nothing");

            SubmitShootServerRpc(aim.origin, aim.direction);
        }

        // client to server: the raw shot. the server does the authoritative raycast; the client only
        // declares where it aimed, never what it hit.
        [Rpc(SendTo.Server)]
        private void SubmitShootServerRpc(Vector3 origin, Vector3 direction)
        {
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxRange))
            {
                Debug.DrawRay(origin, direction * maxRange, Color.yellow, 2f); // missed everything
                return;
            }

            // DEBUG: draw the ray to the hit, and log what it actually struck. View in Scene window with
            // Gizmos on while in Play mode (red = the shot, magenta marks the hit collider).
            Debug.DrawLine(origin, hit.point, Color.red, 2f);
            Debug.Log($"[Shoot] ORIGIN {origin} DIR {direction} | ray hit '{hit.collider.name}' at {hit.point}, dist {hit.distance:F2}");

            // DEBUG: for every OTHER player capsule, how close did this ray pass to its collider centre?
            // Tells us by how much (and which way) we missed the intended target.
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                Vector3 c = cc.transform.TransformPoint(cc.center);
                Vector3 toC = c - origin;
                float along = Vector3.Dot(toC, direction);
                Vector3 closest = origin + direction * along;       // nearest point on the ray to the capsule centre
                float miss = Vector3.Distance(closest, c);
                float worldRadius = cc.radius * Mathf.Max(cc.transform.lossyScale.x, cc.transform.lossyScale.z);
                bool ccHit = cc.Raycast(new Ray(origin, direction), out RaycastHit cch, maxRange);
                Debug.Log($"[Shoot]   '{cc.name}' centre {c}: ray passes {miss:F3}m from centre (radius {worldRadius:F3}) => {(miss <= worldRadius ? "INSIDE" : "outside")} ; closestOnRay {closest} ; cc.Raycast={ccHit} dist={(ccHit ? cch.distance.ToString("F2") : "-")}");
            }

            // resolve the hit to a networked player, ignoring world geometry / non-networked colliders
            var target = hit.collider.GetComponentInParent<NetworkObject>();
            if (target != null && target.OwnerClientId != OwnerClientId)
                Debug.Log($"[Shoot] client {OwnerClientId} hit client {target.OwnerClientId}");

            // broadcast the authoritative hit point and surface normal so every machine marks the same
            // spot; the normal lifts the marker off the surface instead of burying it in the collider
            ShowHitMarkerRpc(hit.point, hit.normal);
        }

        // server to all machines: spawn a short-lived local marker at the hit point. it's a plain local
        // GameObject, not networked, so each machine makes its own (the usual pattern for cosmetic
        // effects: replicate the event, spawn the visual locally).
        [Rpc(SendTo.ClientsAndHost)]
        private void ShowHitMarkerRpc(Vector3 point, Vector3 normal)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            // Lift the sphere off the surface by its own radius so it sits ON the hit surface rather
            // than half-buried inside it (otherwise the target's opaque mesh hides it).
            marker.transform.position = point + normal * (markerSize * 0.5f);
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
