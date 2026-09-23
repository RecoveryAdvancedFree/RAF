using System.Globalization;
using System.Text;

namespace RAF.Core.Imaging;

/// <summary>טווח בתים בתמונה.</summary>
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// קובץ המפה שנשמר לצד כל תמונה (<c>שם.img.map</c>).
///
/// התמונה עצמה היא העתק גולמי, ובמקום סקטור שלא נקרא יש בה אפסים —
/// שאי אפשר להבחין בינם לבין אפסים אמיתיים. המפה היא התיעוד של ההבדל:
/// מה נקרא, מה לא נקרא ומה לא הועתק כלל, וממה נוצרה התמונה.
/// קובץ טקסט פשוט, כדי שגם אדם וגם כלי אחר יוכלו לקרוא אותו.
/// </summary>
public sealed class ImageMap
{
    private const string Header = "# RAF disk image map";

    public string Source { get; init; } = "";

    /// <summary>"disk" — תמונת דיסק שלם עם טבלת מחיצות; "partition" — מחיצה בודדת.</summary>
    public string Kind { get; init; } = "disk";

    public long Size { get; init; }
    public int SectorSize { get; init; } = 512;
    public DateTime Created { get; init; } = DateTime.Now;

    /// <summary>שני המעברים הסתיימו (גם אם נותרו בהם סקטורים פגומים).</summary>
    public bool Complete { get; init; }

    /// <summary>סקטורים שנוסו ולא נקראו — מולאו באפסים בתמונה.</summary>
    public List<ByteRange> Unreadable { get; init; } = new();

    /// <summary>אזורים שלא הועתקו כלל, כי ההעתקה נעצרה לפניהם.</summary>
    public List<ByteRange> NotCopied { get; init; } = new();

    public long UnreadableBytes => Unreadable.Sum(r => r.Length);
    public long NotCopiedBytes => NotCopied.Sum(r => r.Length);

    public static string PathFor(string imagePath) => imagePath + ".map";

    public void Save(string path)
    {
        var b = new StringBuilder();
        b.AppendLine(Header);
        b.AppendLine("# unreadable = sectors that failed to read (zero-filled in the image)");
        b.AppendLine("# notcopied  = ranges never read because imaging was stopped");
        b.AppendLine("version=1");
        b.AppendLine($"source={Source.Replace('\n', ' ')}");
        b.AppendLine($"kind={Kind}");
        b.AppendLine($"size={Size}");
        b.AppendLine($"sector={SectorSize}");
        b.AppendLine($"created={Created.ToString("s", CultureInfo.InvariantCulture)}");
        b.AppendLine($"complete={(Complete ? "true" : "false")}");

        foreach (var r in Unreadable) b.AppendLine($"unreadable={r.Offset}+{r.Length}");
        foreach (var r in NotCopied) b.AppendLine($"notcopied={r.Offset}+{r.Length}");

        // כתיבה לקובץ זמני והחלפה — מפה חצי כתובה גרועה ממפה ישנה.
        string temp = path + ".tmp";
        File.WriteAllText(temp, b.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>קריאת מפה. מחזיר null אם הקובץ חסר או אינו מפה של התוכנה.</summary>
    public static ImageMap? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != Header) return null;

            var values = new Dictionary<string, string>();
            var unreadable = new List<ByteRange>();
            var notCopied = new List<ByteRange>();

            foreach (var line in lines)
            {
                if (line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq], value = line[(eq + 1)..];

                if (key is "unreadable" or "notcopied")
                {
                    var range = ParseRange(value);
                    if (range is null) continue;
                    (key == "unreadable" ? unreadable : notCopied).Add(range.Value);
                }
                else
                {
                    values[key] = value;
                }
            }

            return new ImageMap
            {
                Source = values.GetValueOrDefault("source", ""),
                Kind = values.GetValueOrDefault("kind", "disk"),
                Size = long.TryParse(values.GetValueOrDefault("size"), out var size) ? size : 0,
                SectorSize = int.TryParse(values.GetValueOrDefault("sector"), out var sector) && sector > 0 ? sector : 512,
                Created = DateTime.TryParse(values.GetValueOrDefault("created"), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created) ? created : File.GetCreationTime(path),
                Complete = values.GetValueOrDefault("complete") == "true",
                Unreadable = unreadable,
                NotCopied = notCopied,
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ByteRange? ParseRange(string value)
    {
        int plus = value.IndexOf('+');
        if (plus <= 0) return null;

        return long.TryParse(value[..plus], out long offset) && long.TryParse(value[(plus + 1)..], out long length)
               && offset >= 0 && length > 0
            ? new ByteRange(offset, length)
            : null;
    }
}
