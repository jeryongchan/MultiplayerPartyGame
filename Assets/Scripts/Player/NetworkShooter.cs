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

        // layers the shot can hit: environment + player hitboxes. player bodies carry a dedicated
        // CapsuleCollider (a normal collider raycasts to its true surface, unlike CharacterController,
        // which under-reports its radius and misses edge shots). the CharacterController lives on the
        // "Ignore Raycast" layer so the buggy CC is invisible to shots while still doing movement.
        // we tell "hit a player" from "hit a wall" by GetComponentInParent<NetworkObject>(), not by layer.
        [SerializeField]
        private LayerMask shootableMask = ~0;

        // sniper must be scoped (right mouse held) to fire, which also avoids the hip-fire facing
        // mismatch (scoped = body faces aim = gun points where you shoot). set false for a hip-firing
        // weapon later (which would also want strafe-style movement so the body faces the aim).
        [SerializeField]
        private bool requireScopeToFire = true;

        // seconds between shots. owner-side gate on input (the server isn't enforcing fire rate yet,
        // so a modified client could fire faster; fine for now, revisit when cheating matters).
        [SerializeField]
        private float fireCooldown = 1.5f;

        // Time.time of the next allowed shot. owner-only state.
        private float _nextFireTime;

        // recoil per shot, applied to the owner's camera (which also steers aim, so shots walk off-target
        // under rapid fire). vertical kick is consistent; horizontal kick is randomized either way each shot.
        [SerializeField]
        private float recoilPitch = 2.5f;

        [SerializeField]
        private float recoilYaw = 0.6f;

        // resolved on the owner at first fire; the scene's single follow camera that drives aim.
        private ThirdPersonCamera _ownerCamera;

        // how far back the server is allowed to rewind for a shot. doubles as the cheat-guard window:
        // shots referencing a tick older than this (or in the future) are rejected. keep it under
        // HitboxHistory's historySeconds. peer-hosted, so keep it tight (~0.2s) to limit host advantage.
        [SerializeField]
        private float maxRewindSeconds = 0.5f;

        // how far in the past this client renders remote players (entity interpolation). the shooter saw
        // remotes at (current server tick minus this), so the server must rewind to that tick, not the
        // raw fire tick, otherwise lag comp over-favors the shooter. match NetworkPlayerController's value.
        [SerializeField]
        private float interpolationDelay = 0.1f;

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
            if (mouse == null)
                return;

            // fire on left-click or the F key (alt fire for laptops where left-click while holding
            // right-click is awkward on the trackpad). both are edge events, the press frame only.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool firePressed =
                mouse.leftButton.wasPressedThisFrame
                || (kb != null && kb.fKey.wasPressedThisFrame);
            if (!firePressed)
                return;

            if (requireScopeToFire && !mouse.rightButton.isPressed)
                return;

            // fire-rate gate: ignore the press if we're still cooling down from the last shot
            if (Time.time < _nextFireTime)
                return;
            _nextFireTime = Time.time + fireCooldown;

            // build the shot from the owner's camera: through screen centre, the standard FPS aim.
            // only the owner has this camera, so compute it here and send the result to the server.
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

            // kick the camera (and thus aim) on the confirmed shot. randomize horizontal direction so
            // the muzzle climbs while drifting unpredictably left/right under rapid fire.
            if (_ownerCamera == null)
                _ownerCamera = Object.FindFirstObjectByType<ThirdPersonCamera>();
            if (_ownerCamera != null)
                _ownerCamera.AddRecoil(recoilPitch, Random.Range(-recoilYaw, recoilYaw));

            // the tick the shooter was actually seeing remotes at: current shared server tick minus the
            // interpolation delay (remotes render in the past). the server rewinds hitboxes to this tick.
            int tickRate = NetworkManager != null ? (int)NetworkManager.NetworkConfig.TickRate : 30;
            int renderTick = NetworkManager.ServerTime.Tick - Mathf.RoundToInt(interpolationDelay * tickRate);

            SubmitShootServerRpc(aim.origin, aim.direction, renderTick);
        }

        // client to server: the raw shot plus the tick the client was rendering. the server rewinds all
        // hitboxes to that tick, raycasts, then restores. the client declares where/when it aimed,
        // never what it hit.
        [Rpc(SendTo.Server)]
        private void SubmitShootServerRpc(Vector3 origin, Vector3 direction, int renderTick)
        {
            // cheat guard: reject a shot referencing a future tick or one older than our rewind window
            int serverTick = NetworkManager.ServerTime.Tick;
            int tickRate = (int)NetworkManager.NetworkConfig.TickRate;
            int maxBack = Mathf.CeilToInt(maxRewindSeconds * tickRate);
            if (renderTick > serverTick || renderTick < serverTick - maxBack)
            {
                Debug.Log($"[Shoot] rejected renderTick {renderTick} (server {serverTick}, window {maxBack} ticks)");
                return;
            }

            // lag compensation: rewind every player's hitbox to where it was at the shooter's render tick,
            // flush PhysX, raycast against that historical world, then restore the present. server-only.
            var histories = FindObjectsByType<HitboxHistory>(FindObjectsSortMode.None);
            foreach (var h in histories)
                h.Rewind(renderTick);
            Physics.SyncTransforms(); // push the moved colliders into PhysX before querying.

            bool hitSomething = Physics.Raycast(origin, direction, out RaycastHit hit, maxRange, shootableMask);

            foreach (var h in histories)
                h.Restore();
            Physics.SyncTransforms(); // restore PhysX to the present world.

            if (!hitSomething)
            {
                Debug.DrawRay(origin, direction * maxRange, Color.yellow, 2f); // missed everything
                return;
            }

            // DEBUG: draw the ray to the hit, and log what it actually struck (at the rewound position).
            // view in Scene window with Gizmos on while in Play mode (red = the shot).
            Debug.DrawLine(origin, hit.point, Color.red, 2f);
            Debug.Log($"[Shoot] renderTick {renderTick} | ray hit '{hit.collider.name}' at {hit.point}, dist {hit.distance:F2}");

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
            // strip the collider immediately (DestroyImmediate, not Destroy): CreatePrimitive adds a
            // SphereCollider, and Destroy is deferred to end-of-frame. for that one frame the live
            // collider sits exactly on the hit surface; on the host (server, CharacterController
            // enabled) it overlaps the target's CC, which depenetrates and visibly pops the player.
            // removing it the same frame it spawns avoids that, and it never blocks future raycasts.
            DestroyImmediate(marker.GetComponent<Collider>());
            // lift the sphere off the surface by its own radius so it sits on the hit surface rather
            // than half-buried inside it (otherwise the target's opaque mesh hides it).
            marker.transform.position = point + normal * (markerSize * 0.5f);
            marker.transform.localScale = Vector3.one * markerSize;
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
