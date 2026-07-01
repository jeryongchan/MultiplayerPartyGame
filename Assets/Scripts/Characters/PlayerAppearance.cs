using System;
using Unity.Netcode;

namespace FriendSlop.Characters
{
    // a character's full look as a compact list of per-slot variant indices; the only thing that crosses
    // the network for appearance (never mesh/texture data). slot i of this struct pairs with slot i of
    // CharacterAppearanceCatalog: the stored value is the index into that slot's variant array, or Hidden
    // for an empty slot. each machine resolves indices to meshes through its own catalog copy, so only a
    // handful of bytes travel per player.
    //
    // stored as sbyte[]: one byte per slot covers up to 127 variants (far more than any pack has) and
    // leaves negatives for the Hidden sentinel. length is variable so a larger modular pack (more slots)
    // needs no code change, the array just grows to match the catalog.
    [Serializable]
    public struct PlayerAppearance : INetworkSerializable, IEquatable<PlayerAppearance>
    {
        // sentinel index meaning "this slot shows nothing" (renderer disabled)
        public const sbyte Hidden = -1;

        // per-slot chosen variant index (or Hidden). position matches CharacterAppearanceCatalog.Slots.
        public sbyte[] slots;

        public PlayerAppearance(sbyte[] slots)
        {
            this.slots = slots;
        }

        public bool IsValid => slots != null;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            // length-prefixed so the reader allocates the right array; keeps the wire format self-describing
            // and future-proof if slot count changes between builds mid-development.
            int length = slots != null ? slots.Length : 0;
            serializer.SerializeValue(ref length);

            if (serializer.IsReader)
            {
                slots = new sbyte[length];
            }

            for (int i = 0; i < length; i++)
            {
                serializer.SerializeValue(ref slots[i]);
            }
        }

        public bool Equals(PlayerAppearance other)
        {
            if (slots == null || other.slots == null)
            {
                return slots == other.slots;
            }

            if (slots.Length != other.slots.Length)
            {
                return false;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != other.slots[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj) => obj is PlayerAppearance other && Equals(other);

        public override int GetHashCode()
        {
            if (slots == null)
            {
                return 0;
            }

            int hash = 17;
            foreach (sbyte s in slots)
            {
                hash = hash * 31 + s;
            }

            return hash;
        }
    }
}
