using FriendSlop.Crowd;
using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // the criminal's melee: left-click near a crowd NPC to punch it down and steal a garment. modelled like
    // NetworkShooter: the owner reads input and asks, the server decides, and the result is replicated by the
    // NPC's stream index so every machine drops the same pedestrian locally (the deterministic crowd has no
    // per-NPC networking; we replicate the reactive event, not the NPC).
    //
    // reach is a real trigger collider (MeleeRangeTracker on a child), so it's drag-to-resize in the editor.
    // the tracker maintains "who's in range"; this owns the timing (contact frame) and authority (server RPC).
    // gated to Criminal + Hunt, re-checked on the server.
    public class CriminalMelee : RoleGatedAbility
    {
        [SerializeField]
        private MeleeRangeTracker rangeTracker; // NPCs in reach; found in children if unset.

        // half-angle of the melee cone (deg): an NPC must be within this of straight ahead to be hit.
        [SerializeField]
        private float coneHalfAngle = 60f;

        [SerializeField]
        private float cooldown = 1f;

        // seconds the criminal is rooted while punching. match the punch clip (~0.77s) so movement resumes as the swing ends.
        [SerializeField]
        private float rootDuration = 0.77f;

        private Animator _animator; // on the Character child, fired for the punch on every copy.

        private static readonly int PunchHash = Animator.StringToHash("Punch");

        protected override void Awake()
        {
            base.Awake();
            _animator = GetComponentInChildren<Animator>();
            if (rangeTracker == null)
                rangeTracker = GetComponentInChildren<MeleeRangeTracker>();
        }

        // only the Criminal melees. the alive/phase gate lives in RoleGatedAbility.CanUse.
        protected override bool RolePredicate(PlayerRole role) => role == PlayerRole.Criminal;

        private void Update()
        {
            if (!IsOwner)
                return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            if (!CanUse || !TryConsumeCooldown(cooldown))
                return;

            // start the swing now (feedback), locally + broadcast. the hit is decided later at the contact
            // frame by an animation event (OnPunchContact), so the NPC drops when the fist extends, not on click.
            if (_animator != null)
                _animator.SetTrigger(PunchHash);
            PlaySwingServerRpc();

            // root the criminal for the swing so they can't glide while punching (owner drives its own movement).
            StopAllCoroutines();
            StartCoroutine(RootWhilePunching());
        }

        // hold the owner still for the swing via MovementLocked (same zero-input path as the cutscene/downed freeze).
        private System.Collections.IEnumerator RootWhilePunching()
        {
            if (Controller != null)
                Controller.MovementLocked = true;
            yield return new WaitForSeconds(rootDuration);
            if (Controller != null)
                Controller.MovementLocked = false;
        }

        // fired by an animation event on the punch clip at the contact frame (fist extended). runs on every
        // copy, but only the owner turns it into an authoritative hit request: it sends the nearest in-range
        // NPC's index and the server re-validates before downing it.
        public void OnPunchContact()
        {
            if (!IsOwner || !CanUse || rangeTracker == null)
                return;

            // pick the target on the owner from its own trigger + facing (the owner is the one whose reach the
            // punch represents). send the index; the server re-checks role/phase and downs it for everyone.
            Npc target = rangeTracker.GetBestTarget(transform.position, transform.forward, coneHalfAngle);
            if (target != null)
                SubmitHitServerRpc(target.Index);
        }

        // safety: never leave the player rooted if it despawns mid-swing.
        public override void OnNetworkDespawn()
        {
            if (Controller != null)
                Controller.MovementLocked = false;
        }

        // owner -> server: broadcast the swing to the other copies (the owner already triggered its own locally).
        [Rpc(SendTo.Server)]
        private void PlaySwingServerRpc()
        {
            if (!CanUse)
                return;
            PunchOthersRpc();
        }

        // owner -> server at the contact frame: the NPC index the reach hit. trusting the owner's index for an
        // ambient NPC is low-stakes (it only downs a pedestrian; scored player hits stay server-resolved). the
        // server still enforces role/phase.
        [Rpc(SendTo.Server)]
        private void SubmitHitServerRpc(int npcIndex)
        {
            if (!CanUse)
                return;

            // steal one garment from that NPC onto the criminal (one piece per punch, so a full disguise takes
            // several NPCs and the sketch still matters). the piece is seeded by the NPC index (same NPC -> same
            // piece everywhere), and the NPC's look is regenerated from the crowd seed (pure function of index).
            // capture which slot was stolen so the NPC can visibly lose that same piece.
            int stolenSlot = -1;
            if (CrowdManager.Instance != null
                && CrowdManager.Instance.TryGetAppearance(npcIndex, out var npcLook))
            {
                stolenSlot = Controller.Appearances.StealOneGarment(npcLook, npcIndex,
                    "Hat", "Outwear", "Glasses", "Pants");
            }

            // down the NPC on every machine (fall + lie for the round) and strip the stolen slot from it, using
            // the same slot index so every machine removes the identical garment, no extra networked data.
            NpcDownedRpc(npcIndex, stolenSlot);
        }

        // server -> all: down NPC #index (fall + lie for the round) and strip the stolen slot (-1 = none) via
        // the shared crowd index, so every machine drops the same pedestrian minus the same garment.
        [Rpc(SendTo.ClientsAndHost)]
        private void NpcDownedRpc(int index, int stolenSlot) => CrowdManager.Instance?.DownNpc(index, stolenSlot);

        // server -> all except the owner (it already triggered its own on click): play the swing on every other
        // copy. NotOwner keeps the owner from re-triggering mid-swing and stuttering its animation.
        [Rpc(SendTo.NotOwner)]
        private void PunchOthersRpc()
        {
            if (_animator != null)
                _animator.SetTrigger(PunchHash);
        }
    }
}
