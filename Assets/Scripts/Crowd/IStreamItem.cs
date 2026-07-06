namespace FriendSlop.Crowd
{
    // one item in a DeterministicStream, an NPC or a bus. the stream only needs to know when
    // an item has run its course so it can despawn it; the item computes that from the shared clock (its
    // position is a pure function of time), so Finished flips at the same instant on every
    // machine. everything else about the item is its own concern.
    public interface IStreamItem
    {
        // true once this item has completed its run and should be despawned
        bool Finished { get; }
    }
}
