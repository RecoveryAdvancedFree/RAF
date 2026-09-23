using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>
/// שאילתות מאפיינים על התקן אחסון פתוח, באמצעות IOCTL_STORAGE_QUERY_PROPERTY.
/// כאן מתבצע הזיהוי המבחין בין HDD ל-SSD ובין NVMe ל-USB.
/// </summary>
internal static class StorageQuery
{
    /// <summary>תיאור ההתקן: יצרן, דגם, מספר סידורי וסוג הפס.</summary>
    internal readonly record struct DeviceDescriptor(
        string Vendor, string Product, string Serial,
        Win32.StorageBusType BusType, bool RemovableMedia);

    /// <summary>ביצוע שאילתת מאפיין וקבלת מאגר הבתים הגולמי שהוחזר.</summary>
    private static byte[]? Query(IntPtr handle, Win32.StoragePropertyId propertyId, int bufferSize)
    {
        var query = new Win32.STORAGE_PROPERTY_QUERY
        {
            PropertyId = (uint)propertyId,
            QueryType = 0, // PropertyStandardQuery
        };

        int querySize = Marshal.SizeOf<Win32.STORAGE_PROPERTY_QUERY>();
        IntPtr inBuf = Marshal.AllocHGlobal(querySize);
        IntPtr outBuf = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.StructureToPtr(query, inBuf, false);
            // איפוס מאגר הפלט — חלק מהמנהלים אינם ממלאים את כולו.
            for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(outBuf, i, 0);

            bool ok = Win32.DeviceIoControl(
                handle, Win32.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, (uint)querySize, outBuf, (uint)bufferSize,
                out uint returned, IntPtr.Zero);

            if (!ok || returned == 0) return null;

            byte[] result = new byte[returned];
            Marshal.Copy(outBuf, result, 0, (int)returned);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>קריאת STORAGE_DEVICE_DESCRIPTOR — דגם, יצרן, סידורי וסוג פס.</summary>
    internal static DeviceDescriptor? GetDeviceDescriptor(IntPtr handle)
    {
        byte[]? buf = Query(handle, Win32.StoragePropertyId.StorageDeviceProperty, 4096);
        if (buf is null || buf.Length < 40) return null;

        // מבנה STORAGE_DEVICE_DESCRIPTOR: היסטים קבועים לפי הגדרת ה-SDK.
        bool removable = buf[10] != 0;
        uint vendorOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12));
        uint productOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16));
        uint serialOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(24));
        uint busType = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(28));

        return new DeviceDescriptor(
            ReadAnsiAt(buf, vendorOffset),
            ReadAnsiAt(buf, productOffset),
            ReadAnsiAt(buf, serialOffset),
            (Win32.StorageBusType)(byte)busType,
            removable);
    }

    /// <summary>
    /// השאלה המכרעת: האם להתקן יש "עונש seek"?
    /// מנגנון מכני מסתובב מחזיר true, זיכרון פלאש מחזיר false.
    /// </summary>
    internal static bool? GetIncursSeekPenalty(IntPtr handle)
    {
        byte[]? buf = Query(handle, Win32.StoragePropertyId.StorageDeviceSeekPenaltyProperty, 64);
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version(4) Size(4) IncursSeekPenalty(1)
        if (buf is null || buf.Length < 9) return null;
        return buf[8] != 0;
    }

    /// <summary>האם פקודת TRIM פעילה — קובע דרמטית את סיכויי השחזור ב-SSD.</summary>
    internal static TrimState GetTrimState(IntPtr handle)
    {
        byte[]? buf = Query(handle, Win32.StoragePropertyId.StorageDeviceTrimProperty, 64);
        // DEVICE_TRIM_DESCRIPTOR: Version(4) Size(4) TrimEnabled(1)
        if (buf is null || buf.Length < 9) return TrimState.Unknown;
        return buf[8] != 0 ? TrimState.Enabled : TrimState.NotSupported;
    }

    /// <summary>גדלי סקטור לוגי ופיזי. חיוני ליישור קריאות גולמיות.</summary>
    internal static (int Logical, int Physical)? GetSectorSizes(IntPtr handle)
    {
        byte[]? buf = Query(handle, Win32.StoragePropertyId.StorageAccessAlignmentProperty, 64);
        // STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR:
        // Version(4) Size(4) BytesPerCacheLine(4) BytesOffsetForCacheAlignment(4)
        // BytesPerLogicalSector(4) BytesPerPhysicalSector(4)
        if (buf is null || buf.Length < 24) return null;

        int logical = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16));
        int physical = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20));
        if (logical <= 0) logical = 512;
        if (physical <= 0) physical = logical;
        return (logical, physical);
    }

    /// <summary>גודל הדיסק הכולל בבתים, דרך IOCTL_DISK_GET_DRIVE_GEOMETRY_EX.</summary>
    internal static long GetDiskSize(IntPtr handle)
    {
        const int size = 64;
        IntPtr outBuf = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = Win32.DeviceIoControl(
                handle, Win32.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero, 0, outBuf, size, out uint returned, IntPtr.Zero);

            // DISK_GEOMETRY_EX: Geometry(24 בתים) ואחריו DiskSize כ-LARGE_INTEGER.
            if (!ok || returned < 32) return 0;

            byte[] buf = new byte[returned];
            Marshal.Copy(outBuf, buf, 0, (int)returned);
            return BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24));
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>סיווג סוג המדיה על בסיס כל הנתונים שנאספו.</summary>
    internal static MediaKind Classify(
        Win32.StorageBusType bus, bool? seekPenalty, bool removable, string name = "")
    {
        // סוג הפס הוא האינדיקציה החזקה ביותר כשהוא חד-משמעי.
        switch (bus)
        {
            case Win32.StorageBusType.Nvme:
                return MediaKind.NvmeSsd;
            case Win32.StorageBusType.Sd:
            case Win32.StorageBusType.Mmc:
                return MediaKind.MemoryCard;
            case Win32.StorageBusType.Usb:
                // התקן USB עלול להיות דיסק חיצוני מגנטי ולא רק DiskOnKey.
                if (seekPenalty == true) return MediaKind.HardDisk;
                // כרטיס בקורא כרטיסים מדווח על פס USB; רק שם ההתקן מסגיר אותו.
                return IsCardReader(name) ? MediaKind.MemoryCard : MediaKind.UsbFlash;
            case Win32.StorageBusType.Atapi:
                return MediaKind.Optical;
            case Win32.StorageBusType.Virtual:
            case Win32.StorageBusType.FileBackedVirtual:
                return MediaKind.Virtual;
        }

        // אחרת — מנגנון ה-seek penalty הוא הקובע.
        return seekPenalty switch
        {
            true => MediaKind.HardDisk,
            false => MediaKind.Ssd,
            _ => removable ? MediaKind.UsbFlash : MediaKind.Unknown,
        };
    }

    /// <summary>
    /// שמות שקוראי כרטיסים מדווחים: "Generic- SD/MMC", "USB3.0 CRW -SD",
    /// "Multi-Card Reader" וכדומה. מילים שלמות בלבד — "SD" בתוך "SanDisk" אינו כרטיס.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CardReaderName = new(
        @"card|reader|compact ?flash|\bcrw\b|\b(micro)?sd(hc|xc)?\b|\bmmc\b|\bxd\b|\bcf\b|\bms(-pro)?\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool IsCardReader(string name) => CardReaderName.IsMatch(name);

    /// <summary>קריאת מחרוזת ANSI מסתיימת-אפס מתוך מאגר, לפי היסט.</summary>
    private static string ReadAnsiAt(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length) return "";
        int end = (int)offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
    }
}
