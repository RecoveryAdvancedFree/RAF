using System.Collections.Concurrent;
using RAF.Core.Disks;

namespace RAF.App;

/// <summary>
/// בריאות הכוננים (SMART) — נקראת בנפרד מרשימת הכוננים, כי כונן גוסס עלול לא לענות:
/// הרשימה מוצגת מיד, והבריאות מצטרפת אליה כשהיא מגיעה.
/// </summary>
internal sealed partial class Bridge
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(4);

    /// <summary>כוננים שלא ענו בזמן — לא שואלים אותם שוב, כדי לא לצבור שאילתות תקועות.</summary>
    private readonly ConcurrentDictionary<string, bool> _healthHung = new();

    private async Task<object> DisksHealthAsync()
    {
        var disks = _disks.Where(d => d.ImagePath is null && !d.Unresponsive && d.RawAccessible).ToList();

        var results = await Task.WhenAll(disks.Select(async d =>
        {
            string key = $"{d.DiskNumber}|{d.SerialNumber}|{d.Model}";
            if (_healthHung.ContainsKey(key)) return (d.DiskNumber, Health: DriveHealth.Unknown);
            try
            {
                return (d.DiskNumber, Health: await Task.Run(() => DriveHealth.Read(d.DiskNumber)).WaitAsync(HealthTimeout));
            }
            catch (TimeoutException)
            {
                _healthHung[key] = true;
                return (d.DiskNumber, Health: DriveHealth.Unknown);
            }
        }));

        return results
            .Where(r => r.Health.Level != HealthLevel.Unknown)
            .Select(r => new
            {
                disk = r.DiskNumber,
                level = r.Health.Level.ToString(),
                source = r.Health.Source,
                temperature = r.Health.TemperatureC,
                powerOnHours = r.Health.PowerOnHours,
                percentUsed = r.Health.PercentUsed,
                spareLeft = r.Health.SpareLeft,
                reallocated = r.Health.Reallocated,
                pending = r.Health.Pending,
                uncorrectable = r.Health.Uncorrectable,
                mediaErrors = r.Health.MediaErrors,
                problems = r.Health.Problems,
            })
            .ToList();
    }
}
