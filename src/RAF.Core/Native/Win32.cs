using System.Runtime.InteropServices;

namespace RAF.Core.Native;

/// <summary>
/// עטיפות P/Invoke ל-Windows API. כל הגישה לחומרה בתוכנה עוברת דרך כאן,
/// כדי ששאר הקוד יישאר מנוהל ונקי.
/// </summary>
internal static partial class Win32
{
    // ---------- דגלי CreateFile ----------
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    internal const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---------- קודי IOCTL ----------
    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    private const uint FILE_DEVICE_DISK = 0x00000007;
    private const uint FILE_DEVICE_MASS_STORAGE = 0x0000002d;
    private const uint FILE_DEVICE_VOLUME = 0x00000056;
    private const uint FILE_DEVICE_FILE_SYSTEM = 0x00000009;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    internal static readonly uint IOCTL_STORAGE_QUERY_PROPERTY =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS);   // 0x2D1400
    internal static readonly uint IOCTL_STORAGE_GET_DEVICE_NUMBER =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS);   // 0x2D1080
    internal static readonly uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX =
        CtlCode(FILE_DEVICE_DISK, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS);           // 0x700A0
    internal static readonly uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS =
        CtlCode(FILE_DEVICE_VOLUME, 0x0000, METHOD_BUFFERED, FILE_ANY_ACCESS);         // 0x560000

    // בריאות הכונן: תחזית הכשל של Windows, ונתוני SMART של כונני ATA/SATA.
    internal static readonly uint IOCTL_STORAGE_PREDICT_FAILURE =
        CtlCode(FILE_DEVICE_MASS_STORAGE, 0x0440, METHOD_BUFFERED, FILE_ANY_ACCESS);   // 0x2D1100
    internal static readonly uint SMART_RCV_DRIVE_DATA =
        CtlCode(FILE_DEVICE_DISK, 0x0022, METHOD_BUFFERED, 3);                         // 0x7C088

    // בקשה מ-Windows לקרוא מחדש את טבלת המחיצות אחרי שינוי בה.
    internal static readonly uint IOCTL_DISK_UPDATE_PROPERTIES =
        CtlCode(FILE_DEVICE_DISK, 0x0050, METHOD_BUFFERED, FILE_ANY_ACCESS);           // 0x70140

    // נעילה וניתוק של אמצעי אחסון — תנאי לכתיבה גולמית לסקטורים שלו.
    internal static readonly uint FSCTL_LOCK_VOLUME =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 6, METHOD_BUFFERED, FILE_ANY_ACCESS);         // 0x90018
    internal static readonly uint FSCTL_UNLOCK_VOLUME =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 7, METHOD_BUFFERED, FILE_ANY_ACCESS);         // 0x9001C
    internal static readonly uint FSCTL_DISMOUNT_VOLUME =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 8, METHOD_BUFFERED, FILE_ANY_ACCESS);         // 0x90020

    // ---------- STORAGE_PROPERTY_ID ----------
    internal enum StoragePropertyId : uint
    {
        StorageDeviceProperty = 0,
        StorageAdapterProperty = 1,
        StorageAccessAlignmentProperty = 6,
        StorageDeviceSeekPenaltyProperty = 7,
        StorageDeviceTrimProperty = 8,
        StorageDeviceProtocolSpecificProperty = 50,
    }

    /// <summary>סוג הפס (Bus) שאליו הדיסק מחובר — נדרש לזיהוי NVMe / USB / SATA.</summary>
    internal enum StorageBusType : byte
    {
        Unknown = 0x00, Scsi = 0x01, Atapi = 0x02, Ata = 0x03, Ieee1394 = 0x04,
        Ssa = 0x05, Fibre = 0x06, Usb = 0x07, RAID = 0x08, iScsi = 0x09,
        Sas = 0x0A, Sata = 0x0B, Sd = 0x0C, Mmc = 0x0D, Virtual = 0x0E,
        FileBackedVirtual = 0x0F, Spaces = 0x10, Nvme = 0x11, Scm = 0x12, Ufs = 0x13,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
        private byte _pad0, _pad1, _pad2;
    }

    // ---------- ייבוא פונקציות ----------
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(
        IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(
        IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(IntPtr hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFilePointerEx(
        IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    // ---------- מניית אמצעי אחסון לוגיים ----------
    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr FindFirstVolume([Out] char[] lpszVolumeName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolume(IntPtr hFindVolume, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(IntPtr hFindVolume);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName, [Out] char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string lpRootPathName, [Out] char[] lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags, [Out] char[] lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string lpRootPathName);

    internal const uint DRIVE_REMOVABLE = 2;
    internal const uint DRIVE_FIXED = 3;
    internal const uint DRIVE_REMOTE = 4;
    internal const uint DRIVE_CDROM = 5;
    internal const uint DRIVE_RAMDISK = 6;
}
