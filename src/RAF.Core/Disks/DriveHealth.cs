using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RAF.Core.Native;

namespace RAF.Core.Disks;

public enum HealthLevel { Unknown, Good, Caution, Bad }

/// <summary>
/// בריאות הכונן לפי מה שהוא מדווח על עצמו (SMART). כונן שמתחיל להיכשל עלול
/// להחמיר עם כל קריאה — ואז עדיף להעתיק אותו לקובץ תמונה פעם אחת, ולסרוק מהתמונה.
/// </summary>
public sealed class DriveHealth
{
    public HealthLevel Level { get; init; }

    /// <summary>מקור הנתונים: "NVMe", "SMART" או "Windows" (תחזית הכשל בלבד).</summary>
    public string Source { get; init; } = "";

    public int? TemperatureC { get; init; }
    public long? PowerOnHours { get; init; }

    /// <summary>NVMe: כמה מאורך החיים המתוכנן נוצל, באחוזים (יכול לעבור את 100).</summary>
    public int? PercentUsed { get; init; }

    /// <summary>NVMe: שטח הרזרבה שנותר להחלפת תאים שנשחקו, באחוזים.</summary>
    public int? SpareLeft { get; init; }

    /// <summary>ATA: סקטורים שהכונן כבר החליף ברזרבה.</summary>
    public long? Reallocated { get; init; }

    /// <summary>ATA: סקטורים שאינם נקראים כרגע וממתינים להחלפה.</summary>
    public long? Pending { get; init; }

    /// <summary>ATA: סקטורים שלא ניתן היה לתקן.</summary>
    public long? Uncorrectable { get; init; }

    /// <summary>NVMe: שגיאות נתונים שהבקר לא הצליח לתקן.</summary>
    public long? MediaErrors { get; init; }

    /// <summary>מה שמצדיק אזהרה, במילים פשוטות.</summary>
    public List<string> Problems { get; init; } = new();

    public static readonly DriveHealth Unknown = new() { Level = HealthLevel.Unknown };

    // ---------- קריאה מהכונן ----------

