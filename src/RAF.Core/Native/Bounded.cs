namespace RAF.Core.Native;

/// <summary>
/// הרצת פעולה מול התקן עם מגבלת זמן.
///
/// קריאה מכונן גוסס עלולה להיתקע בתוך מנהל ההתקן לדקות ארוכות, ואין דרך
/// אמינה לבטל אותה. לכן הפעולה רצה בתהליכון רקע משלה: אם היא לא הסתיימה
/// בזמן, הקורא ממשיך הלאה — והתוכנה לא נתקעת. אם היא מסתיימת מאוחר יותר,
/// התוצאה מגיעה דרך <paramref name="late"/>, כדי שאפשר יהיה לעדכן את התצוגה.
/// </summary>
internal static class Bounded
{
    /// <returns>true אם הפעולה הסתיימה בזמן (גם אם זרקה חריגה — אז הערך ברירת מחדל).</returns>
    internal static bool TryRun<T>(Func<T> work, TimeSpan limit, out T? result, Action<T?>? late = null)
    {
        var sync = new object();
        var done = new ManualResetEventSlim(false);
        bool timedOut = false;
        T? value = default;

        var thread = new Thread(() =>
        {
            T? local = default;
            try { local = work(); }
            catch { /* התקן שנכשל נחשב כחסר מידע, לא כשגיאה של התוכנה */ }

            bool deliverLate;
            lock (sync)
            {
                value = local;
                deliverLate = timedOut;
                done.Set();
            }

            if (deliverLate)
            {
                try { late?.Invoke(local); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "RAF device probe",
        };

        thread.Start();

        bool finished = done.Wait(limit);
        lock (sync)
        {
            if (!finished && !done.IsSet)
            {
                timedOut = true;
                result = default;
                return false;
            }

            result = value;
            return true;
        }
    }
}
