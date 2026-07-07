using Unity.Netcode;

namespace FriendSlop.Game
{
    // one player's running match score, replicated in ScoreManager's NetworkList so every client can
    // render the scoreboard. small blittable struct (no managed refs), as NetworkList requires.
    //
    // kills/survivals accumulate across the whole match (CS-style running stats); the per-round transient
    // state (was-I-a-criminal-this-round / am-I-still-up) lives on the player itself, not here.
    public struct PlayerScore : INetworkSerializable, System.IEquatable<PlayerScore>
    {
        public ulong ClientId;
        public int Kills; // criminals this player eliminated (as a sniper), all rounds
        public int Survivals; // rounds this player lived to the end as a criminal
        public float Total; // Kills * pointsPerKill + Survivals * survivalPoints (+ npc penalties)

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ClientId);
            s.SerializeValue(ref Kills);
            s.SerializeValue(ref Survivals);
            s.SerializeValue(ref Total);
        }

        // NetworkList requires IEquatable to diff its contents when replicating changes
        public bool Equals(PlayerScore other) =>
            ClientId == other.ClientId
            && Kills == other.Kills
            && Survivals == other.Survivals
            && Total.Equals(other.Total);
    }
}
