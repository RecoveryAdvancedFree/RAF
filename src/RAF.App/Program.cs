using System.Globalization;

namespace RAF.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // אחרי עדכון של הגרסה הניידת: הגרסה הקודמת הפעילה אותנו ועדיין נסגרת. מחכים לה —
        // מנוע התצוגה מסרב לעלות בהגדרות שונות משל מופע שעדיין מחזיק את תיקיית הנתונים שלו.
        if (args.Length == 2 && args[0] == "--after-update" && int.TryParse(args[1], out int previous))
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(previous); p.WaitForExit(30_000); }
            catch (ArgumentException) { /* כבר נסגרה */ }
        }

        // ממשק עברי מלא: תרבות עברית ו-RTL כברירת מחדל בכל התוכנה.
        var hebrew = new CultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = hebrew;
        CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        Thread.CurrentThread.CurrentCulture = hebrew;
        Thread.CurrentThread.CurrentUICulture = hebrew;

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    private static void ReportCrash(Exception? ex)
    {
        MessageBox.Show(
            L.T("אירעה שגיאה בלתי צפויה:\n\n") + (ex?.Message ?? L.T("שגיאה לא ידועה")) +
            L.T("\n\nפירוט טכני:\n") + (ex?.StackTrace ?? ""),
            L.T("שחזור מתקדם חינם — שגיאה"),
            MessageBoxButtons.OK, MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }
}
