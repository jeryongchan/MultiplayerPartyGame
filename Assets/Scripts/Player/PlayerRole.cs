namespace FriendSlop.Player
{
    public enum PlayerRole
    {
        Sniper,
        Criminal,

        // hunter team's single spotter. for now (no binoculars) the Witness is mechanically identical to
        // a Sniper: scopes, fires, spawns on the rooftop, and differs only by also participating in the
        // sketch phase. gameplay checks that read "is this a shooter?" accept both Sniper and Witness
        // (see PlayerRoleExtensions.IsHunter). kept as its own value so the sketch/spotter divergence has
        // somewhere to hang later.
        Witness,
    }

    public static class PlayerRoleExtensions
    {
        // true for the hunter-team roles that scope and shoot from the rooftop (Sniper and, for now, the
        // Witness). ability components gate on this instead of comparing to Sniper directly, so widening
        // "who can shoot" to include the Witness is one place, not scattered equality checks.
        public static bool IsHunter(this PlayerRole role) =>
            role == PlayerRole.Sniper || role == PlayerRole.Witness;
    }
}
