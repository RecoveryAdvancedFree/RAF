using System.Runtime.InteropServices;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>
/// התקנים ש-Windows מרגיש שהם מחוברים, אך לא הצליח להפעיל.
///
/// כונן כזה אינו מקבל מספר דיסק, ולכן אינו נראה בשום רשימת כוננים — גם
/// לא בניהול הדיסקים. הוא מופיע רק במנהל ההתקנים, עם סימן קריאה צהוב.
/// התוכנה קוראת את אותו מידע ומציגה אותו, כדי שהמשתמש יידע שהכונן כן
/// מחובר, מה Windows מדווח עליו, ומה אפשר לנסות.
/// </summary>
public static class FailedDevices
{
    private static readonly Guid ClassDiskDrive = new("4d36e967-e325-11ce-bfc1-08002be10318");
    private static readonly Guid ClassUsb = new("36fc9e60-c465-11cf-8056-444553540000");

    public static List<FailedDevice> Find()
    {
        // גם SetupAPI פונה למנהלי התקנים — מגבלת זמן כמו בכל שאילתה אחרת.
        return Bounded.TryRun(FindCore, TimeSpan.FromSeconds(5), out var list) && list is not null
            ? list
            : new List<FailedDevice>();
    }

    private static List<FailedDevice> FindCore()
    {
        var result = new List<FailedDevice>();
        Collect(ClassDiskDrive, result, isUsbClass: false);
        Collect(ClassUsb, result, isUsbClass: true);
        return result;
    }

    private static void Collect(Guid classGuid, List<FailedDevice> result, bool isUsbClass)
    {
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (CM_Get_DevNode_Status(out _, out uint problem, data.DevInst, 0) != 0 || problem == 0)
                    continue;

                string name = Property(set, ref data, SPDRP_FRIENDLYNAME)
                              ?? Property(set, ref data, SPDRP_DEVICEDESC) ?? "התקן לא מזוהה";
                string ids = (Property(set, ref data, SPDRP_HARDWAREID) ?? "") + "|" +
                             (Property(set, ref data, SPDRP_COMPATIBLEIDS) ?? "");

                // במחלקת USB מוצגים רק התקני אחסון, או התקן שנכשל כבר בזיהוי
                // הראשוני — שם אי אפשר לדעת מה הוא, וייתכן שזה הכונן.
                // מצלמה או עכבר תקולים אינם רלוונטיים לשיחזור.
                bool storage = ids.Contains(@"USB\Class_08", StringComparison.OrdinalIgnoreCase);
                bool enumFailure = ids.Contains("_FAILURE", StringComparison.OrdinalIgnoreCase);
                if (isUsbClass && !storage && !enumFailure) continue;

                result.Add(new FailedDevice
                {
                    Name = enumFailure && !storage ? "התקן USB שנכשל בזיהוי" : name.Trim(),
                    WindowsName = name.Trim(),
                    ProblemCode = (int)problem,
                    Problem = Describe((int)problem, enumFailure),
                    Advice = Advise((int)problem, isUsbClass || enumFailure),
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string Describe(int code, bool enumFailure)
    {
        if (enumFailure)
            return "משהו מחובר ליציאת ה-USB, אך הוא לא הצליח אפילו להציג את עצמו ל-Windows. " +
                   "כך נראה לרוב כונן שהבקר שלו או המתאם שלו תקולים.";

        return code switch
        {
            10 => "Windows מרגיש שהכונן מחובר, אך לא הצליח להפעיל אותו (קוד 10).",
            43 => "Windows עצר את הכונן כי הוא דיווח על תקלה (קוד 43).",
            28 => "לא מותקן לכונן מנהל התקן (קוד 28).",
            22 => "הכונן מושבת ב-Windows (קוד 22).",
            19 or 31 or 39 => $"יש בעיה במנהל ההתקן של הכונן (קוד {code}).",
            _ => $"Windows מדווח על בעיה בכונן (קוד {code}).",
        };
    }

    private static string Advise(int code, bool usb)
    {
        if (code == 22)
            return "אפשר להפעיל אותו מחדש במנהל ההתקנים: קליק ימני על הכונן ← \"הפעל התקן\".";

        var tips = new StringBuilder("נתק את הכונן, המתן כמה שניות וחבר אותו מחדש. ");
        if (usb)
            tips.Append("נסה יציאת USB אחרת — עדיף יציאה בגב המחשב — וכבל או מתאם אחר. ");
        tips.Append("בכונן קשיח פנימי: חיבור ישיר בכבל SATA במקום מתאם USB מצליח לעיתים קרובות " +
                    "גם כשהמתאם נכשל. כשהכונן יזוהה — צור ממנו תמונה לפני כל דבר אחר.");
        return tips.ToString();
    }

    // ------------------------------------------------------------ SetupAPI

    private const uint DIGCF_PRESENT = 0x02;
    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_HARDWAREID = 0x01;
    private const uint SPDRP_COMPATIBLEIDS = 0x02;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    /// <summary>קריאת מאפיין טקסט של התקן. רשימה (MULTI_SZ) מוחזרת מופרדת ב-|.</summary>
    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        byte[] buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out uint needed))
            return null;

        string text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length));
        text = string.Join("|", text.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType,
        byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
