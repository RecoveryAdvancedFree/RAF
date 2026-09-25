<#
.SYNOPSIS
    Pin or unpin shortcuts from the Windows taskbar programmatically.
    Version 1.5
.DESCRIPTION
    Pins or unpins items to/from the Windows taskbar across all Windows versions.

    PIN strategy :
      - Modern (Win7+) : writes a binary blob entry with a BEEF001D extension block directly
        into the Taskband registry (the only reliable method on Windows 11 where COM pin APIs
        are stubbed out), then notifies the taskbar by posting 0x446 to its pinned-items band.
      - Legacy (Vista) : copies a .lnk shortcut into the Quick Launch directory.

    UNPIN strategy :
      - Removes matching entries from the Taskband registry blob, deletes the .lnk files,
        and notifies the taskbar. Falls back to Quick Launch deletion on Vista.

    When -AllUsers is specified, the script loads each user's offline registry hive
    (NTUSER.DAT) and replicates the operation across all profiles.
.PARAMETER Pin
    Path to .lnk/.exe/.msc/.cpl, directory, bare application name, or shell:AppsFolder
    identifier. Supports semicolons and wildcards. A doubled semicolon ';;' escapes a
    literal semicolon inside an item (some AUMIDs contain one). A bare application name
    resolves to a single application : exact display-name match first, otherwise an
    elimination cascade narrows the substring matches down to one. An explicit wildcard
    matches display names and AUMIDs and pins every match.
.PARAMETER Unpin
    Triggers unpin mode. -Pin becomes a match pattern.
.PARAMETER Silent
    Suppresses all console output. Log file output is not affected.
.PARAMETER LogFile
    Path to a log file. Must end with .txt or .log.
.PARAMETER AllUsers
    Applies pin/unpin to all user profiles (requires elevation).
.EXAMPLE
    .\Pin-Taskbar "C:\Users\John\Desktop\MyApp.lnk"
    .\Pin-Taskbar "C:\Windows\regedit.exe;C:\MyFolder" -AllUsers
    .\Pin-Taskbar "shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
    .\Pin-Taskbar "uwp:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
    .\Pin-Taskbar "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
    .\Pin-Taskbar "C:\App1.lnk;C:\App2.lnk;C:\App3.exe"
    .\Pin-Taskbar "notepad" -logfile C:\Windows\Temp\TaskbarPin.log
    .\Pin-Taskbar "C:\Tools\*.exe"
    .\Pin-Taskbar -Unpin "Notepad*" -AllUsers
    .\Pin-Taskbar "C:\MyApp.lnk" -LogFile "C:\Temp\pin.log" -Silent
    .\Pin-Taskbar "C:\Windows\System32\services.msc;C:\Windows\System32\main.cpl"
.NOTES
    Exit codes : 0 = Success, 2 = Nothing found to pin/unpin, 3 = Fail to pin/unpin.
    Compatible with PowerShell 2.0+ (.NET 2.0+).
#>
param(
    [Parameter(Position = 0)]
    [Alias('Path', 'File', 'Files')][string]$Pin,
    [Alias('Remove')]               [switch]$Unpin,
    [Alias('S')]                    [switch]$Silent,
    [Alias('Log')]                  [string]$LogFile,
    [Alias('Everyone', 'All')]      [switch]$AllUsers
)
$ErrorActionPreference = 'Stop'
if ($LogFile -and -not ($LogFile.EndsWith('.txt') -or $LogFile.EndsWith('.log'))) {
    if (-not $Silent) { Write-Host "ERROR : -LogFile must end with .txt or .log" -ForegroundColor Red }
    exit 3
}


#region ENVIRONMENT

# Returns $true when the given subkey exists under the given registry root.
function Test-RegistrySubKeyExists {
    param($RegistryRootKey, [string]$RegistrySubKeyPath)
    $ProbeHandle = $null
    try { $ProbeHandle = $RegistryRootKey.OpenSubKey($RegistrySubKeyPath, $false) } catch { }
    if ($ProbeHandle) { $ProbeHandle.Close(); return $true }
    return $false
}

# Filesystem and registry locations where Windows stores the taskbar pin state.
# The Favorites binary value of the Taskband key holds the serialized PIDL list of all
# pinned items; writing that blob and notifying the shell is how the modern pin works.
$TaskBarRelativeProfilePath = 'AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$QuickLaunchRelativePath    = 'AppData\Roaming\Microsoft\Internet Explorer\Quick Launch'
$TaskBarPinnedDirectory     = [IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'), 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar')
$QuickLaunchDirectory       = [IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'), 'Microsoft\Internet Explorer\Quick Launch')
$TaskBandRegistrySubKey     = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband'
# Registry value type constants used throughout blob read/write operations.
$DoNotExpandRegistryOption  = [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames
$BinaryRegistryValueKind    = [Microsoft.Win32.RegistryValueKind]::Binary
$DwordRegistryValueKind     = [Microsoft.Win32.RegistryValueKind]::DWord
$WindowsBuildNumber = 0
try { $WindowsBuildNumber = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber } catch { }
$CurrentUserSecurityIdentifier = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value

# Cross-user elevation detection : when the script is launched via RunAs under a different
# account, HKCU and %APPDATA% resolve to the elevated identity rather than the interactive
# session user. The interactive user is identified by matching the current session ID
# against the per-session Volatile Environment subkeys created in HKU at interactive logon.
$IsRunningCrossUser         = $false
$InteractiveSessionUserSID  = $null
$InteractiveUserProfilePath = $null
$EffectivePrimaryUserSID    = $CurrentUserSecurityIdentifier
$CurrentProcessSessionId    = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
foreach ($CandidateSidKeyName in [Microsoft.Win32.Registry]::Users.GetSubKeyNames()) {
    if ($CandidateSidKeyName.Length -lt 20 -or $CandidateSidKeyName.EndsWith('_Classes')) { continue }
    if (Test-RegistrySubKeyExists ([Microsoft.Win32.Registry]::Users) "$CandidateSidKeyName\Volatile Environment\$CurrentProcessSessionId") { $InteractiveSessionUserSID = $CandidateSidKeyName; break }
}
if ($InteractiveSessionUserSID -and $InteractiveSessionUserSID -ne $CurrentUserSecurityIdentifier) {
    $IsRunningCrossUser      = $true
    $EffectivePrimaryUserSID = $InteractiveSessionUserSID
    $ProfileListKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$InteractiveSessionUserSID")
    if ($ProfileListKey) {
        $InteractiveUserProfilePath = $ProfileListKey.GetValue('ProfileImagePath', '')
        $ProfileListKey.Close()
        $TaskBarPinnedDirectory = [IO.Path]::Combine($InteractiveUserProfilePath, $TaskBarRelativeProfilePath)
        $QuickLaunchDirectory   = [IO.Path]::Combine($InteractiveUserProfilePath, $QuickLaunchRelativePath)
    }
}

# Probe which pin strategies are available : modern blob injection requires both the TaskBar
# shortcut directory (to place .lnk files) and the Taskband registry key (to write the
# Favorites blob); Quick Launch alone only allows the legacy Vista strategy.
$TaskBarDirectoryExists     = [IO.Directory]::Exists($TaskBarPinnedDirectory)
$QuickLaunchDirectoryExists = [IO.Directory]::Exists($QuickLaunchDirectory)
$TaskBandRegistryKeyExists  = if ($IsRunningCrossUser) { Test-RegistrySubKeyExists ([Microsoft.Win32.Registry]::Users) "$EffectivePrimaryUserSID\$TaskBandRegistrySubKey" } else { Test-RegistrySubKeyExists ([Microsoft.Win32.Registry]::CurrentUser) $TaskBandRegistrySubKey }
$DirectBlobWriteIsSupported = $TaskBarDirectoryExists -and $TaskBandRegistryKeyExists


#region LOGGING

# Three output channels : Write-Log writes to the log file (if open) and to the console
# unless -Silent; Write-Console writes to the console only; Write-Banner writes colored
# status banners (PIN/UNPIN/OK/FAIL) to both. SuppressLogToConsole prevents log lines from
# tearing apart a pending Write-Console -NoNewline progress line.
$LogFileStreamWriter = $null
$script:SuppressLogToConsole = $false
if ($LogFile) {
    $LogFileParentDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $LogFile)))
    if ($LogFileParentDirectory -and -not [IO.Directory]::Exists($LogFileParentDirectory)) { $null = [IO.Directory]::CreateDirectory($LogFileParentDirectory) }
    $LogFileStreamWriter = New-Object System.IO.StreamWriter($LogFile, $false, [System.Text.Encoding]::UTF8)
    $LogFileStreamWriter.AutoFlush = $true
}

function Write-Log {
    param([string]$Message, [string]$Color = 'Gray')
    $FormattedLogLine = "[$([DateTime]::Now.ToString('HH:mm:ss.fff'))] $Message"
    if (-not $Silent -and -not $script:SuppressLogToConsole) { Write-Host $FormattedLogLine -ForegroundColor $Color }
    if ($LogFileStreamWriter) { $LogFileStreamWriter.WriteLine($FormattedLogLine) }
}

function Write-Console {
    param([string]$Message, [string]$Color = 'White', [switch]$NoNewline, [string]$BackgroundColor)
    if ($Silent) { return }
    $WriteHostParams = @{ Object = $Message; ForegroundColor = $Color }
    if ($NoNewline)       { $WriteHostParams['NoNewline'] = $true }
    if ($BackgroundColor) { $WriteHostParams['BackgroundColor'] = $BackgroundColor }
    Write-Host @WriteHostParams
    $script:SuppressLogToConsole = [bool]$NoNewline
}

function Write-Banner {
    param([string]$Label, [string]$LabelBackground, [string]$Detail)
    if (-not $Silent) {
        Write-Host ""
        Write-Host "  $Label  " -ForegroundColor White -BackgroundColor $LabelBackground -NoNewline
        Write-Host "  $Detail"
        Write-Host ""
    }
    if ($LogFileStreamWriter) { $LogFileStreamWriter.WriteLine("[$([DateTime]::Now.ToString('HH:mm:ss.fff'))] === $Label : $Detail ===") }
}

function Close-Log { if ($LogFileStreamWriter) { $LogFileStreamWriter.Close() } }
trap { Close-Log; break }

# Logs the operation header lines shared by the PIN and UNPIN flows.
function Write-OperationLogHeader {
    param([string]$OperationName, [string]$InputDetail)
    Write-Log "--- $OperationName operation starting ---"
    Write-Log "Input : $InputDetail"
    Write-Log "AllUsers : $AllUsers | Windows build : $WindowsBuildNumber | Blob injection available : $DirectBlobWriteIsSupported"
    Write-Log "TaskBar directory : $TaskBarPinnedDirectory (exists : $TaskBarDirectoryExists)"
    Write-Log "QuickLaunch directory : $QuickLaunchDirectory (exists : $QuickLaunchDirectoryExists)"
    Write-Log "TaskBand registry key : $TaskBandRegistrySubKey (exists : $TaskBandRegistryKeyExists)"
    if ($IsRunningCrossUser) { Write-Log "Cross-user elevation : True (interactive SID : $InteractiveSessionUserSID, profile : $InteractiveUserProfilePath)" }
}


#region INPUT VALIDATION

