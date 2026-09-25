using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Repair;

/// <summary>
/// שחזור מסד נתונים SQLite שהדף הראשון שלו נהרס, בעזרת מסד תקין מאותה אפליקציה.
///
/// בדף הראשון יש הכותרת ורשימת הטבלאות (sqlite_master): לכל טבלה — ההגדרה שלה ומספר
/// הדף שבו מתחיל העץ שלה. כשהדף הזה נדרס, כל שאר הדפים — כל השורות — עדיין בקובץ,
/// אבל אף תוכנה לא יודעת איזה עץ שייך לאיזו טבלה ומה העמודות שלו.
///
/// אפליקציה יוצרת את אותן טבלאות בכל מכשיר. מהמסד לדוגמה נלקחות ההגדרות; בקובץ הפגום
/// נמצאים כל העצים שאף דף לא מצביע אליהם — השורשים — וכל טבלה מותאמת לשורש שלה לפי מספר
/// העמודות וסוגי הערכים בשורות (מספרים, טקסט, נתונים בינאריים). אם חלק מרשימת הטבלאות
/// שרד בדפים אחרים (כשהיא ארוכה מדף אחד), היא נקראת משם — עם מספרי הדפים המדויקים.
///
/// מהקובץ המשוחזר מועתקים הנתונים, טבלה אחר טבלה, למסד חדש ונקי במנוע SQLite עצמו,
/// והאינדקסים נבנים בו מחדש. כך המסד שנכתב תקין לפי SQLite, ולא רק לפי הבדיקה שלנו.
/// </summary>
public static class SqliteTransplant
{
    /// <summary>מסד גדול מזה אינו משוחזר — הוא נקרא כולו לזיכרון.</summary>
    public const long MaxSize = 1536L * 1024 * 1024;

    private sealed record SchemaRow(string Type, string Name, string Table, int Root, string? Sql);

    /// <summary>עץ בקובץ: שורש, סוג (טבלה או אינדקס), מספר השורות, ופרופיל העמודות.</summary>
    private sealed class Tree
    {
        public int Root;
        public bool IsTable;
        public long Records;
        public int MaxColumns;
        public readonly long[,] Classes = new long[MaxProfiled, 5];
    }

    private const int MaxProfiled = 64;

    private sealed class Db
    {
        public byte[] D = [];
        public int PageSize, Usable, Pages, Encoding = 1;
        public int Start(int page) => (page - 1) * PageSize;
        public int Header(int page) => page == 1 ? 100 : 0;
    }

    // ================================================================ אבחון

    /// <summary>
    /// הדף הראשון נהרס (אין בו עץ של רשימת הטבלאות), ושאר הקובץ נראה כמו דפים של מסד.
    /// </summary>
    internal static bool LostSchema(ReadOnlySpan<byte> head, Func<long, int, byte[]> read, long size)
        => head.Length > 100 && head[100] is not (0x05 or 0x0D) && SqliteHeader.InferPageSize(read, size) > 0;

    /// <summary>בדיקת מסד הדוגמה: null — מתאים; אחרת, מה הבעיה, במילים פשוטות.</summary>
    public static string? DescribeReference(string path)
    {
        try
        {
            using var engine = SqliteEngine.Open(path);
            if (engine.Scalar("PRAGMA quick_check") != "ok") return L.T("מסד הדוגמה עצמו פגום. בחרו מסד תקין.");
            if (Schema(engine).Count(r => r.Type == "table") == 0) return L.T("במסד הדוגמה אין טבלאות.");
            return null;
        }
        catch (Exception)
        {
            return L.T("הקובץ שנבחר אינו מסד נתונים SQLite תקין.");
        }
    }

    private static List<SchemaRow> Schema(SqliteEngine engine)
        => engine.Query("SELECT type, name, tbl_name, rootpage, sql FROM sqlite_master ORDER BY rowid")
            .Select(r => new SchemaRow(r[0] ?? "", r[1] ?? "", r[2] ?? "", int.TryParse(r[3], out int p) ? p : 0, r[4]))
            .ToList();

    // ================================================================ שחזור

