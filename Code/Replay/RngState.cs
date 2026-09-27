using System;
using System.Collections;
using System.Text;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// Where the run currently sits in each random stream, as an `rng_state` object on the three
// rows that anchor a replay: header, resume and combat_start.
//
// The seed fixes the SEQUENCE of rolls; it does not give the POSITION in it. Recovering
// position from the seed alone means modelling every consumer of randomness across 50 floors
// without a single miss, and one miss produces a different-but-plausible run rather than an
// error. The measured failure mode is a cascade: on one graded run a single missed draw on
// floor 36 aborted every fight from floor 38 to the end, while floor 36 itself still graded
// clean. Across a 25-journal bench, 170 of 444 fight measurements sat downstream of a first
// divergence and traced to at most 54 distinct errors -- roughly three reported failures per
// actual one. A stated counter makes that drift both non-compounding and detectable at its
// origin instead of three layers downstream.
//
// COUNTERS ONLY, not the generator's internal state. One draw is exactly one MegaRandom
// advance regardless of bound, so seek(counter) reconstructs the stream exactly; s0..s3 would
// tell a consumer nothing it cannot already derive, while putting generator internals in an
// export that is also public run data.
internal static class RngState
{
    // Null when nothing resolves, so the caller's Set() drops the key entirely rather than
    // writing an empty object.
    public static ReplayLine? Read()
    {
        try
        {
            var line = new ReplayLine("rng_state", 16);
            // 12 run-scoped streams off RunState.Rng, 3 player-scoped off Player.PlayerRng.
            // The two sets have disjoint names, but the player ones are prefixed anyway so a
            // consumer can tell which set a stream came from without knowing the enums.
            Collect(line, Reflect.GetMember(Sts2Access.LiveRunState, "Rng"), "");
            Collect(line, Reflect.GetMember(Sts2Access.LivePlayer, "PlayerRng"), "player_");
            return line.Fields.Count > 0 ? line : null;
        }
        catch { return null; }
    }

    // RunRngSet.ToSerializable() / PlayerRngSet.ToSerializable() both hand back a set whose
    // `Rngs` is a Dictionary<enum, SerializableRng>. Going through the dictionary rather than
    // naming 15 properties means a stream the game adds later shows up on its own, and it is
    // the game's own accessor so it cannot drift from what the save file records.
    //
    // Both are pure reads -- ToSerializable copies _counter and MegaRandom.FillSerializableState
    // copies s0..s3 -- so stamping a row cannot perturb the run it is describing.
    private static void Collect(ReplayLine line, object? rngSet, string prefix)
    {
        if (Reflect.Call(rngSet, "ToSerializable") is not { } serializable) return;
        if (Reflect.GetMember(serializable, "Rngs") is not IDictionary rngs) return;
        foreach (DictionaryEntry entry in rngs)
        {
            var name = entry.Key?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            // `counter` is a public field on SerializableRng. A stream we cannot read is
            // OMITTED rather than reported as 0: an absent key must never read as an assertion
            // that the counter was zero, because zero is itself a common true value here (a
            // stream nothing has drawn from yet) and the two are not distinguishable after the
            // fact.
            if (Reflect.GetMember(entry.Value, "counter") is not { } counter) continue;
            try { line.Set(prefix + SnakeCase(name!), Convert.ToInt32(counter)); }
            catch { }
        }
    }

    // StringHelper.SnakeCase over a PascalCase enum name. The stream's name in this exact
    // spelling is what the game hashes to seed it, and what the consumer keys on, so the two
    // have to agree: UpFront -> up_front, MonsterAi -> monster_ai, CombatOrbs -> combat_orbs.
    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
