using System.Globalization;

namespace RAF.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
            "אירעה שגיאה בלתי צפויה:\n\n" + (ex?.Message ?? "שגיאה לא ידועה") +
            "\n\nפירוט טכני:\n" + (ex?.StackTrace ?? ""),
            "שיחזור מתקדם חינם — שגיאה",
            MessageBoxButtons.OK, MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }
}
