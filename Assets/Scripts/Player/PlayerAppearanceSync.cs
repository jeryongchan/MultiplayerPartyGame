using FriendSlop.Characters;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative character appearance for a player. owns the replicated look (a small index struct,
    // never mesh data), rolls a fresh one each round, and lets a criminal steal a garment from an NPC. split out
    // of NetworkPlayerController so the controller is pure movement netcode: this component is the
    // single home for "what does this player look like, and how does that change."
    public class PlayerAppearanceSync : NetworkBehaviour
    {
        // paints the modular character mesh from the replicated PlayerAppearance. lives on the Character child.
        [SerializeField]
        private CharacterAppearanceApplier appearanceApplier;

        // shared lookup table mapping appearance indices to meshes. must be the same asset on every machine
        // (it's a project asset, so it is). the server rolls indices against it; every client resolves them.
        [SerializeField]
        private CharacterAppearanceCatalog appearanceCatalog;

        // this player's randomized look, rolled once by the server on spawn and replicated to every copy as a
        // small index struct (never mesh data). each machine paints its own character mesh from it via the
        // shared catalog. server-write like Role; read + applied by all.
        public readonly NetworkVariable<PlayerAppearance> Appearance =
            new NetworkVariable<PlayerAppearance>(writePerm: NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            // roll the initial look (round 0). re-rolled each round via Roll() so a new round is a fresh
            // character. replicated via the NetworkVariable; every copy paints from it.
            if (IsServer)
                Roll(0);

            // paint the character mesh from the replicated appearance, and repaint if it changes later
            if (appearanceApplier != null)
            {
                if (appearanceCatalog != null)
                    appearanceApplier.SetCatalog(appearanceCatalog);
                Appearance.OnValueChanged += OnAppearanceChanged;
                // late joiners (and the server itself) already have a value here, apply it now. it may be
                // uninitialized (default) on the very first server frame; harmless, the OnValueChanged repaints.
                if (Appearance.Value.IsValid)
                    appearanceApplier.Apply(Appearance.Value);
            }
        }

        public override void OnNetworkDespawn()
        {
            Appearance.OnValueChanged -= OnAppearanceChanged;
        }

        // appearance replicated in (or changed): repaint the character mesh on this copy. runs on every
        // machine, owner, server, and remotes, so all see the same look from the same index struct.
        private void OnAppearanceChanged(PlayerAppearance _, PlayerAppearance appearance)
        {
            if (appearanceApplier != null)
                appearanceApplier.Apply(appearance);
        }

        // server-only. roll this player's replicated appearance for the given round, seeded by (clientId,
        // round) so it's deterministic (reproducible/debuggable) yet genuinely different each round; mixing
        // the round in is what stops it re-rolling the identical outfit. every copy repaints via OnValueChanged.
        public void Roll(int round)
        {
            if (!IsServer || appearanceCatalog == null)
                return;
            int seed = unchecked((int)OwnerClientId * 73856093 ^ round * 19349663);
            Appearance.Value = CharacterAppearanceApplier.Roll(appearanceCatalog, new System.Random(seed));
        }

        // server-only. steal one garment from source (an NPC's look) into this player's, the
        // criminal's disguise-steal. which slot is taken is chosen from pool seeded by
        // seed (the NPC index), so the same NPC always yields the same piece on every
        // machine, deterministic, no networking. taking one piece per punch (not the whole outfit) keeps the
        // disguise from being too strong, so the sketch phase still matters: a full look takes several NPCs.
        //
        // only that one slot changes; the rest of this player's look is kept. writes the replicated Appearance,
        // so every copy repaints via OnValueChanged (zero mesh bandwidth, just the index struct). no-op off
        // the server. names not found in the catalog are skipped when narrowing the pool.
        //
        // returns the catalog slot index that was stolen, or -1 if nothing was (so the caller can tell the
        // NPC which garment to remove). the pick is deterministic in seed.
        public int StealOneGarment(PlayerAppearance source, int seed, params string[] pool)
        {
            if (!IsServer || appearanceCatalog == null || !source.IsValid || pool == null || pool.Length == 0)
                return -1;

            var slots = appearanceCatalog.Slots;

            // resolve the pool names to valid catalog slot indices (skip any missing / out of range)
            var candidates = new System.Collections.Generic.List<int>(pool.Length);
            foreach (var name in pool)
            {
                int idx = System.Array.FindIndex(slots, s => s.childName == name);
                if (idx >= 0 && idx < source.slots.Length)
                    candidates.Add(idx);
            }
            if (candidates.Count == 0)
                return -1;

            // pick one candidate slot, seeded by the NPC index so it's the same everywhere and per-NPC stable
            int pick = candidates[new System.Random(seed * 83492791).Next(candidates.Count)];

            // start from this player's current look so we overwrite only the stolen slot
            var current = Appearance.Value.IsValid && Appearance.Value.slots != null
                ? (sbyte[])Appearance.Value.slots.Clone()
                : new sbyte[appearanceCatalog.SlotCount];
            if (pick < current.Length)
                current[pick] = source.slots[pick]; // copy the NPC's variant for this one slot (may be Hidden)

            Appearance.Value = new PlayerAppearance(current);
            return pick;
        }
    }
}
