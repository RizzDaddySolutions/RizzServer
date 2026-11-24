using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RizzServer.Core;

/// <summary>
/// Append-only security log, one JSON object per line. Every line carries the hash of the previous one, so removing or
/// editing a line breaks the chain and <see cref="Verify"/> says where. (It proves tampering happened; it cannot stop
/// someone with file access from deleting the whole file: ship it elsewhere if that matters.)
/// </summary>
public sealed class AuditLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _lock = new();
    private string _lastHash = "";

    public AuditLog(string? path = null)
    {
        FilePath = path ?? System.IO.Path.Combine(ConfigStore.DataDirectory, "audit.log");
        _lastHash = LastHashOf(FilePath);
    }

    public string FilePath { get; }

    public sealed record Entry(DateTimeOffset Time, string Action, string User, string Target, string Detail, string Prev, string Hash);

    public void Write(string action, string user, string target, string detail)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) Rotate();

                var time = DateTimeOffset.UtcNow;
                var body = JsonSerializer.Serialize(new { time, action, user, target, detail = Clip(detail), prev = _lastHash });
                var hash = HashOf(_lastHash, body);
                var line = body[..^1] + $",\"hash\":\"{hash}\"}}";
                File.AppendAllText(FilePath, line + "\n");
                _lastHash = hash;
            }
            catch (IOException) { /* an unwritable audit log must not take the gateway down; the failure is visible on the next Verify */ }
        }
    }

    private static string Clip(string s) => s.Length <= 500 ? s : s[..500] + "…";

    private void Rotate()
    {
        var archive = FilePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        File.Move(FilePath, archive);
        // The new file starts a new chain that points at the end of the old one.
    }

    private static string HashOf(string prev, string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prev + body))).ToLowerInvariant();

    private static string LastHashOf(string path)
    {
        if (!File.Exists(path)) return "";
        var last = File.ReadLines(path).LastOrDefault(l => l.Length > 0);
        if (last is null) return "";
        try { return JsonDocument.Parse(last).RootElement.GetProperty("hash").GetString() ?? ""; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { return ""; }
    }

    public IReadOnlyList<Entry> Tail(int count)
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return [];
            return [.. File.ReadLines(FilePath).Where(l => l.Length > 0).TakeLast(Math.Clamp(count, 1, 5000)).Select(Parse).OfType<Entry>()];
        }
    }

    private static Entry? Parse(string line)
    {
        try
        {
            var e = JsonDocument.Parse(line).RootElement;
            return new Entry(e.GetProperty("time").GetDateTimeOffset(), e.GetProperty("action").GetString() ?? "", e.GetProperty("user").GetString() ?? "",
                e.GetProperty("target").GetString() ?? "", e.GetProperty("detail").GetString() ?? "", e.GetProperty("prev").GetString() ?? "", e.GetProperty("hash").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Re-computes the hash chain of the current file. Returns the number of lines checked and the first bad line (1-based), if any.</summary>
    public (int Lines, int? FirstBadLine, string? Problem) Verify()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return (0, null, null);
            var prev = "";
            var n = 0;
            var first = true;
            foreach (var line in File.ReadLines(FilePath).Where(l => l.Length > 0))
            {
                n++;
                var e = Parse(line);
                if (e is null) return (n, n, "the line is not a valid entry");
                // Only the first line of a file may point at an older (rotated) file.
                if (!first && e.Prev != prev) return (n, n, "the line does not follow the previous one (a line was removed, inserted or changed)");
                var cut = line.LastIndexOf(",\"hash\":", StringComparison.Ordinal);
                if (cut < 0) return (n, n, "the line has no hash");
                if (HashOf(e.Prev, line[..cut] + "}") != e.Hash) return (n, n, "the content does not match its hash (the line was edited)");
                prev = e.Hash;
                first = false;
            }
            return (n, null, null);
        }
    }
}

/// <summary>Counts failures per key (an IP address, a user name) and says when to stop answering: slows password guessing to a crawl.</summary>
public sealed class FailureTracker(int maxFailures = 10, TimeSpan? window = null, TimeSpan? lockout = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _lockout = lockout ?? TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Count, DateTime First, DateTime? LockedUntil)> _state = new();

    public bool IsLocked(string key) =>
        _state.TryGetValue(key, out var s) && s.LockedUntil is { } until && until > DateTime.UtcNow;

    /// <returns>True when this failure locked the key.</returns>
    public bool Fail(string key)
    {
        var now = DateTime.UtcNow;
        var locked = false;
        _state.AddOrUpdate(key, _ => (1, now, null), (_, s) =>
        {
            if (s.LockedUntil is { } u && u > now) return s;
            var count = now - s.First > _window ? 1 : s.Count + 1;
            var first = count == 1 ? now : s.First;
            if (count >= maxFailures) { locked = true; return (0, now, now + _lockout); }
            return (count, first, null);
        });
        return locked;
    }

    public void Succeed(string key) => _state.TryRemove(key, out _);
}
