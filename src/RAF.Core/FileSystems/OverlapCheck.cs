using RAF.Core.Model;

namespace RAF.Core.FileSystems;

/// <summary>
/// זיהוי קבצים מחוקים שתוכנם נדרס על ידי קובץ מחוק אחר.
///
/// מפת ההקצאה מראה רק מה תפוס עכשיו. קובץ שנכתב על אשכולות של קובץ מחוק —
/// ונמחק גם הוא — לא משאיר בה סימן: האשכולות שוב "פנויים", והקובץ הישן נראה
/// שלם, אבל התוכן שבהם של הקובץ החדש. העקבה נשארת במקום אחר: רשומה מחוקה
/// שמצביעה על אותם אשכולות, עם תאריך מאוחר יותר. כשזה המצב, הקובץ המוקדם
/// מאבד את החלק החופף — ומדורג בהתאם, עם שם הקובץ שדרס אותו.
///
/// נבדק על כונן אמיתי: קובץ של 12MB שנכתב ונמחק על אשכולות של תמונה מחוקה
/// השאיר את התמונה "מצוינת" לפי מפת ההקצאה — ושוחזרה עם תוכן זר.
/// </summary>
internal static class OverlapCheck
{
    private readonly record struct Span(long Start, long End, int File);

    /// <summary>עדכון הדירוג של קבצים מחוקים שנדרסו. מחזיר כמה קבצים עודכנו.</summary>
    internal static int Apply(List<RecoveredFile> files)
    {
        var deleted = files
            .Where(f => f.IsDeleted && !f.IsDirectory && f.Extents.Count > 0 && f.ResidentData is null)
            .ToList();
        if (deleted.Count < 2) return 0;

        // כל מקטע כטווח אשכולות, ממוין לפי התחלה — מעבר אחד מוצא את כל החפיפות.
        var spans = new List<Span>();
        for (int i = 0; i < deleted.Count; i++)
            foreach (var e in deleted[i].Extents)
                if (!e.IsSparse && e.ClusterCount > 0)
                    spans.Add(new Span(e.StartCluster, e.StartCluster + e.ClusterCount, i));
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var lost = new long[deleted.Count];
        var lostTo = new Dictionary<int, HashSet<string>>();
        var unclear = new Dictionary<int, HashSet<string>>();
        var active = new List<Span>();

        foreach (var s in spans)
        {
            active.RemoveAll(a => a.End <= s.Start);

            foreach (var a in active)
            {
                if (a.File == s.File) continue;
                long overlap = Math.Min(a.End, s.End) - s.Start;
                if (overlap <= 0) continue;

                var older = Judge(deleted[a.File], deleted[s.File]);
                switch (older)
                {
                    case Verdict.FirstOlder:
                        Lose(a.File, s.File, overlap);
                        break;
                    case Verdict.SecondOlder:
                        Lose(s.File, a.File, overlap);
                        break;
                    case Verdict.Unclear:
                        Note(unclear, a.File, deleted[s.File].Name);
                        Note(unclear, s.File, deleted[a.File].Name);
                        break;
                }
            }

            active.Add(s);
        }

        void Lose(int victim, int winner, long clusters)
        {
            lost[victim] += clusters;
            Note(lostTo, victim, deleted[winner].Name);
        }

        int changed = 0;
        for (int i = 0; i < deleted.Count; i++)
        {
            var f = deleted[i];
            if (f.Quality == RecoveryQuality.Unrecoverable) continue;

            if (lost[i] > 0)
            {
                long total = f.Extents.Where(e => !e.IsSparse).Sum(e => e.ClusterCount);
                double ratio = Math.Min(1.0, (double)lost[i] / Math.Max(1, total));
                string by = Names(lostTo[i]);

                var (quality, tail) = ratio switch
                {
                    < 0.15 => (RecoveryQuality.Good, "חלק קטן מהקובץ ישוחזר עם תוכן זר."),
                    < 0.85 => (RecoveryQuality.Poor, "הקובץ ישוחזר פגום."),
                    _ => (RecoveryQuality.Unrecoverable, "כמעט כל התוכן שלו נדרס."),
                };

                if (quality > f.Quality) f.Quality = quality;
                f.QualityReason = $"קובץ מחוק אחר שנכתב אחריו — {by} — תפס כ-{ratio:P0} מהאשכולות של הקובץ " +
                                  $"הזה, ולכן התוכן שבהם כבר אינו שלו. {tail}";
                changed++;
            }
            else if (unclear.TryGetValue(i, out var others) && f.Quality < RecoveryQuality.Good)
            {
                f.Quality = RecoveryQuality.Good;
                f.QualityReason += $" קובץ מחוק אחר ({Names(others)}) תפס חלק מאותם אשכולות, " +
                                   "ולא ידוע מי מהשניים נכתב אחרון — ייתכן שחלק מהתוכן שלו.";
                changed++;
            }
        }

        return changed;
    }

    private enum Verdict { FirstOlder, SecondOlder, Unclear, Same }

    /// <summary>
    /// מי מהשניים נכתב קודם. B נכתב אחרי A אם נוצר אחרי השינוי האחרון של A —
    /// ואז הוא זה שדרס. רשומה כפולה של אותו קובץ (אותו מיקום, גודל ותאריכים)
    /// אינה חפיפה בכלל.
    /// </summary>
    private static Verdict Judge(RecoveredFile a, RecoveredFile b)
    {
        if (a.Size == b.Size && a.Extents[0].StartCluster == b.Extents[0].StartCluster &&
            a.Modified == b.Modified && a.Created == b.Created)
            return Verdict.Same;

        DateTime? aBorn = a.Created ?? a.Modified, aLast = a.Modified ?? a.Created;
        DateTime? bBorn = b.Created ?? b.Modified, bLast = b.Modified ?? b.Created;

        bool bAfterA = bBorn is not null && aLast is not null && bBorn > aLast;
        bool aAfterB = aBorn is not null && bLast is not null && aBorn > bLast;

        return bAfterA && !aAfterB ? Verdict.FirstOlder
             : aAfterB && !bAfterA ? Verdict.SecondOlder
             : Verdict.Unclear;
    }

    private static void Note(Dictionary<int, HashSet<string>> map, int file, string name)
    {
        if (!map.TryGetValue(file, out var set)) map[file] = set = new HashSet<string>();
        set.Add(name);
    }

    /// <summary>
    /// שמות הקבצים, כל אחד בבידוד כיווניות (FSI…PDI): רשימה של שמות לועזיים בתוך
    /// משפט עברי התפרקה בממשק לסדר מבלבל. הבידוד שומר כל שם שלם, עברי או לועזי.
    /// </summary>
    private static string Names(HashSet<string> names)
    {
        static string Isolate(string name) => "⁨" + name + "⁩";
        var shown = names.Take(2).Select(Isolate);
        return names.Count <= 2 ? string.Join(", ", shown) : $"{string.Join(", ", shown)} ועוד {names.Count - 2}";
    }
}
