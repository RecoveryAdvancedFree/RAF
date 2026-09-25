using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// כונני לינוקס שנוצרו ונכתבו בליבת לינוקס אמיתית (make.sh בתיקייה C:\linuxtest-raf, דרך WSL):
/// ext2, ext3, ext4 ו-XFS, עם שמות בעברית, קובץ מפוצל לאלפי מקטעים, תיקייה עם 300 קבצים,
/// וקבצים שנמחקו בהרכבה נפרדת. manifest.txt שומר טביעת אצבע לכל קובץ. בלי התיקייה הבדיקה מדלגת.
/// </summary>
public class LinuxRealTests
{
    private const string Folder = @"C:\linuxtest-raf";
    private readonly ITestOutputHelper _out;

    public LinuxRealTests(ITestOutputHelper output) => _out = output;

    // מתוך 5 קבצים שנמחקו. ext2: לינוקס מאפס את מספר האינוד ליד השם — התוכן נמצא רק בלי שם.
    // ext3/4: אחרי הרכבה מחדש היומן נכתב שוב מתחילתו, ודורס חלק מהעותקים הישנים של התיקיות —
    // אילו בדיוק משתנה מהרצה להרצה של make.sh. מה שאבד בשם — נמצא בסריקה העמוקה בלי שם.
    [Theory]
    [InlineData("ext2", 0, 5)]
    [InlineData("ext3", 3, 5)]
    [InlineData("ext4", 3, 5)]
    [InlineData("xfs", 5, 5)]
    public async Task Real_linux_drive_recovers_existing_and_deleted_files(string name, int minByName, int minTotal)
    {
        string img = Path.Combine(Folder, name + ".img");
        if (!File.Exists(img)) return;

        var expected = File.ReadAllLines(Path.Combine(Folder, "manifest.txt"))
            .Select(l => l.Split('|')).Where(p => p[0] == name)
            .Select(p => (Deleted: p[1] == "deleted", Path: p[2], Md5: p[3])).ToList();

        var disk = ImageDisk.Open(img);
        string target = Path.Combine(Path.GetTempPath(), "raf-lx-" + Guid.NewGuid().ToString("N"));
        try
        {
            var part = disk.Partitions.Single();
            var result = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);
            _out.WriteLine($"{result.FileSystem}: {result.Files.Count} files, {result.Files.Count(f => f.IsDeleted)} deleted");
            foreach (var w in result.Warnings) _out.WriteLine("warning: " + w);

            // תיקיית many כולה נמצאת, עם השמות הנכונים.
            Assert.Equal(299, result.Files.Count(f => !f.IsDeleted && f.Path == "many"));

            int n = 0;
            async Task<string?> Recover(RecoveredFile file)
            {
                string dir = Path.Combine(target, (n++).ToString());
                var report = await RecoveryWriter.RecoverAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                    disk.LogicalSectorSize, new[] { file }, new RecoveryOptions { TargetFolder = dir, PreservePaths = false }, null, default);
                // לצד הקובץ נכתבים גם דוח השחזור והסבר — מחפשים רק את הקובץ עצמו.
                string[] got = Directory.Exists(dir) ? Directory.GetFiles(dir, file.Name, SearchOption.AllDirectories) : Array.Empty<string>();
                return report.Succeeded == 1 && got.Length == 1 ? Convert.ToHexString(MD5.HashData(File.ReadAllBytes(got[0]))).ToLowerInvariant() : null;
            }

            var failures = new List<string>();
            var missing = new List<(string Path, string Md5)>();
            int byName = 0;
            foreach (var (deleted, path, md5) in expected)
            {
                string fileName = path[(path.LastIndexOf('/') + 1)..];
                string folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                var candidates = result.Files.Where(f => f.Name == fileName && f.IsDeleted == deleted).ToList();
                string where = string.Join("; ", candidates.Select(c => $"'{c.Path}' {c.Quality} {c.Size}"));

                bool match = false;
                foreach (var c in candidates)
                {
                    bool same = await Recover(c) == md5;
                    if (same && c.Path.Replace('\\', '/') != folder) failures.Add($"{path}: folder '{c.Path}'");
                    // קובץ שמוצג כניתן לשחזור — חייב לחזור זהה. "לא ניתן לשחזר" הוא תשובה כנה.
                    if (!same && c.Quality != RecoveryQuality.Unrecoverable) failures.Add($"{path}: content differs ({where})");
                    match |= same;
                }
                _out.WriteLine($"{(match ? "OK  " : "LOST")} {(deleted ? "deleted" : "exists ")} {path} <- {where}");
                if (match) { if (deleted) byName++; }
                else if (!deleted) failures.Add($"{path}: existing file not recovered ({where})");
                else missing.Add((path, md5));
            }

            // מה שלא נמצא בשם — הסריקה העמוקה אמורה למצוא בלי שם, לפי האינוד שנמחק.
            int nameless = 0;
            if (missing.Count > 0)
            {
                var deep = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                    disk.LogicalSectorSize, ScanMode.Deep, includeExisting: false, TrimState.NotSupported, null, default);
                var hashes = new HashSet<string>();
                foreach (var f in deep.Files.Where(f => f.Path == "?" && f.Quality != RecoveryQuality.Unrecoverable))
                    if (await Recover(f) is { } h) hashes.Add(h);
                foreach (var (path, md5) in missing)
                {
                    bool found = hashes.Contains(md5);
                    if (found) nameless++;
                    _out.WriteLine($"{(found ? "OK  " : "LOST")} deep scan, no name: {path}");
                }
            }
            _out.WriteLine($"deleted recovered: {byName} with name, {nameless} without");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
            Assert.True(byName >= minByName, $"only {byName} deleted files recovered with their name");
            Assert.True(byName + nameless >= minTotal, $"only {byName + nameless} deleted files recovered");
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
