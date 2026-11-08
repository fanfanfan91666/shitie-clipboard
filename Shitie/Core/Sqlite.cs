namespace Shitie.Core;

// Windows ships this SQLite library. No downloaded database runtime is needed.
internal sealed class Sqlite : IDisposable
{
    private IntPtr _db;
    private static readonly IntPtr Transient = new(-1);
    public Sqlite(string path)
    {
        var code = sqlite3_open_v2(path, out _db, 0x2 | 0x4 | 0x10000, IntPtr.Zero);
        if (code != 0) { var message = Error(); Dispose(); throw new IOException("无法打开历史数据库：" + message); }
        sqlite3_busy_timeout(_db, 3000);
    }
    private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "SQLite error";
    public Statement Prepare(string sql, params object?[] args)
    {
        if (sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw new IOException(Error());
        var value = new Statement(this, stmt);
        try { for (var i = 0; i < args.Length; i++) value.Bind(i + 1, args[i]); }
        catch { value.Dispose(); throw; }
        return value;
    }
    public void Execute(string sql, params object?[] args) { using var stmt = Prepare(sql, args); while (stmt.Read()) { } }
    public long Scalar(string sql, params object?[] args) { using var stmt = Prepare(sql, args); return stmt.Read() ? stmt.Long(0) : 0; }
    public void Dispose() { if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; } }
    internal sealed class Statement(Sqlite owner, IntPtr handle) : IDisposable
    {
        public void Bind(int index, object? value)
        {
            var result = value switch
            {
                null => sqlite3_bind_null(handle, index),
                byte[] bytes => sqlite3_bind_blob(handle, index, bytes, bytes.Length, Transient),
                string str => sqlite3_bind_text(handle, index, str, -1, Transient),
                _ => sqlite3_bind_int64(handle, index, Convert.ToInt64(value))
            };
            if (result != 0) throw new IOException(owner.Error());
        }
        public bool Read()
        {
            var code = sqlite3_step(handle);
            if (code == 100) return true;
            if (code == 101) return false;
            throw new IOException(owner.Error());
        }
        public long Long(int column) => sqlite3_column_int64(handle, column);
        public string Text(int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(handle, column)) ?? "";
        public byte[] Blob(int column)
        {
            var size = sqlite3_column_bytes(handle, column);
            var data = new byte[size];
            if (size > 0) Marshal.Copy(sqlite3_column_blob(handle, column), data, 0, size);
            return data;
        }
        public void Dispose() => sqlite3_finalize(handle);
    }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int count, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int count, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
}