    public static PhotoRebuildResult Rebuild(string brokenPath, string referencePath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(brokenPath), Path.GetFullPath(referencePath), StringComparison.OrdinalIgnoreCase))
            return new PhotoRebuildResult { Message = L.T("מסד הדוגמה הוא המסד הפגום עצמו. בחרו מסד תקין אחר מאותה אפליקציה.") };
        if (DescribeReference(referencePath) is { } problem) return new PhotoRebuildResult { Message = problem };
        if (new FileInfo(brokenPath).Length > MaxSize || new FileInfo(referencePath).Length > MaxSize)
            return new PhotoRebuildResult { Message = L.T("המסד גדול מדי לשחזור.") };

        List<SchemaRow> donorSchema;
        string? userVersion, applicationId;
        using (var engine = SqliteEngine.Open(referencePath))
        {
            donorSchema = Schema(engine);
            userVersion = engine.Scalar("PRAGMA user_version");
            applicationId = engine.Scalar("PRAGMA application_id");
        }

        var donor = Load(File.ReadAllBytes(referencePath), null);
        byte[] brokenBytes = File.ReadAllBytes(brokenPath);
        int pageSize = SqliteHeader.InferPageSize((at, n) => brokenBytes.AsSpan((int)Math.Min(at, brokenBytes.Length),
            (int)Math.Min(n, Math.Max(0, brokenBytes.Length - at))).ToArray(), brokenBytes.Length);
        if (pageSize == 0)
            return new PhotoRebuildResult { Message = L.T("לא נמצאו במסד הפגום דפים של נתונים — גודל הדף שלו אינו ניתן לזיהוי.") };
        var broken = Load(brokenBytes, (pageSize, donor.PageSize - donor.Usable));
        broken.Encoding = DetectEncoding(broken) ?? donor.Encoding;

        // --- העצים בקובץ הפגום: כל עץ שאף דף לא מצביע אליו.
        var roots = Roots(broken);
        var schemaPages = new HashSet<int>();
        var ownSchema = OwnSchema(broken, roots, schemaPages);

        var trees = roots.Where(r => !schemaPages.Contains(r))
            .Select(r => Walk(broken, r))
            .OfType<Tree>()
            .ToList();

        // --- ההגדרות: מהקובץ עצמו כשרשימת הטבלאות שרדה בו, ומהדוגמה — מה שחסר.
        var rows = new List<SchemaRow>();
        var own = ownSchema.Where(r => r.Type != "table" || trees.Any(t => t.Root == r.Root)).ToList();
        rows.AddRange(own);
        rows.AddRange(donorSchema.Where(d => !own.Any(o => o.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))));

        // --- התאמה: כל טבלה שאין לה שורש ידוע — לעץ שהכי דומה לה.
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in own.Where(r => r.Type == "table")) assigned[r.Name] = r.Root;
        var free = trees.Where(t => !assigned.Values.Contains(t.Root)).ToList();

        var wanted = rows.Where(r => r.Type == "table" && !assigned.ContainsKey(r.Name) && r.Sql is not null).ToList();
        var candidates = new List<(double Score, long Records, SchemaRow Row, Tree Tree)>();
        foreach (var row in wanted)
        {
            var donorRow = donorSchema.FirstOrDefault(d => d.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            Tree? profile = donorRow is not null ? Walk(donor, donorRow.Root) : null;
            bool withoutRowid = row.Sql!.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
            int columns = profile is { Records: > 0 } ? profile.MaxColumns : ColumnCount(row.Sql);
            foreach (var tree in free)
                if (Score(row, donorRow, profile, columns, withoutRowid, tree) is { } score)
                    candidates.Add((score, tree.Records, row, tree));
        }
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Records))
        {
            if (assigned.ContainsKey(c.Row.Name) || assigned.Values.Contains(c.Tree.Root)) continue;
            assigned[c.Row.Name] = c.Tree.Root;
        }

        var tables = rows.Where(r => r.Type == "table" && r.Sql is not null).ToList();
        var missing = tables.Where(t => !assigned.ContainsKey(t.Name) && !t.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)).ToList();
        // עץ ריק מתאים לכל טבלה — לכן נדרשת לפחות טבלה אחת עם נתונים שהותאמה.
        if (!trees.Any(t => t.Records > 0 && assigned.Values.Contains(t.Root)))
            return new PhotoRebuildResult
            {
                Message = L.T("אף טבלה של מסד הדוגמה לא נמצאה במסד הפגום. בחרו מסד של אותה אפליקציה (ורצוי מאותה גרסה)."),
            };

        // --- מסד ביניים: הקובץ הפגום עם דף ראשון חדש — ממנו SQLite קורא את הנתונים.
        string temp = Path.Combine(Path.GetTempPath(), $"raf-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            if (!WriteIntermediate(broken, rows, assigned, temp))
                return new PhotoRebuildResult { Message = L.T("רשימת הטבלאות ארוכה מדי כדי לבנות אותה מחדש.") };

            return CopyToClean(temp, outputPath, rows, assigned, missing, donor, userVersion, applicationId);
        }
        finally
        {
            foreach (string f in new[] { temp, temp + "-journal", temp + "-wal", temp + "-shm" })
                try { File.Delete(f); } catch { }
        }
    }

    /// <summary>
    /// ציון ההתאמה בין טבלה לעץ, או null כשאינם יכולים להיות אותו דבר. מספר העמודות בעץ
    /// יכול להיות קטן מבהגדרה (עמודה שנוספה בגרסה מאוחרת — שורות ישנות קצרות יותר), לא גדול.
    /// </summary>
    private static double? Score(SchemaRow row, SchemaRow? donorRow, Tree? profile, int columns, bool withoutRowid, Tree tree)
    {
        if (tree.IsTable == withoutRowid) return null;
        double score = 0;
        if (tree.Records > 0)
        {
            if (columns > 0 && (tree.MaxColumns > columns || tree.MaxColumns < columns - 3 || tree.MaxColumns < 1)) return null;
            score += columns <= 0 ? 0.5 : tree.MaxColumns == columns ? 2 : 0.5;
            if (profile is { Records: > 0 })
            {
                // סוגי הערכים חייבים להיות דומים: טבלה אחרת עם אותו מספר עמודות נראית אחרת.
                double similarity = Similarity(profile, tree);
                if (similarity < 0.5) return null;
                score += 3 * similarity;
            }
        }
        else if (profile is { Records: > 0 }) score -= 0.5;             // בדוגמה יש נתונים, כאן ריק — אפשרי, פחות סביר
        if (donorRow is not null && donorRow.Root == tree.Root) score += 1;   // אותו מספר דף כמו בדוגמה
        return score;
    }

    /// <summary>דמיון בין פרופילים: לכל עמודה, החפיפה בין התפלגויות סוגי הערכים.</summary>
    private static double Similarity(Tree a, Tree b)
    {
        int columns = Math.Min(Math.Min(a.MaxColumns, b.MaxColumns), MaxProfiled);
        if (columns == 0) return 0;
        double total = 0;
        for (int c = 0; c < columns; c++)
        {
            double sa = 0, sb = 0, overlap = 0;
            for (int k = 0; k < 5; k++) { sa += a.Classes[c, k]; sb += b.Classes[c, k]; }
            if (sa == 0 || sb == 0) continue;
            for (int k = 0; k < 5; k++) overlap += Math.Min(a.Classes[c, k] / sa, b.Classes[c, k] / sb);
            total += overlap;
        }
        return total / columns;
    }

    /// <summary>מספר העמודות בהגדרת טבלה — כשבמסד הדוגמה אין בה שורות. -1 כשלא ניתן לקרוא.</summary>
    internal static int ColumnCount(string sql)
    {
        int open = sql.IndexOf('('), close = sql.LastIndexOf(')');
        if (open < 0 || close <= open) return -1;
        int depth = 0, count = 0;
        var part = new StringBuilder();
        char quote = '\0';
        void Flush()
        {
            string p = part.ToString().Trim();
            string upper = p.ToUpperInvariant();
            if (p.Length > 0 && !(upper.StartsWith("CONSTRAINT") || upper.StartsWith("PRIMARY KEY") || upper.StartsWith("UNIQUE")
                                  || upper.StartsWith("CHECK") || upper.StartsWith("FOREIGN KEY")))
                count++;
            part.Clear();
        }
        for (int i = open + 1; i < close; i++)
        {
            char ch = sql[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; part.Append(ch); continue; }
            if (ch is '\'' or '"' or '`') quote = ch;
            else if (ch == '[') quote = ']';
            else if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0) { Flush(); continue; }
            part.Append(ch);
        }
        Flush();
        return count;
    }

    // ================================================================ מסד הביניים

    private static bool WriteIntermediate(Db broken, List<SchemaRow> rows, Dictionary<string, int> assigned, string path)
    {
        int page = broken.PageSize;
        int next = broken.Pages + 1;
        var extra = new List<byte[]>();

        // טבלה שלא נמצאה, ואינדקס אוטומטי (של UNIQUE או PRIMARY KEY) — עץ ריק חדש.
        int EmptyTree(byte type)
        {
            byte[] p = new byte[page];
            p[0] = type;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(5), (ushort)(broken.Usable == 65536 ? 0 : broken.Usable));
            extra.Add(p);
            return next++;
        }

        var records = new List<byte[]>();
        foreach (var r in rows)
        {
            int root;
            if (r.Type == "table") root = assigned.TryGetValue(r.Name, out int a) ? a : EmptyTree(r.Sql?.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) == true ? (byte)0x0A : (byte)0x0D);
            else if (r.Type == "index" && r.Sql is null) root = EmptyTree(0x0A);
            else if (r.Type == "index") continue;                        // אינדקס רגיל ייבנה מחדש במסד הנקי
            else root = 0;
            records.Add(Record(broken.Encoding, r.Type, r.Name, r.Table, root, r.Sql));
        }

        // --- עץ רשימת הטבלאות: בדף הראשון אם הוא נכנס; אחרת — עלים בסוף הקובץ, והדף הראשון מצביע אליהם.
        byte[] first = new byte[page];
        var leaves = new List<(byte[] Page, long LastRowid)>();
        long rowid = 1;
        var cells = new List<byte[]>();
        int capacity = broken.Usable - 100 - 8;
        foreach (var rec in records)
        {
            byte[] cell = [.. Varint(rec.Length), .. Varint(rowid), .. rec];
            if (rec.Length > broken.Usable - 35) return false;                    // דורש דפי המשך — לא נתמך
            cells.Add(cell);
            rowid++;
        }

        if (cells.Sum(c => c.Length + 2) <= capacity) FillLeaf(first, 100, cells, broken.Usable);
        else
        {
            var current = new List<byte[]>();
            long id = 0;
            foreach (var cell in cells)
            {
                id++;
                if (current.Sum(c => c.Length + 2) + cell.Length + 2 > broken.Usable - 8)
                {
                    leaves.Add((Leaf(current, page, broken.Usable), id - 1));
                    current.Clear();
                }
                current.Add(cell);
            }
            leaves.Add((Leaf(current, page, broken.Usable), id));

            int firstLeaf = next;
            foreach (var (p, _) in leaves) { extra.Add(p); next++; }
            var pointers = new List<byte[]>();
            for (int i = 0; i < leaves.Count - 1; i++)
            {
                byte[] child = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(child, firstLeaf + i);
                pointers.Add([.. child, .. Varint(leaves[i].LastRowid)]);
            }
            if (pointers.Sum(c => c.Length + 2) > broken.Usable - 100 - 12) return false;
            FillInterior(first, 100, pointers, firstLeaf + leaves.Count - 1, broken.Usable);
        }

        // --- כותרת: קבועה, בלי רשימת דפים פנויים (הם יאבדו — הנתונים לא).
        "SQLite format 3\0"u8.CopyTo(first);
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(16), (ushort)(page == 65536 ? 1 : page));
        first[18] = 1; first[19] = 1;
        first[20] = (byte)(page - broken.Usable);
        first[21] = 64; first[22] = 32; first[23] = 32;
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(28), next - 1);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(44), 4);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(56), broken.Encoding);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(92), 1);
        BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(96), 3045000);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(first);
        file.Write(broken.D, page, broken.Pages * page - page);
        foreach (var p in extra) file.Write(p);
        return true;
    }

    private static byte[] Leaf(List<byte[]> cells, int page, int usable)
    {
        byte[] p = new byte[page];
        FillLeaf(p, 0, cells, usable);
        return p;
    }

    private static void FillLeaf(byte[] p, int header, List<byte[]> cells, int usable)
        => Fill(p, header, 0x0D, 8, cells, usable, 0);

    private static void FillInterior(byte[] p, int header, List<byte[]> cells, int right, int usable)
        => Fill(p, header, 0x05, 12, cells, usable, right);

    private static void Fill(byte[] p, int header, byte type, int headerLength, List<byte[]> cells, int usable, int right)
    {
        p[header] = type;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(header + 3), (ushort)cells.Count);
        int content = usable;
        for (int i = 0; i < cells.Count; i++)
        {
            content -= cells[i].Length;
            cells[i].CopyTo(p, content);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(header + headerLength + 2 * i), (ushort)content);
        }
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(header + 5), (ushort)(content == 65536 ? 0 : content));
        if (type == 0x05) BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(header + 8), right);
    }

    /// <summary>רשומה של רשימת הטבלאות: סוג, שם, טבלה, דף השורש וההגדרה.</summary>
    private static byte[] Record(int encoding, string type, string name, string table, int root, string? sql)
    {
        Encoding text = encoding switch { 2 => Encoding.Unicode, 3 => Encoding.BigEndianUnicode, _ => Encoding.UTF8 };
        byte[][] values = [text.GetBytes(type), text.GetBytes(name), text.GetBytes(table)];
        byte[] rootBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rootBytes, root);
        byte[]? sqlBytes = sql is null ? null : text.GetBytes(sql);

        var header = new List<byte>();
        foreach (var v in values) header.AddRange(Varint(v.Length * 2 + 13));
        header.AddRange(root == 0 ? Varint(8) : Varint(4));              // 8 — הקבוע 0
        header.AddRange(sqlBytes is null ? Varint(0) : Varint(sqlBytes.Length * 2 + 13));

        // אורך הכותרת כולל את עצמו.
        int length = header.Count + 1;
        if (Varint(length).Length > 1) length++;
        var record = new List<byte>(Varint(length));
        record.AddRange(header);
        foreach (var v in values) record.AddRange(v);
        if (root != 0) record.AddRange(rootBytes);
        if (sqlBytes is not null) record.AddRange(sqlBytes);
        return record.ToArray();
    }

    private static byte[] Varint(long value)
    {
        if (value < 0x80) return [(byte)value];
        ulong v = (ulong)value;
        var stack = new Stack<byte>();
        stack.Push((byte)(v & 0x7F));
        v >>= 7;
        while (v > 0) { stack.Push((byte)(v & 0x7F | 0x80)); v >>= 7; }
        return stack.ToArray();
    }

    // ================================================================ העתקה למסד נקי

    private static PhotoRebuildResult CopyToClean(string intermediate, string outputPath, List<SchemaRow> rows,
        Dictionary<string, int> assigned, List<SchemaRow> missing, Db donor, string? userVersion, string? applicationId)
    {
        var applied = new List<string>();
        var failed = new List<string>();
        long total = 0;
        int copied = 0;

        using (var output = SqliteEngine.Open(outputPath, writable: true))
        {
            output.Exec($"PRAGMA page_size = {donor.PageSize}");
            output.Exec("PRAGMA encoding = " + (donor.Encoding switch { 2 => "'UTF-16le'", 3 => "'UTF-16be'", _ => "'UTF-8'" }));
            output.Exec("PRAGMA foreign_keys = OFF");

            var tables = rows.Where(r => r.Type == "table" && r.Sql is not null && !r.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)).ToList();
            output.Exec("BEGIN");
            foreach (var t in tables) output.Exec(t.Sql!);
            output.Exec("COMMIT");
            output.Exec($"ATTACH DATABASE {SqliteEngine.Literal(intermediate)} AS old");

            foreach (var t in tables.Where(t => assigned.ContainsKey(t.Name)))
            {
                string name = SqliteEngine.Quote(t.Name);
                try
                {
                    // "WHERE 1" — בלי ההעתקה המהירה של SQLite, שמעתיקה גם את האינדקסים כמו שהם;
                    // במסד הביניים האינדקסים האוטומטיים ריקים, והם ייבנו כאן מהשורות.
                    output.Exec($"INSERT INTO main.{name} SELECT * FROM old.{name} WHERE 1");
                    long count = long.Parse(output.Scalar($"SELECT count(*) FROM main.{name}") ?? "0");
                    total += count;
                    copied++;
                }
                catch (InvalidOperationException)
                {
                    failed.Add(t.Name);
                }
            }

            // מונה המספור של טבלאות AUTOINCREMENT — כפי שהיה, ולא כפי שנוצר בהעתקה.
            if (assigned.ContainsKey("sqlite_sequence"))
                try
                {
                    output.Exec("DELETE FROM main.sqlite_sequence");
                    output.Exec("INSERT INTO main.sqlite_sequence SELECT * FROM old.sqlite_sequence");
                }
                catch (InvalidOperationException) { }

            output.Exec("DETACH DATABASE old");

            // אינדקסים, תצוגות וטריגרים — אחרי הנתונים, כדי שהטריגרים לא יופעלו על ההעתקה.
            int indexFailures = 0;
            foreach (var r in rows.Where(r => r.Type is "index" or "view" or "trigger" && r.Sql is not null)
                                  .OrderBy(r => r.Type == "trigger"))
                try { output.Exec(r.Sql!); }
                catch (InvalidOperationException) { if (r.Type == "index") indexFailures++; }

            if (long.TryParse(userVersion, out long uv)) output.Exec($"PRAGMA user_version = {uv}");
            if (long.TryParse(applicationId, out long ai)) output.Exec($"PRAGMA application_id = {ai}");

            applied.Add(L.T("שוחזרו {0} טבלאות עם {1} שורות. הגדרות הטבלאות שלא שרדו נלקחו ממסד הדוגמה, " +
                "והנתונים — מהמסד עצמו.", copied.ToString("N0"), total.ToString("N0")));
            if (missing.Count > 0)
                applied.Add(L.T("{0} טבלאות לא נמצאו במסד הפגום ונוצרו ריקות: {1}.", missing.Count,
                    string.Join(", ", missing.Take(10).Select(m => m.Name)) + (missing.Count > 10 ? "…" : "")));
            if (failed.Count > 0)
                applied.Add(L.T("{0} טבלאות נמצאו אבל לא ניתן היה לקרוא אותן (נתונים פגומים), והן ריקות: {1}.", failed.Count,
                    string.Join(", ", failed.Take(10)) + (failed.Count > 10 ? "…" : "")));
            if (indexFailures > 0)
                applied.Add(L.T("{0} אינדקסים לא נבנו מחדש (הנתונים אינם מאפשרים אותם).", indexFailures));

            string? check = output.Scalar("PRAGMA integrity_check");
            if (check != "ok" || copied == 0)
                return new PhotoRebuildResult { Message = L.T("המסד המשוחזר לא עבר את בדיקת השלמות של SQLite."), Applied = applied };
        }

        return new PhotoRebuildResult
        {
            Written = true,
            Complete = missing.Count == 0 && failed.Count == 0,
            Fraction = 1,
            Applied = applied,
            Message = L.T("המסד שוחזר, ועבר את בדיקת השלמות של SQLite."),
        };
    }

    // ================================================================ קריאת הדפים

    private static Db Load(byte[] d, (int PageSize, int Reserved)? known)
    {
        int pageSize = known?.PageSize ?? (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(16)) is 1 ? 65536 : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(16)));
        int reserved = known?.Reserved ?? d[20];
        var db = new Db { D = d, PageSize = pageSize, Usable = pageSize - reserved, Pages = d.Length / pageSize };
        if (known is null) db.Encoding = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(56)) is var e and (2 or 3) ? e : 1;
        return db;
    }

    /// <summary>סוג הדף ומספר התאים, כשהדף הוא עץ תקין; אחרת null.</summary>
    private static (byte Type, int Cells, int PointerArray)? Page(Db db, int page)
    {
        if (page < 1 || page > db.Pages) return null;
        int start = db.Start(page), h = db.Header(page);
        byte type = db.D[start + h];
        if (!SqliteHeader.IsTreePage(type)) return null;
        int cells = BinaryPrimitives.ReadUInt16BigEndian(db.D.AsSpan(start + h + 3));
        int pointers = h + (type is 0x02 or 0x05 ? 12 : 8);
        if (pointers + 2 * cells > db.Usable) return null;
        for (int i = 0; i < cells; i++)
        {
            int p = BinaryPrimitives.ReadUInt16BigEndian(db.D.AsSpan(start + pointers + 2 * i));
            if (p < pointers + 2 * cells || p >= db.Usable) return null;
        }
        return (type, cells, pointers);
    }

    private static int Cell(Db db, int page, int pointers, int i)
        => db.Start(page) + BinaryPrimitives.ReadUInt16BigEndian(db.D.AsSpan(db.Start(page) + pointers + 2 * i));

    private static IEnumerable<int> Children(Db db, int page, (byte Type, int Cells, int PointerArray) info)
    {
        if (info.Type is not (0x02 or 0x05)) yield break;
        for (int i = 0; i < info.Cells; i++) yield return BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(Cell(db, page, info.PointerArray, i)));
        yield return BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(db.Start(page) + db.Header(page) + 8));
    }

    /// <summary>
    /// דפי העץ שאף דף פנימי אינו מצביע אליהם — שורשים של טבלאות ואינדקסים. בלי דפים
    /// שהתפנו: דף שנמחק שומר את התוכן הישן שלו ונראה כמו עץ, אבל השורות בו כבר אינן בטבלה.
    /// </summary>
    private static List<int> Roots(Db db)
    {
        var child = new HashSet<int>();
        var trees = new List<int>();
        for (int p = 2; p <= db.Pages; p++)
        {
            if (Page(db, p) is not { } info) continue;
            trees.Add(p);
            foreach (int c in Children(db, p, info)) child.Add(c);
        }
        var free = FreePages(db, child);
        return trees.Where(p => !child.Contains(p) && !free.Contains(p)).ToList();
    }

    /// <summary>
    /// הדפים הפנויים. המצביע לרשימה שלהם היה בכותרת שנהרסה, אבל דפי הרשימה עצמם שרדו:
    /// מספר הדף הבא ברשימה, כמה דפים רשומים בדף, ומספרי הדפים. דף כזה מזוהה כשכל המספרים
    /// בו חוקיים, שונים זה מזה, ואף אחד מהם אינו חלק מעץ פעיל.
    /// </summary>
    private static HashSet<int> FreePages(Db db, HashSet<int> child)
    {
        var free = new HashSet<int>();
        for (int p = 2; p <= db.Pages; p++)
        {
            if (Page(db, p) is not null) continue;
            int start = db.Start(p);
            int next = BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(start));
            int count = BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(start + 4));
            if (next < 0 || next > db.Pages || next == 1 || count < 1 || count > db.Usable / 4 - 2) continue;
            var listed = new HashSet<int>();
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
            {
                int leaf = BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(start + 8 + 4 * i));
                ok = leaf >= 2 && leaf <= db.Pages && leaf != p && listed.Add(leaf) && !child.Contains(leaf);
            }
            if (ok) { free.Add(p); free.UnionWith(listed); }
        }
        return free;
    }

    /// <summary>מעבר על עץ שלם: מספר השורות ופרופיל העמודות. null — העץ אינו תקין.</summary>
    private static Tree? Walk(Db db, int root)
    {
        if (Page(db, root) is not { } first) return null;
        bool table = first.Type is 0x05 or 0x0D;
        var tree = new Tree { Root = root, IsTable = table };
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(root);
        long sampled = 0;
        while (pending.Count > 0)
        {
            int page = pending.Pop();
            if (!seen.Add(page) || Page(db, page) is not { } info) return null;
            if ((info.Type is 0x05 or 0x0D) != table) return null;
            foreach (int c in Children(db, page, info)) pending.Push(c);
            if (info.Type == 0x05) continue;

            for (int i = 0; i < info.Cells; i++)
            {
                tree.Records++;
                if (sampled >= 5000) continue;
                if (Serials(db, page, info, i) is not { } serials) continue;
                sampled++;
                tree.MaxColumns = Math.Max(tree.MaxColumns, serials.Count);
                for (int c = 0; c < Math.Min(serials.Count, MaxProfiled); c++)
                    if (Class(serials[c]) is int k and >= 0) tree.Classes[c, k]++;
            }
        }
        return tree;
    }

    private static int Class(long serial) => serial switch
    {
        0 => 0,
        >= 1 and <= 6 or 8 or 9 => 1,
        7 => 2,
        >= 13 when serial % 2 == 1 => 3,
        >= 12 => 4,
        _ => -1,
    };

    /// <summary>סוגי הערכים ברשומה (מכותרת הרשומה, שנמצאת תמיד בחלק שבתוך הדף).</summary>
    private static List<long>? Serials(Db db, int page, (byte Type, int Cells, int PointerArray) info, int i)
    {
        int at = Cell(db, page, info.PointerArray, i);
        int end = db.Start(page) + db.Usable;
        if (info.Type == 0x02) at += 4;
        long payload = ReadVarint(db.D, ref at, end);
        if (info.Type == 0x0D) ReadVarint(db.D, ref at, end);
        int recordStart = at;
        long headerLength = ReadVarint(db.D, ref at, end);
        if (payload <= 0 || headerLength < 1 || headerLength > payload || recordStart + headerLength > end) return null;
        var serials = new List<long>();
        while (at < recordStart + headerLength)
        {
            long s = ReadVarint(db.D, ref at, end);
            if (s is 10 or 11 || s < 0) return null;
            serials.Add(s);
        }
        return serials;
    }

    private static long ReadVarint(byte[] d, ref int at, int end)
    {
        long v = 0;
        for (int i = 0; i < 9; i++)
        {
            if (at >= end) return -1;
            byte b = d[at++];
            if (i == 8) return (v << 8) | b;
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return v;
        }
        return v;
    }

    private static int SerialSize(long s) => s switch
    {
        0 or 8 or 9 => 0,
        1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 or 7 => 8,
        >= 12 => (int)((s - (s % 2 == 0 ? 12 : 13)) / 2),
        _ => 0,
    };

    /// <summary>
    /// קידוד הטקסט של המסד הפגום (הכותרת שבה הוא כתוב נהרסה): לפי הטקסטים עצמם — ב-UTF-16
    /// כמעט כל תו לטיני מלווה באפס. null — אין מספיק טקסט להכריע.
    /// </summary>
    private static int? DetectEncoding(Db db)
    {
        long zeroEven = 0, zeroOdd = 0, bytes = 0;
        for (int p = 2; p <= db.Pages && bytes < 200_000; p++)
        {
            if (Page(db, p) is not { Type: 0x0D } info) continue;
            for (int i = 0; i < info.Cells; i++)
            {
                if (Serials(db, p, info, i) is not { } serials) continue;
                int at = Cell(db, p, info.PointerArray, i), end = db.Start(p) + db.Usable;
                ReadVarint(db.D, ref at, end);
                ReadVarint(db.D, ref at, end);
                int record = at;
                long headerLength = ReadVarint(db.D, ref at, end);
                int value = record + (int)headerLength;
                foreach (long s in serials)
                {
                    int size = SerialSize(s);
                    if (s >= 13 && s % 2 == 1 && value + size <= end)
                        for (int k = 0; k < size; k++)
                        {
                            bytes++;
                            if (db.D[value + k] == 0) { if (k % 2 == 0) zeroEven++; else zeroOdd++; }
                        }
                    value += size;
                }
            }
        }
        if (bytes < 1000) return null;
        if ((zeroEven + zeroOdd) < bytes / 5) return 1;
        return zeroOdd > zeroEven ? 2 : 3;
    }

    // ================================================================ רשימת הטבלאות ששרדה

    /// <summary>
    /// עלים של רשימת הטבלאות ששרדו בדפים אחרים (כשהיא ארוכה מדף אחד, הדף הראשון רק מצביע
    /// אליהם): עלה שכל הרשומות בו הן בדיוק מהצורה של רשימת הטבלאות.
    /// </summary>
    private static List<SchemaRow> OwnSchema(Db db, List<int> roots, HashSet<int> schemaPages)
    {
        var found = new List<SchemaRow>();
        foreach (int p in roots)
        {
            if (Page(db, p) is not { Type: 0x0D } info || info.Cells == 0) continue;
            var rows = new List<SchemaRow>();
            for (int i = 0; i < info.Cells && rows.Count == i; i++)
                if (SchemaRecord(db, p, info, i) is { } row) rows.Add(row);
            if (rows.Count != info.Cells) continue;
            schemaPages.Add(p);
            found.AddRange(rows);
        }
        return found.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
    }

    private static SchemaRow? SchemaRecord(Db db, int page, (byte Type, int Cells, int PointerArray) info, int i)
    {
        if (Payload(db, page, info, i) is not { } payload) return null;
        int at = 0;
        long headerLength = ReadVarint(payload, ref at, payload.Length);
        if (headerLength < 6 || headerLength > payload.Length) return null;
        var serials = new List<long>();
        while (at < headerLength) serials.Add(ReadVarint(payload, ref at, payload.Length));
        if (serials.Count != 5) return null;

        Encoding text = db.Encoding switch { 2 => Encoding.Unicode, 3 => Encoding.BigEndianUnicode, _ => Encoding.UTF8 };
        var values = new object?[5];
        int value = (int)headerLength;
        for (int c = 0; c < 5; c++)
        {
            long s = serials[c];
            int size = SerialSize(s);
            if (value + size > payload.Length) return null;
            values[c] = s >= 13 && s % 2 == 1 ? text.GetString(payload, value, size)
                      : s is >= 1 and <= 6 ? ReadInt(payload, value, size)
                      : s is 8 ? 0L : s is 9 ? 1L : s == 0 ? null : (object?)"?";
            value += size;
        }
        if (values[0] is not string type || type is not ("table" or "index" or "view" or "trigger")) return null;
        if (values[1] is not string name || values[2] is not string table || values[3] is not long root) return null;
        if (values[4] is not (null or string)) return null;
        if (values[4] is string sql && !sql.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) return null;
        return new SchemaRow(type, name, table, (int)root, values[4] as string);
    }

    private static long ReadInt(byte[] d, int at, int size)
    {
        long v = (sbyte)d[at];
        for (int i = 1; i < size; i++) v = (v << 8) | d[at + i];
        return v;
    }

    /// <summary>התוכן המלא של רשומה בעלה של טבלה — כולל דפי ההמשך, כשהיא ארוכה מהדף.</summary>
    private static byte[]? Payload(Db db, int page, (byte Type, int Cells, int PointerArray) info, int i)
    {
        int at = Cell(db, page, info.PointerArray, i), end = db.Start(page) + db.Usable;
        long length = ReadVarint(db.D, ref at, end);
        ReadVarint(db.D, ref at, end);
        if (length <= 0 || length > 16 * 1024 * 1024) return null;

        int u = db.Usable, x = u - 35;
        int local = (int)length;
        if (length > x)
        {
            int m = (u - 12) * 32 / 255 - 23;
            int k = m + (int)((length - m) % (u - 4));
            local = k <= x ? k : m;
        }
        if (at + local > end) return null;
        var result = new byte[length];
        Array.Copy(db.D, at, result, 0, local);
        int filled = local;
        int next = local < length && at + local + 4 <= end ? BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(at + local)) : 0;
        var seen = new HashSet<int>();
        while (filled < length)
        {
            if (next < 2 || next > db.Pages || !seen.Add(next)) return null;
            int start = db.Start(next);
            int take = (int)Math.Min(length - filled, u - 4);
            Array.Copy(db.D, start + 4, result, filled, take);
            filled += take;
            next = BinaryPrimitives.ReadInt32BigEndian(db.D.AsSpan(start));
        }
        return result;
    }
}