# Split semicolon-delimited input into items; ';;' escapes a literal semicolon (some AUMIDs
# and filesystem paths legitimately contain one), materialized through an unprintable
# sentinel character that cannot appear in any path or AUMID. Then normalize UWP forms :
# 'uwp:' prefixes and bare AUMIDs (containing !) become canonical 'shell:AppsFolder\' items.
$LiteralSemicolonSentinel = [string][char]1
$ParsedInputItems = @("$Pin".Replace(';;', $LiteralSemicolonSentinel) -split ';' | ForEach-Object { $_.Replace($LiteralSemicolonSentinel, ';').Trim() } | Where-Object { $_ })
if ($ParsedInputItems.Count -eq 0) {
    Write-Log "ERROR : Specify -Pin" -Color Red
    Close-Log; exit 3
}
$ParsedInputItems = @($ParsedInputItems | ForEach-Object {
    if     ($_.StartsWith('uwp:', [StringComparison]::OrdinalIgnoreCase))              { 'shell:AppsFolder\' + $_.Substring(4) }
    elseif ($_.StartsWith('shell:AppsFolder\', [StringComparison]::OrdinalIgnoreCase)) { 'shell:AppsFolder\' + $_.Substring(17) }
    elseif ($_ -match '!' -and $_ -notmatch '[/\\]')                                   { 'shell:AppsFolder\' + $_ }
    else                                                                               { $_ }
})


#region ELEVATION CHECK

function Test-IsAdmin {
    $CurrentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $CurrentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if ($AllUsers -and -not (Test-IsAdmin)) {
    Write-Log "ERROR : -AllUsers requires elevation (run as Administrator)" -Color Red
    Close-Log; exit 3
}


#region NATIVE HELPER

# Compiles the C# interop class providing what pure PowerShell cannot do (or not quickly) :
#   GetBlobEntryEx/Fs   : builds a Favorites blob entry from a .lnk (namespace or filesystem
#                         PIDL) with a BEEF001D extension block injected in the last SHITEMID.
#   FindBlobEntry, RemoveFavEntry, RemoveResEntry : Favorites/FavoritesResolve blob parsing.
#   SendPinNotify       : posts 0x446 to the taskbar pinned-items band to refresh its pins.
#   Acquire/ReleasePinMutex : serializes blob writes with explorer.exe (TaskbarPinListMutex).
#   CreateAppShortcut, CreatePidlShortcut : .lnk creation with PKEY_AppUserModel_ID through
#                         raw COM vtable calls (IShellLink, IPropertyStore, IPersistFile).
#   GetAumid            : reads the AUMID property from an existing .lnk via COM.
#   GetLnkCatalog       : enumerates a directory (junction-safe manual recursion) and parses
#                         every .lnk binary natively, returning ready-to-use LnkEntry objects
#                         (path, name, target, AUMID, icon location).
#   GetIconResourceCount: counts the icon resources of a file (0 = generic shell icon).
function Initialize-NativeHelper {
    if ('TaskbarPin' -as [Type]) { return }
    Write-Log "[init] Compiling C# native helper..."
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public class TaskbarPin {

    // Win32 imports : PIDL creation and inspection, shell display-name parsing, window
    // lookup for the taskbar notification, COM activation, PROPVARIANT cleanup, and the
    // named kernel mutex primitives used to serialize with explorer.exe.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ILCreateFromPathW(string pszPath);
    [DllImport("shell32.dll")] static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconExW(string lpszFile, int nIconIndex, IntPtr phiconLarge, IntPtr phiconSmall, uint nIcons);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnk, uint ctx, ref Guid riid, out IntPtr ppv);
    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pvar);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateMutexExW(IntPtr lpMutexAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReleaseMutex(IntPtr hMutex);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);

    // Delegates mapped onto raw COM vtable slots (IShellLink, IPropertyStore, IPersistFile),
    // called through pointer arithmetic because PowerShell 2.0 lacks modern COM wrappers.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnRelease(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnQueryInterface(IntPtr p, ref Guid riid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetIDList(IntPtr p, IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetValue(IntPtr p, IntPtr key, IntPtr propvar);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnCommitStore(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSaveFile(IntPtr p, IntPtr pszFileName, int fRemember);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnLoadFile(IntPtr p, IntPtr pszFileName, uint dwMode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGetValue(IntPtr p, IntPtr key, IntPtr propvar);

    static readonly Guid CLSID_ShellLink    = new Guid("00021401-0000-0000-C000-000000000046");
    static readonly Guid IID_IShellLinkW    = new Guid("000214F9-0000-0000-C000-000000000046");
    static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    static readonly Guid IID_IPersistFile   = new Guid("0000010B-0000-0000-C000-000000000046");
    static readonly Guid FMTID_AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    // Shell COM operations require an STA thread; PowerShell runs in MTA by default, so this
    // wrapper spawns a dedicated STA thread when needed, runs the operation there, and joins.
    delegate T StaFunc<T>();
    static T RunOnSTA<T>(StaFunc<T> fn) {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return fn();
        T result = default(T);
        Thread staThread = new Thread(delegate() { result = fn(); });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start(); staThread.Join();
        return result;
    }

    // Reads a delegate of type T from a COM vtable slot; Release calls IUnknown slot 2.
    static T Vtbl<T>(IntPtr vtbl, int slot) where T : class {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
    }
    static void Release(IntPtr ppv) { Vtbl<FnRelease>(Marshal.ReadIntPtr(ppv), 2)(ppv); }
    static void Release(IntPtr ppv, IntPtr vtbl) { Vtbl<FnRelease>(vtbl, 2)(ppv); }

    // SHParseDisplayName wrapper returning IntPtr.Zero on failure instead of an HRESULT.
    static IntPtr ParseDisplayName(string name) {
        IntPtr pidl; uint sfgao;
        if (SHParseDisplayName(name, IntPtr.Zero, out pidl, 0, out sfgao) == 0) return pidl;
        return IntPtr.Zero;
    }

    // Allocates a PROPERTYKEY structure for PKEY_AppUserModel_ID (FMTID + PID 5).
    static IntPtr AllocPropertyKey() {
        byte[] propertyKeyBytes = new byte[20];
        Array.Copy(FMTID_AppUserModel.ToByteArray(), 0, propertyKeyBytes, 0, 16);
        propertyKeyBytes[16] = 5;
        IntPtr propertyKeyPtr = Marshal.AllocCoTaskMem(20);
        Marshal.Copy(propertyKeyBytes, 0, propertyKeyPtr, 20);
        return propertyKeyPtr;
    }

    // Writes an AUMID to an IShellLink's property store as VT_LPWSTR and commits the store.
    static bool WriteAumidToStore(IntPtr psl, IntPtr vtLink, string aumid) {
        Guid iid = IID_IPropertyStore; IntPtr pps;
        if (Vtbl<FnQueryInterface>(vtLink, 0)(psl, ref iid, out pps) != 0) return false;
        try {
            IntPtr pkPtr = AllocPropertyKey();
            IntPtr pvPtr = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pvPtr, i, 0);
            Marshal.WriteInt16(pvPtr, 0, 31);
            IntPtr strPtr = Marshal.StringToCoTaskMemUni(aumid);
            Marshal.WriteIntPtr(pvPtr, 8, strPtr);
            try {
                IntPtr vt = Marshal.ReadIntPtr(pps);
                Vtbl<FnSetValue>(vt, 6)(pps, pkPtr, pvPtr);
                Vtbl<FnCommitStore>(vt, 7)(pps);
            } finally { Marshal.FreeCoTaskMem(strPtr); Marshal.FreeCoTaskMem(pvPtr); Marshal.FreeCoTaskMem(pkPtr); }
        } finally { Release(pps); }
        return true;
    }

    // Saves an IShellLink to disk via IPersistFile::Save.
    static bool PersistSave(IntPtr psl, IntPtr vtLink, string lnkPath) {
        Guid iid = IID_IPersistFile; IntPtr ppf;
        if (Vtbl<FnQueryInterface>(vtLink, 0)(psl, ref iid, out ppf) != 0) return false;
        try {
            IntPtr pathPtr = Marshal.StringToCoTaskMemUni(lnkPath);
            try { Vtbl<FnSaveFile>(Marshal.ReadIntPtr(ppf), 6)(ppf, pathPtr, 1); }
            finally { Marshal.FreeCoTaskMem(pathPtr); }
        } finally { Release(ppf); }
        return true;
    }

    // Injects a BEEF001D extension block into a SHITEMID's extension chain. Blocks have an
    // 8-byte header [uint16 cb][uint16 version][uint32 signature] and the last 2 bytes of
    // the SHITEMID store the offset of the first block. BEEF001D payload : [uint16 type = 2]
    // followed by the null-terminated Unicode parsing name the taskbar handler resolves.
    static byte[] InjectBeef001D(byte[] item, string parsingName) {
        ushort cb = BitConverter.ToUInt16(item, 0);
        if (cb < 4) return null;
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(parsingName + "\0");
        int blockCb = 10 + nameBytes.Length;
        byte[] block = new byte[blockCb];
        Array.Copy(BitConverter.GetBytes((ushort)blockCb), 0, block, 0, 2);
        block[4] = 0x1D; block[6] = 0xEF; block[7] = 0xBE; block[8] = 0x02;
        Array.Copy(nameBytes, 0, block, 10, nameBytes.Length);
        ushort extOffset = BitConverter.ToUInt16(item, cb - 2);
        int insertPos;
        if (extOffset > 4 && extOffset < cb - 4) {
            int epos = extOffset;
            while (epos + 8 <= cb) {
                ushort ecb = BitConverter.ToUInt16(item, epos);
                if (ecb < 8 || epos + ecb > cb) break;
                if ((BitConverter.ToUInt32(item, epos + 4) & 0xFFFF0000) != 0xBEEF0000) break;
                epos += ecb;
            }
            insertPos = epos;
        } else { insertPos = cb - 2; extOffset = (ushort)insertPos; }
        int newCb = insertPos + blockCb + 2;
        byte[] result = new byte[newCb];
        Array.Copy(item, 0, result, 0, insertPos);
        Array.Copy(block, 0, result, insertPos, blockCb);
        Array.Copy(BitConverter.GetBytes(extOffset), 0, result, newCb - 2, 2);
        Array.Copy(BitConverter.GetBytes((ushort)newCb), 0, result, 0, 2);
        return result;
    }

    // Builds one Favorites blob entry from a PIDL : [1 byte category = 0x00 (Desktop root)]
    // [uint32 pidlSize][PIDL data with BEEF001D injected into the last SHITEMID].
    static byte[] BuildBlobEntry(IntPtr pidl, string beef001dContent) {
        IntPtr lastPtr = ILFindLastID(pidl);
        if (lastPtr == IntPtr.Zero) return null;
        int prefixLen = (int)((long)lastPtr - (long)pidl);
        ushort lastCb = (ushort)Marshal.ReadInt16(lastPtr);
        if (lastCb < 4) return null;
        byte[] lastItem = new byte[lastCb];
        Marshal.Copy(lastPtr, lastItem, 0, lastCb);
        byte[] patched = InjectBeef001D(lastItem, beef001dContent);
        if (patched == null) return null;
        int newPidlLen = prefixLen + patched.Length + 2;
        byte[] result = new byte[1 + 4 + newPidlLen];
        Array.Copy(BitConverter.GetBytes((uint)newPidlLen), 0, result, 1, 4);
        Marshal.Copy(pidl, result, 5, prefixLen);
        Array.Copy(patched, 0, result, 5 + prefixLen, patched.Length);
        return result;
    }

    // GetBlobEntryEx resolves the .lnk through SHParseDisplayName (namespace PIDLs natively
    // accepted by the taskbar handler); GetBlobEntryFs uses ILCreateFromPathW (filesystem
    // PIDLs) for paths under another user's profile that SHParseDisplayName cannot resolve.
    static byte[] GetBlobEntryInternal(string path, string beef001dContent, bool useFilesystem) {
        IntPtr pidl = useFilesystem ? ILCreateFromPathW(path) : ParseDisplayName(path);
        if (pidl == IntPtr.Zero) return null;
        try { return BuildBlobEntry(pidl, beef001dContent); } finally { ILFree(pidl); }
    }
    public static byte[] GetBlobEntryEx(string lnkFullPath, string beef001dContent) {
        return RunOnSTA<byte[]>(delegate() { return GetBlobEntryInternal(lnkFullPath, beef001dContent, false); });
    }
    public static byte[] GetBlobEntryFs(string lnkFullPath, string beef001dContent) {
        return RunOnSTA<byte[]>(delegate() { return GetBlobEntryInternal(lnkFullPath, beef001dContent, true); });
    }

    // Notifies the taskbar that the pinned-items list changed by posting 0x446 to its
    // pinned-items band (Shell_TrayWnd > ReBarWindow32 > MSTaskSwWClass).
    public static void SendPinNotify() {
        IntPtr reBarWindow = FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "ReBarWindow32", null);
        IntPtr pinnedItemsBand = FindWindowEx(reBarWindow, IntPtr.Zero, "MSTaskSwWClass", null);
        if (pinnedItemsBand != IntPtr.Zero) PostMessage(pinnedItemsBand, 0x446, IntPtr.Zero, IntPtr.Zero);
    }

    // TaskbarPinListMutex acquisition and release, serializing blob writes with explorer.exe.
    static IntPtr _mutexHandle = IntPtr.Zero;
    public static bool AcquirePinMutex(int timeoutMs) {
        IntPtr mutexHandle = CreateMutexExW(IntPtr.Zero, "TaskbarPinListMutex", 0, 0x001F0001);
        if (mutexHandle == IntPtr.Zero) return false;
        uint waitResult = WaitForSingleObject(mutexHandle, (uint)timeoutMs);
        if (waitResult == 0 || waitResult == 0x80) { _mutexHandle = mutexHandle; return true; }
        CloseHandle(mutexHandle);
        return false;
    }
    public static void ReleasePinMutex() {
        if (_mutexHandle == IntPtr.Zero) return;
        ReleaseMutex(_mutexHandle); CloseHandle(_mutexHandle); _mutexHandle = IntPtr.Zero;
    }

    // Searches the Favorites blob ([category][pidlSize][pidlData] entries, 0xFF terminator)
    // for an entry whose PIDL data contains the given Unicode filename. Returns the 0-based
    // entry index, or -1 when not found.
    public static int FindBlobEntry(byte[] blob, string filename) {
        byte[] needle = System.Text.Encoding.Unicode.GetBytes(filename);
        int pos = 0; int idx = 0;
        while (pos < blob.Length && blob[pos] != 0xFF) {
            if (pos + 5 > blob.Length) break;
            int pidlStart = pos + 5;
            int pidlEnd = pidlStart + (int)BitConverter.ToUInt32(blob, pos + 1);
            if (pidlEnd > blob.Length) break;
            for (int b = pidlStart; b + needle.Length <= pidlEnd; b++) {
                bool match = true;
                for (int c = 0; c < needle.Length; c++) { if (blob[b + c] != needle[c]) { match = false; break; } }
                if (match) return idx;
            }
            pos = pidlEnd; idx++;
        }
        return -1;
    }

    // Rebuilds the Favorites blob without the entry at removeIdx.
    public static byte[] RemoveFavEntry(byte[] blob, int removeIdx) {
        System.IO.MemoryStream ms = new System.IO.MemoryStream();
        int pos = 0; int idx = 0;
        while (pos < blob.Length && blob[pos] != 0xFF) {
            if (pos + 5 > blob.Length) break;
            int total = 5 + (int)BitConverter.ToUInt32(blob, pos + 1);
            if (pos + total > blob.Length) break;
            if (idx != removeIdx) ms.Write(blob, pos, total);
            pos += total; idx++;
        }
        ms.WriteByte(0xFF);
        return ms.ToArray();
    }

    // Rebuilds the FavoritesResolve blob ([uint32 linkSize][linkData] repeated, no
    // terminator) without the entry at removeIdx.
    public static byte[] RemoveResEntry(byte[] blob, int removeIdx) {
        System.IO.MemoryStream ms = new System.IO.MemoryStream();
        int pos = 0; int idx = 0;
        while (pos + 4 <= blob.Length) {
            uint linkSize = BitConverter.ToUInt32(blob, pos);
            if (linkSize == 0 || pos + 4 + (int)linkSize > blob.Length) break;
            if (idx != removeIdx) ms.Write(blob, pos, 4 + (int)linkSize);
            pos += 4 + (int)linkSize; idx++;
        }
        return ms.ToArray();
    }

    // Creates an IShellLink from a PIDL, optionally writes the AUMID property, saves to disk.
    static bool CreateShortcutFromPidl(IntPtr pidl, string lnkPath, string aumid) {
        Guid cls = CLSID_ShellLink; Guid iid = IID_IShellLinkW; IntPtr psl;
        if (CoCreateInstance(ref cls, IntPtr.Zero, 1, ref iid, out psl) != 0) return false;
        IntPtr vtLink = Marshal.ReadIntPtr(psl);
        try {
            Vtbl<FnSetIDList>(vtLink, 5)(psl, pidl);
            if (aumid != null && aumid.Length > 0) WriteAumidToStore(psl, vtLink, aumid);
            return PersistSave(psl, vtLink, lnkPath);
        } finally { Release(psl, vtLink); }
    }

    // Creates a .lnk from a shell display name (Control Panel item, shell:AppsFolder entry),
    // storing the given AppUserModelID on the shortcut. CreateAppShortcut is the UWP/MSIX
    // convenience form taking the AUMID directly.
    public static bool CreatePidlShortcut(string displayName, string lnkPath, string appUserModelId) {
        return RunOnSTA<bool>(delegate() {
            IntPtr pidl = ParseDisplayName(displayName);
            if (pidl == IntPtr.Zero) return false;
            try { return CreateShortcutFromPidl(pidl, lnkPath, appUserModelId); } finally { ILFree(pidl); }
        });
    }
    public static bool CreateAppShortcut(string aumid, string lnkPath) {
        return CreatePidlShortcut("shell:AppsFolder\\" + aumid, lnkPath, aumid);
    }

    // Reads PKEY_AppUserModel_ID from an existing .lnk through IPersistFile + IPropertyStore.
    public static string GetAumid(string lnkPath) {
        return RunOnSTA<string>(delegate() {
            Guid cls = CLSID_ShellLink; Guid iid = IID_IShellLinkW; IntPtr psl;
            if (CoCreateInstance(ref cls, IntPtr.Zero, 1, ref iid, out psl) != 0) return "";
            IntPtr vtLink = Marshal.ReadIntPtr(psl);
            try {
                FnQueryInterface qi = Vtbl<FnQueryInterface>(vtLink, 0);
                Guid iidFile = IID_IPersistFile; IntPtr ppf;
                if (qi(psl, ref iidFile, out ppf) != 0) return "";
                try {
                    IntPtr pathPtr = Marshal.StringToCoTaskMemUni(lnkPath);
                    try { if (Vtbl<FnLoadFile>(Marshal.ReadIntPtr(ppf), 5)(ppf, pathPtr, 0) != 0) return ""; }
                    finally { Marshal.FreeCoTaskMem(pathPtr); }
                } finally { Release(ppf); }
                Guid iidStore = IID_IPropertyStore; IntPtr pps;
                if (qi(psl, ref iidStore, out pps) != 0) return "";
                try {
                    IntPtr pkPtr = AllocPropertyKey();
                    IntPtr pvPtr = Marshal.AllocCoTaskMem(24);
                    for (int i = 0; i < 24; i++) Marshal.WriteByte(pvPtr, i, 0);
                    try {
                        if (Vtbl<FnGetValue>(Marshal.ReadIntPtr(pps), 5)(pps, pkPtr, pvPtr) != 0) return "";
                        if (Marshal.ReadInt16(pvPtr) != 31) return "";
                        IntPtr stringPtr = Marshal.ReadIntPtr(pvPtr, 8);
                        if (stringPtr == IntPtr.Zero) return "";
                        return Marshal.PtrToStringUni(stringPtr) ?? "";
                    } finally { PropVariantClear(pvPtr); Marshal.FreeCoTaskMem(pvPtr); Marshal.FreeCoTaskMem(pkPtr); }
                } finally { Release(pps); }
            } finally { Release(psl, vtLink); }
        });
    }

    // Reads a null-terminated ANSI string from a byte array.
    static string ReadAnsiZ(byte[] d, int pos) {
        int end = pos;
        while (end < d.Length && d[end] != 0) end++;
        return System.Text.Encoding.Default.GetString(d, pos, end - pos);
    }

    // Reads a null-terminated Unicode string from a byte array.
    static string ReadUniZ(byte[] d, int pos) {
        int end = pos;
        while (end + 1 < d.Length && (d[end] != 0 || d[end + 1] != 0)) end += 2;
        return System.Text.Encoding.Unicode.GetString(d, pos, end - pos);
    }

    // Walks the serialized property storages of a PropertyStoreDataBlock and returns the
    // PKEY_AppUserModel_ID value, or an empty string when the property is absent.
    // Each storage is : StorageSize(4), Version(4), FormatID(16 byte GUID), then serialized
    // property values : ValueSize(4), Id(4), Reserved(1), Type(2), Padding(2), data.
    // The AUMID is the VT_LPWSTR (0x1F) value with Id 5 under the AppUserModel FMTID.
    static string ReadStoreAumid(byte[] d, int pos, int end) {
        byte[] fmtid = FMTID_AppUserModel.ToByteArray();
        while (pos + 28 <= end) {
            uint storageSize = BitConverter.ToUInt32(d, pos);
            if (storageSize == 0 || pos + storageSize > end) break;
            bool fmtidMatch = true;
            for (int i = 0; i < 16; i++) { if (d[pos + 8 + i] != fmtid[i]) { fmtidMatch = false; break; } }
            if (fmtidMatch) {
                int vpos = pos + 24;
                int vend = pos + (int)storageSize;
                while (vpos + 13 <= vend) {
                    uint valueSize = BitConverter.ToUInt32(d, vpos);
                    if (valueSize == 0 || vpos + valueSize > vend) break;
                    uint propId = BitConverter.ToUInt32(d, vpos + 4);
                    ushort vt   = BitConverter.ToUInt16(d, vpos + 9);
                    if (propId == 5 && vt == 0x1F && vpos + 17 <= vend) return ReadUniZ(d, vpos + 17);
                    vpos += (int)valueSize;
                }
            }
            pos += (int)storageSize;
        }
        return "";
    }

    // Parses the Shell Link binary format in a single pass, without any COM round-trip,
    // extracting the filesystem target path, the explicit AppUserModelID and the icon
    // location. Target resolution order : the LinkInfo local base path, then the
    // EnvironmentVariableDataBlock (0xA0000001) expanded, then the RelativePath StringData
    // entry resolved against the .lnk location. The AUMID comes from the
    // PropertyStoreDataBlock (0xA0000009), which is how UWP shortcuts and identity-aware
    // desktop shortcuts carry their application identity. The icon location comes from the
    // IconLocation StringData entry, or the IconEnvironmentDataBlock (0xA0000007) fallback.
    static void ParseLnk(byte[] d, string lnkDirectory, out string target, out string aumid, out string iconPath) {
        target = ""; aumid = ""; iconPath = "";
        if (d.Length < 0x4C || BitConverter.ToInt32(d, 0) != 0x4C) return;
        uint flags = BitConverter.ToUInt32(d, 20);
        int pos = 0x4C;
        if ((flags & 0x01) != 0) {                          // HasLinkTargetIDList
            if (pos + 2 > d.Length) return;
            pos += 2 + BitConverter.ToUInt16(d, pos);
        }
        if ((flags & 0x02) != 0 && pos + 36 <= d.Length) {  // HasLinkInfo
            int li = pos;
            uint liSize  = BitConverter.ToUInt32(d, li);
            uint liHead  = BitConverter.ToUInt32(d, li + 4);
            uint liFlags = BitConverter.ToUInt32(d, li + 8);
            if ((liFlags & 0x01) != 0) {                    // VolumeIDAndLocalBasePath
                if (liHead >= 0x24) {
                    target = ReadUniZ(d, li + (int)BitConverter.ToUInt32(d, li + 28))
                           + ReadUniZ(d, li + (int)BitConverter.ToUInt32(d, li + 32));
                } else {
                    target = ReadAnsiZ(d, li + (int)BitConverter.ToUInt32(d, li + 16))
                           + ReadAnsiZ(d, li + (int)BitConverter.ToUInt32(d, li + 24));
                }
            }
            pos = li + (int)liSize;
        }
        // Walk the StringData section, capturing the RelativePath entry (flag 0x08) and the
        // IconLocation entry (flag 0x40) on the way : shortcuts written without a LinkInfo
        // block often store their target only as a path relative to the .lnk location.
        // StringData strings are not null-terminated; the leading uint16 is the char count.
        bool isUnicode = (flags & 0x80) != 0;               // IsUnicode
        string relativePath = "";
        uint[] stringDataFlags = new uint[] { 0x04, 0x08, 0x10, 0x20, 0x40 };
        for (int i = 0; i < stringDataFlags.Length; i++) {
            if ((flags & stringDataFlags[i]) == 0) continue;
            if (pos + 2 > d.Length) return;
            int charCount = BitConverter.ToUInt16(d, pos);
            int byteCount = charCount * (isUnicode ? 2 : 1);
            if (pos + 2 + byteCount > d.Length) return;
            if (stringDataFlags[i] == 0x08) {               // HasRelativePath
                relativePath = isUnicode ? System.Text.Encoding.Unicode.GetString(d, pos + 2, byteCount)
                                         : System.Text.Encoding.Default.GetString(d, pos + 2, byteCount);
            }
            else if (stringDataFlags[i] == 0x40) {          // HasIconLocation
                iconPath = isUnicode ? System.Text.Encoding.Unicode.GetString(d, pos + 2, byteCount)
                                     : System.Text.Encoding.Default.GetString(d, pos + 2, byteCount);
            }
            pos += 2 + byteCount;
        }
        // Walk the extra data blocks for the environment target and the property store
        while (pos + 8 <= d.Length) {
            uint blockSize = BitConverter.ToUInt32(d, pos);
            if (blockSize < 8 || pos + blockSize > d.Length) break;
            uint blockSig = BitConverter.ToUInt32(d, pos + 4);
            if (blockSig == 0xA0000001 && target.Length == 0 && blockSize >= 8 + 260 + 520) {
                string envTarget = ReadUniZ(d, pos + 8 + 260);
                if (envTarget.Length == 0) envTarget = ReadAnsiZ(d, pos + 8);
                if (envTarget.Length > 0) target = Environment.ExpandEnvironmentVariables(envTarget);
            }
            if (blockSig == 0xA0000009 && aumid.Length == 0) {
                aumid = ReadStoreAumid(d, pos + 8, pos + (int)blockSize);
            }
            if (blockSig == 0xA0000007 && iconPath.Length == 0 && blockSize >= 8 + 260 + 520) {
                string envIconPath = ReadUniZ(d, pos + 8 + 260);
                if (envIconPath.Length == 0) envIconPath = ReadAnsiZ(d, pos + 8);
                if (envIconPath.Length > 0) iconPath = envIconPath;
            }
            pos += (int)blockSize;
        }
        if (iconPath.Length > 0) iconPath = Environment.ExpandEnvironmentVariables(iconPath);
        // Last resort : resolve the relative path against the .lnk location
        if (target.Length == 0 && relativePath.Length > 0 && lnkDirectory.Length > 0) {
            try { target = System.IO.Path.GetFullPath(System.IO.Path.Combine(lnkDirectory, relativePath)); } catch { }
        }
    }

    // Returns the number of icon resources embedded in a file, 0 for missing or icon-less
    // files. An icon-less source means the shell synthesizes a generic rendering for it.
    public static int GetIconResourceCount(string filePath) {
        if (!System.IO.File.Exists(filePath)) return 0;
        uint iconResourceCount = ExtractIconExW(filePath, -1, IntPtr.Zero, IntPtr.Zero, 0);
        if (iconResourceCount == 0xFFFFFFFF) return 0;
        return (int)iconResourceCount;
    }

    // Adds the .lnk files of one directory. The Win32 3-character-extension quirk makes
    // GetFiles("*.lnk") also return .lnk* files (.lnkbak...), hence the exact-suffix test.
    static void AddLnkFiles(string directory, System.Collections.Generic.List<string> lnkPaths) {
        string[] matchedFiles;
        try { matchedFiles = System.IO.Directory.GetFiles(directory, "*.lnk"); } catch { return; }
        for (int i = 0; i < matchedFiles.Length; i++) {
            if (matchedFiles[i].EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) lnkPaths.Add(matchedFiles[i]);
        }
    }

    // Collects the .lnk files of a directory tree by manual recursion : reparse points are
    // skipped (the localized Start Menu compatibility junctions deny access by design, and
    // a single one aborts an AllDirectories enumeration wholesale) and unreadable folders
    // are ignored instead of discarding the whole walk.
    static void CollectLnkFiles(string directory, System.Collections.Generic.List<string> lnkPaths) {
        AddLnkFiles(directory, lnkPaths);
        string[] subDirectories;
        try { subDirectories = System.IO.Directory.GetDirectories(directory); } catch { return; }
        for (int i = 0; i < subDirectories.Length; i++) {
            try { if ((System.IO.File.GetAttributes(subDirectories[i]) & System.IO.FileAttributes.ReparsePoint) != 0) continue; }
            catch { continue; }
            CollectLnkFiles(subDirectories[i], lnkPaths);
        }
    }

    // Enumerates the .lnk files of a directory and returns one fully populated LnkEntry per
    // shortcut. Enumeration, binary parsing and object construction all happen in native
    // code, so the caller pays a single interop transition for the whole directory and
    // receives ready-to-use objects instead of building one per file.
    public static LnkEntry[] GetLnkCatalog(string directory, bool recurse, int rank) {
        System.Collections.Generic.List<string> files = new System.Collections.Generic.List<string>();
        if (recurse) CollectLnkFiles(directory, files);
        else AddLnkFiles(directory, files);
        LnkEntry[] entries = new LnkEntry[files.Count];
        for (int i = 0; i < files.Count; i++) {
            LnkEntry entry    = new LnkEntry();
            entry.LnkPath     = files[i];
            entry.DisplayName = System.IO.Path.GetFileNameWithoutExtension(files[i]);
            entry.Rank        = rank;
            string target = ""; string aumid = ""; string iconPath = "";
            try {
                byte[] d = System.IO.File.ReadAllBytes(files[i]);
                ParseLnk(d, System.IO.Path.GetDirectoryName(files[i]), out target, out aumid, out iconPath);
            } catch { }
            entry.TargetPath = target;
            entry.Aumid      = aumid;
            entry.IconPath   = iconPath;
            entries[i] = entry;
        }
        return entries;
    }
}

// Plain data carrier for one catalogued shortcut. Public fields are accessed directly
// from PowerShell; instances are built entirely in native code by GetLnkCatalog.
public class LnkEntry {
    public string LnkPath;
    public string DisplayName;
    public string TargetPath;
    public string Aumid;
    public string IconPath;
    public int Rank;
}
'@
    Write-Log "[init] C# native helper compiled successfully"
}

# Opens the Taskband registry key of the effective primary user (HKU\{SID} in cross-user mode).
function Open-EffectiveTaskbandKey {
    param([bool]$Writable = $false)
    if ($IsRunningCrossUser) { return [Microsoft.Win32.Registry]::Users.OpenSubKey("$EffectivePrimaryUserSID\$TaskBandRegistrySubKey", $Writable) }
    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($TaskBandRegistrySubKey, $Writable)
}


#region RESOLUTION HELPERS

# Resolves a .cpl file to its Control Panel namespace item by matching each item's CLSID
# InprocServer32 module path or DefaultIcon value against the .cpl filename. Returns a
# hashtable with Name and Path, or $null when no namespace item owns the file.
function Resolve-CplControlPanelItem {
    param([string]$CplFilePath)
    $CplFileName = [IO.Path]::GetFileName($CplFilePath).ToLower()
    $CplBaseName = [IO.Path]::GetFileNameWithoutExtension($CplFilePath).ToLower()
    $CplShellApplication   = New-Object -ComObject Shell.Application
    $ControlPanelNamespace = $CplShellApplication.Namespace('shell:ControlPanelFolder')
    $MatchedResult = $null
    foreach ($ControlPanelItem in $ControlPanelNamespace.Items()) {
        $ControlPanelItemPath = $ControlPanelItem.Path
        foreach ($GuidMatch in [regex]::Matches($ControlPanelItemPath, '\{[0-9A-Fa-f\-]+\}')) {
            $InprocRegistryKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey("CLSID\$($GuidMatch.Value)\InprocServer32")
            if ($InprocRegistryKey) {
                $ModulePath = $InprocRegistryKey.GetValue($null, ''); $InprocRegistryKey.Close()
                if ($ModulePath -and [IO.Path]::GetFileName($ModulePath).ToLower() -eq $CplFileName) { $MatchedResult = @{ Name = $ControlPanelItem.Name; Path = $ControlPanelItemPath }; break }
            }
            $DefaultIconKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey("CLSID\$($GuidMatch.Value)\DefaultIcon")
            if ($DefaultIconKey) {
                $IconValue = $DefaultIconKey.GetValue($null, ''); $DefaultIconKey.Close()
                if ($IconValue -and $IconValue.ToLower().Contains($CplBaseName)) { $MatchedResult = @{ Name = $ControlPanelItem.Name; Path = $ControlPanelItemPath }; break }
            }
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ControlPanelItem)
        if ($MatchedResult) { break }
    }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ControlPanelNamespace)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($CplShellApplication)
    return $MatchedResult
}

