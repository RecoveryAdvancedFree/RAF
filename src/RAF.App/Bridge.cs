using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RAF.Core.Disks;
using RAF.Core.FileSystems;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Imaging;
using RAF.Core.Native;
using RAF.Core.Model;
using RAF.Core.Recovery;
using RAF.Core.Repair;
using RAF.Core.Signatures;

namespace RAF.App;

/// <summary>
/// גשר בין הממשק (JavaScript) לבין מנוע הליבה.
/// הממשק שולח בקשה עם מזהה ושם שיטה ומקבל תשובה עם אותו מזהה.
/// בנוסף, הגשר דוחף אירועי התקדמות ביוזמתו במהלך סריקה ושחזור.
/// </summary>
internal sealed partial class Bridge
{
    private readonly MainForm _form;
    private List<PhysicalDiskInfo> _disks = new();

    /// <summary>תמונות דיסק שנפתחו. נשמרות בנפרד, כי רענון הרשימה מונה מחדש רק כוננים.</summary>
    private readonly List<PhysicalDiskInfo> _images = new();

    /// <summary>
    /// מחיצות שנקראות דרך עותק הגיבוי של מגזר האתחול (בזיכרון בלבד),
    /// ומערכת הקבצים שזוהתה בהן. נשמר כדי לשרוד רענון של רשימת הדיסקים.
    /// </summary>
    private readonly Dictionary<(int Disk, long Offset), FileSystemKind> _readThrough = new();

    private ScanSession? _session;
    private CancellationTokenSource? _scanCancel;
    private CancellationTokenSource? _recoverCancel;
    private CancellationTokenSource? _imageCancel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // עברית חייבת לעבור כטקסט קריא ולא כרצף \uXXXX.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal Bridge(MainForm form)
    {
        _form = form;
        LoadCustomTypes();
    }

