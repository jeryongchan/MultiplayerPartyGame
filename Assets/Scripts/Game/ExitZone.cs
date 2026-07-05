using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // the street's Exit (GDD Map: "the far end of the street has the Exit"). a trigger volume at the end of
    // the street; a live criminal entering it during Hunt escapes, banks the criminal team's escape point
    // (GDD Resolution: "criminal reaches exit gives +1 criminal team") and is removed from the round like a
    // death (hidden + spectating), so snipers can no longer hit them and they aren't double-counted at
    // Hunt's end.
    //
    // server-authoritative: every player object exists on the host too (server owns all NetworkObjects), so
    // the host's copy fires OnTriggerEnter and we resolve the escape there; clients never decide. guarded to
    // the server, to Hunt, and to a live Criminal, so nothing happens for snipers, dead criminals, NPCs, or a
    // criminal wandering in before the gate opens.
    //
    // setup: put this on a GameObject in GameScene with a trigger Collider (Is Trigger = ON) spanning the
    // exit mouth. needs a Rigidbody somewhere in the pair for trigger callbacks; a CharacterController
    // (on the player) counts, so a plain trigger collider here is enough.
    [RequireComponent(typeof(Collider))]
    public class ExitZone : MonoBehaviour
    {
        private void Reset()
        {
            // author convenience: make the collider a trigger the moment the component is added
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            // only the server resolves the escape (it owns the authoritative game state). every player object
            // is present on the host, so the host's trigger copy is the one that matters; client copies bail.
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            // only escapes during the Hunt count; a criminal can't "reach the exit" before the gate opens
            if (GameFlowManager.Instance == null || !GameFlowManager.Instance.IsHunt)
                return;

            // the controller is on the player root (with the CharacterController); the collider that entered
            // may be a child bone hitbox, so walk up to find it.
            if (!other.TryGetComponent(out NetworkPlayerController player))
                player = other.GetComponentInParent<NetworkPlayerController>();
            if (player == null)
                return;

            // only a still-live criminal escapes. snipers/witness and already-downed criminals are ignored.
            if (player.Role.Value != PlayerRole.Criminal || !player.IsAlive.Value)
                return;

            ScoreManager.Instance?.RecordCriminalEscape(player.OwnerClientId);
            player.SetAlive(false); // remove from the round: hidden + spectating, and not counted again

            // if that was the last criminal in play, end Hunt now instead of running out the clock
            GameFlowManager.Instance?.EndHuntIfNoCriminalsLeft();
        }
    }
}