# Resolves a filesystem input string to one or more absolute paths. Supports direct paths,
# wildcards, bare filenames (searched through the current directory then every PATH entry),
# and extensionless names completed with PATHEXT extensions and .lnk.
function Resolve-FilesystemInput {
    param([string]$InputPath)
    $InputContainsWildcard      = $InputPath.Contains('*') -or $InputPath.Contains('?')
    $InputContainsDirectoryPart = $InputPath.Contains('\') -or $InputPath.Contains('/')
    if (-not $InputContainsWildcard) {
        try {
            $AbsoluteDirectPath = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $InputPath))
            if ([IO.File]::Exists($AbsoluteDirectPath))      { return $AbsoluteDirectPath }
            if ([IO.Directory]::Exists($AbsoluteDirectPath)) { return $AbsoluteDirectPath }
        } catch { }
    }
    $FileNamePattern = [IO.Path]::GetFileName($InputPath)
    if ($InputContainsDirectoryPart) {
        try {
            $ExplicitSearchDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, [IO.Path]::GetDirectoryName($InputPath)))
            if ([IO.Directory]::Exists($ExplicitSearchDirectory)) {
                $FoundFilesInDirectory = @([IO.Directory]::GetFiles($ExplicitSearchDirectory, $FileNamePattern))
                if ($FoundFilesInDirectory.Count -gt 0) { return $FoundFilesInDirectory }
            }
        } catch { }
    } else {
        $DirectoriesToSearch = @($PWD.ProviderPath)
        foreach ($PathEntry in ($env:PATH -split ';')) {
            if ($PathEntry -and [IO.Directory]::Exists($PathEntry)) { $DirectoriesToSearch += $PathEntry }
        }
        $PatternsToTry = @($FileNamePattern)
        if (-not $InputContainsWildcard -and -not [IO.Path]::HasExtension($FileNamePattern)) {
            foreach ($ExecutableExtension in ($env:PATHEXT -split ';')) { $PatternsToTry += "$FileNamePattern$ExecutableExtension" }
            $PatternsToTry += "$FileNamePattern.lnk"
        }
        foreach ($SearchDirectory in $DirectoriesToSearch) {
            foreach ($SearchPattern in $PatternsToTry) {
                try {
                    $FoundFiles = @([IO.Directory]::GetFiles($SearchDirectory, $SearchPattern))
                    if ($FoundFiles.Count -gt 0) { return $FoundFiles }
                } catch { }
            }
        }
    }
    return
}

