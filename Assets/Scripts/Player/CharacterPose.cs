namespace FriendSlop.Player
{
    // a character's deliberate, discrete, non-locomotion body state; the "pose" axis of animation, layered
    // on top of the continuous locomotion (Speed) axis. the two are independent and coexist: a criminal can
    // be walking (locomotion) and handstanding (pose) at once, and a pose is maintained while moving or
    // mid-air.
    //
    // this is a single channel generalizing every chosen upper-/whole-body state: the sniper's aim
    // (Scoped) and the criminal's exotic poses are the same kind of state (discrete, chosen,
    // override locomotion), so they share one enum rather than a bool-per-pose. it rides the per-tick
    // StatePayload exactly like the old Scoped bool did, so it reconciles/interpolates for free and adds no
    // new RPC or NetworkVariable.
    //
    // why it must replicate (not stay local cosmetic): the server's Animator plays this pose too, so the
    // per-bone hitboxes (children of the animated rig) are posed correctly when HitboxHistory records/rewinds
    // them. a handstand that only played locally would leave the server's skeleton standing, grossly wrong
    // hits. so the pose is server-authoritative animation state because it is also a hitbox.
    //
    // byte-backed to keep the wire cost at 1 byte (matches HitZone).
    public enum CharacterPose : byte
    {
        // pure locomotion: idle/walk/run only, no override. the default; sends nothing extra while held.
        None = 0,

        // sniper aim stance (folds in the former Scoped bool). drives the upper-body Aim layer + the body's
        // face-aim/strafe facing. hunter-only; a criminal never enters this pose.
        Scoped,

        // criminal exotic poses. each is a full-body override clip on the Pose layer; the movement
        // CharacterController stays upright (only the mesh + bone colliders take the pose), so shots hit
        // the real posed limbs while walls still collide.
        Handstand,
        Shoelace, // tying shoelace, also an NPC-mimicry blend-in pose
        Split,
    }
}
