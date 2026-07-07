namespace FriendSlop.Player
{
    // The player's team/role. Data, not a subclass tree: ability components gate on this value, so a per-round
    // rotation flips a flag rather than rebuilding the player.
    public enum PlayerRole
    {
        Sniper,
        Criminal,

        // hunter team's single spotter. For now (no binoculars) mechanically identical to a Sniper (scopes,
        // fires, spawns on the rooftop) and differs only by joining the sketch phase. "Is this a shooter?"
        // checks accept both (see IsHunter). Its own value so the spotter divergence has somewhere to hang.
        Witness,
    }

    public static class PlayerRoleExtensions
    {
        // the hunter-team roles that scope + shoot (Sniper and, for now, Witness). Gate on this instead of
        // comparing to Sniper directly, so widening "who can shoot" is one place.
        public static bool IsHunter(this PlayerRole role) =>
            role == PlayerRole.Sniper || role == PlayerRole.Witness;
    }

    // which damage zone a bone collider is: Head = instant kill, Body = costs several hits.
    public enum HitZone
    {
        Body,
        Head,
    }

    // A character's deliberate, discrete, non-locomotion body state: the "pose" axis, layered on top of the
    // continuous Speed axis. The two coexist (walk + handstand at once), and a pose holds while moving/mid-air.
    // See GDD "Hit Registration".
    //
    // One channel generalizes every chosen body state (aim + exotic poses are the same kind: discrete, chosen,
    // override locomotion), so they share an enum rather than a bool each. Rides the per-tick StatePayload like
    // the old Scoped bool did, so it reconciles/interpolates for free with no new RPC/NetworkVariable.
    //
    // It must replicate (not stay local): the server's Animator plays it too, so the per-bone hitboxes are
    // posed correctly when HitboxHistory rewinds them. A handstand played only locally would leave the server's
    // skeleton standing, giving grossly wrong hits. So the pose is server-authoritative BECAUSE it's a hitbox.
    // byte-backed to keep the wire cost at 1 byte.
    public enum CharacterPose : byte
    {
        None = 0,  // pure locomotion, no override. Default; sends nothing extra while held.
        Scoped,    // sniper aim stance (folds in the former Scoped bool). Hunter-only.

        // criminal exotic poses (GDD: "handstand, split, chairfreeze, taunt"). Full-body override clips on the
        // Pose layer; the movement controller stays upright, so shots hit the posed limbs while walls collide.
        Handstand,
        Shoelace,  // tying shoelace, also an NPC-mimicry blend-in pose.
        Split,
    }
}
