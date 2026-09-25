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

    /// <summary>ההרשאה שהמשתמש נתן (בחלון סיסמה אחד לכל הכוננים), בצורה שאפשר להעביר ל-authopen.</summary>
    private static byte[]? _external;

    /// <summary>
    /// בקשת הרשאה לקריאת כמה כוננים — בחלון סיסמה אחד, עם הסבר בשפת התוכנה. אחר כך
    /// authopen מקבל את ההרשאה הזו (‎-extauth) ולא שואל שוב על כל כונן. false — המשתמש ביטל.
    /// </summary>
    public static bool Authorize(IEnumerable<string> paths, string prompt)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (geteuid() == 0) return true;                    // מנהל פותח ישירות — אין מה לבקש
        var rights = paths.Select(p => "sys.openfile.readonly." + p).Distinct().ToArray();
        if (rights.Length == 0) return true;

        var names = rights.Select(r => Marshal.StringToCoTaskMemUTF8(r)).ToArray();
        IntPtr promptText = Marshal.StringToCoTaskMemUTF8(prompt), promptName = Marshal.StringToCoTaskMemUTF8("prompt");
        int itemSize = Marshal.SizeOf<AuthItem>();
        IntPtr items = Marshal.AllocHGlobal(itemSize * names.Length), environment = Marshal.AllocHGlobal(itemSize);
        try
        {
            for (int i = 0; i < names.Length; i++)
                Marshal.StructureToPtr(new AuthItem { Name = names[i] }, items + i * itemSize, false);
            Marshal.StructureToPtr(new AuthItem { Name = promptName, ValueLength = (nuint)Encoding.UTF8.GetByteCount(prompt), Value = promptText },
                environment, false);
            var requested = new AuthSet { Count = (uint)names.Length, Items = items };
            var env = new AuthSet { Count = 1, Items = environment };

            if (AuthorizationCreate(IntPtr.Zero, IntPtr.Zero, 0, out IntPtr auth) != 0) return false;
            const int Interaction = 1, Extend = 2, PreAuthorize = 16;
            if (AuthorizationCopyRights(auth, ref requested, ref env, Interaction | Extend | PreAuthorize, IntPtr.Zero) != 0) return false;
            byte[] form = new byte[32];
            if (AuthorizationMakeExternalForm(auth, form) != 0) return false;
            _external = form;
            return true;
        }
        finally
        {
            foreach (var n in names) Marshal.FreeCoTaskMem(n);
            Marshal.FreeCoTaskMem(promptText);
            Marshal.FreeCoTaskMem(promptName);
            Marshal.FreeHGlobal(items);
            Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>ידית לכונן, או null כשהמשתמש ביטל או שהפתיחה נכשלה.</summary>
    public static SafeFileHandle? Open(string path, bool write)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(Tool)) return null;
        lock (Opened)
        {
            if (!Opened.TryGetValue((path, write), out int fd))
            {
                fd = Request(path, write, _external is not null && !write);
                // ההרשאה שניתנה לא התקבלה (למשל פג תוקפה) — authopen יבקש סיסמה בעצמו.
                if (fd < 0 && _external is not null && !write) fd = Request(path, write, false);
                if (fd < 0) return null;
                Opened[(path, write)] = fd;
            }
            int copy = dup(fd);
            return copy < 0 ? null : new SafeFileHandle(copy, ownsHandle: true);
        }
    }

    /// <summary>הרצת authopen עם הפלט שלו מחובר לשקע, וקבלת הידית שהוא שולח בו.</summary>
    private static int Request(string path, bool write, bool useGranted)
    {
        int[] sockets = new int[2];
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) != 0) return -1;
        try
        {
            IntPtr actions = IntPtr.Zero;
            if (posix_spawn_file_actions_init(ref actions) != 0) return -1;
            // הרשאה שכבר ניתנה עוברת ל-authopen בקלט שלו — בלי חלון סיסמה נוסף.
            byte[]? external = useGranted ? _external : null;
            int[] input = [-1, -1];
            if (external is not null && pipe(input) != 0) return -1;
            var args = new List<string> { Tool, "-stdoutpipe" };
            if (external is not null) args.Add("-extauth");
            if (write) args.AddRange(["-o", "2"]);              // O_RDWR
            args.Add(path);
            IntPtr[] argv = args.Select(a => Marshal.StringToCoTaskMemUTF8(a)).Append(IntPtr.Zero).ToArray();
            IntPtr[] envp = Environ();
            try
            {
                posix_spawn_file_actions_adddup2(ref actions, sockets[1], 1);
                if (external is not null) posix_spawn_file_actions_adddup2(ref actions, input[0], 0);
                if (posix_spawn(out int pid, Tool, ref actions, IntPtr.Zero, argv, envp) != 0) return -1;
                close(sockets[1]);
                sockets[1] = -1;
                if (external is not null)
                {
                    close(input[0]);
                    WriteFd(input[1], external, (nuint)external.Length);
                    close(input[1]);
                }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthItem { public IntPtr Name; public nuint ValueLength; public IntPtr Value; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthSet { public uint Count; public IntPtr Items; }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int AuthorizationCreate(IntPtr rights, IntPtr environment, int flags, out IntPtr authorization);
    [DllImport(Security)] private static extern int AuthorizationCopyRights(IntPtr authorization, ref AuthSet rights, ref AuthSet environment, int flags, IntPtr authorizedRights);
    [DllImport(Security)] private static extern int AuthorizationMakeExternalForm(IntPtr authorization, byte[] externalForm);

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int[] fds);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", EntryPoint = "write", SetLastError = true)] private static extern nint WriteFd(int fd, byte[] buffer, nuint count);
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
