using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using SpireCodex.Api;

namespace SpireCodex.Replay;

// Phase 3: sends a finished journal to /api/runs/{run_hash}/replay.
//
// Ordering is not optional. The endpoint 404s until the run doc exists, so the .run is always
// uploaded first and its response hands us the run_hash — the mod never computes that hash
// itself, which is also what keeps co-op correct (after PR #1019 the response carries the hash
// of the SUBMITTING player's slot, so a non-host's replay lands on their own run).
//
// Gated by UploadReplays, which requires UploadRuns and a consent grant given against a
// disclosure that names replays. Consent.Answered is sticky forever, so gating on Granted alone
// would have started sending per-action journals from everyone who agreed to "completed runs"
// back in v1.0.10 without them ever seeing a card that says so.
internal static class ReplayUploader
{
    // Journals already accepted by the server, so a re-upload is never attempted. Same shape and
    // location as the .run ledger next to it.
    private static readonly object Gate = new();
    private static HashSet<string>? _sent;

    // Sweep journals stranded on disk, at launch and whenever the toggle is switched on.
    //
    // Without this a completed journal is only ever offered once, in the same moment its .run
    // uploads. Anything that misses that window is orphaned for good: recorded while
    // UploadReplays was off, a retryable failure with nothing to re-trigger it, or a run
    // finished before the feature existed. Crash recovery does not cover them either — it only
    // looks for journals with NO end line.
    //
    // The run_hash is not computed here. Each journal names the .run it belongs to (both carry
    // the same start_time), so the .run is re-posted first; that endpoint is idempotent and
    // returns the same hash for an already-known run, which is also the only way to get the
    // right slot's hash in co-op.
    public static async Task SweepAsync()
    {
        try
        {
            if (!SpireCodexConfig.UploadReplays || !Config.UploadRuns || !Consent.ReplaysGranted) return;

            string[] files;
            try { files = Directory.GetFiles(ReplayRecorder.Dir, "*.jsonl"); }
            catch { return; }

            var client = new SpireCodexClient();
            var sent = 0;
            foreach (var path in files)
            {
                if (AlreadySent(path)) continue;
                if (ReplayRecorder.Active && path == ReplayRecorder.CurrentPath) continue; // still being written
                if (!HasTerminalLine(path)) continue; // unfinished; the recorder still owns it

                var run = RunFileFor(path);
                if (run == null) continue; // its .run is gone, nothing to attach the replay to

                string runJson;
                try { runJson = await File.ReadAllTextAsync(run).ConfigureAwait(false); }
                catch { continue; }

                // Idempotent: an already-known run comes back as a duplicate WITH its hash.
                var up = await client
                    .UploadRunAsync(runJson, Config.SteamId, Config.Username, Core.Sts2Version.Current)
                    .ConfigureAwait(false);
                if (!up.Success) continue;
                var hash = HashOf(up.Body);
                if (hash == null) continue;

                await TryUploadAsync(runJson, hash).ConfigureAwait(false);
                if (++sent >= 25) break;          // one launch does a bounded chunk
                await Task.Delay(500).ConfigureAwait(false); // stay well under the rate limit
            }
            if (sent > 0) MainFile.Logger.Info($"replay sweep: offered {sent} stranded journal(s)");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay sweep error: {e.Message}");
        }
    }

