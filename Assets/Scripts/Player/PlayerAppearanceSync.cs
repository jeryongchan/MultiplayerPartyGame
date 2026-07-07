using FriendSlop.Characters;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative appearance. owns the replicated look (a small index struct, never mesh data), rolls
    // a fresh one each round, and lets a criminal steal a garment from an NPC. split out of the controller so
    // it stays pure movement netcode.
    public class PlayerAppearanceSync : NetworkBehaviour
    {
        [SerializeField]
        private CharacterAppearanceApplier appearanceApplier; // paints the mesh from Appearance; on the Character child.

        // shared index -> mesh table. same project asset on every machine: the server rolls indices, clients resolve them.
        [SerializeField]
        private CharacterAppearanceCatalog appearanceCatalog;

        // this player's look, rolled by the server and replicated as a small index struct (never mesh data).
        // each machine paints from it via the shared catalog. server-write like Role.
        public readonly NetworkVariable<PlayerAppearance> Appearance =
            new NetworkVariable<PlayerAppearance>(writePerm: NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                Roll(0); // initial look; re-rolled each round so a new round is a fresh character.

            if (appearanceApplier != null)
            {
                if (appearanceCatalog != null)
                    appearanceApplier.SetCatalog(appearanceCatalog);
                Appearance.OnValueChanged += OnAppearanceChanged;
                // late joiners + the server already have a value: apply it now. may be default on the first
                // server frame; harmless, OnValueChanged repaints.
                if (Appearance.Value.IsValid)
                    appearanceApplier.Apply(Appearance.Value);
            }
        }

        public override void OnNetworkDespawn() => Appearance.OnValueChanged -= OnAppearanceChanged;

        // repaint this copy's mesh when the look replicates in or changes. runs on every machine.
        private void OnAppearanceChanged(PlayerAppearance _, PlayerAppearance appearance)
        {
            if (appearanceApplier != null)
                appearanceApplier.Apply(appearance);
        }

        // server-only. roll the look for `round`, seeded by (clientId, round): deterministic yet different each
        // round (mixing the round in is what stops the identical re-roll). every copy repaints via OnValueChanged.
        public void Roll(int round)
        {
            if (!IsServer || appearanceCatalog == null)
                return;
            int seed = unchecked((int)OwnerClientId * 73856093 ^ round * 19349663);
            Appearance.Value = CharacterAppearanceApplier.Roll(appearanceCatalog, new System.Random(seed));
        }

        // server-only. steal one garment from `source` (an NPC's look) into this player's, for the criminal's
        // disguise-steal. the slot is picked from `pool` seeded by `seed` (the NPC index), so the same NPC
        // always yields the same piece everywhere (deterministic, no networking). one piece per punch keeps the
        // disguise from being too strong, so the sketch still matters. only that slot changes; every copy
        // repaints via OnValueChanged. returns the stolen slot index (-1 if none) so the caller can strip it
        // off the NPC. names missing from the catalog are skipped.
        public int StealOneGarment(PlayerAppearance source, int seed, params string[] pool)
        {
            if (!IsServer || appearanceCatalog == null || !source.IsValid || pool == null || pool.Length == 0)
                return -1;

            var slots = appearanceCatalog.Slots;

            // resolve pool names to valid catalog slot indices (skip missing / out of range).
            var candidates = new System.Collections.Generic.List<int>(pool.Length);
            foreach (var name in pool)
            {
                int idx = System.Array.FindIndex(slots, s => s.childName == name);
                if (idx >= 0 && idx < source.slots.Length)
                    candidates.Add(idx);
            }
            if (candidates.Count == 0)
                return -1;

            // pick one, seeded by the NPC index so it's the same everywhere and per-NPC stable.
            int pick = candidates[new System.Random(seed * 83492791).Next(candidates.Count)];

            // start from the current look so we overwrite only the stolen slot.
            var current = Appearance.Value.IsValid && Appearance.Value.slots != null
                ? (sbyte[])Appearance.Value.slots.Clone()
                : new sbyte[appearanceCatalog.SlotCount];
            if (pick < current.Length)
                current[pick] = source.slots[pick]; // copy the NPC's variant for this slot (may be Hidden).

            Appearance.Value = new PlayerAppearance(current);
            return pick;
        }
    }
}
