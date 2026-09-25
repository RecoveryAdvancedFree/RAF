using System.Text;

namespace RAF.Core.Lvm;

/// <summary>
/// קריאת תיאור המאגר: "שם = ערך", קבוצות בסוגריים מסולסלים, רשימות בסוגריים
/// מרובעים, מחרוזות במירכאות, מספרים, והערות אחרי #. ערך הוא מחרוזת, מספר,
/// רשימה (<see cref="List{Object}"/>) או קבוצה (<see cref="LvmConfig"/>).
/// </summary>
internal sealed class LvmConfig
{
    internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    internal LvmConfig? Section(string name) => Values.TryGetValue(name, out var v) ? v as LvmConfig : null;
    internal string? Text(string name) => Values.TryGetValue(name, out var v) ? v as string : null;
    internal long? Number(string name) => Values.TryGetValue(name, out var v) && v is long n ? n : null;
    internal List<object>? List(string name) => Values.TryGetValue(name, out var v) ? v as List<object> : null;

    /// <summary>הקבוצות שבתוך הקבוצה, לפי הסדר.</summary>
    internal IEnumerable<(string Name, LvmConfig Section)> Sections
        => Values.Where(v => v.Value is LvmConfig).Select(v => (v.Key, (LvmConfig)v.Value));

    /// <summary>פענוח. טקסט פגום — מה שנקרא עד הפגם.</summary>
    internal static LvmConfig Parse(string text)
    {
        var root = new LvmConfig();
        int at = 0;
        try { ParseBody(text, ref at, root, topLevel: true); }
        catch (FormatException) { }
        return root;
    }

    private static void ParseBody(string s, ref int at, LvmConfig into, bool topLevel)
    {
        while (true)
        {
            Skip(s, ref at);
            if (at >= s.Length) { if (topLevel) return; throw new FormatException(); }
            if (s[at] == '}') { if (topLevel) throw new FormatException(); at++; return; }

            string name = Identifier(s, ref at);
            Skip(s, ref at);
            if (at >= s.Length) throw new FormatException();
            if (s[at] == '{')
            {
                at++;
                var section = new LvmConfig();
                ParseBody(s, ref at, section, topLevel: false);
                into.Values[name] = section;
            }
            else if (s[at] == '=')
            {
                at++;
                into.Values[name] = Value(s, ref at);
            }
            else throw new FormatException();
        }
    }

    private static object Value(string s, ref int at)
    {
        Skip(s, ref at);
        if (at >= s.Length) throw new FormatException();
        if (s[at] == '"') return Quoted(s, ref at);
        if (s[at] == '[')
        {
            at++;
            var list = new List<object>();
            while (true)
            {
                Skip(s, ref at);
                if (at >= s.Length) throw new FormatException();
                if (s[at] == ']') { at++; return list; }
                if (s[at] == ',') { at++; continue; }
                list.Add(Value(s, ref at));
            }
        }
        int start = at;
        while (at < s.Length && (char.IsAsciiDigit(s[at]) || s[at] is '-' or '.')) at++;
        if (at == start) throw new FormatException();
        string token = s[start..at];
        return long.TryParse(token, out long n) ? n : token;
    }

    private static string Quoted(string s, ref int at)
    {
        var b = new StringBuilder();
        at++;
        while (at < s.Length && s[at] != '"')
        {
            if (s[at] == '\\' && at + 1 < s.Length) at++;
            b.Append(s[at++]);
        }
        if (at >= s.Length) throw new FormatException();
        at++;
        return b.ToString();
    }

    private static string Identifier(string s, ref int at)
    {
        int start = at;
        while (at < s.Length && (char.IsLetterOrDigit(s[at]) || s[at] is '_' or '-' or '.' or '+')) at++;
        if (at == start) throw new FormatException();
        return s[start..at];
    }

    private static void Skip(string s, ref int at)
    {
        while (at < s.Length)
        {
            if (char.IsWhiteSpace(s[at])) at++;
            else if (s[at] == '#') { while (at < s.Length && s[at] != '\n') at++; }
            else break;
        }
    }
}
