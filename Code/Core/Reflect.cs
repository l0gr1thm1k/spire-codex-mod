using System;
using System.Reflection;

namespace SpireCodex.Core;

// Tiny reflection helpers. All game-type access goes through reflection so the mod
// compiles and degrades gracefully even when an Early Access patch renames a member.
// A missing member returns null/fallback and the caller downgrades the snapshot
// instead of crashing the game.
internal static class Reflect
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        // Guarded: some game getters throw when read too early (e.g. NPotion.Model before
        // the model is set). A throwing member reads as absent, same as a missing one.
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, Flags);
                if (p != null) return p.GetValue(target);
                var f = t.GetField(name, Flags);
                if (f != null) return f.GetValue(target);
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    // Read a public/non-public STATIC property or field by name off a Type. Same graceful
    // contract: a missing or throwing member reads as null. Used for game singletons accessed
    // without a hard type reference (e.g. RunManager.Instance).
    public static object? GetStatic(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null) return p.GetValue(null);
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) return f.GetValue(null);
        }
        catch { /* fall through to null */ }
        return null;
    }

    public static int GetInt(object? target, string name, int fallback = 0)
    {
        var v = GetMember(target, name);
        if (v == null) return fallback;
        try { return Convert.ToInt32(v); }
        catch { return fallback; }
    }

    public static bool GetBool(object? target, string name, bool fallback = false)
        => GetMember(target, name) is bool b ? b : fallback;

    public static string? GetString(object? target, string name)
        => GetMember(target, name)?.ToString();

    // Invoke a parameterless instance method and return its result as a string. Needed for
    // types whose ToString() is unhelpful (e.g. LocString, which resolves its localized
    // text only via GetFormattedText()). Null on any failure, same graceful contract.
    public static string? CallString(object? target, string method)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(method, Flags, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(target, null)?.ToString();
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    // Invoke a parameterless instance method and return its object result (e.g.
    // MerchantRoom.GetLocalInventory()). Null on any failure, same graceful contract.
    public static object? Call(object? target, string method)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(method, Flags, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(target, null);
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    // Invoke a single-argument instance method by name and return its object result (e.g.
    // EncounterModel.GetLossMessageFor(character)). Matches by name + arity. Null on any failure.
    public static object? CallWith(object? target, string method, object? arg)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(Flags))
                    if (m.Name == method && m.GetParameters().Length == 1)
                        return m.Invoke(target, new[] { arg });
        }
        catch { /* fall through to null */ }
        return null;
    }

    // --- resolve-once members -------------------------------------------------------
    //
    // The helpers above collapse "the game has no such member" and "the member read null"
    // into the same null. That is the right trade for a snapshot field, where both cases
    // mean "we do not know". It is the WRONG trade when the null value is itself meaningful:
    // CardSelectCmd.Selector reads null precisely when a human made the choice, so a caller
    // that cannot tell a renamed property from a null one would have to guess, and a guessed
    // field is worse than a missing one. These resolve the member once and hand back the
    // reflection handle, so a caller can emit nothing at all when the member is gone.

    public static PropertyInfo? StaticProperty(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            return type.GetProperty(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }
        catch { return null; }
    }

    // Guarded reads off an already-resolved handle, so the try/catch lives here rather than
    // at every call site. A throwing member reads as null, same contract as GetMember.
    public static object? Read(PropertyInfo? prop)
    {
        if (prop == null) return null;
        try { return prop.GetValue(null); }
        catch { return null; }
    }
}
