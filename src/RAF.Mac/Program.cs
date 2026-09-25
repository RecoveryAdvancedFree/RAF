using System.Globalization;
using Photino.NET;

namespace RAF.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var hebrew = new CultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = hebrew;
        CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        Thread.CurrentThread.CurrentCulture = hebrew;
        Thread.CurrentThread.CurrentUICulture = hebrew;

        new MacHost().Run();
    }
}
