namespace RAF.Core.Text;

/// <summary>
/// שפת ההודעות שהמנוע כותב — עברית (ברירת המחדל) או אנגלית.
///
/// הטקסט העברי נשאר בקוד והוא גם המפתח: L.T("הכונן לא נמצא.") מחזיר באנגלית את
/// התרגום מהמילון (English.cs). {0}, {1}… מוחלפים בערכים שאחרי הטקסט, באותו מקום
/// בשתי השפות. טקסט שעוד לא תורגם מוצג בעברית, ולא נעלם.
///
/// הממשק קובע את השפה (system.language). הודעה שכבר נכתבה — למשל אזהרה של סריקה
/// שהסתיימה — נשארת בשפה שבה נכתבה.
/// </summary>
public static class L
{
    private static volatile bool _english;

    /// <summary>"he" או "en".</summary>
    public static string Language
    {
        get => _english ? "en" : "he";
        set => _english = value == "en";
    }

    public static bool English => _english;

    /// <summary>
    /// האם הטקסט נכתב בתוכנה עצמה (ולא הודעה של Windows או של .NET): בעברית, או
    /// מתחיל באחד התרגומים לאנגלית — כשבמקום {0} יכול לבוא כל דבר.
    /// </summary>
    public static bool IsOwnText(string text)
    {
        if (text.Any(c => c is >= 'א' and <= 'ת')) return true;
        _ownPatterns ??= EnglishTexts.Texts.Values
            .Where(v => v.Length >= 12)
            .Select(v => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Escape(v), @"\\\{\d+}", ".*?"),
                System.Text.RegularExpressions.RegexOptions.Singleline))
            .ToArray();
        return _ownPatterns.Any(r => r.IsMatch(text));
    }

    private static System.Text.RegularExpressions.Regex[]? _ownPatterns;

    public static string T(string hebrew, params object?[] args)
    {
        string text = _english && EnglishTexts.Texts.TryGetValue(hebrew, out var en) ? en : hebrew;
        if (args.Length == 0) return text;

        // החלפה ידנית ולא string.Format: בטקסט יכולים להופיע סוגריים מסולסלים רגילים.
        var result = new System.Text.StringBuilder(text.Length + 32);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close > i + 1 && int.TryParse(text.AsSpan(i + 1, close - i - 1), out int n) && n >= 0 && n < args.Length)
                {
                    result.Append(args[n]);
                    i = close;
                    continue;
                }
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }
}