    internal async Task<string> HandleAsync(string rawMessage)
    {
        string id = "", method = "";
        try
        {
            var request = JsonNode.Parse(rawMessage)?.AsObject()
                          ?? throw new InvalidOperationException(L.T("בקשה ריקה"));

            id = request["id"]?.GetValue<string>() ?? "";
            method = request["method"]?.GetValue<string>() ?? "";
            var p = request["params"]?.AsObject();

            object? result = await DispatchAsync(method, p);
            return Ok(id, result);
        }
        catch (OperationCanceledException)
        {
            return Fail(id, new FriendlyError(L.T("הפעולה בוטלה."), null, null), method);
        }
        catch (Exception ex)
        {
            return Fail(id, FriendlyError.From(ex), method);
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonObject? p) => method switch
    {
        "system.info" => SystemInfo(),
        // שפת ההודעות שהמנוע כותב (ראו RAF.Core.Text.L). הודעה שכבר נכתבה נשארת בשפתה.
        "system.language" => L.Language = p?["lang"]?.GetValue<string>() == "en" ? "en" : "he",
        "diag.report" => WriteDiagnostics(p),
        "disks.list" => await Task.Run(ListDisks),
        "disks.health" => await DisksHealthAsync(),
        "scan.profile" => ScanProfile(p),

        "repair.diagnose" => await Task.Run(() => Diagnose(p)),
        "repair.apply" => await Task.Run(() => ApplyRepair(p)),
        "repair.readThrough" => await Task.Run(() => ReadThrough(p)),
        "repair.pickFolder" => PickFolder(L.T("בחרו תיקייה לגיבוי — חייבת להיות על כונן אחר")),
        "undo.pickFile" => PickUndoFile(),

        "types.list" => ListCustomTypes(),
        "types.learn" => await Task.Run(() => LearnCustomType(p)),
        "types.save" => SaveCustomType(p),
        "types.remove" => RemoveCustomType(p),
        "undo.check" => await Task.Run(() => CheckUndo(p)),
        "undo.apply" => await Task.Run(() => ApplyUndo(p)),

        "doctor.pickFiles" => PickFiles(),
        "doctor.pickFolder" => PickFolder(L.T("בחרו תיקייה לשמירת הקבצים המתוקנים")),
        "doctor.diagnose" => await Task.Run(() => DoctorDiagnose(p)),
        "doctor.diagnoseFolder" => await Task.Run(() => DoctorDiagnoseFolder(p)),
        "doctor.repair" => await Task.Run(() => DoctorRepair(p)),
        "doctor.pickReference" => PickReference(p),
        "doctor.rebuildVideo" => await Task.Run(() => DoctorRebuildVideo(p)),
        "doctor.rebuildPhoto" => await Task.Run(() => DoctorRebuildPhoto(p)),

        "disk.hunt" => await Tracked(LongOperation.Hunt, () => HuntAsync(p)),
        "disk.huntCancel" => Cancel(_huntCancel),
        "partition.restorePlan" => await Task.Run(() => RestorePlanFor(p)),
        "partition.restore" => await Task.Run(() => RestorePartition(p)),

        "image.pickSave" => PickImageTarget(p),
        "image.validate" => ValidateImageTarget(p),
        "image.create" => await Tracked(LongOperation.Imaging, () => CreateImageAsync(p)),
        "image.cancel" => Cancel(_imageCancel),
        "image.open" => OpenImage(p),
        "image.close" => CloseImage(p),
        "bitlocker.open" => OpenUnlockedVolume(p),
        "bitlocker.inspect" => await Task.Run(() => InspectBitLocker(p)),
        "bitlocker.unlock" => await Task.Run(() => UnlockBitLocker(p)),
        "raid.find" => await Task.Run(FindRaids),
        "raid.assemble" => await Task.Run(() => AssembleRaid(p)),
        "raid.detect" => await Task.Run(() => DetectRaid(p)),
        "raid.assembleDetected" => await Task.Run(() => AssembleDetected(p)),
        "lvm.find" => await Task.Run(FindLvm),
        "lvm.open" => await Task.Run(() => OpenLvm(p)),

        "scan.start" => await Tracked(LongOperation.Scan, () => StartScanAsync(p)),
        "scan.cancel" => Cancel(_scanCancel),
        "scan.pause" => PauseScan(),
        "scan.resume" => await Tracked(LongOperation.Scan, () => ResumeScanAsync(p)),
        "scan.children" => Children(p),
        "scan.save" => await SaveScanAs(),
        "scan.recent" => RecentScans(),
        "scan.forget" => ForgetScans(p),
        "scan.load" => await LoadScan(p),
        "scan.list" => ListView(p),
        "scan.select" => Select(p),
        "scan.folderStates" => FolderStates(p),
        "scan.duplicates" => await HideDuplicatesAsync(p),
        "scan.preview" => await Task.Run(() => Preview(p)),
        "scan.thumb" => await Task.Run(() => Thumbnail(p)),
        "scan.summary" => Summary(),

        "recover.pickFolder" => PickFolder(),
        "recover.validate" => ValidateTarget(p),
        "recover.start" => await Tracked(LongOperation.Recovery, () => StartRecoveryAsync(p)),
        "recover.cancel" => Cancel(_recoverCancel),
        "recover.openFolder" => OpenFolder(p),

        "window.theme" => _form.InvokeOnUi(() => _form.ApplyTheme(p?["dark"]?.GetValue<bool>() ?? true)),
        "window.minimize" => _form.InvokeOnUi(() => _form.WindowState = FormWindowState.Minimized),
        "window.toTray" => _form.InvokeOnUi(() => _form.Tray.Send()),
        "window.toggleMaximize" => _form.InvokeOnUi(_form.ToggleMaximize),
        "window.close" => _form.InvokeOnUi(_form.Close),
        "window.beginDrag" => _form.InvokeOnUi(() =>
            NativeChrome.BeginWindowAction(_form.Handle, p?["hit"]?.GetValue<int>() ?? NativeChrome.HTCAPTION)),

        _ => throw new InvalidOperationException(L.T("שיטה לא מוכרת: {0}", method)),
    };

    /// <summary>
    /// פעולה ארוכה עם חיווי בשורת המשימות: פס בזמן הריצה, הבהוב בסיום,
    /// ופס אדום כשהיא נכשלת. ביטול אינו מסמן דבר.
    /// </summary>
    private async Task<T> Tracked<T>(LongOperation kind, Func<Task<T>> operation)
    {
        RunningOperation = kind;
        _form.Taskbar.Start();
        try
        {
            T result = await operation();
            _form.Taskbar.Finish(failed: false);
            return result;
        }
        catch (OperationCanceledException)
        {
            _form.Taskbar.Clear();
            throw;
        }
        catch
        {
            _form.Taskbar.Finish(failed: true);
            throw;
        }
        finally
        {
            RunningOperation = null;
        }
    }

    /// <summary>הפעולה הארוכה שרצה עכשיו, אם יש — כדי להזהיר לפני סגירת החלון.</summary>
    internal LongOperation? RunningOperation { get; private set; }

    // ------------------------------------------------------------ מערכת ודיסקים

    /// <summary>קבלת מדדי העמוד מהממשק וכתיבתם לקובץ אבחון.</summary>
    private object? WriteDiagnostics(JsonObject? p)
    {
        var lines = new List<string>();
        if (p is not null)
            foreach (var pair in p)
                lines.Add($"  {pair.Key,-22}: {pair.Value}");

        _form.WriteDiagnostics(string.Join(Environment.NewLine, lines));
        return null;
    }

    private object SystemInfo()
    {
        // מדדי החלון נקראים על תהליכון הממשק, ומשמשים לאבחון בעיות פריסה.
        (int Width, int Height, int ClientWidth, int ClientHeight, int Dpi) m = default;
        _form.InvokeOnUiSync(() => m = _form.Metrics());

        return new
        {
            appName = L.T("שחזור מתקדם חינם"),
            version = typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            elevated = DiskEnumerator.IsElevated,
            machine = Environment.MachineName,
            windowWidth = m.Width,
            windowHeight = m.Height,
            clientWidth = m.ClientWidth,
            clientHeight = m.ClientHeight,
            dpi = m.Dpi,
            dpiPercent = (int)Math.Round(m.Dpi / 96.0 * 100),
        };
    }

    /// <summary>מונה רענונים: תשובה מאוחרת של כונן מרענון קודם אינה דורסת רשימה חדשה.</summary>
    private int _listGeneration;

    private object ListDisks()
    {
        int generation = Interlocked.Increment(ref _listGeneration);

        // כונן תקוע אינו מעכב את הרשימה: הוא מוצג "לא מגיב", ואם יענה מאוחר
        // יותר — המידע המלא נדחף לממשק כעדכון.
        var disks = DiskEnumerator.EnumerateDisks(late: full => OnLateDisk(full, generation));
        disks.AddRange(_images);
        foreach (var disk in disks) { AttachFound(disk); ApplyReadThrough(disk); }
        _disks = disks;

        return new
        {
            elevated = DiskEnumerator.IsElevated,
            disks = _disks.Select(DiskDto).ToList(),
            failed = FailedDevices.Find().Select(f => new
            {
                name = f.Name,
                windowsName = f.WindowsName,
                code = f.ProblemCode,
                problem = f.Problem,
                advice = f.Advice,
            }).ToList(),
        };
    }

    /// <summary>כונן שענה אחרי שהרשימה כבר הוצגה.</summary>
    private void OnLateDisk(PhysicalDiskInfo full, int generation)
    {
        if (generation != _listGeneration) return;

        AttachFound(full);
        ApplyReadThrough(full);

        // החלפה אטומית של הרשימה, כדי שקוראים אחרים לא יראו אותה באמצע שינוי.
        _disks = _disks.Select(d => d.DiskNumber == full.DiskNumber ? full : d).ToList();
        PushEvent("disks.updated", new { disk = DiskDto(full) });
    }

    private object DiskDto(PhysicalDiskInfo d) => new
    {
        number = d.DiskNumber,
        name = d.DisplayName,
        bus = d.BusType,
        media = d.Media.ToString(),
        mediaLabel = Display.Media(d.Media),
        mediaShort = Display.MediaShort(d.Media),
        trim = d.Trim.ToString(),
        trimLabel = Display.Trim(d.Trim),
        size = d.SizeBytes,
        scheme = Display.Scheme(d.Scheme),
        rawAccessible = d.RawAccessible,
        unresponsive = d.Unresponsive,
        problem = d.Problem,
        isImage = d.ImagePath is not null,
        imagePath = d.ImagePath,
        isVolume = d.ImagePath is { } path && DevicePaths.IsDecryptedVolume(path),
        decrypted = d.ImagePath is { } own && DevicePaths.IsDecryptedPath(own),
        raid = d.ImagePath is { } assembled && DevicePaths.IsRaidPath(assembled) && !assembled.Contains(":lvm-"),
        lvm = d.ImagePath is { } volume && DevicePaths.IsRaidPath(volume) && volume.Contains(":lvm-"),
        imageNote = d.ImageNote,
        imageDamaged = d.ImageDamaged,
        partitions = d.Partitions.Select(PartitionDto).ToList(),
    };

    private object PartitionDto(PartitionInfo p) => new
    {
        index = p.Index,
        disk = p.DiskNumber,
        offset = p.OffsetBytes,
        size = p.SizeBytes,
        free = p.FreeBytes,
        used = p.FreeBytes.HasValue ? p.SizeBytes - p.FreeBytes.Value : (long?)null,
        fs = p.FileSystem.ToString(),
        fsLabel = Display.FileSystem(p.FileSystem),
        fsSupported = Display.IsSupported(p.FileSystem),
        scannable = VolumeScanner.IsSupported(p.FileSystem),
        typeName = p.TypeName,
        label = p.Label,
        letter = p.DriveLetter,
        bootable = p.IsBootable,
        hidden = p.IsHidden,
        unmounted = p.IsUnmounted,
        // מחיצת BitLocker שנפתחה ב-Windows: יש לה אות, ו-Windows מדווח על המקום הפנוי בה.
        unlocked = p.FileSystem == FileSystemKind.BitLocker && p.DriveLetter.Length > 0 && p.FreeBytes.HasValue,
        readThrough = _readThrough.ContainsKey((p.DiskNumber, p.OffsetBytes)),
        found = p.Index >= FoundIndexBase,
        foundInfo = FoundInfo(p),
    };

    private object? FoundInfo(PartitionInfo p)
    {
        if (p.Index < FoundIndexBase || !_found.TryGetValue(p.DiskNumber, out var list)) return null;
        int i = p.Index - FoundIndexBase;
        if (i >= list.Count) return null;

        var f = list[i];
        return new
        {
            damaged = f.BootSectorDamaged,
            overlaps = f.OverlapsExisting,
            inside = f.InsideAnother,
            fsLabel = Display.FileSystem(f.FileSystem),
        };
    }

    private object ScanProfile(JsonObject? p)
    {
        var disk = FindDisk(p?["disk"]?.GetValue<int>() ?? -1);
        var mode = (ScanMode)(p?["mode"]?.GetValue<int>() ?? (int)ScanMode.Quick);
        var profile = RecoveryProfile.For(disk, mode);

        return new
        {
            modeLabel = Display.Mode(mode),
            blockSizeKb = profile.ReadBlockSize / 1024,
            parallelism = profile.Parallelism,
            sequential = profile.SequentialOrder,
            outlook = profile.SuccessOutlook,
            rationale = profile.Rationale,
            warning = profile.Warning,
            mediaLabel = Display.Media(disk.Media),
        };
    }

    // ------------------------------------------------------------ סריקה

    private async Task<object> StartScanAsync(JsonObject? p)
    {
        int diskNumber = p?["disk"]?.GetValue<int>() ?? -1;
        int partIndex = p?["part"]?.GetValue<int>() ?? -1;
        var mode = (ScanMode)(p?["mode"]?.GetValue<int>() ?? (int)ScanMode.Quick);
        bool includeExisting = p?["includeExisting"]?.GetValue<bool>() ?? false;
        bool freeSpaceOnly = p?["freeSpaceOnly"]?.GetValue<bool>() ?? false;
        var types = p?["types"]?.AsArray()?.Select(n => n!.GetValue<string>()).ToList();

        var disk = FindDisk(diskNumber);
        var part = disk.Partitions.FirstOrDefault(x => x.Index == partIndex)
                   ?? throw new InvalidOperationException(L.T("המחיצה לא נמצאה. רענן את רשימת הדיסקים."));

        if (!VolumeScanner.CanScan(part.FileSystem, mode))
            throw new InvalidOperationException(
                L.T("מערכת הקבצים {0} אינה נתמכת לסריקת מטא-דאטה. " +
                "נסו סריקה מתקדמת, שאינה תלויה במערכת הקבצים.", L.T(Display.FileSystem(part.FileSystem))));

        string title = !string.IsNullOrEmpty(part.Label) ? part.Label
            : !string.IsNullOrEmpty(part.DriveLetter) ? L.T("כונן ") + part.DriveLetter
            : L.T("מחיצה ") + part.Index;

        return await RunScanAsync(disk, part.OffsetBytes, part.SizeBytes, part.FileSystem, title,
            mode, includeExisting, freeSpaceOnly, types, resumeFrom: null, NewAutosavePath(title));
    }

    /// <summary>השהיה: הסריקה נעצרת ונשמרת עם נקודת המשך. אפשר להמשיך עכשיו, או אחרי סגירת התוכנה.</summary>
    private volatile bool _pauseRequested;

    private object? PauseScan()
    {
        _pauseRequested = true;
        _scanCancel?.Cancel();
        return null;
    }

    /// <summary>
    /// המשך סריקה מתקדמת שנעצרה — מהסריקה הנוכחית, או מקובץ סריקה שמור (גם אחרי
    /// סגירת התוכנה). הכונן מזוהה כמו בפתיחת סריקה שמורה, וההמשך רץ באותן אפשרויות.
    /// </summary>
    private async Task<object> ResumeScanAsync(JsonObject? p)
    {
        string? path = p?["path"]?.GetValue<string>() ?? _session?.SavedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(L.T("קובץ הסריקה שממנו ממשיכים לא נמצא."));

        var archive = await Task.Run(() => ScanArchive.Load(path));
        var resume = archive.Result.Resume
                     ?? throw new InvalidOperationException(L.T("הסריקה הזו הסתיימה, או שאי אפשר להמשיך אותה."));

        // הרשימה מתרעננת קודם: כונן שנותק עדיין מופיע ברשימה הישנה, וכונן שחובר מחדש
        // עוד לא — ובשני המקרים הייתה מתקבלת שגיאת פתיחה במקום "הכונן אינו מחובר".
        if (archive.Header.Disk.ImagePath is null) ListDisks();
        var disk = MatchDisk(archive.Header.Disk);
        if (disk is null && archive.Header.Disk.ImagePath is { } image && File.Exists(image))
        {
            disk = ImageDisk.Open(image);
            _images.RemoveAll(d => d.DiskNumber == disk.DiskNumber);
            _images.Add(disk);
            _disks.Add(disk);
        }
        if (disk is null)
            throw new InvalidOperationException(L.T("הכונן שנסרק אינו מחובר. חברו אותו, רעננו את רשימת הכוננים ונסו שוב."));

        // מערכת הקבצים של המחיצה — בשביל מפת המקום הפנוי. מחיצה שאינה ברשימה נסרקת כולה.
        var part = disk.Partitions.FirstOrDefault(x => x.OffsetBytes == archive.PartitionOffset);

        return await RunScanAsync(disk, archive.PartitionOffset, archive.PartitionSize,
            part?.FileSystem ?? FileSystemKind.Raw, archive.Header.PartitionTitle,
            ScanMode.Advanced, includeExisting: false, resume.FreeSpaceOnly, resume.Types,
            resumeFrom: archive.Result, autosavePath: path);
    }

    /// <summary>הרצת סריקה — חדשה, או המשך של סריקה מתקדמת שנעצרה.</summary>
    private async Task<object> RunScanAsync(
        PhysicalDiskInfo disk, long offset, long size, FileSystemKind fileSystem, string title,
        ScanMode mode, bool includeExisting, bool freeSpaceOnly, List<string>? types,
        ScanResult? resumeFrom, string autosavePath)
    {
        // סוגי הקבצים שנבחרו (קטגוריות של FileCategories). חסר, או כולן — הכול.
        var chosen = types is { Count: > 0 } && types.Count < FileCategories.Ordered.Length + 1
            ? types.ToHashSet() : null;
        Func<RAF.Core.Signatures.FileSignature, bool>? accept = chosen is null
            ? null
            : signature => signature.Extensions.Any(e => chosen.Contains(FileCategories.Of(e)));

        _scanCancel?.Cancel();
        _scanCancel = new CancellationTokenSource();
        _pauseRequested = false;
        var token = _scanCancel.Token;

        // דיווח ההתקדמות נדחס כך שלא יציף את הממשק בהודעות.
        var lastPush = DateTime.MinValue;
        var progress = new Progress<ScanProgress>(sp =>
        {
            if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 120) return;
            lastPush = DateTime.UtcNow;

            _form.Taskbar.Report(sp.Percent);
            PushEvent("scan.progress", new
            {
                stage = sp.Stage,
                percent = sp.Percent,
                files = sp.FilesFound,
                bytes = sp.BytesProcessed,
                bytesTotal = sp.BytesTotal,
                speed = sp.BytesPerSecond,
                elapsed = sp.Elapsed.TotalSeconds,
                map = MapDto(sp.Map),
            });
        });

        // תוצאות הסריקה המתקדמת מתוארות ביחידות סקטור ולא באשכולות,
        // ולכן החילוץ שלהן חייב לעבור דרך מחיצה גולמית.
        var extractAs = mode == ScanMode.Advanced ? FileSystemKind.Raw : fileSystem;
        var identity = DiskIdentity.Of(disk);
        bool readThrough = _readThrough.ContainsKey((disk.DiskNumber, offset));

        ScanSession Session(ScanResult r) => new(
            r, disk.DiskNumber, offset, size,
            disk.LogicalSectorSize, title, extractAs, identity, readThrough) { SavedPath = autosavePath };

        // נקודות ביניים נשמרות לאותו קובץ שהתוצאה הסופית תדרוס בסוף.
        var result = await VolumeScanner.ScanAsync(
            fileSystem,
            disk.DiskNumber, offset, size, disk.LogicalSectorSize,
            mode, includeExisting, disk.Trim, progress, token,
            checkpoint: snapshot => Autosave(Session(snapshot), partial: true),
            freeSpaceOnly: freeSpaceOnly, accept: accept,
            resumeFrom: resumeFrom, types: types);

        _session = Session(result);
        var session = _session;

        // סריקה שנעצרה עם נקודת המשך נשמרת מיד — כדי שאפשר יהיה להמשיך גם אחרי סגירת התוכנה.
        bool resumable = result.Resume is not null;
        if (resumable) await Task.Run(() => Autosave(session, partial: true));
        else _ = Task.Run(() => Autosave(session, partial: false));

        // השהיה, או כונן שנותק באמצע — בשניהם הסריקה נשמרה עם נקודת המשך.
        bool paused = (_pauseRequested || result.Disconnected) && resumable;
        _pauseRequested = false;
        return paused
            ? new { paused = true, disconnected = result.Disconnected, percent = result.Resume!.Percent, files = result.Files.Count }
            : Summary();
    }

