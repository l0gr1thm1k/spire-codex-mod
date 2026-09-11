using System;
using System.IO;
using System.Text.Json;

namespace SpireCodex.Api;

// Run-upload onboarding state, persisted to %APPDATA%/SpireCodex/consent.json. Three fields:
//  - Answered: the player has made an explicit choice (Turn on / Keep off), so the
//    first-run onboarding prompt shows exactly once per machine.
//  - Granted: the player turned uploads on AND consented, so the uploader may send. Until
//    granted, RunUploader holds every upload at the gate.
//  - Disclosure: WHICH disclosure they agreed to. Answered is sticky forever, so without this
//    a change to what we collect would apply silently to everyone who already said yes.
// Uploads default OFF; "Turn on" grants, "Keep off" records the choice without granting.
public static class Consent
{
    // Bump whenever the consent card describes something new leaving the machine, and add the
    // matching case to ConsentPrompt so already-granted players are told rather than opted in.
    //  1  v1.0.10 and earlier: completed runs + live status.
    //  2  adds per-run replays (every decision, every card played).
    public const int CurrentDisclosure = 2;

    private static bool? _granted;
    private static bool? _answered;
    private static int? _disclosure;

    // Fired on the grant transition so the uploader can flush held runs + kick the backfill.
    public static event Action? OnGranted;

    public static bool Granted => _granted ??= ReadBool("granted");
    public static bool Answered => _answered ??= ReadBool("answered");

    // The disclosure the stored answer was given against. A consent file written before the
    // field existed is a 1 by definition; no file at all is 0 and will be answered fresh.
    public static int Disclosure => _disclosure ??= ReadDisclosure();

    // Replays may only be sent by someone who saw a card that mentions them. This is the gate
    // ReplayUploader uses instead of Granted, so an existing v1 grant uploads runs exactly as
    // before and holds replays until the player answers the new card.
    public static bool ReplaysGranted => Granted && Disclosure >= CurrentDisclosure;

    // True when someone has already granted under an older disclosure and is owed the notice.
    public static bool NeedsRedisclosure => Granted && Disclosure < CurrentDisclosure;

    // "Turn on": enable + consent.
    public static void Grant()
    {
        _granted = true;
        _answered = true;
        _disclosure = CurrentDisclosure;
        Persist();
        OnGranted?.Invoke();
    }

    // The player has read the newer disclosure. Their upload grant is untouched; this only
    // records that they have seen what is now being sent.
    public static void AcknowledgeDisclosure()
    {
        _disclosure = CurrentDisclosure;
        Persist();
        OnGranted?.Invoke(); // lets the replay sweep pick up anything held back until now
    }

    // "Keep off": record the choice so we don't re-ask, without granting.
    public static void Decline()
    {
        _granted = false;
        _answered = true;
        _disclosure = CurrentDisclosure;
        Persist();
    }

    private static void Persist()
    {
        try
        {
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Read THROUGH the lazy getters, so a call that set only one field still writes the
            // other two as they actually are. This used to serialise `_answered ?? false`, and
            // AcknowledgeDisclosure sets only the disclosure, so it wrote answered:false over a
            // stored true. The consent file on this machine ended up {granted:true,
            // answered:false}, a state no real answer can produce: Grant and Decline both set
            // answered. It was invisible because the onboarding card also checks Granted.
            var granted = Granted;
            var answered = Answered;
            var disclosure = _disclosure ?? CurrentDisclosure;
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                granted,
                answered,
                version = disclosure,
                at = DateTimeOffset.UtcNow.ToString("o"),
            }));
        }
        catch { /* the in-memory state still applies this session */ }
    }

    // 0 when there is no consent file, 1 when there is one from before versioning.
    private static int ReadDisclosure()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var n))
                return n;
            return 1;
        }
        catch { return 0; }
    }

    private static bool ReadBool(string key)
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(key, out var v) && v.GetBoolean();
        }
        catch { return false; }
    }

    private static string StorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "consent.json");
}
