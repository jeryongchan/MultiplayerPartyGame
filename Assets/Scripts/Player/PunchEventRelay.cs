using UnityEngine;

namespace FriendSlop.Player
{
    // bridges the punch clip's animation event to CriminalMelee. animation events only fire on components on
    // the Animator's own GameObject (the Character mesh), but CriminalMelee lives on the player root. this
    // sits on the Character to receive the event, then forwards it up.
    public class PunchEventRelay : MonoBehaviour
    {
        private CriminalMelee _melee;

        private void Awake() => _melee = GetComponentInParent<CriminalMelee>();

        // fired by the "OnPunchContact" animation event on Skeleton_01_Attack_Punch_001, at the contact frame.
        public void OnPunchContact() => _melee?.OnPunchContact();
    }
}
