using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// שחזור מסד SQLite שהדף הראשון שלו נהרס, בעזרת מסד תקין של אותה אפליקציה
/// (SqliteTransplant). "האפליקציה" כאן יוצרת את אותן טבלאות בכל מסד, עם נתונים אחרים.
/// </summary>
public class DatabaseReferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _output;

    public DatabaseReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"raf-db-{Guid.NewGuid():N}");
        _output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    private static readonly string[] Tables = ["messages", "chats", "contacts", "settings", "later", "trash"];

    /// <summary>מסד של "האפליקציה". extraTables — עוד טבלאות, כדי שרשימת הטבלאות תהיה ארוכה מדף.</summary>
    private string App(string name, int seed, int messages, int pageSize = 4096, int extraTables = 0, bool withLater = true)
    {
        string path = Path.Combine(_dir, name);
        using var db = SqliteEngine.Open(path, writable: true);
        db.Exec($"PRAGMA page_size = {pageSize}; PRAGMA journal_mode = DELETE; PRAGMA secure_delete = OFF;");
        db.Exec("""
            CREATE TABLE chats(_id INTEGER PRIMARY KEY, jid TEXT UNIQUE, title TEXT, archived INTEGER);
            CREATE TABLE messages(_id INTEGER PRIMARY KEY AUTOINCREMENT, chat_id INTEGER, text TEXT, ts INTEGER, media BLOB);
            CREATE TABLE contacts(jid TEXT PRIMARY KEY, name TEXT, score REAL) WITHOUT ROWID;
            CREATE TABLE settings(key TEXT, value TEXT);
            CREATE TABLE empty_one(a INTEGER, b TEXT, c REAL);
            CREATE TABLE trash(a TEXT, b INTEGER);
            CREATE INDEX messages_chat ON messages(chat_id, ts);
            CREATE VIEW recent AS SELECT * FROM messages ORDER BY ts DESC LIMIT 10;
            CREATE TRIGGER touch AFTER INSERT ON messages BEGIN UPDATE chats SET archived = 0 WHERE _id = new.chat_id; END;
            """);
        if (withLater) db.Exec("CREATE TABLE later(x INTEGER, y TEXT)");
        for (int t = 0; t < extraTables; t++)
            db.Exec($"CREATE TABLE extra_{t}(id INTEGER PRIMARY KEY, label TEXT, note TEXT, amount REAL, flags INTEGER); " +
                    $"CREATE INDEX extra_{t}_label ON extra_{t}(label)");

        db.Exec("BEGIN");
        db.Exec($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 30)
            INSERT INTO chats(jid, title, archived) SELECT 'chat' || i || '-{seed}@s.whatsapp.net', 'קבוצה ' || i, i % 2 FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < {messages})
            INSERT INTO messages(chat_id, text, ts, media)
              SELECT 1 + (i * {seed}) % 30, 'הודעה ' || i || ' ' || hex(randomblob(10 + i % 50)), 1700000000 + i * 60,
                     CASE WHEN i % 7 = 0 THEN randomblob(40) END FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 200)
            INSERT INTO contacts SELECT 'c' || i || '-{seed}', 'איש קשר ' || i, i * 1.5 FROM n;
            INSERT INTO settings VALUES ('theme', 'dark'), ('lang', 'he'), ('seed', '{seed}');
            """);
        if (withLater) db.Exec($"INSERT INTO later VALUES ({seed}, 'x')");
        for (int t = 0; t < extraTables; t++)
            db.Exec($"INSERT INTO extra_{t}(label, note, amount, flags) VALUES ('l{t}', 'n', {t}.5, {t})");
        db.Exec("COMMIT");
        // שורות שנמחקו (בפעולה נפרדת, כמו באפליקציה): הדפים שלהן התפנו אבל שומרים את
        // התוכן הישן — הן לא אמורות לחזור.
        db.Exec("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 400) " +
                "INSERT INTO trash SELECT 'נמחק ' || hex(randomblob(30)), i FROM n");
        db.Exec("DELETE FROM trash");
        db.Exec("PRAGMA user_version = 42");
        return path;
    }

    /// <summary>כל התוכן של הטבלאות, לשם השוואה.</summary>
    private static string Dump(string path, IEnumerable<string> tables)
    {
        using var db = SqliteEngine.Open(path);
        return string.Join("\n", tables.Select(t =>
            t + ":" + string.Join("|", db.Query($"SELECT * FROM {t} ORDER BY 1").Select(r => string.Join(",", r)))));
    }

    private static void Damage(string path, int pageSize, bool zeros)
    {
        byte[] d = File.ReadAllBytes(path);
        if (zeros) Array.Clear(d, 0, pageSize);
        else new Random(1).NextBytes(d.AsSpan(0, pageSize));
        File.WriteAllBytes(path, d);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void First_page_lost_is_restored_from_the_reference(bool zeros)
    {
        string original = App("orig.db", 3, 3000);
        string expected = Dump(original, Tables);
        string broken = Path.Combine(_dir, "broken.db");
        File.Copy(original, broken);
        Damage(broken, 4096, zeros);
        string donor = App("donor.db", 7, 500);

        var d = FileDoctor.Diagnose(broken);
        Assert.True(d.NeedsReferenceDatabase, string.Join(" ", d.Issues.Select(i => i.Kind)));

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message + " " + string.Join(" ", r.Applied));
        Assert.Equal(expected, Dump(r.OutputPath!, Tables));

        using var repaired = SqliteEngine.Open(r.OutputPath!);
        Assert.Equal("42", repaired.Scalar("PRAGMA user_version"));
        Assert.Equal("1", repaired.Scalar("SELECT count(*) FROM sqlite_master WHERE name = 'messages_chat'"));
        Assert.Equal("3000", repaired.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'messages'"));
    }

    [Fact]
    public void Long_table_list_that_survived_is_read_from_the_file_itself()
    {
        // עם דפים של 1KB ועשרות טבלאות, רשימת הטבלאות ארוכה מדף — הדף הראשון רק מצביע
        // לעלים שלה, והם שרדו. הדוגמה ישנה יותר: חסרות בה הטבלאות הנוספות וגם "later".
        string original = App("orig.db", 5, 400, pageSize: 1024, extraTables: 40);
        var all = Tables.Concat(Enumerable.Range(0, 40).Select(t => $"extra_{t}")).ToList();
        string expected = Dump(original, all);
        string broken = Path.Combine(_dir, "broken.db");
        File.Copy(original, broken);
        Damage(broken, 1024, zeros: false);
        string donor = App("donor.db", 9, 50, pageSize: 1024, withLater: false);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message + " " + string.Join(" ", r.Applied));
        Assert.Equal(expected, Dump(r.OutputPath!, all));
    }

    [Fact]
    public void Missing_table_is_created_empty_and_reported()
    {
        // הדוגמה חדשה יותר: יש בה טבלה שאין במסד הפגום.
        string original = App("orig.db", 11, 800, withLater: false);
        string broken = Path.Combine(_dir, "broken.db");
        File.Copy(original, broken);
        Damage(broken, 4096, zeros: true);
        string donor = App("donor.db", 12, 100);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.NotNull(r.OutputPath);
        Assert.Contains(r.Applied, a => a.Contains("later"));
        Assert.Equal(Dump(original, ["messages", "chats", "contacts", "settings"]),
                     Dump(r.OutputPath!, ["messages", "chats", "contacts", "settings"]));
    }

    [Fact]
    public void Database_of_another_app_is_rejected()
    {
        string original = App("orig.db", 13, 300);
        string broken = Path.Combine(_dir, "broken.db");
        File.Copy(original, broken);
        Damage(broken, 4096, zeros: true);

        string other = Path.Combine(_dir, "other.db");
        using (var db = SqliteEngine.Open(other, writable: true))
            db.Exec("CREATE TABLE notes(id INTEGER PRIMARY KEY, a TEXT, b TEXT, c TEXT, d TEXT, e TEXT, f TEXT, g TEXT); " +
                    "INSERT INTO notes(a) VALUES ('x')");

        var r = FileDoctor.RepairPhoto(broken, other, _output);
        Assert.Null(r.OutputPath);
    }

    [Fact]
    public void Healthy_database_does_not_ask_for_a_reference()
    {
        Assert.False(FileDoctor.Diagnose(App("ok.db", 14, 100)).NeedsReferenceDatabase);
        Assert.NotNull(SqliteTransplant.DescribeReference(App("x.db", 15, 10) + ".missing"));
    }

    [Fact]
    public void Column_count_is_read_from_the_definition()
    {
        Assert.Equal(5, SqliteTransplant.ColumnCount("CREATE TABLE m(_id INTEGER PRIMARY KEY, a TEXT DEFAULT 'x,y', b NUMERIC(10,2), c, d BLOB)"));
        Assert.Equal(2, SqliteTransplant.ColumnCount("CREATE TABLE t(a, b, PRIMARY KEY(a, b), UNIQUE(b), CHECK (a > 0))"));
    }
}
