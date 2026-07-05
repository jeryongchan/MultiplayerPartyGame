using FriendSlop.Crowd;
using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // the criminal's melee: left-click near a crowd NPC to punch it down. server-authoritative and modelled
    // exactly like NetworkShooter: the owner reads input and asks, the server decides, and the
    // result is replicated by the NPC's stream index so every machine drops the same pedestrian locally (the
    // deterministic crowd has no per-NPC networking; we replicate the reactive event, not the NPC).
    //
    // reach is a real trigger collider (see MeleeRangeTracker on a child), so it's visible and
    // drag-to-resize in the editor instead of a runtime OverlapSphere. the tracker only maintains "who's in
    // range"; this component owns the timing (punch contact frame) and the authority (server RPC).
    //
    // this first pass only downs the NPC (fall + lie for the round) and plays the criminal's punch. stealing
    // the NPC's hat/outwear onto the criminal is a later step layered on the same RPC.
    //
    // gated to Criminal role + Hunt phase, re-checked on the server so a hacked client can't melee as a hunter
    // or outside Hunt.
    public class CriminalMelee : NetworkBehaviour
    {
        // the trigger volume that tracks NPCs in punch reach. a child with a trigger collider +
        // MeleeRangeTracker. if unset, found in children at Awake.
        [SerializeField]
        private MeleeRangeTracker rangeTracker;

        // half-angle of the melee cone (degrees). an NPC must be within this of straight ahead to be
        // hit, so you punch what you face, not someone beside you who happens to be in the trigger.
        [SerializeField]
        private float coneHalfAngle = 60f;

        // seconds between punches
        [SerializeField]
        private float cooldown = 1f;

        // how long the criminal is rooted in place while punching (seconds). match the punch clip
        // length (~0.77s) so movement resumes right as the swing ends.
        [SerializeField]
        private float rootDuration = 0.77f;

        // Time.time of the next allowed punch. owner-only state.
        private float _nextTime;

        // sibling controller holding the replicated Role/phase we gate on
        private NetworkPlayerController _controller;
        // this player's animator (on the Character child), fired for the punch on every copy
        private Animator _animator;

        private static readonly int PunchHash = Animator.StringToHash("Punch");

        private void Awake()
        {
            _controller = GetComponent<NetworkPlayerController>();
            _animator = GetComponentInChildren<Animator>();
            if (rangeTracker == null)
                rangeTracker = GetComponentInChildren<MeleeRangeTracker>();
        }

        // criminals melee, only while alive and during Hunt. mirrors NetworkShooter.CanShoot but for the other
        // team. server re-checks in the RPC so this is authoritative, not just an owner-side convenience.
        private bool CanMelee =>
            _controller != null
            && _controller.Role.Value == PlayerRole.Criminal
            && _controller.IsAlive.Value
            && (GameFlowManager.Instance == null || GameFlowManager.Instance.IsHunt);

        private void Update()
        {
            if (!IsOwner)
                return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            if (!CanMelee)
                return;

            if (Time.time < _nextTime)
                return;
            _nextTime = Time.time + cooldown;

            // start the swing now (feedback for trying). it plays locally on the owner immediately and is
            // broadcast to everyone else. the actual hit is decided later, at the punch's contact frame, by
            // an animation event (OnPunchContact), so the NPC only goes down when the fist is extended, not
            // at the click.
            if (_animator != null)
                _animator.SetTrigger(PunchHash);
            PlaySwingServerRpc();

            // root the criminal in place for the swing so they can't glide while punching. owner drives its own
            // movement, so locking here (owner-side) is enough; the server simulates the same zero input.
            StopAllCoroutines();
            StartCoroutine(RootWhilePunching());
        }

        // hold the owner still for the punch, then release. uses the controller's MovementLocked flag, which
        // feeds zero move input through the same path as the cutscene/downed freeze.
        private System.Collections.IEnumerator RootWhilePunching()
        {
            if (_controller != null)
                _controller.MovementLocked = true;
            yield return new WaitForSeconds(rootDuration);
            if (_controller != null)
                _controller.MovementLocked = false;
        }

        // called by an animation event on the punch clip at the contact frame (fist fully extended). fires on
        // every copy of the criminal, but only the owner turns it into an authoritative hit request, so the
        // down lands at the moment of impact, and the server (not the client) still decides what was hit. we
        // send the nearest in-range NPC's index; the server re-validates before downing it.
        public void OnPunchContact()
        {
            if (!IsOwner || !CanMelee || rangeTracker == null)
                return;

            // pick the target on the owner from its own trigger + facing (the owner is the one whose reach the
            // punch represents). send the index; the server re-checks role/phase and downs it for everyone.
            Npc target = rangeTracker.GetBestTarget(transform.position, transform.forward, coneHalfAngle);
            if (target != null)
                SubmitHitServerRpc(target.Index);
        }

        // safety: never leave the player rooted if the object goes away mid-swing (despawn/scene change)
        public override void OnNetworkDespawn()
        {
            if (_controller != null)
                _controller.MovementLocked = false;
        }

        // owner to server: broadcast the swing to the other copies so everyone sees the criminal punch. the
        // owner already triggered its own animator locally (no need to round-trip its own swing).
        [Rpc(SendTo.Server)]
        private void PlaySwingServerRpc()
        {
            if (!CanMelee)
                return;
            PunchOthersRpc();
        }

        // owner to server, at the contact frame: the NPC index the owner's reach hit. server re-checks the gate
        // and replicates the down to everyone. (trusting the owner's index for an ambient NPC is low-stakes,
        // it only downs a pedestrian; the score-relevant player hits stay fully server-resolved elsewhere. the
        // server still enforces role/phase so a non-criminal can't down NPCs.)
        [Rpc(SendTo.Server)]
        private void SubmitHitServerRpc(int npcIndex)
        {
            if (!CanMelee)
                return;

            // replicate the reactive event by index so every machine downs its own copy of NPC #index
            // (no score/appearance yet, that's a later step)
            NpcDownedRpc(npcIndex);
        }

        // server to all machines: down NPC #index locally (fall + lie for the round) via the shared crowd index
        [Rpc(SendTo.ClientsAndHost)]
        private void NpcDownedRpc(int index) => CrowdManager.Instance?.DownNpc(index);

        // server to all machines except the owner (it already triggered its own swing locally on click): play
        // this criminal's punch on every other copy so all players see the swing. NotOwner keeps the owner
        // from re-triggering mid-swing (which could restart/stutter its own animation).
        [Rpc(SendTo.NotOwner)]
        private void PunchOthersRpc()
        {
            if (_animator != null)
                _animator.SetTrigger(PunchHash);
        }
    }
}
