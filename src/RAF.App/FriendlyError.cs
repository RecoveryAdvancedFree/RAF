namespace RAF.App;

/// <summary>פעולות ארוכות, שסגירת החלון באמצען מפסיקה אותן.</summary>
internal enum LongOperation { Scan, Recovery, Hunt, Imaging }

/// <summary>
/// הודעת שגיאה במבנה אחיד: מה קרה, ומה לעשות עכשיו. ההודעה הטכנית
/// המקורית נשמרת בנפרד ומוצגת מקופלת — היא חשובה לאבחון, אבל
/// "The device is not ready" לא עוזר למי שמנסה להציל תמונות.
/// </summary>
internal sealed record FriendlyError(string Message, string? Advice, string? Detail)
{
    // קודי שגיאה של Windows, כפי שהם מגיעים בתוך IOException.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorCrc = 23;
    private const int ErrorSectorNotFound = 27;
    private const int ErrorReadFault = 30;
    private const int ErrorGenFailure = 31;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private const int ErrorFilenameTooLong = 206;
    private const int ErrorIoDevice = 1117;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorMediaChanged = 1110;

    private const string FailingDrive =
        "אם זה חוזר, ייתכן שהכונן מתחיל להיכשל. מומלץ ליצור ממנו תמונת דיסק ולעבוד מהתמונה — " +
        "כך כל קריאה נוספת לא תסכן אותו.";

    internal static FriendlyError From(Exception ex)
    {
        // הודעה בעברית נוסחה בתוכנה עצמה, כבר עם ההסבר המלא — גם כשהיא IOException.
        // רק הודעות של Windows ו-.NET, שמגיעות באנגלית, מתורגמות כאן.
        if (IsHebrew(ex.Message))
            return new FriendlyError(ex.Message, null, null);

        int code = ex.HResult & 0xFFFF;
        string detail = ex.Message;

        return ex switch
        {
            UnauthorizedAccessException => new(
                "Windows לא אישר גישה לקובץ או לתיקייה.",
                "ודאו שהתוכנה פועלת עם הרשאות מנהל, ושהקובץ אינו פתוח בתוכנה אחרת. " +
                "אם זו תיקיית מערכת — בחרו תיקייה אחרת.",
                detail),

            PathTooLongException => new(
                "הנתיב ארוך מדי.",
                "בחרו תיקיית יעד קרובה יותר לשורש הכונן, למשל D:\\שחזור.",
                detail),

            FileNotFoundException or DirectoryNotFoundException => new(
                "הקובץ או התיקייה לא נמצאו.",
                "ייתכן שהם נמחקו, הועברו או שהכונן נותק. רעננו ונסו שוב.",
                detail),

            IOException => code switch
            {
                ErrorNotReady or ErrorDeviceNotConnected or ErrorNoSuchDevice or ErrorMediaChanged => new(
                    "הכונן לא מגיב — ייתכן שהוא נותק.",
                    "בדקו שהכונן מחובר היטב (ב-USB נסו יציאה אחרת, בלי מפצל), ורעננו את רשימת הכוננים.",
                    detail),

                ErrorCrc or ErrorSectorNotFound or ErrorReadFault or ErrorGenFailure or ErrorIoDevice => new(
                    "הכונן לא הצליח לקרוא חלק מהנתונים.",
                    FailingDrive,
                    detail),

                ErrorDiskFull or ErrorHandleDiskFull => new(
                    "אין מספיק מקום פנוי בכונן היעד.",
                    "פנו מקום או בחרו כונן אחר, ונסו שוב. מה שכבר נכתב נשאר במקומו.",
                    detail),

                ErrorSharingViolation or ErrorLockViolation => new(
                    "הקובץ פתוח בתוכנה אחרת.",
                    "סגרו את התוכנה שמשתמשת בו ונסו שוב.",
                    detail),

                ErrorAccessDenied => new(
                    "Windows לא אישר גישה.",
                    "ודאו שהתוכנה פועלת עם הרשאות מנהל, ושאף תוכנה אחרת אינה נועלת את הכונן.",
                    detail),

                ErrorFileNotFound or ErrorPathNotFound => new(
                    "הקובץ או התיקייה לא נמצאו.",
                    "ייתכן שהם נמחקו, הועברו או שהכונן נותק. רעננו ונסו שוב.",
                    detail),

                ErrorFilenameTooLong => new(
                    "הנתיב ארוך מדי.",
                    "בחרו תיקיית יעד קרובה יותר לשורש הכונן, למשל D:\\שחזור.",
                    detail),

                _ => new("אירעה שגיאה בקריאה או בכתיבה.", "נסו שוב. " + FailingDrive, detail),
            },

            _ => new(
                "אירעה שגיאה לא צפויה.",
                "נסו שוב. אם זה חוזר, סגרו את התוכנה ופתחו אותה מחדש.",
                detail),
        };
    }

    /// <summary>ההודעה והעצה בשורה אחת, למקומות שמציגים טקסט יחיד.</summary>
    internal string Text => Advice is null ? Message : $"{Message} {Advice}";

    private static bool IsHebrew(string text) => text.Any(c => c is >= 'א' and <= 'ת');
}
