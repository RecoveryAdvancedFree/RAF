using System.Runtime.InteropServices;
using System.Text;

namespace RAF.Core.Repair;

/// <summary>
/// מנוע SQLite של Windows (winsqlite3.dll, חלק ממערכת ההפעלה מאז Windows 10) — לקריאת
/// מסד הדוגמה, להעתקת הנתונים למסד נקי ולבדיקת השלמות שלו. בלי ספרייה חיצונית.
/// </summary>
internal sealed class SqliteEngine : IDisposable
{
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll")] private static extern void sqlite3_free(IntPtr p);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_errmsg(IntPtr db);

    private const int ReadOnly = 1, ReadWrite = 2, Create = 4;
    private const int Row = 100;

    private IntPtr _db;

    static SqliteEngine() => UseSystemSqlite(typeof(SqliteEngine).Assembly);

    /// <summary>
    /// במק ובלינוקס אין winsqlite3 — יש את ספריית SQLite של המערכת. אותו ממשק, שם אחר.
    /// </summary>
    internal static void UseSystemSqlite(System.Reflection.Assembly assembly)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
                name != "winsqlite3.dll" ? IntPtr.Zero
                : NativeLibrary.Load(OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib" : "libsqlite3.so.0"));
        }
        catch (InvalidOperationException) { }              // כבר הוגדר
    }

    private SqliteEngine(IntPtr db) => _db = db;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static SqliteEngine Open(string path, bool writable = false)
    {
        int rc = sqlite3_open_v2(Utf8(path), out var db, writable ? ReadWrite | Create : ReadOnly, IntPtr.Zero);
        var engine = new SqliteEngine(db);
        if (rc != 0) { string message = engine.Error(); engine.Dispose(); throw new InvalidOperationException(message); }
        return engine;
    }

    private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "SQLite";

    public void Exec(string sql)
    {
        if (sqlite3_exec(_db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error) == 0) return;
        string message = Marshal.PtrToStringUTF8(error) ?? Error();
        sqlite3_free(error);
        throw new InvalidOperationException(message);
    }

    /// <summary>כל השורות, כל ערך כטקסט (null — NULL).</summary>
    public List<string?[]> Query(string sql)
    {
        if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero) != 0)
            throw new InvalidOperationException(Error());
        try
        {
            var rows = new List<string?[]>();
            int rc;
            while ((rc = sqlite3_step(statement)) == Row)
            {
                var row = new string?[sqlite3_column_count(statement)];
                for (int i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            if (rc != 101) throw new InvalidOperationException(Error());      // 101 — SQLITE_DONE
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }

    public string? Scalar(string sql) => Query(sql) is [var row, ..] && row.Length > 0 ? row[0] : null;

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Literal(string text) => "'" + text.Replace("'", "''") + "'";

    public void Dispose()
    {
        if (_db != IntPtr.Zero) sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }
}
