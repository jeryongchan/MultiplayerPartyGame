using FriendSlop.Characters;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // drives a continuous deterministic pedestrian stream. the crowd is not replicated: every machine
    // builds the same crowd locally from one shared seed plus the synchronized network clock, so it
    // costs (essentially) one ulong of bandwidth regardless of crowd size.
    //
    // model: a never-ending numbered stream of NPCs. NPC #k is "born" at time k * spawnInterval and
    // walks the path once, then despawns. it never loops, so a recognizable face is never recycled back
    // to the start. birth time and per-NPC traits (lateral offset, direction) are pure functions of
    // (seed, k), so every machine spawns and despawns the exact same NPC #k at the exact same instant,
    // no per-NPC networking, just the shared seed and clock. each frame the manager instantiates any
    // NPCs whose birth time has passed (up to the current clock) and destroys any that report Finished.
    // the server owns the seed; clients reconstruct.
    public class CrowdManager : NetworkBehaviour
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

        // seconds between consecutive NPCs entering the path. lower = denser crowd (randomness to be
        // added later so density varies).
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

        // shared seed: the host writes it once; clients read it. everything per-NPC is derived from this,
        // so the whole crowd is reproducible on every machine. server-writable only. forced non-zero so 0
        // can mean "not set yet".
        private readonly NetworkVariable<ulong> _seed =
            new(writePerm: NetworkVariableWritePermission.Server);

        // the shared-clock time the stream starts counting from (NPC #0's birth time). the host writes it
        // alongside the seed so every machine uses the same value; capturing it locally when each machine
        // first sees the seed would differ per machine (clients see it later) and desync NPC indices.
        private readonly NetworkVariable<double> _streamStartTime =
            new(writePerm: NetworkVariableWritePermission.Server);

        // highest NPC index spawned so far on this machine. we instantiate forward from here as the clock
        // advances; the value itself is the same on every machine since births are clock-derived.
        private int _nextIndex;

        private bool _streaming;

        // cached once the stream begins: the path never moves and speed is fixed, so there's no reason to
        // re-snapshot the points or re-divide for the walk duration every spawn/frame.
        private Vector3[] _pathPoints;
        private double _walkDuration;

        // live NPCs, so we can poll them for Finished and despawn. local bookkeeping only.
        private readonly System.Collections.Generic.List<Npc> _active = new();

        // index to live NPC, so a replicated kill can find the right pedestrian on this machine (the index
        // is the shared identity; the Npc instance itself is per-machine). same key set everywhere.
        private readonly System.Collections.Generic.Dictionary<int, Npc> _byIndex = new();

        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (IsServer)
            {
                _seed.Value =
                    (
                        (ulong)Random.Range(int.MinValue, int.MaxValue)
                        ^ (ulong)System.DateTime.Now.Ticks
                    ) | 1UL; // force non-zero.

                // start the stream clock one full walk-duration in the past so the street begins at
                // steady-state density: the manager's catch-up loop immediately spawns all the NPCs that
                // "would already be" partway down the street, instead of an empty street filling over time.
                double walkDuration = path.GetTotalLength() / Mathf.Max(npcPrefab.GetComponent<Npc>().Speed, 0.01f);
                _streamStartTime.Value = NetworkManager.ServerTime.Time - walkDuration;
            }

            _seed.OnValueChanged += OnSeedChanged;
            _streamStartTime.OnValueChanged += OnStreamStartTimeChanged;
            TryBeginStream();
        }

        public override void OnNetworkDespawn()
        {
            _seed.OnValueChanged -= OnSeedChanged;
            _streamStartTime.OnValueChanged -= OnStreamStartTimeChanged;
            if (Instance == this)
                Instance = null;
        }

        private void OnSeedChanged(ulong _, ulong __) => TryBeginStream();
        private void OnStreamStartTimeChanged(double _, double __) => TryBeginStream();

        // latch the stream start time once the seed is known. idempotent.
        private void TryBeginStream()
        {
            // wait until both NetworkVariables have arrived from the server. on clients they sync
            // independently and may arrive in separate frames; starting before _streamStartTime is set
            // would compute birth times from 0 and desync the crowd vs the host.
            if (_streaming || _seed.Value == 0 || _streamStartTime.Value == 0)
                return;
            if (npcPrefab == null || path == null || path.Count < 2)
            {
                Debug.LogError("CrowdManager: npcPrefab/path not set up.", this);
                return;
            }
            _pathPoints = path.GetWorldPoints();
            _walkDuration = path.GetTotalLength() / Mathf.Max(npcPrefab.GetComponent<Npc>().Speed, 0.01f);
            _streaming = true;
        }

        private void Update()
        {
            if (!_streaming)
                return;

            // spawn every NPC whose birth time (index * interval, from stream start) has arrived. skip any
            // that would already have finished walking before now (e.g. on a late join); instantiating
            // them just to destroy them next frame is wasted work, their absence is correct anyway.
            double now = NetworkManager.ServerTime.Time;
            while (_streamStartTime.Value + _nextIndex * spawnInterval <= now)
            {
                double birth = _streamStartTime.Value + _nextIndex * spawnInterval;
                if (now - birth < _walkDuration) // still on the path
                    SpawnNpc(_nextIndex);
                _nextIndex++;
            }

            // despawn any that have finished their one-way walk
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i] == null || _active[i].Finished)
                {
                    if (_active[i] != null)
                    {
                        _byIndex.Remove(_active[i].Index);
                        Destroy(_active[i].gameObject);
                    }
                    _active.RemoveAt(i);
                }
            }
        }

        // kill the NPC with this stream index on this machine (freeze + fade, then normal despawn). called
        // on every machine from the shooter's replicated kill RPC, so the same pedestrian dies everywhere.
        // a no-op if that index isn't currently live here (already despawned, or a stale/duplicate event).
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
        // hat/outwear that pedestrian is wearing. returns false if the crowd isn't streaming or has no catalog.
        public bool TryGetAppearance(int index, out FriendSlop.Characters.PlayerAppearance appearance)
        {
            if (!_streaming || appearanceCatalog == null)
            {
                appearance = default;
                return false;
            }
            appearance = FriendSlop.Characters.CharacterAppearanceApplier.Roll(appearanceCatalog, AppearanceRngFor(index));
            return true;
        }

        // instantiate NPC #index locally with its seed-derived traits and its clock-derived birth time.
        // every trait is a pure function of (seed, index), no runtime history, so a machine that joins
        // late and skips earlier indices still computes identical results.
        private void SpawnNpc(int index)
        {
            bool forward = Forward(index);
            float lateralOffset = LateralOffset(index);
            double birthTime = _streamStartTime.Value + index * spawnInterval;

            GameObject go = Instantiate(npcPrefab);
            go.name = $"CrowdNPC_{index}";

            var npc = go.GetComponent<Npc>();
            npc.Initialize(index, _pathPoints, lateralOffset, forward, birthTime);

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

            _active.Add(npc);
            _byIndex[index] = npc;
        }

        // a fresh deterministic PRNG for one NPC: same (seed, index) gives the same stream on every
        // machine, at any join time. mix the index into the seed so each NPC gets a distinct but
        // reproducible sequence.
        private System.Random RngFor(int index) =>
            new System.Random((int)(_seed.Value ^ (ulong)(index * 2654435761))); // Knuth mix

        private bool Forward(int index) => RngFor(index).NextDouble() < forwardFraction;

        // a separate deterministic PRNG stream for appearance, salted differently from RngFor so a given
        // NPC's look is independent of its movement traits (and vice versa). same (seed, index) gives the
        // same look on every machine, at any join time; the whole point of the deterministic crowd.
        private System.Random AppearanceRngFor(int index) =>
            new System.Random((int)(_seed.Value ^ (ulong)(index * 40503) ^ 0x5A5A5A5AUL)); // distinct salt

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
            var rng = RngFor(index);
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
