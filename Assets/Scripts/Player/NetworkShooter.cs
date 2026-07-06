using FriendSlop.Crowd;
using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // minimal server-authoritative hitscan. owner left-clicks, builds a ray from its camera,
    // sends origin+direction to the server, server raycasts, logs who was hit, and broadcasts the
    // authoritative hit point so every machine drops a debug marker there. no cooldown, damage, or
    // lag-compensation yet.
    //
    // mirrors NetworkPlayerController's model: same shared script on every copy, but only the owner
    // reads input and sends, and only the server decides the hit (clients never declare "I hit X").
    public class NetworkShooter : RoleGatedAbility
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

        // recoil per shot, applied to the owner's camera (which also steers aim, so shots walk off-target
        // under rapid fire). vertical kick is consistent; horizontal kick is randomized either way each shot.
        [SerializeField]
        private float recoilPitch = 2.5f;

        [SerializeField]
        private float recoilYaw = 0.6f;

        // master switch for server-side rewind. turn off to A/B test: with lag + a moving target, off
        // makes shots miss behind the target (server raycasts the present), on makes them hit (server
        // rewinds to where the shooter saw it). server-only effect; toggle it on the host's prefab.
        [SerializeField]
        private bool lagCompensation = true;

        // upper bound on how far back the server will rewind, in seconds. caps the lag-comp window so a
        // very high-latency (or spoofed) client can't make the server rewind arbitrarily far and shoot
        // people who were long gone from a position. keep at or under HitboxHistory.historySeconds.
        // Valve/Unity recommend ~0.25-0.5s; peer-hosted games keep it tight to limit host advantage.
        [SerializeField]
        private float maxRewindSeconds = 0.5f;

        // how far in the past this client renders remote players (entity interpolation). the shooter was
        // looking at remotes this far behind the server clock, so the rewind target is
        // (serverTick - shooterRTT - interpolationDelay). match NetworkPlayerController's value.
        [SerializeField]
        private float interpolationDelay = 0.1f;

        [SerializeField]
        private float markerSize = 0.2f;

        [SerializeField]
        private float markerLifetime = 5f;

        // shared materials for all markers (so we don't leak a Material per shot, or render pink).
        // green = hit a player, red = hit anything else (wall/world).
        private static Material _playerMarkerMaterial;
        private static Material _npcMarkerMaterial;
        private static Material _worldMarkerMaterial;

        // hunters shoot (Sniper + Witness for now). the role/alive/phase gate lives in RoleGatedAbility.CanUse;
        // this only narrows which roles: any hunter.
        protected override bool RolePredicate(PlayerRole role) => role.IsHunter();

        private void Update()
        {
            // only the owner fires, and only on the click frame (an edge event, like jump)
            if (!IsOwner)
                return;

            // owner-side role gate: a non-Sniper never even builds a shot (cheap, responsive). the server
            // re-checks below so a modified client that bypasses this still can't fire.
            if (!CanUse)
                return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
                return;

            // fire on left-click or the F key (alt fire for laptops where left-click while holding
            // right-click is awkward on the trackpad). both are edge events, the press frame only.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool firePressed =
                mouse.leftButton.wasPressedThisFrame || (kb != null && kb.fKey.wasPressedThisFrame);
            if (!firePressed)
                return;

            // sniper can only fire while scoped. right-click is now an AWP-style toggle (cycled in
            // NetworkPlayerController), not a held button, so read the resulting state from there.
            if (requireScopeToFire && !Controller.IsScoped)
                return;

            // fire-rate gate: ignore the press if we're still cooling down from the last shot
            if (!TryConsumeCooldown(fireCooldown))
                return;

            // build the shot from the owner's camera: through screen centre, the standard FPS aim.
            // only the owner has this camera, so compute it here and send the result to the server.
            Camera cam = Camera.main;
            if (cam == null)
                return;

            Ray aim = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));

            // kick the camera (and thus aim) on the confirmed shot. randomize horizontal direction so
            // the muzzle climbs while drifting unpredictably left/right under rapid fire.
            if (ThirdPersonCamera.Instance != null)
                ThirdPersonCamera.Instance.AddRecoil(recoilPitch, Random.Range(-recoilYaw, recoilYaw));

            // send only where we aimed. the server owns the when: it derives the rewind tick from its own
            // clock and our measured latency, so we never send a tick from the client's (offset) clock.
            SubmitShootServerRpc(aim.origin, aim.direction);
        }

        // client to server: only where the owner aimed. the server alone decides when to rewind to, then
        // raycasts that historical world. the client never declares what it hit, nor a tick (which would
        // be in the client's own offset clock).
        [Rpc(SendTo.Server)]
        private void SubmitShootServerRpc(Vector3 origin, Vector3 direction)
        {
            // authoritative role gate: the server is the source of truth. a hacked client that strips its
            // owner-side check still lands here, where Role was written by the server from RoleRegistry,
            // so a Criminal physically cannot fire (GDD: server-authoritative).
            if (!CanUse)
                return;

            if (!ResolveShot(origin, direction, out RaycastHit hit))
                return;

            HitKind kind = Classify(hit.collider, out NetworkObject player, out CrowdNpcHitbox npc);

            switch (kind)
            {
                case HitKind.Player:
                    // which zone was struck: a head collider is lethal, any other bone is a body hit.
                    // defaults to Body if the collider somehow lacks a BoneHitbox marker.
                    HitZone zone = hit.collider.GetComponent<BoneHitbox>() is { } bh ? bh.Zone : HitZone.Body;
                    Debug.Log($"[Shoot] client {OwnerClientId} hit client {player.OwnerClientId} ({zone})");
                    // a hit only scores/downs during Hunt, and only against a criminal who's still up
                    // (hitting a fellow hunter, or anyone outside Hunt, does nothing but drop a marker)
                    if (GameFlowManager.Instance != null && GameFlowManager.Instance.IsHunt
                        && player.TryGetComponent(out NetworkPlayerController victim)
                        && victim.Role.Value == PlayerRole.Criminal && victim.Health.IsAlive.Value)
                    {
                        // head = instant down; body = costs several hits. only score the kill on the blow
                        // that actually downs them (TakeHit returns true exactly once).
                        if (victim.Health.TakeHit(zone))
                        {
                            ScoreManager.Instance?.RecordCriminalKill(OwnerClientId);
                            // if that downed the last criminal, end Hunt now rather than waiting out the clock
                            GameFlowManager.Instance?.EndHuntIfNoCriminalsLeft();
                        }
                    }
                    break;
                case HitKind.Npc:
                    if (GameFlowManager.Instance != null && GameFlowManager.Instance.IsHunt)
                        ScoreManager.Instance?.RecordNpcHit(OwnerClientId);
                    // replicate the kill by index (the shared identity) so every machine fades out its own
                    // copy of the same pedestrian; the server's Npc instance is local-only.
                    if (npc.Npc != null)
                        NpcKilledRpc(npc.Npc.Index);
                    break;
            }

            // broadcast the authoritative hit point + surface normal + what was hit, so every rendering
            // machine marks the same spot (the normal lifts the marker off the surface; the kind colours
            // it: green = player, yellow = NPC, red = world/wall).
            ShowHitMarkerRpc(hit.point, hit.normal, kind);
        }

        // server to all rendering machines: the NPC with this stream index was shot. each machine kills its
        // own local copy (freeze + fade + despawn) through the deterministic crowd's shared index. like the
        // hit marker, this replicates the event and lets every machine produce the visual locally.
        [Rpc(SendTo.ClientsAndHost)]
        private void NpcKilledRpc(int index) => CrowdManager.Instance?.KillNpc(index);

        // what a shot landed on. the shooter resolves this server-side from the hit collider: a player has
        // a NetworkObject in its parents; an NPC has a CrowdNpcHitbox; anything else is world/wall.
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

        // the tick the server rewinds to for this shooter's shots = serverTick - (RTT/2 + interpDelay),
        // clamped to the rewind window. the shooter saw remotes one network trip (RTT/2) plus the
        // interpolation delay in the past; the shot's own trip to the server is already elapsed by the
        // time we process it, so we don't add it again (using full RTT over-rewinds, making you aim
        // behind a moving target). server-only.
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

        // server-only core of a shot: optionally rewind all other players' hitboxes to the time the
        // shooter was looking at, raycast that world, then restore the present. returns the hit (if any).
        // all times come from the server's own clock, no client clock-offset bug.
        private bool ResolveShot(Vector3 origin, Vector3 direction, out RaycastHit hit)
        {
            int rewindTick = CurrentRewindTick();

            // rewind every other player's hitbox to that tick (the shooter's own isn't a valid target and
            // its present position is irrelevant to a camera-origin ray), flush PhysX, raycast, restore.
            // when lagCompensation is off, we skip the rewind and raycast the present world (the bug we fix).
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
            Physics.SyncTransforms(); // restore PhysX to the present world.

            return hitSomething;
        }

        // server to all machines: spawn a short-lived local marker at the hit point. it's a plain local
        // GameObject, not networked, so each machine makes its own (the usual pattern for cosmetic
        // effects: replicate the event, spawn the visual locally).
        [Rpc(SendTo.ClientsAndHost)]
        private void ShowHitMarkerRpc(Vector3 point, Vector3 normal, HitKind kind)
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
            marker.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(kind);
            Destroy(marker, markerLifetime);
        }

        // green = player, yellow = NPC, red = world/wall. each colour is one shared, lazily-created material.
        private static Material MarkerMaterial(HitKind kind)
        {
            ref Material slot = ref kind == HitKind.Player
                ? ref _playerMarkerMaterial
                : ref kind == HitKind.Npc
                    ? ref _npcMarkerMaterial
                    : ref _worldMarkerMaterial;
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