# Builds and caches a one-time snapshot of every shell:AppsFolder entry : its AUMID, its
# display name, and -- for desktop applications backed by a shortcut -- the parsing path of
# the executable it launches. Reused for every application lookup so the Applications
# namespace is enumerated only once per run.
$script:AppsFolderSnapshot = $null
function Get-AppsFolderSnapshot {
    if ($null -ne $script:AppsFolderSnapshot) { return $script:AppsFolderSnapshot }
    $CollectedApplicationEntries = New-Object System.Collections.ArrayList
    try {
        $ShellApplicationForAppsFolder = New-Object -ComObject Shell.Application
        $ApplicationsNamespace         = $ShellApplicationForAppsFolder.Namespace('shell:AppsFolder')
        foreach ($ApplicationItem in $ApplicationsNamespace.Items()) {
            $ApplicationTargetParsingPath = ''
            try { $ApplicationTargetParsingPath = [string]$ApplicationItem.ExtendedProperty('System.Link.TargetParsingPath') } catch { }
            [void]$CollectedApplicationEntries.Add(@{ Aumid = $ApplicationItem.Path; DisplayName = $ApplicationItem.Name; TargetParsingPath = $ApplicationTargetParsingPath })
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ApplicationItem)
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ApplicationsNamespace)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ShellApplicationForAppsFolder)
    } catch { }
    Write-Log "  [apps] shell:AppsFolder : $($CollectedApplicationEntries.Count) installed application(s)"
    $script:AppsFolderSnapshot = @($CollectedApplicationEntries.ToArray())
    return $script:AppsFolderSnapshot
}

# Builds and caches a catalog of .lnk shortcuts from the application shortcut locations,
# searched by priority rank : 1 = Start Menu (machine-wide and primary user), 2 = Quick
# Launch. The Start Menu is indexed from its ROOT rather than its Programs subfolder :
# some installers create their group directly at that level (the shell's Apps list also
# reads both levels), and GetLnkCatalog's manual recursion skips the localized
# compatibility junctions living there. The paths are composed from environment variables
# because the CommonPrograms/CommonStartMenu SpecialFolder values do not exist on .NET
# 3.5, where PowerShell 2.0 runs. In AllUsers mode the Start Menu and Quick Launch of
# every other profile are included. Enumeration, parsing and entry construction all happen
# in native code through GetLnkCatalog, which returns ready-to-use LnkEntry objects.
# Quick Launch is scanned non-recursively so the User Pinned subdirectory stays out of scope.
$script:ShortcutCatalog = $null
function Get-ShortcutCatalog {
    if ($null -ne $script:ShortcutCatalog) { return $script:ShortcutCatalog }
    Initialize-NativeHelper
    $StartMenuRelativeProfilePath  = 'AppData\Roaming\Microsoft\Windows\Start Menu'
    $PrimaryUserStartMenuDirectory = if ($IsRunningCrossUser -and $InteractiveUserProfilePath) { [IO.Path]::Combine($InteractiveUserProfilePath, $StartMenuRelativeProfilePath) } else { [IO.Path]::Combine($env:APPDATA, 'Microsoft\Windows\Start Menu') }
    $ShortcutSearchRoots = @(
        @{ Directory = [IO.Path]::Combine($env:ProgramData, 'Microsoft\Windows\Start Menu'); Rank = 1; Recurse = $true },
        @{ Directory = $PrimaryUserStartMenuDirectory;                                       Rank = 1; Recurse = $true },
        @{ Directory = $QuickLaunchDirectory;                                                Rank = 2; Recurse = $false }
    )
    if ($AllUsers) {
        foreach ($UserProfile in @(Get-UserProfiles)) {
            $ShortcutSearchRoots += @{ Directory = [IO.Path]::Combine($UserProfile.ProfilePath, $StartMenuRelativeProfilePath); Rank = 1; Recurse = $true }
            $ShortcutSearchRoots += @{ Directory = [IO.Path]::Combine($UserProfile.ProfilePath, $QuickLaunchRelativePath);      Rank = 2; Recurse = $false }
        }
    }
    $CollectedShortcutEntries = New-Object System.Collections.ArrayList
    foreach ($SearchRoot in $ShortcutSearchRoots) {
        if (-not $SearchRoot.Directory -or -not [IO.Directory]::Exists($SearchRoot.Directory)) { continue }
        $CollectedShortcutEntries.AddRange([TaskbarPin]::GetLnkCatalog($SearchRoot.Directory, $SearchRoot.Recurse, $SearchRoot.Rank))
    }
    Write-Log "  [apps] Shortcut catalog : $($CollectedShortcutEntries.Count) shortcut(s) indexed from $($ShortcutSearchRoots.Count) location(s)"
    $script:ShortcutCatalog = @($CollectedShortcutEntries.ToArray())
    return $script:ShortcutCatalog
}

# Resolves an executable to the application identity (AUMID) it is registered under, by
# priority : the shell:AppsFolder entry whose target parsing path is the executable, then
# any catalogued shortcut targeting the executable and carrying an explicit AUMID.
# Returns an object exposing Aumid and DisplayName, or $null when no identity exists.
function Resolve-ExecutableIdentity {
    param([string]$ExecutableFullPath)
    foreach ($ApplicationEntry in (Get-AppsFolderSnapshot)) {
        if ($ApplicationEntry.TargetParsingPath -and $ApplicationEntry.TargetParsingPath -eq $ExecutableFullPath) { return $ApplicationEntry }
    }
    foreach ($CatalogRank in 1, 2) {
        foreach ($ShortcutEntry in (Get-ShortcutCatalog)) {
            if ($ShortcutEntry.Rank -ne $CatalogRank -or $ShortcutEntry.TargetPath -ne $ExecutableFullPath) { continue }
            if ($ShortcutEntry.Aumid) { return @{ Aumid = $ShortcutEntry.Aumid; DisplayName = $ShortcutEntry.DisplayName } }
        }
    }
    return $null
}

# Matches installed applications across all known sources, consulted by priority
# (shell:AppsFolder, then Start Menu, then Quick Launch), stopping at the first source
# producing matches. An explicit wildcard pattern searches every source by display name
# AND by AUMID and returns all matches (mass pin/unpin), deduplicated by AUMID and by
# target path so an application present both as an AppsFolder entry and as a Start Menu
# shortcut is only returned once. A bare name matches display names ONLY -- an AUMID often
# embeds its Start Menu group folder name, which would drag unrelated siblings (an
# 'Uninstall' entry of the same suite) into the match set -- and always resolves to a
# single application : an exact display-name match wins outright, several matches are
# narrowed down to exactly one by Select-SingleApplicationMatch. Each result carries a
# Kind of 'Aumid' (pin through the UWP pipeline) or 'Lnk' (pin the shortcut file itself,
# whose own AUMID is honoured by the .lnk pipeline downstream).
function Find-ApplicationMatches {
    param([string]$NameOrAumidPattern)
    if ($NameOrAumidPattern -match '[*?]') {
        # Invalid wildcard syntax (a stray '[') throws at the first -like : degrade the
        # input to a literal pattern instead of crashing outside the exit-code contract.
        try { $null = ('' -like $NameOrAumidPattern) } catch { $NameOrAumidPattern = [System.Management.Automation.WildcardPattern]::Escape($NameOrAumidPattern) }
        $MatchedApplications   = @()
        $AlreadyMatchedAumids  = @{}
        $AlreadyMatchedTargets = @{}
        foreach ($ApplicationEntry in (Get-AppsFolderSnapshot)) {
            if ($ApplicationEntry.DisplayName -like $NameOrAumidPattern -or $ApplicationEntry.Aumid -like $NameOrAumidPattern) {
                $MatchedApplications += @{ Kind = 'Aumid'; Aumid = $ApplicationEntry.Aumid; DisplayName = $ApplicationEntry.DisplayName }
                $AlreadyMatchedAumids[$ApplicationEntry.Aumid] = $true
                if ($ApplicationEntry.TargetParsingPath) { $AlreadyMatchedTargets[$ApplicationEntry.TargetParsingPath.ToLower()] = $true }
            }
        }
        foreach ($ShortcutEntry in (Get-ShortcutCatalog)) {
            if ($ShortcutEntry.DisplayName -notlike $NameOrAumidPattern) { continue }
            if ($ShortcutEntry.TargetPath -and $AlreadyMatchedTargets.ContainsKey($ShortcutEntry.TargetPath.ToLower())) { continue }
            if ($ShortcutEntry.Aumid -and $AlreadyMatchedAumids.ContainsKey($ShortcutEntry.Aumid)) { continue }
            $MatchedApplications += @{ Kind = 'Lnk'; LnkPath = $ShortcutEntry.LnkPath; DisplayName = $ShortcutEntry.DisplayName }
            if ($ShortcutEntry.Aumid) { $AlreadyMatchedAumids[$ShortcutEntry.Aumid] = $true }
            if ($ShortcutEntry.TargetPath) { $AlreadyMatchedTargets[$ShortcutEntry.TargetPath.ToLower()] = $true }
        }
        return $MatchedApplications
    }
    # Bare name : one pass per source collects the display-name substring matches while
    # sorting out the exact matches, so exact resolution costs no extra scan. The name is
    # escaped because [ ] form a wildcard character class under -like : a literal bracket
    # would otherwise crash the run (unbalanced) or select a wrong application (balanced).
    $SubstringMatchPattern = '*' + [System.Management.Automation.WildcardPattern]::Escape($NameOrAumidPattern) + '*'
    $ExactNameMatches      = @()
    $SubstringNameMatches  = @()
    foreach ($ApplicationEntry in (Get-AppsFolderSnapshot)) {
        if ($ApplicationEntry.DisplayName -notlike $SubstringMatchPattern) { continue }
        $CandidateMatch = @{ Kind = 'Aumid'; Aumid = $ApplicationEntry.Aumid; DisplayName = $ApplicationEntry.DisplayName; CandidateTargetPath = [string]$ApplicationEntry.TargetParsingPath; CandidateIconPath = '' }
        if ([string]::Equals($ApplicationEntry.DisplayName, $NameOrAumidPattern, [StringComparison]::OrdinalIgnoreCase)) { $ExactNameMatches += $CandidateMatch } else { $SubstringNameMatches += $CandidateMatch }
    }
    # Direct assignment (no if-expression) : assigning a one-element array out of an
    # if-expression unwraps it to the bare hashtable, whose .Count counts its keys.
    $CandidateSet = $SubstringNameMatches
    if ($ExactNameMatches.Count -gt 0) { $CandidateSet = $ExactNameMatches }
    if ($CandidateSet.Count -eq 1) { return $CandidateSet }
    if ($CandidateSet.Count -gt 1) { return @(Select-SingleApplicationMatch $CandidateSet $NameOrAumidPattern) }
    foreach ($CatalogRank in 1, 2) {
        $ExactNameMatches     = @()
        $SubstringNameMatches = @()
        foreach ($ShortcutEntry in (Get-ShortcutCatalog)) {
            if ($ShortcutEntry.Rank -ne $CatalogRank -or $ShortcutEntry.DisplayName -notlike $SubstringMatchPattern) { continue }
            $CandidateMatch = @{ Kind = 'Lnk'; LnkPath = $ShortcutEntry.LnkPath; DisplayName = $ShortcutEntry.DisplayName; Aumid = [string]$ShortcutEntry.Aumid; CandidateTargetPath = [string]$ShortcutEntry.TargetPath; CandidateIconPath = [string]$ShortcutEntry.IconPath }
            if ([string]::Equals($ShortcutEntry.DisplayName, $NameOrAumidPattern, [StringComparison]::OrdinalIgnoreCase)) { $ExactNameMatches += $CandidateMatch } else { $SubstringNameMatches += $CandidateMatch }
        }
        $CandidateSet = $SubstringNameMatches
        if ($ExactNameMatches.Count -gt 0) { $CandidateSet = $ExactNameMatches }
        if ($CandidateSet.Count -eq 1) { return $CandidateSet }
        if ($CandidateSet.Count -gt 1) { return @(Select-SingleApplicationMatch $CandidateSet $NameOrAumidPattern) }
    }
    return @()
}

