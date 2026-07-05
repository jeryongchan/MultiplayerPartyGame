using UnityEngine;

namespace FriendSlop.Player
{
    // bridges the punch clip's animation event to CriminalMelee. animation events are delivered
    // only to components on the Animator's own GameObject (the Character mesh), not its parents, but
    // CriminalMelee lives on the player root (where the NetworkObject/Role are). this relay sits on the
    // Character so it receives the event, then forwards it up to the root's CriminalMelee.
    public class PunchEventRelay : MonoBehaviour
    {
        private CriminalMelee _melee;

        private void Awake() => _melee = GetComponentInParent<CriminalMelee>();

        // invoked by the "OnPunchContact" animation event on Skeleton_01_Attack_Punch_001 at the contact frame
        public void OnPunchContact() => _melee?.OnPunchContact();
    }
}
