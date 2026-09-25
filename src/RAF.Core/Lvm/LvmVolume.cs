using RAF.Core.Raid;

namespace RAF.Core.Lvm;

/// <summary>
/// מאגר לוגי (קבוצת כוננים) ואזוריו, מתוך התיאור שנשמר בכוננים.
/// המאגר מחולק ליחידות בגודל קבוע. אזור הוא רשימת קטעים: כל קטע — מספר יחידות רצופות
/// של האזור, שיושבות בכונן אחד ברצף, או לסירוגין על כמה כוננים ("פיזור").
/// </summary>
internal sealed class LvmGroup
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal long Sequence { get; init; }
    internal long ExtentBytes { get; init; }
    /// <summary>הכוננים של המאגר לפי הסדר: מזהה (בלי מקפים) והיכן מתחילות היחידות בכונן.</summary>
    internal List<(string Name, string Id, long DataStart)> Pvs { get; } = new();
    internal List<LvmVolume> Volumes { get; } = new();

    internal static LvmGroup? Parse(string metadata)
    {
        var root = LvmConfig.Parse(metadata);
        var (name, vg) = root.Sections.FirstOrDefault();
        if (vg is null || vg.Section("physical_volumes") is not { } pvs) return null;
        long extent = (vg.Number("extent_size") ?? 0) * 512;
        if (extent <= 0) return null;

        var group = new LvmGroup
        {
            Id = vg.Text("id") ?? name,
            Name = name,
            Sequence = vg.Number("seqno") ?? 0,
            ExtentBytes = extent,
        };
        foreach (var (pvName, pv) in pvs.Sections)
            group.Pvs.Add((pvName, (pv.Text("id") ?? "").Replace("-", ""), (pv.Number("pe_start") ?? 0) * 512));

        if (vg.Section("logical_volumes") is { } lvs)
            foreach (var (lvName, lv) in lvs.Sections)
                group.Volumes.Add(LvmVolume.Parse(lvName, lv, group));
        return group;
    }
}

internal sealed class LvmVolume : IComposedVolume
{
    internal string Name { get; init; } = "";
    /// <summary>אזור שמוצג למשתמש (לא אזור פנימי של המאגר עצמו).</summary>
    internal bool Visible { get; init; }
    public long Size { get; private set; }
    /// <summary>למה אי אפשר לקרוא את האזור, או null.</summary>
    internal string? Problem { get; private set; }
    /// <summary>הכוננים שהאזור יושב עליהם (מספרם במאגר).</summary>
    internal HashSet<int> UsesPvs { get; } = new();

    private readonly List<Segment> _segments = new();
    private long _extent;
    private List<(string Name, string Id, long DataStart)> _pvs = new();

    private sealed record Segment(long Start, long Length, long StripeBytes, (int Pv, long Extent)[] Stripes);

    internal static LvmVolume Parse(string name, LvmConfig lv, LvmGroup group)
    {
        var status = lv.List("status")?.OfType<string>().ToList() ?? new();
        var volume = new LvmVolume { Name = name, Visible = status.Contains("VISIBLE"), _extent = group.ExtentBytes, _pvs = group.Pvs };

        foreach (var (_, seg) in lv.Sections.Where(s => s.Name.StartsWith("segment", StringComparison.Ordinal)))
        {
            string type = seg.Text("type") ?? "";
            long start = seg.Number("start_extent") ?? 0, count = seg.Number("extent_count") ?? 0;
            if (type is not ("striped" or "linear"))
            {
                volume.Problem ??= type switch
                {
                    "thin" or "thin-pool" => L.T("האזור \"דליל\" (thin) — המיקום של כל קטע בו רשום במבנה נפרד, שעוד לא נקרא בגרסה זו. סריקה מתקדמת של המאגר תמצא את הקבצים לפי סוג."),
                    _ => L.T("סוג האזור ({0}) עוד לא נתמך. סריקה מתקדמת תמצא את הקבצים לפי סוג.", type),
                };
                volume.Size = Math.Max(volume.Size, (start + count) * group.ExtentBytes);
                continue;
            }

            var list = seg.List("stripes") ?? new();
            var stripes = new List<(int, long)>();
            for (int i = 0; i + 1 < list.Count; i += 2)
            {
                int pv = group.Pvs.FindIndex(p => p.Name == list[i] as string);
                if (pv < 0 || list[i + 1] is not long ext) { volume.Problem ??= L.T("תיאור האזור פגום."); break; }
                stripes.Add((pv, ext));
                volume.UsesPvs.Add(pv);
            }
            if (stripes.Count == 0) continue;
            long stripeBytes = stripes.Count > 1 ? (seg.Number("stripe_size") ?? 0) * 512 : 0;
            if (stripes.Count > 1 && stripeBytes <= 0) { volume.Problem ??= L.T("תיאור האזור פגום."); continue; }
            volume._segments.Add(new Segment(start * group.ExtentBytes, count * group.ExtentBytes, stripeBytes, stripes.ToArray()));
            volume.Size = Math.Max(volume.Size, (start + count) * group.ExtentBytes);
        }
        volume._segments.Sort((a, b) => a.Start.CompareTo(b.Start));
        return volume;
    }

    public int Read(long offset, Span<byte> destination, MemberRead read)
    {
        if (offset >= Size) return 0;
        int total = (int)Math.Min(destination.Length, Size - offset);
        for (int done = 0; done < total; )
        {
            long at = offset + done;
            var seg = _segments.FirstOrDefault(s => at >= s.Start && at < s.Start + s.Length);
            if (seg is null)
            {
                // חור בין קטעים (לא אמור לקרות) — אפסים עד הקטע הבא.
                long next = _segments.Where(s => s.Start > at).Select(s => s.Start).DefaultIfEmpty(Size).Min();
                int gap = (int)Math.Min(total - done, next - at);
                destination.Slice(done, gap).Clear();
                done += gap;
                continue;
            }

            long within = at - seg.Start;
            int n = seg.Stripes.Length;
            long piece, pvOffset;
            int pv;
            if (n == 1)
            {
                pv = seg.Stripes[0].Pv;
                pvOffset = seg.Stripes[0].Extent * _extent + within;
                piece = seg.Length - within;
            }
            else
            {
                long chunk = within / seg.StripeBytes;
                var stripe = seg.Stripes[chunk % n];
                pv = stripe.Pv;
                pvOffset = stripe.Extent * _extent + chunk / n * seg.StripeBytes + within % seg.StripeBytes;
                piece = seg.StripeBytes - within % seg.StripeBytes;
            }
            var target = destination.Slice(done, (int)Math.Min(total - done, piece));
            int got = read(pv, _pvs[pv].DataStart + pvOffset, target);
            if (got < target.Length) target[Math.Max(0, got)..].Clear();
            done += target.Length;
        }
        return total;
    }
}
