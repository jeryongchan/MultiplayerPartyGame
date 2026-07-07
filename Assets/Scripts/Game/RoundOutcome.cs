namespace FriendSlop.Game
{
    // which team won a resolved round, replicated so every client shows the same verdict.
    public enum RoundOutcome
    {
        None, // no round resolved yet (fresh match / lobby)
        Hunters, // hunter team (snipers + witness) scored higher
        Criminals, // criminal team scored higher
        Draw, // equal totals
    }
}
