using System.Runtime.CompilerServices;
using RAF.Core.Repair;

namespace RAF.Core.Tests;

internal static class TestSetup
{
    /// <summary>במק ובלינוקס: SQLite של המערכת גם לבדיקות שקוראות לה ישירות (RealSqlite).</summary>
    [ModuleInitializer]
    internal static void Init() => SqliteEngine.UseSystemSqlite(typeof(TestSetup).Assembly);
}
