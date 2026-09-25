namespace RAF.Core.Raid;

/// <summary>קריאה מכונן לפי מספרו בכונן המורכב, מהיסט בתוך הכונן. מחזירה כמה נקרא.</summary>
internal delegate int MemberRead(int member, long offset, Span<byte> buffer);

/// <summary>
/// כונן שמורכב מחלקים של כמה כוננים — מערך RAID, או אזור במאגר לוגי של לינוקס.
/// הוא יודע רק את הגאומטריה: איזה בית יושב באיזה כונן ובאיזה מקום. הקריאה עצמה
/// מהכוננים עוברת דרך <see cref="MemberRead"/>.
/// </summary>
internal interface IComposedVolume
{
    long Size { get; }

    /// <summary>קריאה מהכונן המורכב. אזור שאין ממה לקרוא — אפסים.</summary>
    int Read(long offset, Span<byte> destination, MemberRead read);
}
