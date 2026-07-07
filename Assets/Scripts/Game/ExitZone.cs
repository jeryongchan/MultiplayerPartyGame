using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Game
{
    // the street's Exit. a trigger volume at the end of the street; a live criminal entering it during
    // Hunt escapes (banks the criminal team's escape point) and is removed from the round like a death
    // (hidden + spectating), so snipers can no longer hit them and they aren't double-counted at Hunt's end.
    //
    // server-authoritative: every player object exists on the host too, so the host's copy fires
    // OnTriggerEnter and resolves the escape there, clients never decide.
    //
    // setup: put on a GameObject in GameScene with a trigger Collider spanning the exit mouth. needs a
    // Rigidbody somewhere in the pair for trigger callbacks; the player's CharacterController counts.
    [RequireComponent(typeof(Collider))]
    public class ExitZone : MonoBehaviour
    {
        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true; // author convenience: make it a trigger the moment it's added
        }

        private void OnTriggerEnter(Collider other)
        {
            // only the server resolves the escape; every player object is present on the host, so the
            // host's trigger copy is the one that matters, client copies bail.
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            // only escapes during Hunt count, a criminal can't "reach the exit" before the gate opens.
            if (GameFlowManager.Instance == null || !GameFlowManager.Instance.IsHunt)
                return;

            // the controller is on the player root; the collider that entered may be a child bone hitbox,
            // so walk up to find it.
            if (!other.TryGetComponent(out NetworkPlayerController player))
                player = other.GetComponentInParent<NetworkPlayerController>();
            if (player == null)
                return;

            // only a still-live criminal escapes; snipers/witness and already-downed criminals are ignored.
            if (player.Role.Value != PlayerRole.Criminal || !player.Health.IsAlive.Value)
                return;

            ScoreManager.Instance?.RecordCriminalEscape(player.OwnerClientId);
            player.Health.SetAlive(false); // remove from the round: hidden + spectating, not counted again

            // if that was the last criminal in play, end Hunt now instead of running out the clock.
            GameFlowManager.Instance?.EndHuntIfNoCriminalsLeft();
        }
    }
}