    /// <summary>מפת הסקטורים לממשק: תו לכל ריבוע, הריבוע שהמעבר נמצא בו, וגודל האזור.</summary>
    private static object? MapDto(SectorMap? map) => map is null ? null : new
    {
        cells = map.Snapshot(),
        cursor = map.Cursor < 0 ? -1 : map.CellOf(map.Cursor),
        length = map.Length,
    };

    private object Summary()
    {
        var session = RequireSession();
        var result = session.Result;

        return new
        {
            partition = session.PartitionTitle,
            fileSystem = result.FileSystem,
            mode = Display.Mode(result.Mode),
            cancelled = result.Cancelled,
            duration = result.Duration.TotalSeconds,
            recordsExamined = result.RecordsExamined,
            bytesRead = result.BytesRead,
            total = result.Files.Count(f => !f.IsDirectory),
            deleted = result.DeletedCount,
            recoverable = result.Files.Count(f => !f.IsDirectory && f.IsWorthRecovering),
            emptied = result.Files.Count(f => f.Content == ContentCheck.Empty),
            evidence = result.Files.Count(f =>
                f.Source is DiscoverySource.UsnJournal or DiscoverySource.LogFile),
            warnings = SessionWarnings(session).Concat(result.Warnings),
            offline = session.Offline,
            partial = session.Partial,
            resumePercent = session.Offline ? (double?)null : result.Resume?.Percent,
            selection = SelectionDto(session.Summary(null)),
        };
    }

    /// <summary>אזהרות על מצב הסריקה עצמה — מוצגות לפני אזהרות הסורק.</summary>
    private static IEnumerable<string> SessionWarnings(ScanSession session)
    {
        if (session.Offline)
            yield return L.T("הכונן שנסרק אינו מחובר, ולכן אפשר רק לעיין ברשימה — בלי תצוגה מקדימה ובלי שחזור. " +
                         "חברו את הכונן, חזרו לרשימת הכוננים ופתחו את הסריקה שוב.");
        if (session.Partial)
            yield return L.T("זו נקודת ביניים שנשמרה באמצע סריקה, ולא כל המחיצה נסרקה. " +
                         "הקבצים שברשימה ניתנים לשחזור; כדי למצוא את השאר — הריצו את הסריקה שוב.");
    }

    /// <summary>רמה אחת בעץ התוצאות: תיקיות המשנה שבנתיב. הקבצים נמשכים דרך scan.list.</summary>
    private object Children(JsonObject? p)
    {
        var session = RequireSession();
        string path = p?["path"]?.GetValue<string>() ?? "";
        bool evidence = p?["evidence"]?.GetValue<bool>() ?? false;

        return new
        {
            path,
            folders = session.SubFolders(path, evidence).Select(name => new
            {
                name,
                path = string.IsNullOrEmpty(path) ? name : path + Separator + name,
            }),
        };
    }

    /// <summary>מפריד הנתיבים של NTFS.</summary>
    private const string Separator = "\\";

    /// <summary>התצוגה שהממשק מציג, כפי שהיא נשלחת בשדה view של הבקשה.</summary>
    private static ViewQuery? ReadView(JsonNode? v) => v is not JsonObject o ? null : new(
        o["path"]?.GetValue<string>() ?? "",
        o["query"]?.GetValue<string>(),
        o["evidence"]?.GetValue<bool>() ?? false,
        o["category"]?.GetValue<string>() ?? FileCategories.All,
        o["recoverableOnly"]?.GetValue<bool>() ?? false,
        Enum.TryParse<ViewSort>(o["sort"]?.GetValue<string>(), true, out var sort) ? sort : ViewSort.Name,
        o["desc"]?.GetValue<bool>() ?? false,
        Day(o["from"]), Day(o["to"]),
        o["minSize"]?.GetValue<long>() ?? 0);

