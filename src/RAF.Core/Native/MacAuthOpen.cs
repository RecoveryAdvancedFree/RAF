using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RAF.Core.Native;

/// <summary>
/// פתיחת כונן במק דרך authopen — הכלי הרשמי של Apple לפתיחת קובץ שדורש הרשאת מנהל.
/// הוא מציג את חלון הסיסמה של המערכת, פותח את הכונן, ומעביר לתוכנה רק את הידית שלו
/// (דרך שקע יוניקס, בהודעת SCM_RIGHTS). כך התוכנה עצמה לא רצה כמנהל — ורק הכונן
/// שנבחר נפתח.
///
/// ידית שנפתחה נשמרת לכל משך הריצה, וכל פתיחה נוספת מקבלת עותק שלה (dup): הסיסמה
/// נדרשת פעם אחת לכל כונן, ולא בכל קריאה.
/// </summary>
internal static class MacAuthOpen
{
    private const string Tool = "/usr/libexec/authopen";

    private static readonly ConcurrentDictionary<(string Path, bool Write), int> Opened = new();

    /// <summary>ידית לכונן, או null כשהמשתמש ביטל או שהפתיחה נכשלה.</summary>
    public static SafeFileHandle? Open(string path, bool write)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(Tool)) return null;
        lock (Opened)
        {
            if (!Opened.TryGetValue((path, write), out int fd))
            {
                fd = Request(path, write);
                if (fd < 0) return null;
                Opened[(path, write)] = fd;
            }
            int copy = dup(fd);
            return copy < 0 ? null : new SafeFileHandle(copy, ownsHandle: true);
        }
    }

    /// <summary>הרצת authopen עם הפלט שלו מחובר לשקע, וקבלת הידית שהוא שולח בו.</summary>
    private static int Request(string path, bool write)
    {
        int[] sockets = new int[2];
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) != 0) return -1;
        try
        {
            IntPtr actions = IntPtr.Zero;
            if (posix_spawn_file_actions_init(ref actions) != 0) return -1;
            var args = new List<string> { Tool, "-stdoutpipe" };
            if (write) args.AddRange(["-o", "2"]);              // O_RDWR
            args.Add(path);
            IntPtr[] argv = args.Select(a => Marshal.StringToCoTaskMemUTF8(a)).Append(IntPtr.Zero).ToArray();
            IntPtr[] envp = Environ();
            try
            {
                posix_spawn_file_actions_adddup2(ref actions, sockets[1], 1);
                if (posix_spawn(out int pid, Tool, ref actions, IntPtr.Zero, argv, envp) != 0) return -1;
                close(sockets[1]);
                sockets[1] = -1;

                int fd = ReceiveHandle(sockets[0]);
                waitpid(pid, out int status, 0);
                if (fd >= 0 && status != 0) { close(fd); fd = -1; }
                return fd;
            }
            finally
            {
                posix_spawn_file_actions_destroy(ref actions);
                foreach (var p in argv.Concat(envp)) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
        }
        finally
        {
            close(sockets[0]);
            if (sockets[1] >= 0) close(sockets[1]);
        }
    }

    /// <summary>קבלת ידית שנשלחה בשקע (הודעת בקרה SCM_RIGHTS). -1 — לא נשלחה.</summary>
    private static unsafe int ReceiveHandle(int socket)
    {
        byte* data = stackalloc byte[64];
        byte* control = stackalloc byte[ControlSpace];
        var iov = new IoVec { Base = (IntPtr)data, Length = 64 };
        var message = new MsgHdr
        {
            Iov = (IntPtr)(&iov), IovLength = 1,
            Control = (IntPtr)control, ControlLength = ControlSpace,
        };
        long received = recvmsg(socket, ref message, 0);
        if (received < 0 || message.ControlLength < 16) return -1;

        // cmsghdr: אורך (4), רמה (4), סוג (4), ואז הנתונים — מיושרים ל-4 בתים במק.
        int level = *(int*)(control + 4), type = *(int*)(control + 8);
        return level == SOL_SOCKET && type == SCM_RIGHTS ? *(int*)(control + 12) : -1;
    }

    private static IntPtr[] Environ()
        => Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Select(k => Marshal.StringToCoTaskMemUTF8(k + "=" + Environment.GetEnvironmentVariable(k)))
            .Append(IntPtr.Zero).ToArray();

    private const int AF_UNIX = 1, SOCK_STREAM = 1, SOL_SOCKET = 0xFFFF, SCM_RIGHTS = 1, ControlSpace = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec { public IntPtr Base; public nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public IntPtr Name;
        public uint NameLength;
        public IntPtr Iov;
        public int IovLength;
        public IntPtr Control;
        public uint ControlLength;
        public int Flags;
    }

    [DllImport("libc", SetLastError = true)] private static extern int socketpair(int domain, int type, int protocol, int[] sv);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern long recvmsg(int socket, ref MsgHdr message, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(ref IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newfd);
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref IntPtr actions,
        IntPtr attributes, IntPtr[] argv, IntPtr[] envp);
}
