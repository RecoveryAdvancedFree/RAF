using System.Collections.Concurrent;

namespace RAF.Core.Native;

/// <summary>
/// תיקונים בזיכרון בלבד: בתים שמוצגים לקוראי המחיצה במקום מה שכתוב בדיסק.
///
/// משמש לקריאת מחיצה שמגזר האתחול שלה פגום דרך עותק הגיבוי — המנועים
/// רואים מחיצה תקינה ומפענחים שמות ותיקיות, בזמן שעל הדיסק לא נכתב דבר.
/// המפתח הוא הדיסק והיסט המחיצה, כך שכל קורא של אותה מחיצה — סריקה,
/// תצוגה מקדימה ושיחזור — רואה את אותו תיקון.
/// </summary>
internal static class ReadOverlays
{
    internal readonly record struct Overlay(long Offset, byte[] Data)
    {
        public long End => Offset + Data.Length;
    }

    private static readonly ConcurrentDictionary<(int Disk, long PartitionOffset), Overlay> Map = new();

    internal static void Set(int disk, long partitionOffset, long relativeOffset, byte[] data)
        => Map[(disk, partitionOffset)] = new Overlay(relativeOffset, data);

    internal static void Remove(int disk, long partitionOffset) => Map.TryRemove((disk, partitionOffset), out _);

    internal static bool Has(int disk, long partitionOffset) => Map.ContainsKey((disk, partitionOffset));

    internal static Overlay? Get(int disk, long partitionOffset)
        => Map.TryGetValue((disk, partitionOffset), out var o) ? o : null;
}
