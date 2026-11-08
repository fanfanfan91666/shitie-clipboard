namespace Shitie.Core;

public sealed class HistoryStore : IDisposable
{
    private readonly Sqlite _db;
    private readonly object _gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Shitie.clipboard.v1");
    public HistoryStore(string root)
    {
        Directory.CreateDirectory(root);
        _db = new Sqlite(Path.Combine(root, "history.db"));
        _db.Execute("PRAGMA journal_mode=WAL");
        _db.Execute("PRAGMA secure_delete=ON");
        _db.Execute("PRAGMA auto_vacuum=INCREMENTAL");
        _db.Execute("CREATE TABLE IF NOT EXISTS clips (id INTEGER PRIMARY KEY AUTOINCREMENT, hash TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, last_used INTEGER NOT NULL, size INTEGER NOT NULL, summary BLOB NOT NULL, payload BLOB NOT NULL)");
        _db.Execute("CREATE INDEX IF NOT EXISTS clips_recent ON clips(last_used DESC, id DESC)");
    }
    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    public long Upsert(ClipPayload payload, long? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw = JsonSerializer.SerializeToUtf8Bytes(payload);
        var preview = payload.Preview;
        var summary = new ClipEntry { Kind = payload.Kind, Preview = preview[..Math.Min(600, preview.Length)], Source = payload.Source, WasCut = payload.WasCut };
        var encrypted = Protect(raw);
        var encryptedSummary = Protect(JsonSerializer.SerializeToUtf8Bytes(summary));
        var hash = payload.Fingerprint();
        lock (_gate)
        {
            _db.Execute("INSERT INTO clips(hash,kind,last_used,size,summary,payload) VALUES(?,?,?,?,?,?) ON CONFLICT(hash) DO UPDATE SET last_used=excluded.last_used, size=excluded.size, summary=excluded.summary, payload=excluded.payload",
                hash, (int)payload.Kind, now, encrypted.Length + encryptedSummary.Length, encryptedSummary, encrypted);
            return _db.Scalar("SELECT id FROM clips WHERE hash=?", hash);
        }
    }
    public ClipPayload? Get(long id)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("SELECT payload FROM clips WHERE id=?", id);
            return stmt.Read() ? JsonSerializer.Deserialize<ClipPayload>(Unprotect(stmt.Blob(0))) : null;
        }
    }
    public List<ClipEntry> List(string query = "", ClipKind? kind = null, int offset = 0, int limit = 80)
    {
        lock (_gate)
        {
            var entries = new List<ClipEntry>();
            using var stmt = _db.Prepare("SELECT id,kind,last_used,summary,payload FROM clips " + (kind.HasValue ? "WHERE kind=? " : "") + "ORDER BY last_used DESC,id DESC", kind.HasValue ? [(int)kind.Value] : []);
            var matched = 0;
            while (stmt.Read())
            {
                ClipEntry? item;
                try { item = JsonSerializer.Deserialize<ClipEntry>(Unprotect(stmt.Blob(3))); }
                catch (CryptographicException) { continue; }
                catch (JsonException) { continue; }
                if (item == null) continue;
                // Search the full encrypted payload, not just its truncated preview.
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var haystack = item.Preview + " " + item.Source;
                    if (!haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var payload = JsonSerializer.Deserialize<ClipPayload>(Unprotect(stmt.Blob(4)));
                            haystack = (payload?.Text ?? "") + " " + string.Join(" ", payload?.Files ?? []);
                        }
                        catch (Exception ex) when (ex is CryptographicException or JsonException) { continue; }
                        if (!haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                }
                if (matched++ < offset) continue;
                item.Id = stmt.Long(0);
                item.LastUsed = stmt.Long(2);
                entries.Add(item);
                if (entries.Count >= limit) break;
            }
            return entries;
        }
    }
    public void Touch(long id)
    {
        lock (_gate) _db.Execute("UPDATE clips SET last_used=? WHERE id=?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id);
    }
    public void Delete(long id) { lock (_gate) _db.Execute("DELETE FROM clips WHERE id=?", id); }
    public void Clear() { lock (_gate) { _db.Execute("DELETE FROM clips"); _db.Execute("PRAGMA wal_checkpoint(TRUNCATE)"); _db.Execute("VACUUM"); } }
    public (long Count, long Bytes) Stats()
    {
        lock (_gate) return (_db.Scalar("SELECT count(*) FROM clips"), _db.Scalar("SELECT coalesce(sum(size),0) FROM clips"));
    }
    public void Cleanup(AppSettings settings, long? timestamp = null)
    {
        lock (_gate)
        {
            var now = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (settings.RetentionDays > 0) _db.Execute("DELETE FROM clips WHERE last_used < ?", now - (long)settings.RetentionDays * 86_400_000);
            var budget = (long)settings.MaximumStorageMb * 1024 * 1024;
            var size = _db.Scalar("SELECT coalesce(sum(size),0) FROM clips");
            if (size > budget)
            {
                var remove = new List<long>();
                using (var stmt = _db.Prepare("SELECT id,size FROM clips ORDER BY last_used ASC,id ASC"))
                    while (size > budget && stmt.Read()) { remove.Add(stmt.Long(0)); size -= stmt.Long(1); }
                _db.Execute("BEGIN IMMEDIATE");
                try { foreach (var id in remove) _db.Execute("DELETE FROM clips WHERE id=?", id); _db.Execute("COMMIT"); }
                catch { _db.Execute("ROLLBACK"); throw; }
            }
            _db.Execute("PRAGMA wal_checkpoint(PASSIVE)");
            _db.Execute("PRAGMA incremental_vacuum(128)");
        }
    }
    public void Dispose() { lock (_gate) _db.Dispose(); }
}