    // A journal is finished when its last line is the terminal record. Read the tail only.
    private static bool HasTerminalLine(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 4096);
            if (take == 0) return false;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var at = tail.LastIndexOf("\"t\":\"end\"", StringComparison.Ordinal);
            return at >= 0 && tail.IndexOf('\n', at) >= 0;
        }
        catch { return false; }
    }

    // "<seed>-<start_time>.jsonl" -> the "<start_time>.run" it belongs to, anywhere in the save
    // tree (profiles and the modded/vanilla split mean the directory is not fixed).
    private static string? RunFileFor(string journalPath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(journalPath);
            var dash = name.LastIndexOf('-');
            if (dash < 0) return null;
            var stamp = name.Substring(dash + 1);
            var root = Api.RunUploader.FindSaveRoot();
            if (root == null) return null;
            foreach (var f in Directory.GetFiles(root, stamp + ".run", SearchOption.AllDirectories))
                return f;
        }
        catch { }
        return null;
    }

    private static string? HashOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("run_hash", out var h)) return h.GetString();
        }
        catch { }
        return null;
    }

    // Called after a .run upload succeeds. `runJson` identifies which journal belongs to it.
    public static async Task TryUploadAsync(string runJson, string runHash)
    {
        try
        {
            if (!SpireCodexConfig.UploadReplays || !Config.UploadRuns || !Consent.ReplaysGranted) return;
            if (string.IsNullOrEmpty(runHash)) return;

            var path = JournalFor(runJson);
            if (path == null) return; // no journal for this run (recording was off, or an old run)
            if (AlreadySent(path)) return;

            var raw = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var gzip = Gzip(raw);
            // Never post something that cannot be read back. A truncated member would be
            // rejected as 400 not_gzip after the upload had already spent a request.
            if (!RoundTrips(gzip, raw.Length))
            {
                MainFile.Logger.Info($"replay: gzip of {Path.GetFileName(path)} did not round-trip; not sending");
                return;
            }

            var result = await new SpireCodexClient().UploadReplayAsync(runHash, gzip).ConfigureAwait(false);
            var code = Code(result.Body);

            if (result.Success || code == "replay_exists")
            {
                // replay_exists means the server already holds one for this run and keeps the
                // first; either way this journal is done and must not be retried.
                MarkSent(path);
                MainFile.Logger.Info($"replay upload {Path.GetFileName(path)}: ok ({result.StatusCode}) " +
                                     $"{raw.Length}B -> {gzip.Length}B");
                return;
            }

            // 404: the run doc is not visible yet. 5xx/503 storage: the server is unwell. Both
            // are worth another attempt on a later launch, so the journal stays unmarked.
            var retryable = result.StatusCode == 404 || result.StatusCode >= 500 || result.StatusCode == 0;
            MainFile.Logger.Info(
                $"replay upload {Path.GetFileName(path)}: FAILED ({result.StatusCode}) {code ?? "?"}"
                + (retryable ? " — will retry on a later launch" : " — permanent, not retrying"));

            // A permanent rejection (header_mismatch, too_large, not_owner) will never succeed
            // for this file, so stop asking.
            if (!retryable) MarkSent(path);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay upload error: {e.Message}");
        }
    }

    // The journal belonging to a .run, matched on the identity both files carry. The recorder
    // names journals "<seed>-<start_time>.jsonl" using the SAME seed string and start time the
    // .run records, precisely so this lookup needs no index.
    private static string? JournalFor(string runJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(runJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("seed", out var s) || s.GetString() is not { Length: > 0 } seed)
                return null;
            if (!root.TryGetProperty("start_time", out var t)) return null;
            var start = t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            if (start <= 0) return null;

            var path = Path.Combine(ReplayRecorder.Dir, $"{seed}-{start}.jsonl");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    // One gzip member, closed once. CompressionLevel.Fastest, not Optimal: measured on a real
    // journal the difference is ~6% of an already-small file, and this runs right after a run
    // ends when the game is still doing its own save work.
    private static byte[] Gzip(byte[] raw)
    {
        using var outp = new MemoryStream();
        using (var gz = new GZipStream(outp, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return outp.ToArray();
    }

    private static bool RoundTrips(byte[] gzip, int expectedLength)
    {
        try
        {
            using var inp = new MemoryStream(gzip);
            using var gz = new GZipStream(inp, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            gz.CopyTo(outp);
            return outp.Length == expectedLength;
        }
        catch { return false; }
    }

    // The server returns {"detail": {"code": "...", "message": "..."}} on every error.
    private static string? Code(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d)
                && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("code", out var c))
                return c.GetString();
        }
        catch { }
        return null;
    }

    // --- ledger ----------------------------------------------------------------------

    private static string? LedgerPath()
    {
        try
        {
            if (string.IsNullOrEmpty(Config.SteamId)) return null;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireCodex");
            return Path.Combine(dir, $"replays-sent-{Config.SteamId}.txt");
        }
        catch { return null; }
    }

    private static bool AlreadySent(string path)
    {
        lock (Gate)
        {
            if (_sent == null)
            {
                _sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lp = LedgerPath();
                    if (lp != null && File.Exists(lp))
                        foreach (var line in File.ReadAllLines(lp))
                            if (!string.IsNullOrWhiteSpace(line)) _sent.Add(line.Trim());
                }
                catch { /* a missing ledger just means we may re-send once; the server dedupes */ }
            }
            return _sent.Contains(Path.GetFullPath(path));
        }
    }

    private static void MarkSent(string path)
    {
        lock (Gate)
        {
            _sent ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_sent.Add(Path.GetFullPath(path))) return;
            try
            {
                var lp = LedgerPath();
                if (lp == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(lp)!);
                File.AppendAllText(lp, Path.GetFullPath(path) + "\n");
            }
            catch { /* best effort; the server's duplicate check is the real guard */ }
        }
    }
}
