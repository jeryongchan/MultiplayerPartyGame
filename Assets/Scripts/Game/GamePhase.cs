namespace FriendSlop.Game
{
    // the single, linear match state every system reads off of. server-owned (only GameFlowManager
    // writes it); replicated to all clients as one enum. subsystems (movement gating, gate, spawn-area
    // walls, scoring, sketch) subscribe to phase changes and gate their own behaviour instead of knowing
    // about each other; the flow manager is a conductor.
    //
    // progression is one-directional and loops:
    //   Lobby -> RoleAssign -> Sketch -> SketchReveal -> Hunt -> Resolution -> (rotate) -> RoleAssign ...
    public enum GamePhase
    {
        // waiting for players; host presses Start to leave this phase. the only non-timed phase.
        Lobby,

        // brief: server assigns/rotates roles and teleports everyone to their spawn area
        RoleAssign,

        // witness sketches the criminals (private crime-scene view). everyone else is contained in
        // their spawn area by scene geometry (invisible walls) but can move/interact freely within it.
        Sketch,

        // reporter cutscene: all sketches revealed to everyone at once. input is frozen here.
        SketchReveal,

        // main gameplay: gate opens, spawn-area walls drop, movement/shooting live, scoring accrues
        Hunt,

        // tally the round, show the scoreboard, then loop back to RoleAssign with rotated roles
        Resolution,
    }
}