    /// <summary>תאריך מהממשק ("yyyy-MM-dd"), או null.</summary>
    private static DateTime? Day(JsonNode? n)
        => DateTime.TryParseExact(n?.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// טווח שורות מתוך הרשימה המוצגת. הממשק מבקש רק את מה שנראה על המסך,
    /// כך שגם תיקייה של מאות אלפי קבצים נטענת מיד. הבקשה הראשונה (offset 0)
    /// מחזירה גם את הספירות לשבבי הסינון ואת מצב הבחירה.
    /// </summary>
    private object ListView(JsonObject? p)
    {
        var session = RequireSession();
        var query = ReadView(p?["view"]) ?? throw new ArgumentException(L.T("חסרה תצוגה בבקשה."));
        var view = session.View(query);
        int offset = Math.Clamp(p?["offset"]?.GetValue<int>() ?? 0, 0, view.Count);
        int count = Math.Clamp(p?["count"]?.GetValue<int>() ?? 200, 0, 1000);

        return new
        {
            total = view.Count,
            offset,
            files = view.Skip(offset).Take(count).Select(f => FileDto(session, f)),
            counts = offset == 0 ? session.CategoryCounts(query) : null,
            selection = offset == 0 ? SelectionDto(session.Summary(query)) : null,
            groups = offset == 0 ? session.Groups(query).Select(g => new { label = g.Label, start = g.Start, count = g.Count }) : null,
            // קבצים בלי תאריך אינם נכנסים לסינון לפי תאריך — בסריקה מתקדמת אלה כמעט כולם.
            undated = offset == 0 ? session.UndatedCount(query) : 0,
        };
    }

    /// <summary>
    /// הסתרת כפילויות. בפעם הראשונה המנוע משווה קבצים באותו גודל בדיוק, ולכן
    /// זו פעולה ארוכה (longCall); אחר כך התוצאה שמורה.
    /// </summary>
    private async Task<object> HideDuplicatesAsync(JsonObject? p)
    {
        var session = RequireSession();
        bool on = p?["on"]?.GetValue<bool>() ?? true;

        Func<RecoveredFile, byte[]?> readHead = session.Offline
            ? _ => null
            : f =>
            {
                try
                {
                    return FileContentReader.ReadHead(session.FileSystem, session.DiskNumber, session.PartitionOffset,
                        session.PartitionSize, session.SectorSize, f, Duplicates.HeadBytes);
                }
                catch (Exception) { return null; }
            };

        var (hidden, bytes, deselected) = await Task.Run(() => session.SetHideDuplicates(on, readHead, CancellationToken.None));
        return new
        {
            hidden, bytes, deselected,
            selection = SelectionDto(session.Summary(null)),
        };
    }

    /// <summary>
    /// סימון או ביטול: קבצים בודדים (ids), תיקייה שלמה כולל תיקיות משנה (folder),
    /// או כל הרשימה המוצגת (all) — גם שורות שהממשק עוד לא טען.
    /// </summary>
    private object Select(JsonObject? p)
    {
        var session = RequireSession();
        bool on = p?["on"]?.GetValue<bool>() ?? true;
        var view = ReadView(p?["view"]);

        IEnumerable<RecoveredFile> files =
            p?["ids"] is JsonArray ids ? ids.Select(n => session.ById(n!.GetValue<long>())).OfType<RecoveredFile>()
            : p?["folder"] is JsonNode folder ? session.AllUnder(folder.GetValue<string>())
            : p?["all"]?.GetValue<bool>() == true && view is not null ? session.View(view)
            : Enumerable.Empty<RecoveredFile>();

        session.Select(files, on);
        return SelectionDto(session.Summary(view));
    }

    private static object SelectionDto(SelectionSummary s) => new
    {
        count = s.Count,
        bytes = s.Bytes,
        viewSelectable = s.ViewSelectable,
        viewSelected = s.ViewSelected,
    };

    /// <summary>מצב הסימון של ענפי העץ הפתוחים.</summary>
    private object FolderStates(JsonObject? p)
    {
        var session = RequireSession();
        var paths = p?["paths"]?.AsArray().Select(n => n!.GetValue<string>()) ?? Enumerable.Empty<string>();
        return paths.Distinct().ToDictionary(path => path, session.FolderState);
    }

    private static object FileDto(ScanSession session, RecoveredFile f) => new
    {
        id = f.Id,
        selected = session.IsSelected(f.Id),
        thumb = FileCategories.Thumbnailable.Contains(f.Extension) && f.IsWorthRecovering,
        name = f.Name,
        path = f.Path,
        size = f.Size,
        ext = f.Extension,
        deleted = f.IsDeleted,
        modified = f.Modified?.ToString("yyyy-MM-dd HH:mm"),
        quality = f.Quality.ToString(),
        qualityLabel = Display.Quality(f.Quality),
        qualityReason = f.QualityReason,
        verified = f.Content == ContentCheck.HasData,
        emptyContent = f.Content == ContentCheck.Empty,
        source = Display.Source(f.Source),
        // רשומות שמקורן ביומנים מעידות שהקובץ היה קיים, אך אינן מכילות
        // את מיקום תוכנו. ההבחנה הזו חייבת להיות גלויה למשתמש.
        evidence = f.Source is DiscoverySource.UsnJournal or DiscoverySource.LogFile,
        recoverable = f.IsWorthRecovering,
        compressed = f.IsCompressed,
        namePartial = f.NameIsPartial,
        recycledAt = f.RecycledAt?.ToString("dd/MM/yyyy HH:mm"),
    };

    // ------------------------------------------------------- תצוגה מקדימה

    private object Preview(JsonObject? p)
    {
        var session = RequireOnlineSession();
        long id = p?["id"]?.GetValue<long>() ?? -1;

        var file = session.ById(id)
                   ?? throw new InvalidOperationException(L.T("הקובץ לא נמצא בתוצאות הסריקה."));

        const int maxPreview = 512 * 1024;
        return PreviewOf(session, file, maxPreview);
    }

    /// <summary>
    /// תמונה ממוזערת לתצוגת הגלריה. ההקטנה נעשית כאן ולא בממשק: תמונה של
    /// 5MB הופכת ל-JPEG של כעשרה קילובייט, ורק הוא עובר בגשר.
    /// </summary>
    private object Thumbnail(JsonObject? p)
    {
        var session = RequireOnlineSession();
        long id = p?["id"]?.GetValue<long>() ?? -1;
        int box = Math.Clamp(p?["size"]?.GetValue<int>() ?? 200, 32, 400);

        var file = session.ById(id);
        if (file is null || !file.IsWorthRecovering || !FileCategories.Thumbnailable.Contains(file.Extension))
            return new { ok = false };

        // תמונה גדולה מזה נקראת חלקית; JPEG קטוע עדיין מפוענח ברוב המקרים,
        // ומה שלא מפוענח מוצג כסמל — עדיף מלקרוא מאות מגה בשביל ריבוע קטן.
        const int maxRead = 24 * 1024 * 1024;
        byte[] data = FileContentReader.ReadHead(
            session.FileSystem, session.DiskNumber, session.PartitionOffset,
            session.PartitionSize, session.SectorSize, file, maxRead);
        if (data.Length == 0) return new { ok = false };

        try
        {
            using var input = new MemoryStream(data);
            using var image = System.Drawing.Image.FromStream(input, false, false);

            double scale = Math.Min(1.0, (double)box / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)(image.Width * scale));
            int h = Math.Max(1, (int)(image.Height * scale));

            using var thumb = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(image, 0, 0, w, h);
            }

            using var output = new MemoryStream();
            thumb.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
            return new { ok = true, data = Convert.ToBase64String(output.ToArray()) };
        }
        catch
        {
            // נתונים שאינם מפוענחים כתמונה — קובץ פגום או זיהוי שגוי.
            return new { ok = false };
        }
    }

    private object PreviewOf(ScanSession session, RecoveredFile file, int maxPreview)
    {
        byte[] head = FileContentReader.ReadHead(
            session.FileSystem,
            session.DiskNumber, session.PartitionOffset, session.PartitionSize,
            session.SectorSize, file, maxPreview);

        if (head.Length == 0)
            return new { kind = "none", name = file.Name, reason = L.T("לא ניתן לקרוא את תוכן הקובץ.") };

        var signature = FileSignatures.Identify(head);
        string hex = HexDump(head, 256);

        // וידאו ושמע: הנגן שבממשק מבקש קטעים מהכתובת media/<מספר>, לפי מה שמנגנים.
        // קובץ דחוס של NTFS אינו ניתן לקריאה מאמצע, ולכן אינו מוצע לנגן.
        if (MediaType(file.Extension) is { } media && (!file.IsCompressed || file.ResidentData is not null))
        {
            return new
            {
                kind = "media",
                name = file.Name,
                video = media.Video,
                url = WebAssets.BaseUrl + "media/" + file.Id,
                signature = signature?.Name,
                matchesExtension = signature?.MatchesExtension(file.Extension),
                hex,
            };
        }

        // תמונה: נשלחת לממשק כ-data URL להצגה ישירה.
        if (signature is { IsImage: true } && head.Length <= maxPreview)
        {
            return new
            {
                kind = "image",
                name = file.Name,
                mime = signature.MimeType,
                data = Convert.ToBase64String(head),
                signature = signature.Name,
                matchesExtension = signature.MatchesExtension(file.Extension),
                hex,
            };
        }

        if (LooksLikeText(head))
        {
            return new
            {
                kind = "text",
                name = file.Name,
                text = DecodeText(head, 64 * 1024),
                signature = signature?.Name,
                matchesExtension = signature?.MatchesExtension(file.Extension),
                hex,
            };
        }

        return new
        {
            kind = "binary",
            name = file.Name,
            signature = signature?.Name,
            mime = signature?.MimeType,
            matchesExtension = signature?.MatchesExtension(file.Extension),
            hex,
        };
    }

    /// <summary>היפוך תחילת הקובץ לתצוגת HEX קריאה.</summary>
    private static string HexDump(byte[] data, int maxBytes)
    {
        int length = Math.Min(data.Length, maxBytes);
        var builder = new StringBuilder();

        for (int offset = 0; offset < length; offset += 16)
        {
            builder.Append(offset.ToString("X8")).Append("  ");

            for (int i = 0; i < 16; i++)
            {
                builder.Append(offset + i < length ? data[offset + i].ToString("X2") : "  ");
                builder.Append(i == 7 ? "  " : " ");
            }

            builder.Append(' ');
            for (int i = 0; i < 16 && offset + i < length; i++)
            {
                byte b = data[offset + i];
                builder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>הערכה האם התוכן הוא טקסט, לפי שיעור הבתים הבלתי מודפסים.</summary>
    private static bool LooksLikeText(byte[] data)
    {
        int sample = Math.Min(data.Length, 4096);
        if (sample == 0) return false;

        int suspicious = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0) return false; // בית אפס כמעט תמיד מעיד על קובץ בינארי
            if (b < 9 || (b > 13 && b < 32)) suspicious++;
        }

        return suspicious * 100 / sample < 5;
    }

    private static string DecodeText(byte[] data, int maxChars)
    {
        // זיהוי BOM נפוץ; אחרת UTF-8, שהוא ברירת המחדל המעשית.
        Encoding encoding =
            data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? Encoding.UTF8 :
            data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode :
            data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF ? Encoding.BigEndianUnicode :
            Encoding.UTF8;

        string text = encoding.GetString(data);
        return text.Length > maxChars ? text[..maxChars] + L.T("\n\n… (התצוגה נקטעה)") : text;
    }

    // ------------------------------------------------------------ שחזור

    private object PickFolder() => PickFolder(L.T("בחרו תיקיית יעד לשחזור — חייבת להיות על כונן אחר"));

    private object PickFolder(string description)
    {
        string? selected = null;

        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };

            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selected = dialog.SelectedPath;
        });

        return new { path = selected };
    }

    private object ValidateTarget(JsonObject? p)
    {
        var session = RequireOnlineSession();
        string target = p?["target"]?.GetValue<string>() ?? "";

        try
        {
            RecoveryWriter.ValidateTarget(target, session.DiskNumber);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!);
            return new { valid = true, freeSpace = drive.AvailableFreeSpace, error = (string?)null };
        }
        catch (Exception ex)
        {
            return new { valid = false, freeSpace = 0L, error = FriendlyError.From(ex).Text };
        }
    }

    private async Task<object> StartRecoveryAsync(JsonObject? p)
    {
        var session = RequireOnlineSession();

        string target = p?["target"]?.GetValue<string>() ?? "";
        bool preservePaths = p?["preservePaths"]?.GetValue<bool>() ?? true;
        // הבחירה נשמרת במנוע (scan.select), כך שגם תיקייה של מאות אלפי קבצים
        // אינה עוברת בגשר כרשימת מזהים.
        var files = session.SelectedFiles();

        if (files.Count == 0)
            throw new InvalidOperationException(L.T("לא נבחרו קבצים לשחזור."));

        _recoverCancel?.Cancel();
        _recoverCancel = new CancellationTokenSource();

        var lastPush = DateTime.MinValue;
        var progress = new Progress<RecoveryProgress>(rp =>
        {
            if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 100) return;
            lastPush = DateTime.UtcNow;

            _form.Taskbar.Report(rp.Percent);
            PushEvent("recover.progress", new
            {
                file = rp.CurrentFile,
                done = rp.FilesDone,
                total = rp.FilesTotal,
                bytes = rp.BytesWritten,
                percent = rp.Percent,
                elapsed = rp.Elapsed.TotalSeconds,
            });
        });

        var report = await RecoveryWriter.RecoverAsync(
            session.FileSystem,
            session.DiskNumber, session.PartitionOffset, session.PartitionSize, session.SectorSize,
            files, new RecoveryOptions
            {
                TargetFolder = target, PreservePaths = preservePaths,
                Source = session.Disk.ImagePath is { } image && !DevicePaths.IsDecryptedVolume(image) && !DevicePaths.IsRaidPath(image)
                    ? L.T("{0} · תמונת דיסק {1}", session.PartitionTitle, Path.GetFileName(image))
                    : $"{session.PartitionTitle} · {session.Disk.Model}",
            },
            progress, _recoverCancel.Token);

        return new
        {
            succeeded = report.Succeeded,
            skipped = report.Skipped,
            empty = report.EmptyFiles,
            failed = report.Failures.Count,
            bytes = report.BytesWritten,
            duration = report.Duration.TotalSeconds,
            cancelled = report.Cancelled,
            partial = report.PartialFiles.Take(50),
            failures = report.Failures.Take(50).Select(f => new { file = f.FileName, reason = f.Reason }),
            target,
            reportPath = report.ReportPath,
            partialFolder = report.PartialFolder,
            previews = report.PreviewsSaved,
        };
    }

    /// <summary>
    /// פתיחת תיקייה בסייר הקבצים. רק תיקייה קיימת — הנתיב מגיע מהממשק, ולכן
    /// הוא מועבר כארגומנט יחיד ל-explorer ולא כפקודה.
    /// </summary>
    private static object? OpenFolder(JsonObject? p)
    {
        string path = p?["path"]?.GetValue<string>() ?? "";
        if (!Directory.Exists(path)) throw new InvalidOperationException(L.T("התיקייה לא נמצאה."));

        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path));
        System.Diagnostics.Process.Start(start);
        return null;
    }

    // ------------------------------------------------------------ סריקת כונן

    /// <summary>מספר המחיצה הראשונה שנמצאה בסריקה — כדי לא להתנגש במחיצות מהטבלה.</summary>
    private const int FoundIndexBase = 100;

    /// <summary>מחיצות שנמצאו בסריקת כונן, לפי מספר הדיסק. שורדות רענון של הרשימה.</summary>
    private readonly Dictionary<int, List<FoundPartition>> _found = new();

    private CancellationTokenSource? _huntCancel;

    private async Task<object> HuntAsync(JsonObject? p)
    {
        var disk = FindDisk(p?["disk"]?.GetValue<int>() ?? -1);

        _huntCancel?.Cancel();
        _huntCancel = new CancellationTokenSource();

        var lastPush = DateTime.MinValue;
        var progress = new Progress<HuntProgress>(hp =>
        {
            if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 150 && hp.Percent < 100) return;
            lastPush = DateTime.UtcNow;

            _form.Taskbar.Report(hp.Percent);
            PushEvent("hunt.progress", new
            {
                percent = hp.Percent,
                done = hp.BytesDone,
                total = hp.BytesTotal,
                found = hp.Found,
                speed = hp.BytesPerSecond,
                elapsed = hp.Elapsed.TotalSeconds,
                map = MapDto(hp.Map),
            });
        });

        // הסריקה משווה מול הטבלה בלבד — לא מול מחיצות שנמצאו בסריקה קודמת.
        var tableOnly = new PhysicalDiskInfo
        {
            DiskNumber = disk.DiskNumber,
            SizeBytes = disk.SizeBytes,
            LogicalSectorSize = disk.LogicalSectorSize,
            Partitions = disk.Partitions.Where(x => x.Index < FoundIndexBase).ToList(),
        };

        var result = await PartitionHunter.HuntAsync(tableOnly, progress, _huntCancel.Token);

        _found[disk.DiskNumber] = result.Found;
        AttachFound(disk);

        return new
        {
            found = result.Found.Count,
            cancelled = result.Cancelled,
            duration = result.Duration.TotalSeconds,
            unreadable = result.UnreadableBytes,
            hidden = result.HiddenInside,
            disk = DiskDto(disk),
        };
    }

    /// <summary>הוספת המחיצות שנמצאו לרשימת המחיצות של הדיסק, כמחיצות לכל דבר.</summary>
    private void AttachFound(PhysicalDiskInfo disk)
    {
        disk.Partitions.RemoveAll(x => x.Index >= FoundIndexBase);
        if (!_found.TryGetValue(disk.DiskNumber, out var list)) return;

        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            disk.Partitions.Add(new PartitionInfo
            {
                Index = FoundIndexBase + i,
                DiskNumber = disk.DiskNumber,
                OffsetBytes = f.Offset,
                SizeBytes = f.Size,

                // מחיצה שתחילתה נהרסה אינה נקראת ישירות — היא מוצגת כלא מזוהה,
                // והממשק מוביל אותה לאבחון ולקריאה דרך עותק הגיבוי.
                FileSystem = f.BootSectorDamaged ? FileSystemKind.Raw : f.FileSystem,
                TypeName = L.T("מחיצה שנמצאה · ") + Display.FileSystem(f.FileSystem),
                Label = f.Label,
                IsUnmounted = true,
            });
        }
    }

    private FoundPartition? FoundFor(PhysicalDiskInfo disk, PartitionInfo part)
        => part.Index >= FoundIndexBase && _found.TryGetValue(disk.DiskNumber, out var list)
           && part.Index - FoundIndexBase < list.Count
            ? list[part.Index - FoundIndexBase]
            : null;

    private object RestorePlanFor(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        var found = FoundFor(disk, part)
                    ?? throw new InvalidOperationException(L.T("זו אינה מחיצה שנמצאה בסריקת כונן."));

        var plan = PartitionTableWriter.Plan(disk, found);
        return new { canRestore = plan.CanRestore, explanation = plan.Explanation, whatWillChange = plan.WhatWillChange };
    }

    private object RestorePartition(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        var found = FoundFor(disk, part)
                    ?? throw new InvalidOperationException(L.T("זו אינה מחיצה שנמצאה בסריקת כונן."));

        string undoFolder = p?["undoFolder"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(undoFolder))
            throw new InvalidOperationException(L.T("יש לבחור תיקייה לגיבוי לפני הכתיבה."));
        RequireConfirmWord(p);

        var result = PartitionTableWriter.Restore(disk, found, undoFolder);

        // אחרי החזרה המחיצה כבר בטבלה — אין עוד טעם להציג אותה כ"נמצאה".
        if (result.Succeeded && _found.TryGetValue(disk.DiskNumber, out var list))
            list.Remove(found);

        return new
        {
            succeeded = result.Succeeded,
            rolledBack = result.RolledBack,
            message = result.Message,
            undoFile = result.UndoFile,
        };
    }

    // ------------------------------------------------------------ תמונת דיסק

    /// <summary>המקור של תמונה: דיסק שלם (part = -1) או מחיצה אחת.</summary>
    private (PhysicalDiskInfo Disk, PartitionInfo? Part, long Offset, long Size, string Description) ImageSource(JsonObject? p)
    {
        var disk = FindDisk(p?["disk"]?.GetValue<int>() ?? -1);
        int partIndex = p?["part"]?.GetValue<int>() ?? -1;

        if (partIndex < 0)
            return (disk, null, 0, disk.SizeBytes, L.T("{0} (דיסק {1}, {2})", disk.DisplayName, disk.DiskNumber, L.T(disk.BusType)));

        var part = disk.Partitions.FirstOrDefault(x => x.Index == partIndex)
                   ?? throw new InvalidOperationException(L.T("המחיצה לא נמצאה. רענן את רשימת הדיסקים."));

        string name = !string.IsNullOrEmpty(part.DriveLetter) ? L.T("כונן ") + part.DriveLetter
            : !string.IsNullOrEmpty(part.Label) ? part.Label : L.T("מחיצה ") + part.Index;

        return (disk, part, part.OffsetBytes, part.SizeBytes,
            L.T("{0} (דיסק {1}) · {2} · {3}", disk.DisplayName, disk.DiskNumber, name, L.T(Display.FileSystem(part.FileSystem))));
    }

    private object PickImageTarget(JsonObject? p)
    {
        var (disk, part, _, _, _) = ImageSource(p);

        string baseName = part is null ? disk.DisplayName
            : !string.IsNullOrEmpty(part.DriveLetter) ? $"{disk.DisplayName}-{part.DriveLetter.TrimEnd(':')}"
            : $"{disk.DisplayName}-part{part.Index}";

        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        string suggested = $"RAF-{baseName.Trim()}-{DateTime.Now:yyyyMMdd-HHmm}.img";

        string? selected = null;
        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = L.T("שמירת תמונת הדיסק — בחרו כונן אחר מהכונן המקורי"),
                FileName = suggested,
                // כונן שלם: אפשר גם VHD, ש-Windows מחבר ככונן בלחיצה כפולה.
                Filter = part is null
                    ? L.T("תמונת דיסק גולמית (*.img)|*.img|כונן וירטואלי שאפשר לחבר ב-Windows (*.vhd)|*.vhd")
                    : L.T("תמונת דיסק גולמית (*.img)|*.img"),
                DefaultExt = "img",
                // תמונה קיימת אינה בהכרח דריסה — אפשר להמשיך ממנה. הלוח מסביר מה יקרה.
                OverwritePrompt = false,
            };

            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selected = dialog.FileName;
        });

        return new { path = selected };
    }

    private object ValidateImageTarget(JsonObject? p)
    {
        var (disk, part, _, size, _) = ImageSource(p);
        string path = p?["path"]?.GetValue<string>() ?? "";

        try
        {
            DiskImager.ValidateDestination(path, disk.DiskNumber, size, part is null ? "disk" : "partition");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);

            // תמונה קודמת באותו נתיב — אפשר להמשיך ממנה במקום להתחיל מחדש.
            var existing = DiskImager.Inspect(path, size, part is null ? "disk" : "partition");
            return new
            {
                valid = true, size, freeSpace = drive.AvailableFreeSpace, error = (string?)null,
                existing = existing is null ? null : new
                {
                    canResume = existing.CanResume,
                    notCopied = existing.NotCopiedBytes,
                    unreadable = existing.UnreadableBytes,
                    complete = existing.Complete,
                    source = existing.Source,
                    reason = existing.Reason,
                },
            };
        }
        catch (Exception ex)
        {
            return new { valid = false, size, freeSpace = 0L, error = FriendlyError.From(ex).Text, existing = (object?)null };
        }
    }

    private async Task<object> CreateImageAsync(JsonObject? p)
    {
        var (disk, part, offset, size, description) = ImageSource(p);
        string path = p?["path"]?.GetValue<string>() ?? "";

        DiskImager.ValidateDestination(path, disk.DiskNumber, size, part is null ? "disk" : "partition");

        _imageCancel?.Cancel();
        _imageCancel = new CancellationTokenSource();

        var lastPush = DateTime.MinValue;
        var progress = new Progress<ImagingProgress>(ip =>
        {
            if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 150) return;
            lastPush = DateTime.UtcNow;

            _form.Taskbar.Report(ip.Percent);
            PushEvent("image.progress", new
            {
                pass = ip.Pass,
                stage = ip.Stage,
                percent = ip.Percent,
                done = ip.BytesDone,
                total = ip.BytesTotal,
                problems = ip.ProblemBytes,
                speed = ip.BytesPerSecond,
                elapsed = ip.Elapsed.TotalSeconds,
                map = MapDto(ip.Map),
            });
        });

        var result = await DiskImager.CreateAsync(
            disk.DiskNumber, offset, size, disk.LogicalSectorSize,
            part is null ? "disk" : "partition", description, path,
            progress, _imageCancel.Token,
            resume: p?["resume"]?.GetValue<bool>() ?? false,
            retryUnreadable: p?["retryUnreadable"]?.GetValue<bool>() ?? false);

        return new
        {
            path = result.ImagePath,
            map = result.MapPath,
            size = result.Size,
            complete = result.Complete,
            cancelled = result.Cancelled,
            unreadable = result.UnreadableBytes,
            unreadableRanges = result.UnreadableRanges,
            notCopied = result.NotCopiedBytes,
            duration = result.Duration.TotalSeconds,
            message = result.Message,
        };
    }

    /// <summary>פתיחת תמונה — מנתיב שהתקבל, או מתיבת בחירת קובץ.</summary>
    private object OpenImage(JsonObject? p)
    {
        string? path = p?["path"]?.GetValue<string>();

        if (string.IsNullOrEmpty(path))
        {
            _form.InvokeOnUiSync(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = L.T("פתיחת תמונת דיסק"),
                    Filter = L.T("תמונות דיסק וכוננים וירטואליים (*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01)|*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01|" +
                             "כוננים וירטואליים של Windows (*.vhd;*.vhdx)|*.vhd;*.vhdx|" +
                             "כוננים של VirtualBox ו-VMware (*.vmdk)|*.vmdk|" +
                             "תמונות של כלי חקירה (*.e01)|*.e01|כל הקבצים (*.*)|*.*"),
                    CheckFileExists = true,
                };

                if (dialog.ShowDialog(_form) == DialogResult.OK)
                    path = dialog.FileName;
            });

            if (string.IsNullOrEmpty(path)) return new { number = (int?)null };
        }

        var disk = ImageDisk.Open(path);
        _images.RemoveAll(d => d.DiskNumber == disk.DiskNumber);
        _images.Add(disk);

        return new { number = (int?)disk.DiskNumber };
    }

    /// <summary>
    /// מחיצת BitLocker שנפתחה ב-Windows — כדיסק נוסף ברשימה, שנקרא דרך Windows
    /// ולכן מפוענח. אם הוא כבר פתוח, מוחזר אותו מספר.
    /// </summary>
    private object OpenUnlockedVolume(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        if (part.FileSystem != FileSystemKind.BitLocker || part.DriveLetter.Length == 0)
            throw new InvalidOperationException(L.T("המחיצה אינה כונן BitLocker עם אות כונן."));

        string title = string.IsNullOrWhiteSpace(part.Label) ? L.T("כונן {0}", part.DriveLetter) : $"{part.Label} ({part.DriveLetter})";
        var volume = ImageDisk.OpenUnlockedVolume(part.DriveLetter, part.SizeBytes, disk.LogicalSectorSize, title);
        _images.RemoveAll(d => d.DiskNumber == volume.DiskNumber);
        _images.Add(volume);

        return new { number = volume.DiskNumber };
    }

    /// <summary>לפני הפתיחה: אילו מגנים הוגדרו לכונן, ואם אזור הניהול שלו נקרא בכלל.</summary>
    private object InspectBitLocker(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        var info = BitLockerDisk.Inspect(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize);
        return new { protectors = info.Protectors, typedKey = info.TypedKey, suspended = info.Suspended, problem = info.Problem };
    }

    /// <summary>
    /// פתיחת מחיצת BitLocker נעולה במפתח השחזור או בסיסמה — התוכנה מפענחת בעצמה,
    /// והמחיצה מופיעה ככונן נוסף ברשימה. המפתח אינו נשמר בשום מקום.
    /// </summary>
    private object UnlockBitLocker(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        string key = p?["key"]?.GetValue<string>() ?? "";
        string title = !string.IsNullOrWhiteSpace(part.Label) ? part.Label
                     : part.DriveLetter.Length > 0 ? L.T("כונן {0}", part.DriveLetter)
                     : disk.ImagePath is not null ? disk.Model
                     : L.T("מחיצה {0}", part.Index);

        var volume = BitLockerDisk.Unlock(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize, key, title);
        _images.RemoveAll(d => d.DiskNumber == volume.DiskNumber);
        _images.Add(volume);

        return new { number = volume.DiskNumber };
    }

    /// <summary>המערכים שנמצאו בחיפוש האחרון — ההרכבה בוחרת מהם לפי המזהה.</summary>
    private List<RaidDisk.Found> _raids = new();

    /// <summary>
    /// חיפוש מערכי RAID של לינוקס בכל הכוננים והתמונות שברשימה. קריאה בלבד.
    /// לכל מערך: מה נמצא, מה חסר, ואם אפשר להרכיב אותו.
    /// </summary>
    private object FindRaids()
    {
        _raids = RaidDisk.Find(_disks.ToList());
        return _raids.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            level = r.Level,
            disks = r.Disks,
            size = r.Size,
            chunk = r.Chunk,
            updated = r.Updated,
            problem = r.Problem,
            missing = r.MissingRoles.Select(m => m + 1).ToList(),
            members = r.Members.Select(m => new { disk = m.Disk, offset = m.Offset, title = m.Title, role = m.Role + 1, stale = m.Stale }).ToList(),
            open = _images.Any(d => d.ImagePath == DevicePaths.RaidPathOf(r.Id)),
        }).ToList();
    }

    /// <summary>הרכבת מערך שנמצא. הוא מופיע ברשימה ככונן נוסף, לקריאה בלבד.</summary>
    private object AssembleRaid(JsonObject? p)
    {
        string id = p?["id"]?.GetValue<string>() ?? "";
        var found = _raids.FirstOrDefault(r => r.Id == id)
            ?? throw new InvalidOperationException(L.T("המערך לא נמצא. חפשו שוב."));
        var array = RaidDisk.Assemble(found);
        _images.RemoveAll(d => d.DiskNumber == array.DiskNumber);
        _images.Add(array);
        return new { number = array.DiskNumber };
    }

    /// <summary>הזיהוי האחרון של מערך בלי כותרת: הכוננים שנבחרו והמבנים שהתיישבו.</summary>
    private (List<PhysicalDiskInfo> Disks, List<RaidDisk.Detected> Found) _detected = (new(), new());

    /// <summary>זיהוי המבנה של מערך בלי כותרת (כרטיס RAID) מהכוננים שהמשתמש בחר. קריאה בלבד.</summary>
    private object DetectRaid(JsonObject? p)
    {
        var numbers = p?["disks"]?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? new();
        var disks = numbers.Select(n => _disks.FirstOrDefault(d => d.DiskNumber == n)
            ?? throw new InvalidOperationException(L.T("אחד הכוננים שנבחרו כבר לא ברשימה. רעננו ובחרו שוב."))).ToList();
        var found = RaidDisk.Detect(disks);
        _detected = (disks, found);
        return found.Select(g => new { level = g.Level, chunk = g.Chunk, order = g.Order, fileSystem = g.FileSystem, confident = g.Confident }).ToList();
    }

    private object AssembleDetected(JsonObject? p)
    {
        int index = p?["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || index >= _detected.Found.Count) throw new InvalidOperationException(L.T("המבנה לא נמצא. זהו שוב."));
        var array = RaidDisk.Assemble(_detected.Found[index], _detected.Disks);
        _images.RemoveAll(d => d.DiskNumber == array.DiskNumber);
        _images.Add(array);
        return new { number = array.DiskNumber };
    }

    /// <summary>המאגרים שנמצאו בחיפוש האחרון — הפתיחה בוחרת מהם לפי המזהה.</summary>
    private List<LvmDisk.Found> _lvms = new();

    /// <summary>חיפוש מאגרים לוגיים של לינוקס בכל הכוננים, התמונות והמערכים שברשימה. קריאה בלבד.</summary>
    private object FindLvm()
    {
        _lvms = LvmDisk.Find(_disks.ToList());
        return _lvms.Select(g => new
        {
            id = g.Id,
            name = g.Name,
            pvs = g.Pvs.Select(p => p?.Title).ToList(),
            missing = g.Missing.Count,
            volumes = g.Volumes.Select(v => new
            {
                name = v.Name,
                size = v.Size,
                problem = v.Problem,
                open = _images.Any(d => d.ImagePath == DevicePaths.RaidPathOf($"lvm-{g.Id}-{v.Name}")),
            }).ToList(),
        }).ToList();
    }

    /// <summary>פתיחת אזור במאגר ככונן נוסף, לקריאה בלבד.</summary>
    private object OpenLvm(JsonObject? p)
    {
        string id = p?["id"]?.GetValue<string>() ?? "";
        string name = p?["volume"]?.GetValue<string>() ?? "";
        var group = _lvms.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException(L.T("המאגר לא נמצא. חפשו שוב."));
        var volume = LvmDisk.Open(group, name);
        _images.RemoveAll(d => d.DiskNumber == volume.DiskNumber);
        _images.Add(volume);
        return new { number = volume.DiskNumber };
    }

    private object? CloseImage(JsonObject? p)
    {
        int number = p?["disk"]?.GetValue<int>() ?? -1;

        // מערך או אזור במאגר שנבנו על הכונן הזה — נסגרים איתו: אין להם יותר ממה לקרוא.
        foreach (int n in DevicePaths.DependentsOf(number).Append(number))
        {
            // תוצאות סריקה של התמונה מצביעות עליה; אחרי הסגירה אי אפשר לחלץ מהן.
            if (_session?.DiskNumber == n)
                _session = null;

            _images.RemoveAll(d => d.DiskNumber == n);
            _disks.RemoveAll(d => d.DiskNumber == n);
            ImageDisk.Close(n);
        }
        return null;
    }

    // ------------------------------------------------------------ תיקון מחיצה

    /// <summary>אבחון מחיצה שאינה נקראת. פעולה זו אינה כותבת דבר לדיסק.</summary>
    private object Diagnose(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);

        var result = PartitionDiagnosis.Diagnose(
            disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize);

        return new
        {
            outlook = result.Outlook.ToString(),
            canRepair = result.CanRepair,
            fileSystem = Display.FileSystem(result.DetectedFileSystem),
            summary = result.Summary,
            whatWillChange = result.WhatWillChange,
            backupOffset = result.BackupOffset,
            repairLength = result.RepairLength,
            // סריקה מתקדמת אינה תלויה במערכת הקבצים, ולכן היא הדרך
            // לשחזר קבצים גם כשאי אפשר לתקן.
            canRecover = true,
        };
    }

    /// <summary>
    /// קריאת מחיצה ש-Windows מבקש לפרמט דרך עותק הגיבוי של מגזר האתחול,
    /// בזיכרון בלבד. מכאן והלאה המחיצה נסרקת כרגיל — עם שמות ותיקיות —
    /// ועל הכונן לא נכתב דבר.
    /// </summary>
    private object ReadThrough(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);

        var diagnosis = VirtualRepair.Apply(
            disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize);

        if (!diagnosis.CanRepair)
            return new { ok = false, message = diagnosis.Summary };

        _readThrough[(disk.DiskNumber, part.OffsetBytes)] = diagnosis.DetectedFileSystem;
        ApplyReadThrough(disk);

        return new { ok = true, message = "", fs = Display.FileSystem(diagnosis.DetectedFileSystem) };
    }

    /// <summary>הצגת מחיצה שנקראת דרך הגיבוי עם מערכת הקבצים שזוהתה בה.</summary>
    private void ApplyReadThrough(PhysicalDiskInfo disk)
    {
        for (int i = 0; i < disk.Partitions.Count; i++)
        {
            var p = disk.Partitions[i];
            if (!_readThrough.TryGetValue((p.DiskNumber, p.OffsetBytes), out var kind) || p.FileSystem == kind)
                continue;

            disk.Partitions[i] = new PartitionInfo
            {
                Index = p.Index,
                DiskNumber = p.DiskNumber,
                OffsetBytes = p.OffsetBytes,
                SizeBytes = p.SizeBytes,
                FileSystem = kind,
                TypeName = p.TypeName,
                TypeGuid = p.TypeGuid,
                PartitionGuid = p.PartitionGuid,
                Label = p.Label,
                DriveLetter = p.DriveLetter,
                IsBootable = p.IsBootable,
                IsHidden = p.IsHidden,
                FreeBytes = p.FreeBytes,
                IsUnmounted = p.IsUnmounted,
            };
        }
    }

    /// <summary>ביצוע התיקון. זו הפעולה היחידה בתוכנה שכותבת לדיסק המקור.</summary>
    private object ApplyRepair(JsonObject? p)
    {
        var (disk, part) = FindPartition(p);
        string undoFolder = p?["undoFolder"]?.GetValue<string>() ?? "";

        if (string.IsNullOrWhiteSpace(undoFolder))
            throw new InvalidOperationException(L.T("יש לבחור תיקייה לגיבוי לפני התיקון."));
        RequireConfirmWord(p);

        var diagnosis = PartitionDiagnosis.Diagnose(
            disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize);

        var result = PartitionRepair.Repair(
            disk, part.OffsetBytes, part.SizeBytes,
            diagnosis, undoFolder, part.DriveLetter);

        // אחרי תיקון אמיתי הדיסק עצמו נכון, והקריאה דרך הגיבוי מיותרת.
        if (result.Succeeded)
        {
            VirtualRepair.Remove(disk.DiskNumber, part.OffsetBytes);
            _readThrough.Remove((disk.DiskNumber, part.OffsetBytes));
        }

        return new
        {
            succeeded = result.Succeeded,
            rolledBack = result.RolledBack,
            message = result.Message,
            undoFile = result.UndoFile,
        };
    }

    // ------------------------------------------------------------ ביטול תיקון קודם

    private object PickUndoFile()
    {
        string? selected = null;
        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = L.T("בחרו את קובץ הביטול שנשמר בזמן התיקון"),
                Filter = L.T("קובצי ביטול|RAF-undo-*.bin|כל הקבצים|*.*"),
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK) selected = dialog.FileName;
        });
        return new { path = selected };
    }

    /// <summary>מה הביטול יעשה, ולאיזה כונן — לפני שנכתב דבר.</summary>
    private object CheckUndo(JsonObject? p)
    {
        string path = p?["path"]?.GetValue<string>() ?? "";
        var check = UndoService.Check(path, _disks);
        var file = check.File;

        return new
        {
            canUndo = check.CanUndo,
            message = check.Message,
            what = file is null ? null
                : file.Kind == UndoKind.PartitionTable ? L.T("החזרת מחיצה לטבלת המחיצות") : L.T("תיקון מחיצה"),
            created = file?.Created,
            diskName = check.Disk?.DisplayName,
            diskSize = check.Disk?.SizeBytes,
        };
    }

    private object ApplyUndo(JsonObject? p)
    {
        string path = p?["path"]?.GetValue<string>() ?? "";
        RequireConfirmWord(p);

        var result = UndoService.Apply(path, _disks);
        return new { succeeded = result.Succeeded, rolledBack = result.RolledBack, message = result.Message };
    }

    // ------------------------------------------------------------ תיקון קבצים

    private object PickFiles()
    {
        string[] selected = Array.Empty<string>();

        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = L.T("בחרו קבצים לבדיקה ולתיקון"),
                Multiselect = true,
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(_form) == DialogResult.OK)
                selected = dialog.FileNames;
        });

        return new { paths = selected };
    }

    private object DoctorDiagnose(JsonObject? p)
    {
        var paths = p?["paths"]?.AsArray()?.Select(n => n!.GetValue<string>()) ?? Enumerable.Empty<string>();

        // גרירה יכולה להביא גם תיקיות: הן נפרשות לקבצים שבהן, כמו "בדיקת תיקייה".
        var files = paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                : new[] { path })
            .Take(5000);

        return new { files = files.Select(DiagnosisDto).ToList() };
    }

    /// <summary>
    /// בדיקת כל הקבצים בתיקייה — משמש אחרי שחזור, כדי לאתר מיד
    /// קבצים שחזרו פגומים ולהציע לתקן אותם.
    /// </summary>
    private object DoctorDiagnoseFolder(JsonObject? p)
    {
        string folder = p?["folder"]?.GetValue<string>() ?? "";
        if (!Directory.Exists(folder))
            throw new InvalidOperationException(L.T("התיקייה לא נמצאה."));

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Take(5000)
            .Select(DiagnosisDto)
            .ToList();

        return new
        {
            files,
            total = files.Count,
            healthy = files.Count(f => f.healthy),
            fixable = files.Count(f => !f.healthy && f.canRepair),
        };
    }

    private object DoctorRepair(JsonObject? p)
    {
        var paths = p?["paths"]?.AsArray()?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
        string output = p?["output"]?.GetValue<string>() ?? "";

        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(L.T("יש לבחור תיקייה לשמירת הקבצים המתוקנים."));

        var results = paths.Select(path =>
        {
            try
            {
                var r = FileDoctor.Repair(path, output);
                return new
                {
                    path,
                    name = Path.GetFileName(path),
                    succeeded = r.Succeeded,
                    output = r.OutputPath,
                    applied = r.Applied,
                    message = r.Message,
                    healthyAfter = r.After?.IsHealthy ?? false,
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    path,
                    name = Path.GetFileName(path),
                    succeeded = false,
                    output = (string?)null,
                    applied = new List<string>(),
                    message = FriendlyError.From(ex).Text,
                    healthyAfter = false,
                };
            }
        }).ToList();

        return new
        {
            results,
            repaired = results.Count(r => r.succeeded && r.output is not null),
            output,
        };
    }

    /// <summary>
    /// בחירת קובץ ייחוס — סרטון או תמונה תקינים, מאותו מכשיר. נבדק מיד שהוא מתאים
    /// (בסרטון: אינדקס ותמונה בקידוד שאפשר לעבוד איתו), כדי שהשגיאה תופיע עכשיו ולא
    /// אחרי בנייה ארוכה.
    /// </summary>
    private object PickReference(JsonObject? p)
    {
        bool photo = p?["kind"]?.GetValue<string>() == "photo";
        string ext = (p?["ext"]?.GetValue<string>() ?? "").TrimStart('.').ToLowerInvariant();
        bool raw = photo && RAF.Core.Repair.TiffTransplant.RawExtensions.Contains(ext);
        string? selected = null;
        _form.InvokeOnUiSync(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = raw ? L.T("בחרו קובץ RAW תקין שצולם באותה מצלמה ובאותן הגדרות")
                    : photo ? L.T("בחרו תמונה תקינה שצולמה באותה מצלמה ובאותן הגדרות")
                    : L.T("בחרו סרטון תקין שצולם באותו מכשיר ובאותן הגדרות"),
                Filter = raw ? L.T("קובצי {0}|*.{1}|כל הקבצים|*.*", ext.ToUpperInvariant(), ext)
                    : photo ? L.T("תמונות JPEG|*.jpg;*.jpeg|כל הקבצים|*.*")
                    : L.T("סרטונים|*.mp4;*.mov;*.m4v;*.3gp;*.3g2|כל הקבצים|*.*"),
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK) selected = dialog.FileName;
        });

        if (selected is null) return new { path = (string?)null, problem = (string?)null };

        string? problem = null;
        try
        {
            problem = raw ? RAF.Core.Repair.TiffTransplant.DescribeReference(selected)
                : photo ? RAF.Core.Repair.JpegTransplant.DescribeReference(selected)
                : RAF.Core.Repair.Mp4Rebuilder.DescribeReference(selected);
        }
        catch (Exception ex)
        {
            problem = FriendlyError.From(ex).Text;
        }
        return new { path = selected, problem };
    }

    private object DoctorRebuildVideo(JsonObject? p)
    {
        string path = p?["path"]?.GetValue<string>() ?? "";
        string reference = p?["reference"]?.GetValue<string>() ?? "";
        string output = p?["output"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(L.T("יש לבחור תיקייה לשמירת הסרטון המתוקן."));

        var last = DateTime.MinValue;
        var progress = new Progress<double>(percent =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 150 && percent < 100) return;
            last = DateTime.UtcNow;
            PushEvent("doctor.progress", new { percent });
        });

        var r = FileDoctor.RepairVideo(path, reference, output, progress);
        return new
        {
            name = Path.GetFileName(path),
            succeeded = r.Succeeded,
            output = r.OutputPath,
            folder = r.OutputPath is null ? null : Path.GetDirectoryName(r.OutputPath),
            applied = r.Applied,
            message = r.Message,
        };
    }

    private object DoctorRebuildPhoto(JsonObject? p)
    {
        string path = p?["path"]?.GetValue<string>() ?? "";
        string reference = p?["reference"]?.GetValue<string>() ?? "";
        string output = p?["output"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(L.T("יש לבחור תיקייה לשמירת התמונה המתוקנת."));

        var r = FileDoctor.RepairPhoto(path, reference, output);
        return new
        {
            name = Path.GetFileName(path),
            succeeded = r.Succeeded,
            output = r.OutputPath,
            folder = r.OutputPath is null ? null : Path.GetDirectoryName(r.OutputPath),
            applied = r.Applied,
            message = r.Message,
        };
    }

    private sealed record DiagnosisView(
        string path, string name, long size, bool healthy, bool canRepair,
        string? detected, string? expected, string? suggestedExtension,
        List<IssueView> issues, bool needsReference = false, string? referenceKind = null);

    private sealed record IssueView(string kind, string description, bool fixable);

    private static DiagnosisView DiagnosisDto(string path)
    {
        try
        {
            var d = FileDoctor.Diagnose(path);
            return new DiagnosisView(
                path, Path.GetFileName(path), d.Size, d.IsHealthy, d.CanRepair,
                d.DetectedFormat, d.ExpectedFormat, d.SuggestedExtension,
                d.Issues.Select(i => new IssueView(i.Kind.ToString(), i.Description, i.Fixable)).ToList(),
                d.NeedsReferenceVideo || d.NeedsReferencePhoto,
                d.NeedsReferencePhoto ? "photo" : d.NeedsReferenceVideo ? "video" : null);
        }
        catch (Exception ex)
        {
            return new DiagnosisView(
                path, Path.GetFileName(path), 0, false, false, null, null, null,
                new List<IssueView> { new("Error", L.T("לא ניתן לקרוא את הקובץ. ") + FriendlyError.From(ex).Text, false) });
        }
    }

    // ------------------------------------------------------------ עזר

    /// <summary>איתור דיסק ומחיצה מתוך פרמטרי הבקשה.</summary>
    private (PhysicalDiskInfo Disk, PartitionInfo Part) FindPartition(JsonObject? p)
    {
        var disk = FindDisk(p?["disk"]?.GetValue<int>() ?? -1);
        var part = disk.Partitions.FirstOrDefault(x => x.Index == (p?["part"]?.GetValue<int>() ?? -1))
                   ?? throw new InvalidOperationException(L.T("המחיצה לא נמצאה. רענן את רשימת הדיסקים."));

        return (disk, part);
    }

    private PhysicalDiskInfo FindDisk(int number)
        => _disks.FirstOrDefault(d => d.DiskNumber == number)
           ?? throw new InvalidOperationException(L.T("הדיסק לא נמצא. רענן את רשימת הדיסקים."));

    private ScanSession RequireSession()
        => _session ?? throw new InvalidOperationException(L.T("לא בוצעה סריקה עדיין."));

    private static object? Cancel(CancellationTokenSource? source)
    {
        source?.Cancel();
        return null;
    }

    /// <summary>קבצים ותיקיות שנגררו אל החלון — נשלחים לממשק, שמחליט מה לעשות בהם.</summary>
    internal void FilesDropped(List<string> paths)
    {
        if (paths.Count > 0) PushEvent("files.dropped", new { paths });
    }

    /// <summary>כונן חובר או נותק. הממשק מחליט אם לרענן — לא באמצע פעולה.</summary>
    internal void DisksChanged() => PushEvent("disks.changed", new { });

    /// <summary>דחיפת אירוע לממשק ללא בקשה מוקדמת.</summary>
    private void PushEvent(string name, object data)
    {
        string payload = JsonSerializer.Serialize(new { @event = name, data }, JsonOptions);
        _form.PostToWeb(payload);
    }

    private static string Ok(string id, object? data) =>
        JsonSerializer.Serialize(new { id, ok = true, data }, JsonOptions);

    /// <summary>
    /// המילה שהמשתמש מקליד לפני כתיבה לכונן — בכל שפת ממשק המילה שלה (confirmWord בממשק).
    /// </summary>
    private static readonly string[] ConfirmWords = { "מאשר", "CONFIRM" };   // לא לתרגום: תווית, הממשק מתרגם

    /// <summary>
    /// בדיקה כפולה למילת האישור: הממשק לא מאפשר ללחוץ בלעדיה, אבל הכתיבה לכונן
    /// היא הפעולה המסוכנת היחידה בתוכנה — ולכן גם כאן, ולא רק בכפתור.
    /// </summary>
    private static void RequireConfirmWord(JsonObject? p)
    {
        string typed = p?["confirm"]?.GetValue<string>()?.Trim() ?? "";
        if (!ConfirmWords.Any(w => string.Equals(typed, w, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.T("כדי לכתוב לכונן יש להקליד את המילה \"{0}\". שום דבר לא נכתב.", ConfirmWords[0]));
    }

    /// <summary>הפעולות היחידות שכותבות לכונן המקור. בכל השאר, שגיאה אינה נוגעת בו.</summary>
    private static readonly HashSet<string> WritesToSource = new() { "repair.apply", "partition.restore", "undo.apply" };

    private static string Fail(string id, FriendlyError e, string method) =>
        JsonSerializer.Serialize(new
        {
            id, ok = false, error = e.Message, advice = e.Advice, detail = e.Detail,
            sourceUntouched = !WritesToSource.Contains(method),
        }, JsonOptions);
}
