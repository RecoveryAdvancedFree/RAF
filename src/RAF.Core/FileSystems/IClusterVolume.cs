namespace RAF.Core.FileSystems;

/// <summary>
/// מחיצה פתוחה לקריאה, המאורגנת באשכולות.
///
/// כל מערכות הקבצים שהתוכנה תומכת בהן מתארות את תוכן הקבצים כרצפי אשכולות,
/// ולכן מנגנון החילוץ והשיחזור עובד מול ההפשטה הזו ואינו תלוי במערכת קבצים
/// מסוימת. הפרטים הייחודיים — פענוח רשומות, טבלאות הקצאה ושמות — נשארים
/// במימוש של כל מערכת קבצים בנפרד.
/// </summary>
internal interface IClusterVolume : IDisposable
{
    /// <summary>גודל אשכול בבתים.</summary>
    int BytesPerCluster { get; }

    /// <summary>המרת מספר אשכול להיסט בבתים מתחילת המחיצה.</summary>
    long ClusterToOffset(long cluster);

    /// <summary>קריאה ישירה מהיסט יחסי לתחילת המחיצה.</summary>
    int ReadRaw(long offset, Span<byte> destination);

    /// <summary>
    /// האם האשכול מסומן כתפוס בטבלת ההקצאה.
    /// null פירושו שהמידע אינו זמין, ולא שהאשכול פנוי.
    /// </summary>
    bool? IsClusterAllocated(long cluster);
}
