using System.Runtime.InteropServices;
using System.Text;

namespace RAF.Core.Tests;

/// <summary>
/// מסדי נתונים אמיתיים, דרך SQLite שמובנית ב-Windows (winsqlite3.dll) — כדי לבדוק
/// שהמסד המתוקן באמת נפתח, ולא רק שהכותרת שלו נראית נכון.
/// </summary>
internal static class RealSqlite
{
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_open(byte[] path, out IntPtr db);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr statement);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>מסד עם טבלה של rows שורות, באורך דף נתון.</summary>
    internal static byte[] Create(int pageSize, int rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"raf-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            Exec(path, $"PRAGMA page_size={pageSize}; PRAGMA journal_mode=DELETE; CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);" +
                       $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{rows}) " +
                       "INSERT INTO t(name) SELECT 'שורה מספר ' || i || ' ' || hex(randomblob(20)) FROM n;");
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// פתיחת המסד מהקובץ: תוצאת בדיקת התקינות של SQLite ומספר השורות בטבלה.
    /// מסד שלא נפתח מחזיר את הודעת השגיאה במקום "ok".
    /// </summary>
    internal static (string Integrity, long Rows) Open(string path)
    {
        if (sqlite3_open(Utf8(path), out var db) != 0) return ("לא נפתח", 0);
        try
        {
            string integrity = Scalar(db, "PRAGMA integrity_check;") ?? "שגיאה";
            long rows = long.TryParse(Scalar(db, "SELECT count(*) FROM t;"), out long n) ? n : -1;
            return (integrity, rows);
        }
        finally
        {
            sqlite3_close(db);
        }
    }

    private static void Exec(string path, string sql)
    {
        if (sqlite3_open(Utf8(path), out var db) != 0) throw new InvalidOperationException("לא נפתח");
        try
        {
            if (sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out _) != 0)
                throw new InvalidOperationException("הפקודה נכשלה");
        }
        finally
        {
            sqlite3_close(db);
        }
    }

    private static string? Scalar(IntPtr db, string sql)
    {
        if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != 0) return null;
        try
        {
            return sqlite3_step(statement) == 100 ? Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) : null;
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }
}
