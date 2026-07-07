using FriendSlop.Crowd;
using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative hitscan with lag comp. owner left-clicks -> builds a camera ray -> sends
    // origin+direction to the server -> server rewinds hitboxes, raycasts, decides the hit, and broadcasts the
    // authoritative hit point so every machine drops a marker there. extends RoleGatedAbility for the gate.
    //
    // same shared script on every copy, but only the owner reads input and sends, and only the server decides
    // the hit (clients never declare "I hit X").
    public class NetworkShooter : RoleGatedAbility
    {
        [SerializeField]
        private float maxRange = 200f; // beyond this a shot simply misses.

        // layers the shot can hit: world + player hitboxes. player bodies carry a dedicated collider (raycasts
        // to its true surface, unlike CharacterController, which under-reports its radius and misses edge shots)
        // on a shootable layer; the CC lives on Ignore Raycast. player-vs-wall is told apart by NetworkObject.
        [SerializeField]
        private LayerMask shootableMask = ~0;

        // must be scoped to fire, which also avoids the hip-fire facing mismatch (scoped = body faces aim = gun
        // points where you shoot). set false for a hip-firing weapon later.
        [SerializeField]
        private bool requireScopeToFire = true;

        // seconds between shots. owner-side only for now (the server doesn't enforce fire rate, so a modified
        // client could fire faster; revisit when cheating matters).
        [SerializeField]
        private float fireCooldown = 1.5f;

        // recoil per shot, applied to the owner's camera (which steers aim, so shots walk off-target under
        // rapid fire). vertical kick is consistent; horizontal is randomized each shot.
        [SerializeField]
        private float recoilPitch = 2.5f;

        [SerializeField]
        private float recoilYaw = 0.6f;

        // master switch for server-side rewind. off + lag + a moving target makes shots miss behind it (server
        // raycasts the present); on makes them hit (server rewinds to where the shooter saw it). toggle on the host.
        [SerializeField]
        private bool lagCompensation = true;

        // cap on how far back the server rewinds, so a very high-latency or spoofed client can't rewind
        // arbitrarily far. keep <= HitboxHistory.historySeconds. Valve/Unity recommend ~0.25-0.5s.
        [SerializeField]
        private float maxRewindSeconds = 0.5f;

        // how far in the past this client renders remotes (entity interp). the shooter saw remotes this far
        // behind the server clock, so the rewind target is (serverTick - shooterRTT/2 - this). match the controller.
        [SerializeField]
        private float interpolationDelay = 0.1f;

        [SerializeField]
        private float markerSize = 0.2f;

        [SerializeField]
        private float markerLifetime = 5f;

        // shared marker materials (so we don't leak one per shot). green = player, yellow = NPC, red = world.
        private static Material _playerMarkerMaterial;
        private static Material _npcMarkerMaterial;
        private static Material _worldMarkerMaterial;

        // hunters shoot (Sniper + Witness). the role/alive/phase gate lives in CanUse; this only narrows which roles.
        protected override bool RolePredicate(PlayerRole role) => role.IsHunter();

        private void Update()
        {
            if (!IsOwner)
                return;

            // owner-side role gate (cheap, responsive); the server re-checks below so a bypass still can't fire.
            if (!CanUse)
                return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
                return;

            // fire on left-click or F (alt fire for trackpads where left-click while holding right-click is awkward).
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool firePressed =
                mouse.leftButton.wasPressedThisFrame || (kb != null && kb.fKey.wasPressedThisFrame);
            if (!firePressed)
                return;

            if (requireScopeToFire && !Controller.IsScoped) // scope is a toggle owned by the input reader.
                return;

            if (!TryConsumeCooldown(fireCooldown))
                return;

            // build the shot from the owner's camera (through screen centre) and send the result to the server.
            Camera cam = Camera.main;
            if (cam == null)
                return;

            Ray aim = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));

            // kick the camera (and thus aim) on the shot: the muzzle climbs while drifting under rapid fire.
            if (ThirdPersonCamera.Instance != null)
                ThirdPersonCamera.Instance.AddRecoil(
                    recoilPitch,
                    Random.Range(-recoilYaw, recoilYaw)
                );

            // send only where we aimed. the server owns the when (it derives the rewind tick from its own clock
            // and our measured latency), so we never send a tick from the client's offset clock.
            SubmitShootServerRpc(aim.origin, aim.direction);
        }

        // client -> server: only where the owner aimed. the server decides when to rewind to, then raycasts
        // that historical world. the client never declares what it hit.
        [Rpc(SendTo.Server)]
        private void SubmitShootServerRpc(Vector3 origin, Vector3 direction)
        {
            // authoritative role gate: a hacked client that strips its owner-side check still lands here, where
            // Role was written by the server, so a Criminal physically cannot fire.
            if (!CanUse)
                return;

            if (!ResolveShot(origin, direction, out RaycastHit hit))
                return;

            HitKind kind = Classify(hit.collider, out NetworkObject player, out CrowdNpcHitbox npc);

            switch (kind)
            {
                case HitKind.Player:
                    // head collider = lethal, any other bone = body hit. defaults to Body if the marker is missing.
                    HitZone zone = hit.collider.GetComponent<BoneHitbox>() is { } bh
                        ? bh.Zone
                        : HitZone.Body;
                    // only scores/downs during Hunt, against a live criminal (hitting a hunter just drops a marker).
                    if (
                        GameFlowManager.Instance != null
                        && GameFlowManager.Instance.IsHunt
                        && player.TryGetComponent(out NetworkPlayerController victim)
                        && victim.Role.Value == PlayerRole.Criminal
                        && victim.Health.IsAlive.Value
                    )
                    {
                        // score only on the downing blow (TakeHit returns true exactly once).
                        if (victim.Health.TakeHit(zone))
                        {
                            ScoreManager.Instance?.RecordCriminalKill(OwnerClientId);
                            GameFlowManager.Instance?.EndHuntIfNoCriminalsLeft(); // end Hunt if that was the last criminal.
                        }
                    }
                    break;
                case HitKind.Npc:
                    if (GameFlowManager.Instance != null && GameFlowManager.Instance.IsHunt)
                        ScoreManager.Instance?.RecordNpcHit(OwnerClientId);
                    // replicate the kill by index so every machine fades its own copy of the same pedestrian.
                    if (npc.Npc != null)
                        NpcKilledRpc(npc.Npc.Index);
                    break;
            }

            // broadcast the hit point + normal + kind so every machine marks the same spot (normal lifts the
            // marker off the surface; kind colours it).
            ShowHitMarkerRpc(hit.point, hit.normal, kind);
        }

        // server -> all: NPC #index was shot. each machine kills its own copy via the shared crowd index.
        [Rpc(SendTo.ClientsAndHost)]
        private void NpcKilledRpc(int index) => CrowdManager.Instance?.KillNpc(index);

        // what a shot landed on, resolved server-side from the collider: a player has a NetworkObject in its
        // parents; an NPC has a CrowdNpcHitbox; anything else is world.
        private enum HitKind
        {
            World,
            Player,
            Npc,
        }

        private HitKind Classify(Collider col, out NetworkObject player, out CrowdNpcHitbox npc)
        {
            player = col.GetComponentInParent<NetworkObject>();
            if (player != null && player.OwnerClientId != OwnerClientId)
            {
                npc = null;
                return HitKind.Player;
            }

            npc = col.GetComponentInParent<CrowdNpcHitbox>();
            if (npc != null)
            {
                player = null;
                return HitKind.Npc;
            }

            player = null;
            npc = null;
            return HitKind.World;
        }

        // the tick to rewind to = serverTick - (RTT/2 + interpDelay), clamped to the window. the shooter saw
        // remotes one trip (RTT/2) plus the interp delay in the past; the shot's own trip to the server has
        // already elapsed, so we don't add it again (full RTT over-rewinds, aiming behind a moving target).
        private int CurrentRewindTick()
        {
            int tickRate = (int)NetworkManager.NetworkConfig.TickRate;
            float rttSeconds =
                NetworkManager.NetworkConfig.NetworkTransport.GetCurrentRtt(OwnerClientId) / 1000f;
            int rewindTicks = Mathf.Clamp(
                Mathf.RoundToInt((rttSeconds * 0.5f + interpolationDelay) * tickRate),
                0,
                Mathf.CeilToInt(maxRewindSeconds * tickRate)
            );
            return NetworkManager.ServerTime.Tick - rewindTicks;
        }

        // server-only core of a shot: optionally rewind all other players' hitboxes to the time the shooter
        // saw, raycast that world, then restore the present. all times come from the server's own clock.
        private bool ResolveShot(Vector3 origin, Vector3 direction, out RaycastHit hit)
        {
            int rewindTick = CurrentRewindTick();

            // rewind every other player's hitbox (the shooter's own isn't a valid target), flush PhysX,
            // raycast, restore. with lag comp off we skip the rewind and raycast the present (the bug we fix).
            var histories = lagCompensation
                ? FindObjectsByType<HitboxHistory>(FindObjectsSortMode.None)
                : System.Array.Empty<HitboxHistory>();
            foreach (var h in histories)
                if (h.OwnerClientId != OwnerClientId)
                    h.Rewind(rewindTick);
            Physics.SyncTransforms(); // push the moved colliders into PhysX before querying.

            bool hitSomething = Physics.Raycast(
                origin,
                direction,
                out hit,
                maxRange,
                shootableMask
            );

            foreach (var h in histories)
                h.Restore();
            Physics.SyncTransforms(); // restore PhysX to the present.

            return hitSomething;
        }

        // server -> all: spawn a short-lived local marker at the hit point (a plain local object, not
        // networked; each machine makes its own).
        [Rpc(SendTo.ClientsAndHost)]
        private void ShowHitMarkerRpc(Vector3 point, Vector3 normal, HitKind kind)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            // strip the collider immediately (not deferred Destroy): for the one frame it lives, the collider
            // sits on the hit surface and on the host overlaps the target's CC, which depenetrates and pops the
            // player. removing it the same frame avoids that.
            DestroyImmediate(marker.GetComponent<Collider>());
            // lift the sphere off the surface by its radius so it isn't half-buried (and hidden) in the mesh.
            marker.transform.position = point + normal * (markerSize * 0.5f);
            marker.transform.localScale = Vector3.one * markerSize;
            marker.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(kind);
            Destroy(marker, markerLifetime);
        }

        // green = player, yellow = NPC, red = world. each colour is one shared, lazily-created material.
        private static Material MarkerMaterial(HitKind kind)
        {
            ref Material slot = ref kind == HitKind.Player
                ? ref _playerMarkerMaterial
                : ref kind == HitKind.Npc ? ref _npcMarkerMaterial : ref _worldMarkerMaterial;
            if (slot == null)
            {
                Color c = kind switch
                {
                    HitKind.Player => Color.green,
                    HitKind.Npc => Color.yellow,
                    _ => Color.red,
                };
                slot = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = c };
            }
            return slot;
        }
    }
}