    /// <summary>
    /// קריאת הבריאות של \\.\PhysicalDriveN. דיסק-און-קי וכרטיסים בדרך כלל אינם
    /// מדווחים דבר — Unknown. דורש הרשאות מנהל.
    /// </summary>
    public static DriveHealth Read(int diskNumber)
    {
        string path = $@"\\.\PhysicalDrive{diskNumber}";
        IntPtr handle = Win32.CreateFile(
            path, Win32.GENERIC_READ | Win32.GENERIC_WRITE, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero) return Unknown;

        try
        {
            if (ReadNvmeLog(handle) is { } log) return FromNvme(log);

            // ATA/SATA: טבלת התכונות וספי הכשל שלהן.
            if (SmartCommand(handle, 0xD0) is { } attributes)
                return FromAta(attributes, SmartCommand(handle, 0xD1), predictFailure: null);

            // מנהל ההתקן אינו מעביר פקודות SMART — תחזית הכשל של Windows, שלעתים כוללת את הטבלה.
            if (PredictFailure(handle) is { } prediction)
            {
                return prediction.Data is { } data && HasAttributes(data)
                    ? FromAta(data, thresholds: null, prediction.Failing)
                    : prediction.Failing
                        ? new DriveHealth
                        {
                            Level = HealthLevel.Bad, Source = "Windows",
                            Problems = { "Windows מדווח שהכונן צפוי להיכשל" },
                        }
                        : Unknown;
            }
            return Unknown;
        }
        catch
        {
            return Unknown;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// <summary>דף הבריאות של NVMe (Log Page 0x02), 512 בתים — דרך שאילתת מאפיין ייעודית לפרוטוקול.</summary>
    private static byte[]? ReadNvmeLog(IntPtr handle)
    {
        // STORAGE_PROPERTY_QUERY (8) ואחריה STORAGE_PROTOCOL_SPECIFIC_DATA (40) ומקום לנתונים.
        const int specific = 40, logSize = 512;
        int size = 8 + specific + logSize;
        var input = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0), (uint)Win32.StoragePropertyId.StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0);              // PropertyStandardQuery
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 3);              // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 2);             // NVMeDataTypeLogPage
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), 2);             // SMART / Health Information
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(24), specific);      // ProtocolDataOffset
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(28), logSize);       // ProtocolDataLength

        var output = Ioctl(handle, Win32.IOCTL_STORAGE_QUERY_PROPERTY, input, size);
        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version(4) Size(4) ואחריהם אותו מבנה — הנתונים ב-8+40.
        if (output is null || output.Length < 8 + specific + logSize) return null;
        return output.AsSpan(8 + specific, logSize).ToArray();
    }

    /// <summary>פקודת SMART של ATA (READ DATA = 0xD0, READ THRESHOLDS = 0xD1) — 512 הבתים שחזרו.</summary>
    private static byte[]? SmartCommand(IntPtr handle, byte feature)
    {
        // SENDCMDINPARAMS: cBufferSize(4) IDEREGS(8) bDriveNumber(1) bReserved(3) dwReserved(16) bBuffer(1)
        var input = new byte[33];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0), 512);
        input[4] = feature;        // bFeaturesReg
        input[5] = 1;              // bSectorCountReg
        input[6] = 1;              // bSectorNumberReg
        input[7] = 0x4F;           // bCylLowReg — חתימת SMART
        input[8] = 0xC2;           // bCylHighReg
        input[9] = 0xA0;           // bDriveHeadReg
        input[10] = 0xB0;          // SMART_CMD

        // SENDCMDOUTPARAMS: cBufferSize(4) DRIVERSTATUS(12) ואחריהם 512 בתי הנתונים.
        var output = Ioctl(handle, Win32.SMART_RCV_DRIVE_DATA, input, 16 + 512);
        if (output is null || output.Length < 16 + 512 || output[4] != 0) return null;
        var data = output.AsSpan(16, 512).ToArray();
        return feature == 0xD0 && !HasAttributes(data) ? null : data;
    }

    /// <summary>STORAGE_PREDICT_FAILURE: PredictFailure(4) ואחריו 512 בתים של נתוני היצרן.</summary>
    private static (bool Failing, byte[]? Data)? PredictFailure(IntPtr handle)
    {
        var output = Ioctl(handle, Win32.IOCTL_STORAGE_PREDICT_FAILURE, Array.Empty<byte>(), 4 + 512);
        if (output is null || output.Length < 4) return null;
        bool failing = BinaryPrimitives.ReadUInt32LittleEndian(output) != 0;
        return (failing, output.Length >= 4 + 512 ? output.AsSpan(4, 512).ToArray() : null);
    }

    private static byte[]? Ioctl(IntPtr handle, uint code, byte[] input, int outputSize)
    {
        IntPtr inBuf = input.Length > 0 ? Marshal.AllocHGlobal(input.Length) : IntPtr.Zero;
        IntPtr outBuf = Marshal.AllocHGlobal(outputSize);
        try
        {
            if (input.Length > 0) Marshal.Copy(input, 0, inBuf, input.Length);
            Marshal.Copy(new byte[outputSize], 0, outBuf, outputSize);
            if (!Win32.DeviceIoControl(handle, code, inBuf, (uint)input.Length, outBuf, (uint)outputSize,
                    out uint returned, IntPtr.Zero) || returned == 0)
                return null;
            var result = new byte[returned];
            Marshal.Copy(outBuf, result, 0, (int)returned);
            return result;
        }
        finally
        {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    // ---------- פענוח ----------

    /// <summary>דף הבריאות של NVMe (לפי תקן NVMe, Log Page 0x02).</summary>
    internal static DriveHealth FromNvme(ReadOnlySpan<byte> log)
    {
        byte critical = log[0];
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        int spare = log[3], spareThreshold = log[4], used = log[5];
        long powerOnHours = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(log[128..]), long.MaxValue);
        long mediaErrors = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(log[160..]), long.MaxValue);

        var problems = new List<string>();
        var level = HealthLevel.Good;
        void Flag(HealthLevel l, string text) { problems.Add(text); if (l > level) level = l; }

        if ((critical & 0x01) != 0 || (spareThreshold > 0 && spare < spareThreshold))
            Flag(HealthLevel.Bad, $"שטח הרזרבה להחלפת תאים שנשחקו כמעט נגמר ({spare}%)");
        if ((critical & 0x04) != 0)
            Flag(HealthLevel.Bad, "הכונן מדווח שהאמינות שלו נפגעה");
        if ((critical & 0x08) != 0)
            Flag(HealthLevel.Bad, "הכונן עבר למצב קריאה בלבד — כך כוננים מגינים על עצמם לפני כשל");
        if ((critical & 0x02) != 0)
            Flag(HealthLevel.Caution, "הכונן מדווח על טמפרטורה חריגה");
        if (mediaErrors > 0)
            Flag(HealthLevel.Caution, $"{mediaErrors:N0} שגיאות נתונים שלא תוקנו");
        if (used >= 100)
            Flag(HealthLevel.Caution, $"הכונן עבר את אורך החיים המתוכנן ({used}% נוצלו)");
        else if (used >= 90)
            Flag(HealthLevel.Caution, $"הכונן קרוב לסוף אורך החיים המתוכנן ({used}% נוצלו)");

        return new DriveHealth
        {
            Level = level,
            Source = "NVMe",
            TemperatureC = kelvin > 200 ? kelvin - 273 : null,
            PowerOnHours = powerOnHours,
            PercentUsed = used,
            SpareLeft = spare,
            MediaErrors = mediaErrors,
            Problems = problems,
        };
    }

    /// <summary>טבלת תכונות SMART: גרסה (2) ואחריה 30 רשומות של 12 בתים.</summary>
    private static bool HasAttributes(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < 30; i++)
            if (data[2 + i * 12] != 0) return true;
        return false;
    }

    /// <summary>
    /// תכונות SMART של ATA. כל רשומה: מזהה(1) דגלים(2) ערך נוכחי(1) הגרוע ביותר(1) גולמי(6) שמור(1).
    /// ערך נוכחי שירד אל הסף שהיצרן קבע — היצרן עצמו מצהיר שהכונן נכשל.
    /// </summary>
    internal static DriveHealth FromAta(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> thresholds, bool? predictFailure)
    {
        var limits = new Dictionary<byte, byte>();
        if (thresholds.Length >= 2 + 30 * 12)
            for (int i = 0; i < 30; i++)
            {
                var t = thresholds.Slice(2 + i * 12, 12);
                if (t[0] != 0) limits[t[0]] = t[1];
            }

        long? reallocated = null, pending = null, uncorrectable = null, hours = null, reported = null;
        int? temperature = null;
        bool belowThreshold = false;

        for (int i = 0; i < 30; i++)
        {
            var a = attributes.Slice(2 + i * 12, 12);
            byte id = a[0];
            if (id == 0) continue;
            byte current = a[3];
            long raw = (long)((BinaryPrimitives.ReadUInt64LittleEndian(a.Slice(4, 8)) >> 8) & 0xFFFF_FFFF_FFFF);

            if (limits.TryGetValue(id, out byte limit) && limit > 0 && current > 0 && current <= limit)
                belowThreshold = true;

            switch (id)
            {
                case 5: reallocated = raw & 0xFFFF_FFFF; break;
                case 9: hours = raw & 0xFFFF_FFFF; break;
                case 187: reported = raw & 0xFFFF; break;
                case 194: temperature = (int)(raw & 0xFF); break;
                case 197: pending = raw & 0xFFFF_FFFF; break;
                case 198: uncorrectable = raw & 0xFFFF_FFFF; break;
            }
        }

        var problems = new List<string>();
        var level = HealthLevel.Good;
        void Flag(HealthLevel l, string text) { problems.Add(text); if (l > level) level = l; }

        if (predictFailure == true || belowThreshold)
            Flag(HealthLevel.Bad, "הכונן עצמו מדווח שהוא צפוי להיכשל");
        if (pending > 0)
            Flag(HealthLevel.Bad, $"{pending:N0} סקטורים שאינם נקראים כרגע");
        if (uncorrectable > 0)
            Flag(HealthLevel.Bad, $"{uncorrectable:N0} סקטורים שלא ניתן היה לתקן");
        if (reallocated > 0)
            Flag(HealthLevel.Caution, $"{reallocated:N0} סקטורים פגומים כבר הוחלפו ברזרבה");
        if (reported > 0)
            Flag(HealthLevel.Caution, $"{reported:N0} שגיאות קריאה שדווחו למחשב");

        return new DriveHealth
        {
            Level = level,
            Source = "SMART",
            TemperatureC = temperature is > 0 and < 100 ? temperature : null,
            PowerOnHours = hours,
            Reallocated = reallocated,
            Pending = pending,
            Uncorrectable = uncorrectable,
            Problems = problems,
        };
    }
}