# Keeps the candidates whose EliminationMetric equals the smallest value of the set.
function Select-MinimumMetricCandidates {
    param($ScoredCandidates)
    $SmallestMetricValue = $ScoredCandidates[0].EliminationMetric
    for ($CandidateIndex = 1; $CandidateIndex -lt $ScoredCandidates.Count; $CandidateIndex++) {
        if ($ScoredCandidates[$CandidateIndex].EliminationMetric -lt $SmallestMetricValue) { $SmallestMetricValue = $ScoredCandidates[$CandidateIndex].EliminationMetric }
    }
    $CandidatesAtMinimum = @()
    foreach ($ScoredCandidate in $ScoredCandidates) {
        if ($ScoredCandidate.EliminationMetric -eq $SmallestMetricValue) { $CandidatesAtMinimum += $ScoredCandidate }
    }
    return $CandidatesAtMinimum
}

# Determines whether a candidate displays an icon of its own. UWP applications always do
# (the package manifest requires a logo asset), and candidates whose TARGET lives under
# the Windows directory are exempt from the judgement altogether (system tools are not
# penalized for how they carry their icon). Other filesystem candidates resolve their
# icon source the way the shell does -- the shortcut IconLocation when set, the target
# otherwise : a UNC source is accepted unprobed (a dead share would stall the run for the
# SMB timeout); an ADS-embedded icon or a standalone .ico file is an explicit asset; a
# source borrowed from the Windows directory is a generic system icon; a PE source owns
# its icon only when it embeds icon resources (none, and the shell synthesizes a generic
# rendering for it, typical of 'Uninstall'/'Reinstall' suite utilities). The Windows
# directory prefix is boundary-checked so C:\Windows.old or C:\Windows Kits do not match.
function Test-CandidateOwnsIcon {
    param($Candidate)
    if ($Candidate.IsUwpApplication) { return $true }
    $WindowsDirectoryPrefix = $env:SystemRoot
    if (-not $WindowsDirectoryPrefix.EndsWith('\')) { $WindowsDirectoryPrefix = $WindowsDirectoryPrefix + '\' }
    $CandidateTargetPath = $Candidate.CandidateTargetPath
    if ($CandidateTargetPath -and $CandidateTargetPath.StartsWith($WindowsDirectoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $IconSourcePath = $Candidate.CandidateIconPath
    if (-not $IconSourcePath) { $IconSourcePath = $CandidateTargetPath }
    if (-not $IconSourcePath) { return $false }
    if ($IconSourcePath.StartsWith('\\')) { return $true }
    if ($IconSourcePath.EndsWith(':icon.ico', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($IconSourcePath.StartsWith($WindowsDirectoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($IconSourcePath.EndsWith('.ico', [StringComparison]::OrdinalIgnoreCase)) { return [IO.File]::Exists($IconSourcePath) }
    return ([TaskbarPin]::GetIconResourceCount($IconSourcePath) -gt 0)
}

# Reduces several bare-name matches to exactly one application through successive
# elimination stages, each applied only when at least one candidate survives it :
#   1. drop candidates that are neither UWP applications nor .exe-backed
#   2. drop candidates without an icon of their own (their displayed icon is synthesized)
#   3. keep the shortest display name
#   4. keep the shallowest target path -- a single candidate without a measurable target
#      (UWP application, registered-AUMID entry) among measurable ones wins instead
#   5. keep the earliest position of the typed name inside the display name
#   6. keep the first display name in ordinal order (deterministic last resort)
# Expensive probes (icon resource counting) only ever run on the survivors of the
# preceding stages, and every stage is skipped once a single candidate remains.
function Select-SingleApplicationMatch {
    param($CandidateMatches, [string]$BareNameInput)
    Initialize-NativeHelper
    $RemainingCandidates = @($CandidateMatches)
    Write-Log "  [select] '$BareNameInput' matched $($RemainingCandidates.Count) applications -- reducing to a single one"
    foreach ($Candidate in $RemainingCandidates) {
        # UWP is recognized by the AUMID shape alone (PackageFamily!AppId : a '!' and no
        # path separator), whatever the source : a Start Menu .lnk carrying a packaged
        # AUMID is as much a UWP application as its AppsFolder entry.
        $CandidateAumid = [string]$Candidate.Aumid
        $Candidate.IsUwpApplication = ($CandidateAumid.IndexOf('!') -ge 0 -and $CandidateAumid.IndexOf('\') -lt 0)
    }
    if ($RemainingCandidates.Count -gt 1) {
        $ExecutableBackedCandidates = @()
        foreach ($Candidate in $RemainingCandidates) {
            if ($Candidate.IsUwpApplication -or $Candidate.CandidateTargetPath.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { $ExecutableBackedCandidates += $Candidate }
        }
        if ($ExecutableBackedCandidates.Count -gt 0 -and $ExecutableBackedCandidates.Count -lt $RemainingCandidates.Count) {
            Write-Log "  [select] executable/UWP filter : $($RemainingCandidates.Count) -> $($ExecutableBackedCandidates.Count)"
            $RemainingCandidates = $ExecutableBackedCandidates
        }
    }
    if ($RemainingCandidates.Count -gt 1) {
        $OwnIconCandidates = @()
        foreach ($Candidate in $RemainingCandidates) {
            if (Test-CandidateOwnsIcon $Candidate) { $OwnIconCandidates += $Candidate }
        }
        if ($OwnIconCandidates.Count -gt 0 -and $OwnIconCandidates.Count -lt $RemainingCandidates.Count) {
            Write-Log "  [select] own-icon filter : $($RemainingCandidates.Count) -> $($OwnIconCandidates.Count)"
            $RemainingCandidates = $OwnIconCandidates
        }
    }
    if ($RemainingCandidates.Count -gt 1) {
        foreach ($Candidate in $RemainingCandidates) { $Candidate.EliminationMetric = $Candidate.DisplayName.Length }
        $RemainingCandidates = @(Select-MinimumMetricCandidates $RemainingCandidates)
    }
    if ($RemainingCandidates.Count -gt 1) {
        # Depth is measured on the filesystem target only. Candidates without one (UWP
        # applications, registered-AUMID entries) cannot be compared : a single
        # unmeasurable candidate among measurable ones wins (its depth cannot disqualify
        # it), several unmeasurable candidates make the stage inapplicable.
        $MeasurableCandidates   = @()
        $UnmeasurableCandidates = @()
        foreach ($Candidate in $RemainingCandidates) {
            if ($Candidate.CandidateTargetPath) { $MeasurableCandidates += $Candidate } else { $UnmeasurableCandidates += $Candidate }
        }
        if ($UnmeasurableCandidates.Count -eq 0) {
            foreach ($Candidate in $RemainingCandidates) { $Candidate.EliminationMetric = $Candidate.CandidateTargetPath.Split('\').Length }
            $RemainingCandidates = @(Select-MinimumMetricCandidates $RemainingCandidates)
        } elseif ($UnmeasurableCandidates.Count -eq 1 -and $MeasurableCandidates.Count -gt 0) {
            $RemainingCandidates = $UnmeasurableCandidates
        }
    }
    if ($RemainingCandidates.Count -gt 1) {
        # -like folds some characters case-invariantly that an ordinal IndexOf does not
        # (Turkish dotted I...), so a match can still yield -1 here : clamp it to the
        # worst rank instead of letting -1 beat every real position.
        foreach ($Candidate in $RemainingCandidates) {
            $PatternPositionInTitle = $Candidate.DisplayName.IndexOf($BareNameInput, [StringComparison]::OrdinalIgnoreCase)
            if ($PatternPositionInTitle -lt 0) { $PatternPositionInTitle = [int]::MaxValue }
            $Candidate.EliminationMetric = $PatternPositionInTitle
        }
        $RemainingCandidates = @(Select-MinimumMetricCandidates $RemainingCandidates)
    }
    $SelectedApplicationMatch = $RemainingCandidates[0]
    for ($CandidateIndex = 1; $CandidateIndex -lt $RemainingCandidates.Count; $CandidateIndex++) {
        if ([string]::CompareOrdinal($RemainingCandidates[$CandidateIndex].DisplayName, $SelectedApplicationMatch.DisplayName) -lt 0) { $SelectedApplicationMatch = $RemainingCandidates[$CandidateIndex] }
    }
    Write-Log "  [select] selected : '$($SelectedApplicationMatch.DisplayName)'"
    return $SelectedApplicationMatch
}

# Produces a .lnk suitable for taskbar pinning from a resolved filesystem path, and reports
# through $Beef001dContentRef the parsing name to embed in the BEEF001D block. That name is
# the identity the taskbar resolves the pinned button to, so it must be unique per item,
# and an explicit AppUserModelID found on a .lnk takes precedence over its raw target path :
# identity-aware applications register their jump list under that AUMID, and a parsing name
# pointing at the bare executable would leave the button's right-click menu empty.
#   .lnk        : returned as-is; BEEF001D = its AUMID, or its target path without one.
#   .cpl        : resolved through the Control Panel namespace (proper icon and name), with
#                 a rundll32 shortcut as second chance; BEEF001D = namespace or .cpl path.
#   directories : temporary explorer.exe shortcut; BEEF001D = the directory path.
#   .exe/other  : temporary shortcut named after the FileDescription when available;
#                 BEEF001D = the target path.
function New-TargetShortcut {
    param([string]$ResolvedTargetPath, [ref]$Beef001dContentRef, [ref]$WshShellComObjectRef)
    $TargetFileExtension = [IO.Path]::GetExtension($ResolvedTargetPath).ToLower()
    if ($TargetFileExtension -eq '.lnk') {
        if (-not $WshShellComObjectRef.Value) { $WshShellComObjectRef.Value = New-Object -ComObject WScript.Shell }
        $ShortcutObject = $WshShellComObjectRef.Value.CreateShortcut($ResolvedTargetPath)
        $ShortcutTargetPath = $ShortcutObject.TargetPath
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ShortcutObject)
        $ShortcutAppUserModelId = ''
        if ('TaskbarPin' -as [Type]) { $ShortcutAppUserModelId = [TaskbarPin]::GetAumid($ResolvedTargetPath) }
        if ($ShortcutAppUserModelId) { $Beef001dContentRef.Value = $ShortcutAppUserModelId } else { $Beef001dContentRef.Value = $ShortcutTargetPath }
        return $ResolvedTargetPath
    }
    if ($TargetFileExtension -eq '.cpl') {
        Initialize-NativeHelper
        $CplControlPanelMatch = Resolve-CplControlPanelItem $ResolvedTargetPath
        if ($CplControlPanelMatch) {
            $CplTemporaryLnkPath = [IO.Path]::Combine($env:TEMP, "$($CplControlPanelMatch.Name -replace '[<>:"/\\|?*]', '_').lnk")
            if ([TaskbarPin]::CreatePidlShortcut($CplControlPanelMatch.Path, $CplTemporaryLnkPath, $CplControlPanelMatch.Path)) {
                $Beef001dContentRef.Value = $CplControlPanelMatch.Path
                return $CplTemporaryLnkPath
            }
        }
    }
    $ShortcutDisplayName = [IO.Path]::GetFileNameWithoutExtension($ResolvedTargetPath)
    if ($TargetFileExtension -eq '.exe') {
        try {
            $FileVersionDescription = [Diagnostics.FileVersionInfo]::GetVersionInfo($ResolvedTargetPath).FileDescription
            if ($FileVersionDescription -and $FileVersionDescription.Trim()) {
                $CandidateDisplayName = $FileVersionDescription.Trim() -replace '[<>:"/\\|?*]', '_'
                if (-not [IO.File]::Exists([IO.Path]::Combine($TaskBarPinnedDirectory, "$CandidateDisplayName.lnk"))) { $ShortcutDisplayName = $CandidateDisplayName }
            }
        } catch { }
    }
    $TemporaryLnkPath = [IO.Path]::Combine($env:TEMP, "$ShortcutDisplayName.lnk")
    if (-not $WshShellComObjectRef.Value) { $WshShellComObjectRef.Value = New-Object -ComObject WScript.Shell }
    $NewShortcutObject = $WshShellComObjectRef.Value.CreateShortcut($TemporaryLnkPath)
    if ([IO.Directory]::Exists($ResolvedTargetPath)) {
        # Checked before the .cpl extension : a directory whose name ends in .cpl is a
        # directory, and a rundll32 Control_RunDLL shortcut would be broken for it.
        $NewShortcutObject.TargetPath       = [IO.Path]::Combine($env:SystemRoot, 'explorer.exe')
        $NewShortcutObject.Arguments        = "`"$ResolvedTargetPath`""
        $NewShortcutObject.IconLocation     = [IO.Path]::Combine($env:SystemRoot, 'System32\shell32.dll') + ',3'
        $NewShortcutObject.WorkingDirectory = $ResolvedTargetPath
    } elseif ($TargetFileExtension -eq '.cpl') {
        # The BEEF001D content is the .cpl argument rather than rundll32.exe : the host
        # executable is shared by many items and the parsing name must stay unique per pin.
        $NewShortcutObject.TargetPath       = [IO.Path]::Combine($env:SystemRoot, 'System32\rundll32.exe')
        $NewShortcutObject.Arguments        = "shell32.dll,Control_RunDLL `"$ResolvedTargetPath`""
        $NewShortcutObject.IconLocation     = "$ResolvedTargetPath,0"
        $NewShortcutObject.WorkingDirectory = [IO.Path]::GetDirectoryName($ResolvedTargetPath)
    } else {
        $NewShortcutObject.TargetPath       = $ResolvedTargetPath
        $NewShortcutObject.WorkingDirectory = [IO.Path]::GetDirectoryName($ResolvedTargetPath)
    }
    $Beef001dContentRef.Value = $ResolvedTargetPath
    $NewShortcutObject.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($NewShortcutObject)
    return $TemporaryLnkPath
}


#region PIN STATE HELPERS

# Appends new entries to the Favorites blob ([category][pidlSize][pidlData] entries with a
# 0xFF terminator). Duplicates are skipped by .lnk filename. The write order is critical :
# blob first, then FavoritesVersion, then FavoritesChanges -- the taskbar handler uses the
# changes counter as a guard, so writing it last guarantees the blob is fully committed
# before the handler reads it. Returns the number of entries actually added.
function Write-BlobToRegistryKey {
    param($RegistryKeyHandle, [byte[]]$ExistingFavoritesBlob, $NewBlobEntriesToAdd)
    if (-not $ExistingFavoritesBlob -or $ExistingFavoritesBlob.Length -lt 2) { $ExistingFavoritesBlob = [byte[]]@(0xFF) }
    $BlobInsertionOffset = 0
    while ($BlobInsertionOffset -lt $ExistingFavoritesBlob.Length -and $ExistingFavoritesBlob[$BlobInsertionOffset] -ne 0xFF) {
        if ($BlobInsertionOffset + 5 -gt $ExistingFavoritesBlob.Length) { break }
        $BlobInsertionOffset += 1 + 4 + [BitConverter]::ToUInt32($ExistingFavoritesBlob, $BlobInsertionOffset + 1)
    }
    $OutputBlobStream = New-Object System.IO.MemoryStream
    if ($BlobInsertionOffset -gt 0) { $OutputBlobStream.Write($ExistingFavoritesBlob, 0, $BlobInsertionOffset) }
    $NumberOfEntriesActuallyAdded = 0
    foreach ($NewEntry in $NewBlobEntriesToAdd) {
        $ShortcutFileName = [IO.Path]::GetFileName($NewEntry.DestinationLnkPath)
        if ([TaskbarPin]::FindBlobEntry($ExistingFavoritesBlob, $ShortcutFileName) -ge 0) {
            Write-Log "    [blob] '$ShortcutFileName' already present in Favorites blob -- skipping duplicate" 'Yellow'
            continue
        }
        $OutputBlobStream.Write($NewEntry.SerializedBlobEntry, 0, $NewEntry.SerializedBlobEntry.Length)
        $NumberOfEntriesActuallyAdded++
        Write-Log "    [blob] Appended '$ShortcutFileName' to blob ($($NewEntry.SerializedBlobEntry.Length) bytes)"
    }
    $OutputBlobStream.WriteByte(0xFF)
    $FinalBlobBytes = $OutputBlobStream.ToArray()
    $OutputBlobStream.Dispose()
    if ($NumberOfEntriesActuallyAdded -eq 0) { Write-Log "    [blob] No new entries to add -- Favorites blob unchanged"; return 0 }
    $CurrentFavoritesChangesCounter = [int]$RegistryKeyHandle.GetValue('FavoritesChanges', 0, $DoNotExpandRegistryOption)
    $RegistryKeyHandle.SetValue('Favorites',        $FinalBlobBytes,                       $BinaryRegistryValueKind)
    $RegistryKeyHandle.SetValue('FavoritesVersion',  3,                                    $DwordRegistryValueKind)
    $RegistryKeyHandle.SetValue('FavoritesChanges', ($CurrentFavoritesChangesCounter + 1), $DwordRegistryValueKind)
    Write-Log "    [blob] Favorites : $($ExistingFavoritesBlob.Length) -> $($FinalBlobBytes.Length) bytes (+$NumberOfEntriesActuallyAdded entries) | FavoritesChanges : $CurrentFavoritesChangesCounter -> $($CurrentFavoritesChangesCounter + 1)"
    return $NumberOfEntriesActuallyAdded
}

# Returns the user profiles relevant to AllUsers mode (SID + ProfilePath each), excluding
# the effective primary user (handled in the main flow) and system/service accounts, and
# including the Default template profile so new users inherit the pins at first logon.
function Get-UserProfiles {
    $ProfileListRegistryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList')
    if (-not $ProfileListRegistryKey) { return @() }
    $DiscoveredProfiles = @()
    foreach ($ProfileSid in $ProfileListRegistryKey.GetSubKeyNames()) {
        if ($ProfileSid.Length -lt 20 -or $ProfileSid -eq $EffectivePrimaryUserSID) { continue }
        $ProfileSubKey = $ProfileListRegistryKey.OpenSubKey($ProfileSid)
        if (-not $ProfileSubKey) { continue }
        $ProfileImagePath = $ProfileSubKey.GetValue('ProfileImagePath', '')
        $ProfileSubKey.Close()
        if (-not $ProfileImagePath -or -not [IO.Directory]::Exists($ProfileImagePath)) { continue }
        $ProfileFolderName = [IO.Path]::GetFileName($ProfileImagePath).ToLower()
        if ($ProfileFolderName -eq 'systemprofile' -or $ProfileFolderName -eq 'localservice' -or $ProfileFolderName -eq 'networkservice') { continue }
        $DiscoveredProfiles += @{ SID = $ProfileSid; ProfilePath = $ProfileImagePath }
    }
    $ProfileListRegistryKey.Close()
    if ([IO.File]::Exists([IO.Path]::Combine($env:SystemDrive, 'Users\Default\NTUSER.DAT'))) {
        $DiscoveredProfiles += @{ SID = 'Default'; ProfilePath = [IO.Path]::Combine($env:SystemDrive, 'Users\Default') }
    }
    return $DiscoveredProfiles
}

# Runs reg.exe hidden and returns its exit code. Used to load/unload offline NTUSER.DAT hives.
function Invoke-RegistryExecutable {
    param([string]$ArgumentLine)
    $RegProcessStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $RegProcessStartInfo.FileName              = 'reg.exe'
    $RegProcessStartInfo.Arguments             = $ArgumentLine
    $RegProcessStartInfo.UseShellExecute       = $false
    $RegProcessStartInfo.CreateNoWindow        = $true
    $RegProcessStartInfo.RedirectStandardError = $true
    $RegProcess = [System.Diagnostics.Process]::Start($RegProcessStartInfo)
    $null = $RegProcess.WaitForExit(10000)
    return $RegProcess.ExitCode
}

# Loads a user's offline NTUSER.DAT hive (unless already mounted because the user is logged
# in), opens its Taskband key with write access (creating it if missing), executes the given
# scriptblock with that key, then unloads the hive. A GC pass before unload releases the
# .NET RegistryKey handles that would otherwise make reg.exe unload fail with access denied.
function Invoke-WithOfflineHive {
    param([string]$ProfileSID, [string]$ProfileDirectoryPath, [scriptblock]$ActionToPerform)
    $NtUserDatFilePath = [IO.Path]::Combine($ProfileDirectoryPath, 'NTUSER.DAT')
    if (-not [IO.File]::Exists($NtUserDatFilePath)) { Write-Log "    [hive] NTUSER.DAT not found at '$NtUserDatFilePath'" 'Yellow'; return $false }
    $LoadedHiveRegistryPath = $null
    $HiveRequiresUnload     = $false
    if ($ProfileSID -ne 'Default' -and (Test-RegistrySubKeyExists ([Microsoft.Win32.Registry]::Users) "$ProfileSID\$TaskBandRegistrySubKey")) {
        $LoadedHiveRegistryPath = $ProfileSID
        Write-Log "    [hive] Hive for SID $ProfileSID is already loaded (user is logged in)"
    }
    if (-not $LoadedHiveRegistryPath) {
        $TemporaryHiveName = "TempPin_$($ProfileSID.Replace('-','').Substring(0, [Math]::Min(12, $ProfileSID.Replace('-','').Length)))"
        Write-Log "    [hive] Loading NTUSER.DAT as HKU\$TemporaryHiveName..."
        if ((Invoke-RegistryExecutable "load `"HKU\$TemporaryHiveName`" `"$NtUserDatFilePath`"") -ne 0) {
            Write-Log "    [hive] reg.exe load FAILED -- profile may be locked by another process" 'Yellow'
            return $false
        }
        $LoadedHiveRegistryPath = $TemporaryHiveName
        $HiveRequiresUnload     = $true
    }
    try {
        $TaskBandKeyHandle = [Microsoft.Win32.Registry]::Users.OpenSubKey("$LoadedHiveRegistryPath\$TaskBandRegistrySubKey", $true)
        if (-not $TaskBandKeyHandle) { $TaskBandKeyHandle = [Microsoft.Win32.Registry]::Users.CreateSubKey("$LoadedHiveRegistryPath\$TaskBandRegistrySubKey") }
        if ($TaskBandKeyHandle) {
            try { & $ActionToPerform $TaskBandKeyHandle } finally { $TaskBandKeyHandle.Close(); $TaskBandKeyHandle = $null }
        }
    } finally {
        if ($HiveRequiresUnload) {
            [GC]::Collect(); [GC]::WaitForPendingFinalizers(); Start-Sleep -Milliseconds 200
            if ((Invoke-RegistryExecutable "unload `"HKU\$TemporaryHiveName`"") -ne 0) { Write-Log "    [hive] WARNING : reg.exe unload FAILED for HKU\$TemporaryHiveName -- handle may be leaked" 'Yellow' }
            else { Write-Log "    [hive] Unloaded HKU\$TemporaryHiveName" }
        }
    }
    return $true
}

# Scans a directory of pinned shortcuts and returns those matching the unpin patterns.
# Matching is performed against the shortcut filename, its target path (basename and
# filename, or the full path for patterns containing a directory separator), and its AUMID,
# all read in a single native pass through GetLnkCatalog.
function Find-MatchingPins {
    param([string]$PinnedShortcutDirectory, [string[]]$PatternsToMatch)
    $MatchedShortcutPaths = @()
    foreach ($PinnedShortcutEntry in @([TaskbarPin]::GetLnkCatalog($PinnedShortcutDirectory, $false, 0))) {
        foreach ($Pattern in $PatternsToMatch) {
            $ShortcutTargetPath = $PinnedShortcutEntry.TargetPath
            if ($Pattern -match '[/\\]') {
                $PatternMatched = [bool]($ShortcutTargetPath -and $ShortcutTargetPath -like $Pattern)
            } else {
                $PatternMatched = ($PinnedShortcutEntry.DisplayName -like $Pattern) -or
                                  ($ShortcutTargetPath -and ([IO.Path]::GetFileNameWithoutExtension($ShortcutTargetPath) -like $Pattern -or [IO.Path]::GetFileName($ShortcutTargetPath) -like $Pattern)) -or
                                  ($PinnedShortcutEntry.Aumid -and $PinnedShortcutEntry.Aumid -like $Pattern)
            }
            if ($PatternMatched) {
                Write-Log "    [match] '$([IO.Path]::GetFileName($PinnedShortcutEntry.LnkPath))' matched pattern '$Pattern'"
                $MatchedShortcutPaths += $PinnedShortcutEntry.LnkPath
                break
            }
        }
    }
    Write-Log "  [scan] '$PinnedShortcutDirectory' : $($MatchedShortcutPaths.Count) matching shortcut(s)"
    return $MatchedShortcutPaths
}

# Removes matching entries from the Favorites and FavoritesResolve blobs. Entries are
# located by their Unicode .lnk filename inside the PIDL data, then removed in descending
# index order so earlier indices stay valid throughout the removals.
function Invoke-UnpinFromBlob {
    param($RegistryKeyHandle, [string[]]$ShortcutFilenamesToRemove)
    $FavoritesBlob        = $RegistryKeyHandle.GetValue('Favorites', $null, $DoNotExpandRegistryOption)
    $FavoritesResolveBlob = $RegistryKeyHandle.GetValue('FavoritesResolve', $null, $DoNotExpandRegistryOption)
    if (-not $FavoritesBlob -or $FavoritesBlob.Length -lt 6) { Write-Log "    [blob] Favorites blob is empty or absent -- nothing to unpin"; return 0 }
    $EntriesToRemove = @()
    foreach ($ShortcutFilename in $ShortcutFilenamesToRemove) {
        $FoundBlobIndex = [TaskbarPin]::FindBlobEntry($FavoritesBlob, $ShortcutFilename)
        if ($FoundBlobIndex -ge 0) { $EntriesToRemove += @{ Name = $ShortcutFilename; Index = $FoundBlobIndex }; Write-Log "    [blob] Found '$ShortcutFilename' at blob index $FoundBlobIndex" }
        else                       { Write-Log "    [blob] '$ShortcutFilename' not found in blob -- already absent or never pinned" }
    }
    foreach ($EntryToRemove in @($EntriesToRemove | Sort-Object { $_.Index } -Descending)) {
        $FavoritesBlob = [TaskbarPin]::RemoveFavEntry($FavoritesBlob, $EntryToRemove.Index)
        if ($FavoritesResolveBlob) { $FavoritesResolveBlob = [TaskbarPin]::RemoveResEntry($FavoritesResolveBlob, $EntryToRemove.Index) }
        Write-Log "    [blob] Removed '$($EntryToRemove.Name)' (was at index $($EntryToRemove.Index))"
    }
    if ($EntriesToRemove.Count -gt 0) {
        $CurrentFavoritesChanges = [int]$RegistryKeyHandle.GetValue('FavoritesChanges', 0, $DoNotExpandRegistryOption)
        $RegistryKeyHandle.SetValue('Favorites', ([byte[]]$FavoritesBlob), $BinaryRegistryValueKind)
        if ($FavoritesResolveBlob) { $RegistryKeyHandle.SetValue('FavoritesResolve', ([byte[]]$FavoritesResolveBlob), $BinaryRegistryValueKind) }
        $RegistryKeyHandle.SetValue('FavoritesVersion', 3, $DwordRegistryValueKind)
        $RegistryKeyHandle.SetValue('FavoritesChanges', ($CurrentFavoritesChanges + 1), $DwordRegistryValueKind)
        Write-Log "    [blob] $($EntriesToRemove.Count) entries removed | FavoritesChanges : $CurrentFavoritesChanges -> $($CurrentFavoritesChanges + 1)"
    }
    return $EntriesToRemove.Count
}


#region UNPIN FLOW

if ($Unpin) {
    # Build one match pattern per input : AppsFolder inputs match by AUMID suffix; .cpl items
    # are pinned under their Control Panel display name so the name is resolved through the
    # namespace; full paths and bare .msc/.exe names match by filename without extension;
    # anything else is used as-is for -like matching.
    $UnpinMatchPatterns = @()
    foreach ($InputItem in $ParsedInputItems) {
        if ($InputItem.StartsWith('shell:AppsFolder\', [StringComparison]::OrdinalIgnoreCase)) { $UnpinMatchPatterns += $InputItem.Substring(17); continue }
        $InputHasWildcard = $InputItem -match '[*?]'
        $InputExtension   = [IO.Path]::GetExtension($InputItem).ToLower()
        if ($InputExtension -eq '.cpl' -and -not $InputHasWildcard) {
            $CplResolvedPattern = $null
            foreach ($ResolvedCplPath in @(Resolve-FilesystemInput $InputItem)) {
                if ($ResolvedCplPath -and [IO.File]::Exists($ResolvedCplPath)) {
                    $CplMatch = Resolve-CplControlPanelItem $ResolvedCplPath
                    if ($CplMatch) { $CplResolvedPattern = $CplMatch.Name -replace '[<>:"/\\|?*]', '_'; break }
                }
            }
            # Literal names are escaped before use as -like patterns ([ ] would misfire).
            if ($CplResolvedPattern) { $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape($CplResolvedPattern); Write-Log "  [pattern] CPL '$InputItem' resolved to display name pattern '$CplResolvedPattern'" }
            else { $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape([IO.Path]::GetFileNameWithoutExtension($InputItem)); Write-Log "  [pattern] CPL '$InputItem' could not be resolved via namespace -- using its base name" 'Yellow' }
        } else {
            # Baseline pattern : same behavior as before so explicit and wildcard patterns keep working.
            if (($InputItem -match '[/\\]' -and -not $InputHasWildcard) -or $InputExtension -eq '.msc' -or $InputExtension -eq '.exe') { $UnpinMatchPatterns += [IO.Path]::GetFileNameWithoutExtension($InputItem) }
            else { $UnpinMatchPatterns += $InputItem }
            # Mirror the pin resolution : a pinned shortcut may carry the basename of a file
            # the input resolved to (wildcard expansion included), or the display name and
            # AUMID of the application identity an executable or a bare name was pinned
            # under. AUMID-based shortcuts have no target path, so without these patterns
            # they can never be matched back from the original input.
            $ResolvedUnpinPaths = @(Resolve-FilesystemInput $InputItem)
            if ($ResolvedUnpinPaths.Count -gt 0) {
                foreach ($ResolvedUnpinPath in $ResolvedUnpinPaths) {
                    $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape([IO.Path]::GetFileNameWithoutExtension($ResolvedUnpinPath))
                    if ([IO.Path]::GetExtension($ResolvedUnpinPath).ToLower() -eq '.exe') {
                        $UnpinExecutableIdentity = Resolve-ExecutableIdentity $ResolvedUnpinPath
                        if ($UnpinExecutableIdentity) {
                            $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape(($UnpinExecutableIdentity.DisplayName -replace '[<>:"/\\|?*]', '_'))
                            $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape($UnpinExecutableIdentity.Aumid)
                            Write-Log "  [pattern] '$InputItem' carries application identity '$($UnpinExecutableIdentity.DisplayName)' ($($UnpinExecutableIdentity.Aumid))"
                        }
                    }
                }
            } elseif ($InputItem -notmatch '[/\\]') {
                foreach ($UnpinApplicationMatch in @(Find-ApplicationMatches $InputItem)) {
                    $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape(($UnpinApplicationMatch.DisplayName -replace '[<>:"/\\|?*]', '_'))
                    if ($UnpinApplicationMatch.Kind -eq 'Aumid') { $UnpinMatchPatterns += [System.Management.Automation.WildcardPattern]::Escape($UnpinApplicationMatch.Aumid) }
                    Write-Log "  [pattern] '$InputItem' matched installed application '$($UnpinApplicationMatch.DisplayName)'"
                }
            }
        }
    }
    $UnpinMatchPatterns = @($UnpinMatchPatterns | Where-Object { $_ } | Select-Object -Unique)
    # Raw input kept as a pattern may carry invalid wildcard syntax (a stray '[') that
    # would throw at the first -like : degrade those to literal patterns.
    $UnpinMatchPatterns = @(foreach ($UnpinPattern in $UnpinMatchPatterns) {
        try { $null = ('' -like $UnpinPattern); $UnpinPattern } catch { [System.Management.Automation.WildcardPattern]::Escape($UnpinPattern) }
    })
    $DisplayPatternLabel = $UnpinMatchPatterns -join ', '
    Write-Banner 'UNPIN' 'DarkRed' "$DisplayPatternLabel$(if ($AllUsers) { ' (AllUsers)' })"
    Write-OperationLogHeader 'UNPIN' "patterns : $DisplayPatternLabel"
    Initialize-NativeHelper
    $PinnedDirectoriesToScan = @()
    if ($TaskBarDirectoryExists)     { $PinnedDirectoriesToScan += $TaskBarPinnedDirectory }
    if ($QuickLaunchDirectoryExists) { $PinnedDirectoriesToScan += $QuickLaunchDirectory }
    if ($PinnedDirectoriesToScan.Count -eq 0 -and -not $AllUsers) {
        Write-Console "  [!] No pin locations found on this system" -Color Yellow
        Write-Log "No TaskBar or QuickLaunch directory found -- nothing to unpin" 'Yellow'
        Close-Log; exit 2
    }
    $MatchedShortcutPaths = @()
    foreach ($DirectoryToScan in $PinnedDirectoriesToScan) { $MatchedShortcutPaths += @(Find-MatchingPins $DirectoryToScan $UnpinMatchPatterns) }
    Write-Log "Total matched shortcuts (current user) : $($MatchedShortcutPaths.Count)"
    if ($MatchedShortcutPaths.Count -eq 0 -and -not $AllUsers) {
        Write-Console "  [!] No pinned items match" -Color Yellow
        Write-Log "No pinned items matched the given patterns" 'Yellow'
        Write-Console ""; Close-Log; exit 2
    }
    if ($MatchedShortcutPaths.Count -gt 0 -and $TaskBandRegistryKeyExists) {
        $MutexWasAcquired = [TaskbarPin]::AcquirePinMutex(5000)
        Write-Log "[mutex] TaskbarPinListMutex acquired : $MutexWasAcquired"
        try {
            $TaskBandRegistryKey = Open-EffectiveTaskbandKey $true
            if ($TaskBandRegistryKey) {
                try { $null = Invoke-UnpinFromBlob $TaskBandRegistryKey @($MatchedShortcutPaths | ForEach-Object { [IO.Path]::GetFileName($_) }) }
                finally { $TaskBandRegistryKey.Close() }
            }
        } finally {
            if ($MutexWasAcquired) { [TaskbarPin]::ReleasePinMutex(); Write-Log "[mutex] TaskbarPinListMutex released" }
        }
    }
    $UnpinFailedDeleteCount = 0
    foreach ($ShortcutPath in $MatchedShortcutPaths) {
        if ([IO.File]::Exists($ShortcutPath)) {
            try   { [IO.File]::Delete($ShortcutPath); Write-Log "  [file] Deleted '$ShortcutPath'" }
            catch { Write-Log "  [file] FAILED to delete '$ShortcutPath' : $_" 'Yellow'; $UnpinFailedDeleteCount++ }
        }
    }
    if ($MatchedShortcutPaths.Count -gt 0) { [TaskbarPin]::SendPinNotify(); Write-Log "[notify] 0x446 posted to the taskbar pinned-items band" }
    if ($AllUsers) {
        foreach ($UserProfile in @(Get-UserProfiles)) {
            Write-Log "  [profile] $($UserProfile.ProfilePath) (SID : $($UserProfile.SID))"
            $ProfileTaskBarDirectory = [IO.Path]::Combine($UserProfile.ProfilePath, $TaskBarRelativeProfilePath)
            $ProfileMatchedShortcuts = @()
            if ([IO.Directory]::Exists($ProfileTaskBarDirectory)) { $ProfileMatchedShortcuts = @(Find-MatchingPins $ProfileTaskBarDirectory $UnpinMatchPatterns) }
            if ($ProfileMatchedShortcuts.Count -eq 0) { Write-Log "    No matching pins in this profile -- skipping"; continue }
            $ProfileShortcutFilenames = @($ProfileMatchedShortcuts | ForEach-Object { [IO.Path]::GetFileName($_) })
            $null = Invoke-WithOfflineHive $UserProfile.SID $UserProfile.ProfilePath { param($OfflineRegistryKey) $null = Invoke-UnpinFromBlob $OfflineRegistryKey $ProfileShortcutFilenames }
            foreach ($ProfileShortcutPath in $ProfileMatchedShortcuts) { if ([IO.File]::Exists($ProfileShortcutPath)) { try { [IO.File]::Delete($ProfileShortcutPath) } catch { } } }
            Write-Log "    [file] Deleted $($ProfileMatchedShortcuts.Count) .lnk file(s)"
        }
    }
    foreach ($UnpinnedPath in $MatchedShortcutPaths) { Write-Console "  [-] $([IO.Path]::GetFileName($UnpinnedPath))" -Color Cyan }
    if ($UnpinFailedDeleteCount -gt 0) {
        Write-Banner 'FAIL' 'DarkRed' "$UnpinFailedDeleteCount item(s) could not be deleted"
        Write-Log "--- UNPIN FAILED : $UnpinFailedDeleteCount deletion(s) failed ---"
        Close-Log; exit 3
    }
    Write-Banner 'OK' 'DarkGreen' "Unpinned $($MatchedShortcutPaths.Count) item(s)$(if ($AllUsers) { ' (AllUsers)' })"
    Write-Log "--- UNPIN complete : $($MatchedShortcutPaths.Count) item(s) unpinned ---"
    Close-Log; exit 0
}


#region PIN : RESOLVE INPUT

Write-Banner 'PIN' 'DarkBlue' "$Pin$(if ($AllUsers) { ' (AllUsers)' })"
Write-OperationLogHeader 'PIN' "$Pin ($($ParsedInputItems.Count) parsed item(s))"
$UwpInputItems        = @($ParsedInputItems | Where-Object { $_.StartsWith('shell:AppsFolder\', [StringComparison]::OrdinalIgnoreCase) })
$FilesystemInputItems = @($ParsedInputItems | Where-Object { -not $_.StartsWith('shell:AppsFolder\', [StringComparison]::OrdinalIgnoreCase) })
Write-Log "[resolve] UWP inputs : $($UwpInputItems.Count) | filesystem inputs : $($FilesystemInputItems.Count)"
$ResolvedPinTargets               = @()
$AlreadyResolvedApplicationAumids = @{}
$AlreadySeenFilesystemPaths       = @{}

# -- Resolve UWP inputs --
# Exact AUMIDs (containing !) resolve in O(1) through ParseName; display-name and wildcard
# patterns are matched against the AppsFolder snapshot.
if ($UwpInputItems.Count -gt 0) {
    $ExactAumidInputs = @($UwpInputItems | Where-Object { $_ -notmatch '[*?]' -and $_.Contains('!') })
    $PatternUwpInputs = @($UwpInputItems | Where-Object { $_ -match '[*?]' -or -not $_.Contains('!') })
    if ($ExactAumidInputs.Count -gt 0) {
        $ShellApplicationCom    = New-Object -ComObject Shell.Application
        $AppsFolderNamespaceCom = $ShellApplicationCom.Namespace('shell:AppsFolder')
        foreach ($ExactUwpInput in $ExactAumidInputs) {
            $ResolvedAppItem = $AppsFolderNamespaceCom.ParseName($ExactUwpInput.Substring(17))
            if ($ResolvedAppItem) {
                if (-not $AlreadyResolvedApplicationAumids.ContainsKey($ResolvedAppItem.Path)) {
                    $AlreadyResolvedApplicationAumids[$ResolvedAppItem.Path] = $true
                    $ResolvedPinTargets += @{ PinType = 'UWP'; Aumid = $ResolvedAppItem.Path; DisplayName = $ResolvedAppItem.Name }
                    Write-Log "  [uwp] Resolved : '$($ResolvedAppItem.Name)' ($($ResolvedAppItem.Path))"
                }
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ResolvedAppItem)
            } else {
                Write-Console "  [!] Not found : $ExactUwpInput" -Color Yellow
                Write-Log "  [uwp] AUMID not found in shell:AppsFolder : '$($ExactUwpInput.Substring(17))'" 'Yellow'
            }
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($AppsFolderNamespaceCom)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ShellApplicationCom)
    }
    foreach ($PatternUwpInput in $PatternUwpInputs) {
        $UwpMatchPattern = $PatternUwpInput.Substring(17)
        if (-not $UwpMatchPattern) { continue }
        # Invalid wildcard syntax degrades to a literal pattern instead of crashing.
        try { $null = ('' -like $UwpMatchPattern) } catch { $UwpMatchPattern = [System.Management.Automation.WildcardPattern]::Escape($UwpMatchPattern) }
        $MatchedAnyApplication = $false
        foreach ($ApplicationEntry in (Get-AppsFolderSnapshot)) {
            if (($ApplicationEntry.DisplayName -like $UwpMatchPattern -or $ApplicationEntry.Aumid -like $UwpMatchPattern) -and -not $AlreadyResolvedApplicationAumids.ContainsKey($ApplicationEntry.Aumid)) {
                $AlreadyResolvedApplicationAumids[$ApplicationEntry.Aumid] = $true
                $ResolvedPinTargets += @{ PinType = 'UWP'; Aumid = $ApplicationEntry.Aumid; DisplayName = $ApplicationEntry.DisplayName }
                $MatchedAnyApplication = $true
                Write-Log "  [uwp] Pattern '$UwpMatchPattern' matched '$($ApplicationEntry.DisplayName)' ($($ApplicationEntry.Aumid))"
            }
        }
        if (-not $MatchedAnyApplication) {
            Write-Console "  [!] Not found : shell:AppsFolder\$UwpMatchPattern" -Color Yellow
            Write-Log "  [uwp] No installed application matched : '$UwpMatchPattern'" 'Yellow'
        }
    }
}

# -- Resolve filesystem inputs --
foreach ($FilesystemInput in $FilesystemInputItems) {
    Write-Log "  [fs] Resolving : '$FilesystemInput'..."
    $ResolvedFilePaths = @(Resolve-FilesystemInput $FilesystemInput)
    foreach ($ResolvedPath in $ResolvedFilePaths) {
        if (-not $ResolvedPath -or $AlreadySeenFilesystemPaths.ContainsKey($ResolvedPath)) { continue }
        $AlreadySeenFilesystemPaths[$ResolvedPath] = $true
        # An executable registered as an application is pinned through its AUMID rather than
        # its raw path, so the taskbar button carries the identity the application groups
        # its windows and registers its jump list under.
        $ExecutableIdentity = $null
        if ([IO.Path]::GetExtension($ResolvedPath).ToLower() -eq '.exe') { $ExecutableIdentity = Resolve-ExecutableIdentity $ResolvedPath }
        if ($ExecutableIdentity) {
            if (-not $AlreadyResolvedApplicationAumids.ContainsKey($ExecutableIdentity.Aumid)) {
                $AlreadyResolvedApplicationAumids[$ExecutableIdentity.Aumid] = $true
                $ResolvedPinTargets += @{ PinType = 'UWP'; Aumid = $ExecutableIdentity.Aumid; DisplayName = $ExecutableIdentity.DisplayName }
                Write-Log "  [fs] '$ResolvedPath' has application identity '$($ExecutableIdentity.Aumid)' ('$($ExecutableIdentity.DisplayName)')"
            }
        } else {
            $ResolvedPinTargets += @{ PinType = 'FS'; ResolvedPath = $ResolvedPath }
            Write-Log "  [fs] Resolved : '$ResolvedPath'"
        }
    }
    # Nothing on disk matched : a bare name (no path separators) is searched as an installed
    # application by display name or AUMID across shell:AppsFolder, the Start Menu and Quick
    # Launch. AUMID matches go through the UWP pipeline; shortcut matches are pinned as the
    # .lnk itself so its own AUMID is honoured downstream.
    if ($ResolvedFilePaths.Count -eq 0) {
        $ApplicationMatches = @()
        if ($FilesystemInput -notmatch '[/\\]') { $ApplicationMatches = @(Find-ApplicationMatches $FilesystemInput) }
        if ($ApplicationMatches.Count -gt 0) {
            foreach ($ApplicationMatch in $ApplicationMatches) {
                if ($ApplicationMatch.Kind -eq 'Aumid') {
                    if ($AlreadyResolvedApplicationAumids.ContainsKey($ApplicationMatch.Aumid)) { continue }
                    $AlreadyResolvedApplicationAumids[$ApplicationMatch.Aumid] = $true
                    $ResolvedPinTargets += @{ PinType = 'UWP'; Aumid = $ApplicationMatch.Aumid; DisplayName = $ApplicationMatch.DisplayName }
                    Write-Log "  [fs] '$FilesystemInput' matched application '$($ApplicationMatch.DisplayName)' (AUMID '$($ApplicationMatch.Aumid)')"
                } else {
                    if ($AlreadySeenFilesystemPaths.ContainsKey($ApplicationMatch.LnkPath)) { continue }
                    $AlreadySeenFilesystemPaths[$ApplicationMatch.LnkPath] = $true
                    $ResolvedPinTargets += @{ PinType = 'FS'; ResolvedPath = $ApplicationMatch.LnkPath }
                    Write-Log "  [fs] '$FilesystemInput' matched shortcut '$($ApplicationMatch.LnkPath)'"
                }
            }
        } else {
            Write-Console "  [!] Not found : $FilesystemInput" -Color Yellow
            Write-Log "  [fs] No file or installed application found for '$FilesystemInput'" 'Yellow'
        }
    }
}
Write-Log "[resolve] Total resolved pin targets : $($ResolvedPinTargets.Count)"
if ($ResolvedPinTargets.Count -eq 0) {
    Write-Console "  [X] No items found to pin" -Color Red
    Write-Log "No items could be resolved -- aborting" 'Red'
    Write-Console ""
    Close-Log; exit 2
}


#region PIN : BLOB INJECTION

# The modern pin strategy : place a .lnk in the TaskBar pinned directory, build a binary
# blob entry (PIDL + BEEF001D extension block), append it to the Favorites blob, then post
# 0x446 so the taskbar re-enumerates its pins. The BEEF001D parsing name is the critical
# piece -- the handler uses it to resolve the item when its ILIsEqual primary match fails,
# which it always does for externally written PIDLs (the SHITEMID timestamps differ from
# the handler's cached copy). Items that cannot be prepared fall back to Quick Launch.
$SuccessfullyPinnedCount      = 0
$BlobEntriesReadyForInjection = @()
$ItemsDeferredToQuickLaunch   = @()
if ($DirectBlobWriteIsSupported) {
    Write-Console "  [>] Preparing blob entries ($($ResolvedPinTargets.Count) item(s))..." -Color DarkGray -NoNewline
    Write-Log "[blob-prep] Preparing $($ResolvedPinTargets.Count) item(s)..."
    Initialize-NativeHelper
    $WshShellForPinCreation = $null
    foreach ($PinTarget in $ResolvedPinTargets) {
        $SourceShortcutPath  = $null
        $ShortcutIsTemporary = $false
        if ($PinTarget.PinType -eq 'UWP') {
            # UWP : the AUMID is both the shortcut identity and the BEEF001D parsing name.
            $PinTargetDisplayName = $PinTarget.DisplayName
            $Beef001dParsingName  = $PinTarget.Aumid
            $DestinationLnkPath   = [IO.Path]::Combine($TaskBarPinnedDirectory, "$($PinTarget.DisplayName -replace '[<>:"/\\|?*]', '_').lnk")
            $DestinationLnkAlreadyPinned = [IO.File]::Exists($DestinationLnkPath)
            if (-not $DestinationLnkAlreadyPinned) {
                Write-Log "  [uwp] Creating shortcut '$([IO.Path]::GetFileName($DestinationLnkPath))' for AUMID '$($PinTarget.Aumid)'..."
                if (-not [TaskbarPin]::CreateAppShortcut($PinTarget.Aumid, $DestinationLnkPath)) {
                    Write-Log "  [uwp] CreateAppShortcut FAILED for '$($PinTarget.Aumid)' -- deferring to Quick Launch" 'Yellow'
                    if ([IO.File]::Exists($DestinationLnkPath)) { try { [IO.File]::Delete($DestinationLnkPath) } catch { } }
                    $ItemsDeferredToQuickLaunch += $PinTarget; continue
                }
            }
        } else {
            # Filesystem : create (or reuse) a .lnk and extract the BEEF001D parsing name.
            # An existing destination .lnk is reused to avoid icon cache corruption from the
            # SHCNE_UPDATEITEM burst a delete/recreate window would trigger.
            $Beef001dContentReference = [ref]''
            $SourceShortcutPath   = New-TargetShortcut $PinTarget.ResolvedPath $Beef001dContentReference ([ref]$WshShellForPinCreation)
            $Beef001dParsingName  = $Beef001dContentReference.Value
            $ShortcutIsTemporary  = ($SourceShortcutPath -ne $PinTarget.ResolvedPath)
            $PinTargetDisplayName = [IO.Path]::GetFileName($SourceShortcutPath)
            $DestinationLnkPath   = [IO.Path]::Combine($TaskBarPinnedDirectory, $PinTargetDisplayName)
            $DestinationLnkAlreadyPinned = [IO.File]::Exists($DestinationLnkPath)
            Write-Log "  [fs] Target : '$($PinTarget.ResolvedPath)' | shortcut : '$PinTargetDisplayName' | BEEF001D : '$Beef001dParsingName'"
            if (-not $DestinationLnkAlreadyPinned) { [IO.File]::Copy($SourceShortcutPath, $DestinationLnkPath) }
        }
        # Build the binary blob entry. The .lnk lives under the profile so SHParseDisplayName
        # yields a namespace PIDL; cross-user mode uses filesystem PIDLs instead because
        # SHParseDisplayName cannot resolve paths under another user's profile.
        $SerializedBlobEntry = $null
        if ($Beef001dParsingName) {
            if ($IsRunningCrossUser) { $SerializedBlobEntry = [TaskbarPin]::GetBlobEntryFs($DestinationLnkPath, $Beef001dParsingName) }
            else                     { $SerializedBlobEntry = [TaskbarPin]::GetBlobEntryEx($DestinationLnkPath, $Beef001dParsingName) }
        }
        if ($SerializedBlobEntry) {
            $BlobEntriesReadyForInjection += @{ DestinationLnkPath = $DestinationLnkPath; SerializedBlobEntry = $SerializedBlobEntry; DisplayName = $PinTargetDisplayName; ShortcutIsTemporary = $ShortcutIsTemporary; SourceShortcutPath = $SourceShortcutPath; Beef001dContent = $Beef001dParsingName }
            Write-Log "  [blob] Entry ready for '$PinTargetDisplayName' : $($SerializedBlobEntry.Length) bytes"
        } else {
            Write-Log "  [blob] Blob entry could not be built for '$PinTargetDisplayName' (SHParseDisplayName failed) -- deferring to Quick Launch" 'Yellow'
            # Only clean up a .lnk created by this run : never delete a pre-existing live pin.
            if ($DestinationLnkPath -and -not $DestinationLnkAlreadyPinned -and [IO.File]::Exists($DestinationLnkPath)) { try { [IO.File]::Delete($DestinationLnkPath) } catch { } }
            $ItemsDeferredToQuickLaunch += $PinTarget
        }
    }
    if ($WshShellForPinCreation) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($WshShellForPinCreation) }
    # Write all prepared entries to the registry in a single atomic operation, serialized
    # with explorer.exe through the TaskbarPinListMutex, then notify the taskbar.
    if ($BlobEntriesReadyForInjection.Count -gt 0) {
        $MutexWasAcquired = [TaskbarPin]::AcquirePinMutex(5000)
        Write-Log "[mutex] TaskbarPinListMutex acquired : $MutexWasAcquired"
        $BlobEntriesAddedCount = 0
        try {
            $TaskBandRegistryKey = Open-EffectiveTaskbandKey $true
            try {
                $ExistingFavoritesBlob = $TaskBandRegistryKey.GetValue('Favorites', $null, $DoNotExpandRegistryOption)
                $BlobEntriesAddedCount = Write-BlobToRegistryKey $TaskBandRegistryKey $ExistingFavoritesBlob $BlobEntriesReadyForInjection
            } finally { $TaskBandRegistryKey.Close() }
        } finally {
            if ($MutexWasAcquired) { [TaskbarPin]::ReleasePinMutex(); Write-Log "[mutex] TaskbarPinListMutex released" }
        }
        if ($BlobEntriesAddedCount -gt 0) { [TaskbarPin]::SendPinNotify(); Write-Log "[notify] 0x446 posted to the taskbar pinned-items band" }
        Write-Console " done" -Color Green
        foreach ($ReadyEntry in $BlobEntriesReadyForInjection) {
            Write-Console "  [+] $($ReadyEntry.DisplayName)" -Color Cyan
            $SuccessfullyPinnedCount++
            if ($ReadyEntry.ShortcutIsTemporary -and $ReadyEntry.SourceShortcutPath) { try { [IO.File]::Delete($ReadyEntry.SourceShortcutPath) } catch { } }
        }
        Write-Console ""
        Write-Log "[blob-write] $BlobEntriesAddedCount new entries written, $SuccessfullyPinnedCount items pinned"
    } else {
        Write-Console " nothing to inject" -Color Yellow
        Write-Log "[blob-write] No blob entries to write (all items were duplicates or failed preparation)"
        Write-Console ""
    }
    # AllUsers : copy each shortcut into every other profile and write profile-specific blob
    # entries (filesystem PIDLs) into each offline hive.
    if ($AllUsers -and $BlobEntriesReadyForInjection.Count -gt 0) {
        $AllUserProfiles = @(Get-UserProfiles)
        Write-Log "[allUsers] Replicating to $($AllUserProfiles.Count) additional profile(s)..."
        $AllUsersProfilesUpdatedCount = 0
        foreach ($UserProfile in $AllUserProfiles) {
            $ProfileTaskBarDirectory = [IO.Path]::Combine($UserProfile.ProfilePath, $TaskBarRelativeProfilePath)
            Write-Log "  [profile] $($UserProfile.ProfilePath) (SID : $($UserProfile.SID))"
            if (-not [IO.Directory]::Exists($ProfileTaskBarDirectory)) {
                try   { $null = [IO.Directory]::CreateDirectory($ProfileTaskBarDirectory) }
                catch { Write-Log "    [file] FAILED to create TaskBar directory : $_" 'Yellow'; continue }
            }
            $ProfileSpecificBlobEntries = @()
            foreach ($ReadyEntry in $BlobEntriesReadyForInjection) {
                $ProfileShortcutPath = [IO.Path]::Combine($ProfileTaskBarDirectory, [IO.Path]::GetFileName($ReadyEntry.DestinationLnkPath))
                if (-not [IO.File]::Exists($ProfileShortcutPath)) {
                    try { [IO.File]::Copy($ReadyEntry.DestinationLnkPath, $ProfileShortcutPath) } catch { Write-Log "    [file] Copy FAILED for '$([IO.Path]::GetFileName($ProfileShortcutPath))' : $_" 'Yellow'; continue }
                }
                $ProfileBlobEntry = [TaskbarPin]::GetBlobEntryFs($ProfileShortcutPath, $ReadyEntry.Beef001dContent)
                if ($ProfileBlobEntry) { $ProfileSpecificBlobEntries += @{ DestinationLnkPath = $ProfileShortcutPath; SerializedBlobEntry = $ProfileBlobEntry } }
            }
            if ($ProfileSpecificBlobEntries.Count -eq 0) { Write-Log "    No blob entries could be built -- skipping"; continue }
            $OfflineHiveResult = Invoke-WithOfflineHive $UserProfile.SID $UserProfile.ProfilePath {
                param($OfflineRegistryKey)
                $OfflineFavoritesBlob = $OfflineRegistryKey.GetValue('Favorites', $null, $DoNotExpandRegistryOption)
                $OfflineAddedCount = Write-BlobToRegistryKey $OfflineRegistryKey $OfflineFavoritesBlob $ProfileSpecificBlobEntries
                Write-Log "    [blob] $OfflineAddedCount entries written to offline profile"
            }
            if ($OfflineHiveResult) { $AllUsersProfilesUpdatedCount++ }
        }
        Write-Console "  [*] AllUsers : $AllUsersProfilesUpdatedCount profile(s) updated" -Color DarkCyan
        Write-Log "[allUsers] $AllUsersProfilesUpdatedCount profile(s) updated"
        Write-Console ""
    }
    $RemainingPinTargets = @($ItemsDeferredToQuickLaunch)
} else {
    Write-Log "[blob-prep] Direct blob injection NOT available -- all $($ResolvedPinTargets.Count) item(s) will go through Quick Launch"
    $RemainingPinTargets = @($ResolvedPinTargets)
}


#region PIN : QUICK LAUNCH FALLBACK

# Legacy strategy for systems lacking the TaskBar pinned directory or the Taskband registry
# key (primarily Vista) : copy the .lnk into the Quick Launch directory. UWP apps cannot be
# pinned this way.
if ($RemainingPinTargets.Count -gt 0 -and $QuickLaunchDirectoryExists) {
    Write-Log "[quicklaunch] Processing $($RemainingPinTargets.Count) remaining item(s)..."
    $WshShellForFallback = $null
    foreach ($FallbackTarget in $RemainingPinTargets) {
        if ($FallbackTarget.PinType -eq 'UWP') { Write-Log "  [quicklaunch] Skipping UWP item '$($FallbackTarget.DisplayName)' -- Quick Launch cannot pin UWP apps" 'Yellow'; continue }
        $Beef001dFallbackRef  = [ref]''
        $FallbackShortcutPath = New-TargetShortcut $FallbackTarget.ResolvedPath $Beef001dFallbackRef ([ref]$WshShellForFallback)
        $FallbackIsTemporary  = ($FallbackShortcutPath -ne $FallbackTarget.ResolvedPath)
        $FallbackShortcutName = [IO.Path]::GetFileName($FallbackShortcutPath)
        [IO.File]::Copy($FallbackShortcutPath, [IO.Path]::Combine($QuickLaunchDirectory, $FallbackShortcutName), $true)
        Write-Console "  [+] $FallbackShortcutName" -Color Cyan
        Write-Log "  [quicklaunch] Copied '$FallbackShortcutName' to Quick Launch directory"
        $SuccessfullyPinnedCount++
        if ($FallbackIsTemporary -and $FallbackShortcutPath) { try { [IO.File]::Delete($FallbackShortcutPath) } catch { } }
    }
    if ($WshShellForFallback) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($WshShellForFallback) }
}


#region FINAL STATUS

if ($SuccessfullyPinnedCount -gt 0) {
    Write-Banner 'OK' 'DarkGreen' "Pinned $SuccessfullyPinnedCount item(s)$(if ($AllUsers) { ' (AllUsers)' })"
    Write-Log "--- PIN complete : $SuccessfullyPinnedCount pinned ---"
    Close-Log; exit 0
}
Write-Banner 'FAIL' 'DarkRed' "No items could be pinned"
Write-Log "--- PIN FAILED : 0/$($ResolvedPinTargets.Count) items pinned ---"
Close-Log; exit 3
