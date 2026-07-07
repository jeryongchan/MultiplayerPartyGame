namespace FriendSlop.Game
{
    // the single, linear match state every system reads off. server-owned (only GameFlowManager writes
    // it), replicated to all clients as one enum. subsystems gate their own behaviour off phase changes
    // instead of knowing about each other; the flow manager is a conductor.
    //
    // loops: Lobby -> RoleAssign -> Sketch -> SketchReveal -> Hunt -> Resolution -> (rotate) -> RoleAssign
    public enum GamePhase
    {
        Lobby, // waiting for players; host presses Start to leave. the only non-timed phase.
        RoleAssign, // server assigns/rotates roles and teleports everyone to their spawn area.

        // witness sketches the criminals (private crime-scene view). everyone else is contained in their
        // spawn area by scene geometry (invisible walls) but can move/interact freely within it.
        Sketch,

        SketchReveal, // reporter cutscene: all sketches revealed at once. input frozen here.
        Hunt, // main gameplay: gate opens, spawn walls drop, movement/shooting live, scoring accrues.
        Resolution, // tally the round, show the scoreboard, then loop back to RoleAssign with rotated roles.
    }
}
