using System.Collections.Generic;
using FriendSlop.Characters;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // drives a continuous deterministic pedestrian stream. a single DeterministicStream: every
    // machine rebuilds the same crowd locally from the shared seed + network clock, so it costs (essentially)
    // one ulong of bandwidth regardless of crowd size. this class only supplies the crowd-specific pieces:
    // spawning an Npc, its seed-derived look and lateral offset, and the replicated kill/down/
    // steal reactions; the base owns the seed/clock plumbing and the spawn/despawn loops.
    public class CrowdManager : DeterministicStream
    {
        // the scene's single crowd manager, so the shooter's replicated kill can reach it on every machine
        // without a per-shooter reference. one crowd per scene, set on spawn and cleared on despawn.
        public static CrowdManager Instance { get; private set; }

        [Header("Spawning")]
        [SerializeField]
        private GameObject npcPrefab;

        [SerializeField]
        private CrowdPath path;

        [Header("Appearance")]
        // shared appearance catalog, the same asset players use. each NPC's look is rolled deterministically
        // from (seed, index) against it, so every machine paints the identical crowd with zero appearance
        // bandwidth (matches the deterministic-crowd design: traits are pure functions of seed+index).
        [SerializeField]
        private CharacterAppearanceCatalog appearanceCatalog;

        // seconds between consecutive NPCs entering the path. lower = denser crowd.
        [SerializeField]
        private float spawnInterval = 1.5f;

        // total sidewalk width to spread the crowd across (metres). offsets span [-w/2, +w/2].
        [SerializeField]
        private float sidewalkWidth = 4f;

        // fraction of NPCs that walk the path forwards (start to end). the rest walk it reversed (end to
        // start). 1 = everyone one way, 0.5 = even mix.
        [Range(0f, 1f)]
        [SerializeField]
        private float forwardFraction = 0.8f;

        [Header("Anti-clipping")]
        // how much to randomly nudge each NPC's lateral position off the even golden-ratio spread, as a
        // fraction of the spacing (0 = perfectly even/lattice-like, ~0.3 = organic, 1 = nearly random).
        // breaks the too-uniform look without reintroducing clumping.
        [Range(0f, 1f)]
        [SerializeField]
        private float lateralSpread = 0.3f;

        // cached once the stream begins: the path never moves and speed is fixed, so there's no reason to
        // re-snapshot the points or re-divide for the walk duration every spawn/frame.
        private Vector3[] _pathPoints;
        private double _walkDuration;

        // index to live NPC, so a replicated kill/down can find the right pedestrian on this machine (the index
        // is the shared identity; the Npc instance itself is per-machine). same key set everywhere.
        private readonly Dictionary<int, Npc> _byIndex = new();

        public override void OnNetworkSpawn()
        {
            Instance = this;
            base.OnNetworkSpawn();
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (Instance == this)
                Instance = null;
        }

        // start the stream clock one full walk-duration in the past so the street opens at steady-state
        // density: the base's catch-up loop immediately spawns every NPC that "would already be" partway down
        // the street, instead of an empty street filling over time.
        protected override double StreamStartOffset =>
            -(path.GetTotalLength() / Mathf.Max(npcPrefab.GetComponent<Npc>().Speed, 0.01f));

        protected override float Interval(int stream) => spawnInterval;

        protected override void OnStreamReady()
        {
            if (npcPrefab == null || path == null || path.Count < 2)
            {
                Debug.LogError("CrowdManager: npcPrefab/path not set up.", this);
                return;
            }
            _pathPoints = path.GetWorldPoints();
            _walkDuration = path.GetTotalLength() / Mathf.Max(npcPrefab.GetComponent<Npc>().Speed, 0.01f);
        }

        // skip NPCs that would already have finished walking before now (e.g. on a late join)
        protected override bool IsStillAlive(int stream, int index, double birth, double now) =>
            now - birth < _walkDuration;

        // instantiate NPC #index locally with its seed-derived traits and clock-derived birth time. every trait
        // is a pure function of (seed, index), no runtime history, so a late joiner computes identical results.
        protected override IStreamItem SpawnItem(int stream, int index, double birth)
        {
            bool forward = Forward(index);
            float lateralOffset = LateralOffset(index);

            GameObject go = Instantiate(npcPrefab);
            go.name = $"CrowdNPC_{index}";

            var npc = go.GetComponent<Npc>();
            npc.Initialize(index, _pathPoints, lateralOffset, forward, birth);

            // paint this NPC's look, rolled deterministically from (seed, index) so every machine spawns the
            // identical crowd. uses a separate salted RNG stream from the movement traits above, so appearance
            // stays independent of direction/lateral choices (changing one never reshuffles the other).
            if (appearanceCatalog != null)
            {
                var applier = go.GetComponentInChildren<CharacterAppearanceApplier>();
                if (applier != null)
                {
                    applier.SetCatalog(appearanceCatalog);
                    applier.Apply(CharacterAppearanceApplier.Roll(appearanceCatalog, AppearanceRngFor(index)));
                }
            }

            _byIndex[index] = npc;
            return npc;
        }

        protected override void OnDespawn(IStreamItem item)
        {
            if (item is Npc npc)
                _byIndex.Remove(npc.Index);
        }

        // kill the NPC with this stream index on this machine (freeze + fade, then normal despawn). called on
        // every machine from the shooter's replicated kill RPC, so the same pedestrian dies everywhere. a no-op
        // if that index isn't currently live here (already despawned, or a stale/duplicate event).
        public void KillNpc(int index)
        {
            if (_byIndex.TryGetValue(index, out var npc) && npc != null)
                npc.Kill();
        }

        // down the NPC with this stream index on this machine (stop + lie down for the round). called on every
        // machine from the criminal's replicated melee-steal RPC, so the same pedestrian goes down everywhere.
        // a no-op if that index isn't currently live here.
        public void DownNpc(int index, int stolenSlot)
        {
            if (_byIndex.TryGetValue(index, out var npc) && npc != null)
                npc.Down(stolenSlot);
        }

        // the appearance of NPC #index, regenerated from the shared seed rather than stored; appearance is a
        // pure function of (seed, index) (see AppearanceRngFor), so the server can reproduce any
        // NPC's look on demand with zero per-NPC storage. used by the criminal disguise-steal to read which
        // hat/outwear that pedestrian is wearing. returns false if there's no catalog.
        public bool TryGetAppearance(int index, out PlayerAppearance appearance)
        {
            if (appearanceCatalog == null)
            {
                appearance = default;
                return false;
            }
            appearance = CharacterAppearanceApplier.Roll(appearanceCatalog, AppearanceRngFor(index));
            return true;
        }

        private bool Forward(int index) => RngFor(0, index).NextDouble() < forwardFraction;

        // a separate deterministic PRNG stream for appearance, salted differently from RngFor so a given
        // NPC's look is independent of its movement traits (and vice versa). same (seed, index) gives the
        // same look on every machine, at any join time; the whole point of the deterministic crowd.
        private System.Random AppearanceRngFor(int index) =>
            new((int)(Seed ^ (ulong)(index * 40503) ^ 0x5A5A5A5AUL)); // distinct salt from RngFor

        // the conjugate of the golden ratio, frac(1/phi). stepping by this around [0,1) is a
        // low-discrepancy sequence: consecutive indices land maximally far apart, so NPCs close in time
        // spread sideways automatically, no rejection sampling, no neighbour comparisons, no history.
        private const float GoldenConjugate = 0.61803398875f;

        // this NPC's lateral offset across the sidewalk. golden-ratio gives an even, clump-free spread by
        // construction; a small per-NPC random nudge (lateralSpread) breaks the otherwise too-regular look
        // without reintroducing clumping. pure function of (seed, index): O(1), and identical on every
        // machine including late joiners, so the crowd is bit-identical for fair shooting.
        private float LateralOffset(int index)
        {
            var rng = RngFor(0, index);
            rng.NextDouble(); // consume the direction draw so the nudge uses a stable stream position

            // even golden-ratio spread, then a small random nudge (lateralSpread) to break the lattice
            // look. the nudge is scaled by the golden step (~0.382 of the width) so even at spread=1 a
            // nudged NPC can't jump past its neighbours into the next slot.
            float u = Frac(index * GoldenConjugate); // even spread in [0,1)
            float nudge = ((float)rng.NextDouble() - 0.5f) * lateralSpread * (1f - GoldenConjugate);
            return (Frac(u + nudge) - 0.5f) * sidewalkWidth; // [-w/2, +w/2]
        }

        private static float Frac(float x) => x - Mathf.Floor(x);
    }
}
