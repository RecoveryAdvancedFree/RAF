using System.Buffers.Binary;
using RAF.Core.FileSystems;
using RAF.Core.Signatures;

namespace RAF.Core.Carving;

/// <summary>מידת הוודאות שיש לנו לגבי אורך הקובץ שחולץ.</summary>
internal enum LengthConfidence
{
    /// <summary>המבנה נקרא מתחילתו ועד סופו — האורך מאומת.</summary>
    Exact = 0,

    /// <summary>
    /// האורך נקרא משדה יחיד בכותרת. הוא מדויק לקובץ אמיתי, אך נתונים אחרים
    /// יכולים להיראות כך במקרה — טבלת האותיות של exFAT, למשל, זהה לכותרת ICO.
    /// </summary>
    Declared,

    /// <summary>האורך נקבע לפי חתימת סיום שנמצאה.</summary>
    Footer,

    /// <summary>לא נמצאה עדות לאורך; נלקח גודל מרבי סביר.</summary>
    Guess,
}

internal readonly record struct ResolvedLength(long Bytes, LengthConfidence Confidence);

/// <summary>
/// קביעת אורכו של קובץ שזוהה לפי חתימה.
///
/// זהו ההבדל המרכזי בין חילוץ טוב לגרוע: רוב הפורמטים נושאים את אורכם
/// בתוך המבנה שלהם, וקריאתו נותנת קובץ מדויק. חילוץ שמסתמך רק על "עד
/// החתימה הבאה" מייצר קבצים תפוחים או קטועים.
/// </summary>
internal static class FileLength
{
    /// <summary>תקרת קריאה בחיפוש חתימת סיום.</summary>
    private const long FooterSearchLimit = 256L * 1024 * 1024;

    private const int Window = 1 * 1024 * 1024;

    /// <summary>
    /// קביעת אורך הקובץ המתחיל בהיסט נתון.
    /// </summary>
    internal static ResolvedLength Resolve(
        FileSignature signature, IClusterVolume volume, long offset, long available)
    {
        long ceiling = Math.Min(signature.MaxSize, available);
        if (ceiling <= 0) return new ResolvedLength(0, LengthConfidence.Guess);

        // הבתים הראשונים מספיקים לכל הכותרות שנקראות ישירות.
        byte[] head = Read(volume, offset, 8192, ceiling);
        if (head.Length < 16) return new ResolvedLength(0, LengthConfidence.Guess);

        // חתימה קצרה מופיעה בנתונים אקראיים לעיתים קרובות. בלי אימות מבני
        // כל התאמה כזו הופכת ל"קובץ", וכשאורכו משוער הסורק מדלג עליו —
        // ובולע קבצים אמיתיים שיושבים אחריו.
        if (!StructureCheck.IsPlausible(signature, head))
            return new ResolvedLength(0, LengthConfidence.Guess);

        long exact = ReadDeclaredLength(signature, head, volume, offset, ceiling);
        if (exact > 0 && exact <= ceiling)
        {
            // ב-RW2 הסוף שנקרא הוא תחילת תמונת ה-RAW, שנמשכת עד סוף הקובץ — בלי תקרה.
            if (signature.Structure == "tif")
                exact = ExtendOverUntaggedData(volume, offset, exact, ceiling,
                    reach: signature.Extensions[0] == "rw2" ? ceiling : 8L * 1024 * 1024);
            return new ResolvedLength(exact, StructureCheck.ConfidenceOf(signature, volume, offset, exact));
        }

        // בפורמטים שמבנם נקרא במלואו, כישלון בקריאה מעיד על נתונים פגומים
        // או על התאמת שווא. חיפוש חתימת סיום קצרה היה מוצא מופע אקראי שלה.
        if (StructureCheck.HasFullParser(signature))
            return new ResolvedLength(0, LengthConfidence.Guess);

        if (signature.Footer is { Length: > 0 })
        {
            // PDF שנשמר בעדכונים מצטברים מכיל כמה סימני סיום, והאחרון הוא
            // הסוף האמיתי. עצירה בראשון הייתה חותכת את הקובץ.
            bool pdf = signature.Extensions.Contains("pdf");

            long viaFooter = pdf
                ? FindLastFooterBeforeNextFile(volume, offset, signature, ceiling)
                : FindFooter(volume, offset, signature.Footer, ceiling);

            if (viaFooter > 0) return new ResolvedLength(viaFooter, LengthConfidence.Footer);
        }

        // ללא עדות, נלקח גודל שמרני: גדול מספיק לקובץ טיפוסי
        // וקטן מספיק שלא לבלוע את שכניו.
        long fallback = Math.Min(ceiling, 16L * 1024 * 1024);
        return new ResolvedLength(fallback, LengthConfidence.Guess);
    }

    /// <summary>
    /// ב-TIFF וב-RAW, סוף הנתון הרחוק שהתגיות מכירות אינו תמיד סוף הקובץ: נבדק
    /// על 10 קובצי RAW אמיתיים — ב-Leica M10 נותרו אחריו 651KB של נתונים שאף תגית
    /// רגילה אינה מצביעה אליהם. לכן ממשיכים כל עוד הנתונים ממשיכים: עד סקטור
    /// שכולו אפסים (הריפוד שמצלמות מוסיפות — ב-Sony A6000), עד תחילת קובץ אחר,
    /// או עד 8MB. חלק עודף בסוף קובץ RAW אינו מזיק לו; חלק חסר — כן.
    /// </summary>
    private static long ExtendOverUntaggedData(IClusterVolume volume, long offset, long end, long ceiling, long reach)
    {
        const int sector = 512;
        const int block = 1024 * 1024;                               // קריאה בבלוקים — 512 בתים בכל פעם היה איטי בכרטיס

        long at = (end + sector - 1) / sector * sector;              // הגבול הבא, יחסית לתחילת הקובץ
        long limit = Math.Min(ceiling, end + reach);
        byte[] buffer = new byte[block];
        long extended = end;

        while (at + sector <= limit)
        {
            int want = (int)Math.Min(block, (limit - at) / sector * sector);
            int read = volume.ReadRaw(offset + at, buffer.AsSpan(0, want));
            if (read < sector) break;

            for (int i = 0; i + sector <= read; i += sector)
            {
                var chunk = buffer.AsSpan(i, sector);
                // תחילת קובץ אחר — רק כשגם המבנה שלה הגיוני: בתוך נתוני RAW דחוסים מופיעים
                // במקרה "BM" או "MZ" בתחילת סקטור (נמצא ב-Panasonic G9).
                if (!chunk.ContainsAnyExcept((byte)0) || StartsAnotherFile(chunk)) return extended;
                extended = at + i + sector;
            }

            at += read / sector * sector;
        }

        return extended;
    }

    private static bool StartsAnotherFile(Span<byte> sector)
    {
        byte[] head = sector.ToArray();
        return FileSignatures.Identify(head) is { } other && StructureCheck.IsPlausible(other, head);
    }

    /// <summary>
    /// קריאת האורך מתוך מבנה הקובץ, לפי הפורמט. אפס פירושו שהפורמט
    /// אינו נושא את אורכו או שהמבנה אינו קריא.
    /// </summary>
    internal static long ReadDeclaredLength(
        FileSignature signature, byte[] head, IClusterVolume volume, long offset, long ceiling)
    {
        string first = signature.Structure;

        return first switch
        {
            "bmp" => ReadBmp(head),
            "wav" or "avi" or "webp" => ReadRiff(head),
            "png" => ReadPng(volume, offset, ceiling),
            "gif" => StructureCheck.ReadGif(new WindowReader(volume, offset, ceiling)),
            "jpg" => StructureCheck.ReadJpeg(new WindowReader(volume, offset, ceiling)),
            "exe" => StructureCheck.ReadPe(new WindowReader(volume, offset, ceiling)),
            "ico" => StructureCheck.ReadIco(head),
            "db" or "sqlite" or "sqlite3" => ReadSqlite(head),
            "mp4" or "m4v" or "m4a" or "mov" => ReadIsoBmff(volume, offset, ceiling),
            "zip" => StructureCheck.ReadZip(new WindowReader(volume, offset, ceiling)),
            "mkv" => Matroska.ReadSegment(head)?.DeclaredFileLength ?? 0,
            "tif" => StructureCheck.ReadTiff(new WindowReader(volume, offset, ceiling)),
            "m2ts" => MediaLength.ReadTransportStream(new WindowReader(volume, offset, ceiling), 192),
            "ts" => MediaLength.ReadTransportStream(new WindowReader(volume, offset, ceiling), 188),
            "mpg" => MediaLength.ReadProgramStream(new WindowReader(volume, offset, ceiling)),
            "asf" => MediaLength.ReadAsf(new WindowReader(volume, offset, ceiling)),
            "amr" => MediaLength.ReadAmr(new WindowReader(volume, offset, ceiling)),
            "ogg" => MediaLength.ReadOgg(new WindowReader(volume, offset, ceiling)),
            _ => 0,
        };
    }

    /// <summary>BMP נושא את גודלו בהיסט 2.</summary>
    private static long ReadBmp(byte[] head)
        => head.Length < 6 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(2));

    /// <summary>משפחת RIFF נושאת את גודלה בהיסט 4, ללא שמונת בתי הכותרת.</summary>
    private static long ReadRiff(byte[] head)
        => head.Length < 8 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L;

    /// <summary>
    /// SQLite: גודל הדף בהיסט 16 ומספר הדפים בהיסט 28, שניהם big-endian.
    /// הערך 1 בגודל הדף מייצג 65536.
    /// </summary>
    private static long ReadSqlite(byte[] head)
    {
        if (head.Length < 32) return 0;

        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16));
        long actualPageSize = pageSize == 1 ? 65536 : pageSize;
        long pages = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(28));

        if (actualPageSize < 512 || pages <= 0) return 0;
        return actualPageSize * pages;
    }

    /// <summary>
    /// PNG בנוי משרשרת מקטעים; המעבר עליהם עד IEND נותן אורך מדויק.
    /// </summary>
    private static long ReadPng(IClusterVolume volume, long offset, long ceiling)
    {
        long at = 8; // אחרי חתימת הקובץ

        for (int guard = 0; guard < 10_000 && at + 12 <= ceiling; guard++)
        {
            byte[] header = Read(volume, offset + at, 8, ceiling - at);
            if (header.Length < 8) return 0;

            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

            if (length > 0x7FFFFFFF) return 0;

            // אורך המקטע: כותרת, נתונים וארבעה בתי ביקורת.
            at += 12L + length;

            if (type == "IEND") return at <= ceiling ? at : 0;
        }

        return 0;
    }

    /// <summary>
    /// MP4 ומשפחתו בנויים מתיבות. סכום גודלי התיבות ברמה העליונה
    /// הוא אורך הקובץ.
    /// </summary>
    private static long ReadIsoBmff(IClusterVolume volume, long offset, long ceiling)
    {
        long at = 0;

        for (int guard = 0; guard < 10_000 && at + 8 <= ceiling; guard++)
        {
            byte[] header = Read(volume, offset + at, 16, ceiling - at);
            if (header.Length < 8) break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

            // סוג תיבה חייב להיות תווים מודפסים.
            foreach (char c in type)
                if (c is < ' ' or > '~') return at > 0 ? at : 0;

            if (size == 1)
            {
                // גודל 64 ביט מופיע מיד אחרי הכותרת.
                if (header.Length < 16) break;
                size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
            }
            else if (size == 0)
            {
                // התיבה נמשכת עד סוף הקובץ.
                return ceiling;
            }

            if (size < 8) break;

            at += size;
            if (at > ceiling) return 0;
        }

        return at > 0 && at <= ceiling ? at : 0;
    }

    /// <summary>
    /// חיפוש חתימת הסיום, וקביעת האורך עד סופה.
    /// </summary>
    private static long FindFooter(
        IClusterVolume volume, long offset, byte[] footer, long ceiling)
    {
        long limit = Math.Min(ceiling, FooterSearchLimit);
        long at = 0;
        long lastMatch = 0;

        // חפיפה בין חלונות, כדי שחתימה שנחתכה בגבול לא תלך לאיבוד.
        int overlap = footer.Length - 1;

        while (at < limit)
        {
            int want = (int)Math.Min(Window, limit - at);
            byte[] block = Read(volume, offset + at, want + overlap, limit - at + overlap);
            if (block.Length < footer.Length) break;

            int from = 0;
            while (true)
            {
                int found = IndexOf(block, footer, from);
                if (found < 0) break;

                lastMatch = at + found + footer.Length;
                from = found + 1;

                // ברוב הפורמטים החתימה הראשונה היא הסיום האמיתי.
                if (footer.Length >= 4) return lastMatch;
            }

            at += want;
        }

        return lastMatch;
    }

    /// <summary>
    /// חיפוש סימן הסיום האחרון, עד לתחילת הקובץ הבא מאותו סוג.
    ///
    /// קובץ PDF בעדכונים מצטברים מסתיים ב-%%EOF אחרי כל שמירה. הסיום האמיתי
    /// הוא האחרון — אבל רק עד שמתחיל קובץ PDF חדש, אחרת הסורק היה בולע אותו.
    /// קובץ חדש מתחיל תמיד בגבול סקטור, ולכן החיפוש אחריו מוגבל לגבולות כאלה.
    /// </summary>
    private static long FindLastFooterBeforeNextFile(
        IClusterVolume volume, long offset, FileSignature signature, long ceiling)
    {
        long limit = Math.Min(ceiling, FooterSearchLimit);
        byte[] footer = signature.Footer!;
        int sector = volume.BytesPerCluster;
        int overlap = footer.Length - 1;

        long at = 0;
        long lastMatch = 0;

        while (at < limit)
        {
            // אחרי סימן סיום, עדכון מצטבר נוסף מגיע מיד אחריו. מרחק גדול בלי
            // סימן חדש פירושו שהקובץ נגמר — בלי הגבול הזה, PDF של 0.1MB גרם
            // לקריאת 256MB (כמעט 5 שניות על כונן USB).
            if (lastMatch > 0 && at - lastMatch > MaxTailAfterFooter) break;

            int want = (int)Math.Min(Window, limit - at);
            byte[] block = Read(volume, offset + at, want + overlap, limit - at + overlap);
            if (block.Length < footer.Length) break;

            int scanEnd = Math.Min(block.Length, want);
            int firstBoundary = (int)((sector - at % sector) % sector);

            // תחילת קובץ חדש מאותו סוג בגבול סקטור, שאינה הקובץ הנוכחי.
            int nextFile = -1;
            for (int s = firstBoundary; s + 8 <= scanEnd; s += sector)
            {
                if (at + s == 0) continue;
                if (signature.Matches(block.AsSpan(s))) { nextFile = s; break; }
            }

            int searchEnd = nextFile >= 0 ? nextFile : block.Length;

            int from = 0;
            while (true)
            {
                int found = IndexOf(block, footer, from);
                if (found < 0 || found + footer.Length > searchEnd) break;

                // אחרי סימן סיום, קובץ מכל סוג שמתחיל בגבול סקטור מסיים את ה-PDF.
                // לפני הסימן הראשון אין עוצרים כך: PDF מכיל תמונות JPEG שלמות,
                // ואחת מהן עשויה ליפול במקרה על גבול סקטור.
                if (lastMatch > 0 && OtherFileStarts(block, at, lastMatch, at + found, sector, firstBoundary))
                    return lastMatch;

                lastMatch = at + found + footer.Length;
                from = found + 1;
            }

            if (nextFile >= 0) break;
            if (lastMatch > 0 && OtherFileStarts(block, at, lastMatch, at + scanEnd, sector, firstBoundary)) break;
            at += want;
        }

        return lastMatch;
    }

    /// <summary>מרחק מרבי אחרי סימן הסיום האחרון, שבו עוד מחפשים עדכון מצטבר.</summary>
    private const long MaxTailAfterFooter = 16L * 1024 * 1024;

    /// <summary>האם קובץ מוכר כלשהו מתחיל בגבול סקטור בטווח [from, to) — יחסית לתחילת ה-PDF.</summary>
    private static bool OtherFileStarts(byte[] block, long at, long from, long to, int sector, int firstBoundary)
    {
        for (int s = firstBoundary; s + 16 <= block.Length && at + s < to; s += sector)
        {
            if (at + s < from) continue;
            var other = FileSignatures.Identify(block.AsSpan(s, Math.Min(64, block.Length - s)));
            if (other is not null &&
                StructureCheck.IsPlausible(other, block.AsSpan(s, Math.Min(8192, block.Length - s)).ToArray()))
                return true;
        }
        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }

            if (match) return i;
        }

        return -1;
    }

    private static byte[] Read(IClusterVolume volume, long offset, int count, long available)
    {
        int want = (int)Math.Min(count, Math.Max(0, available));
        if (want <= 0) return Array.Empty<byte>();

        byte[] buffer = new byte[want];
        int read = volume.ReadRaw(offset, buffer);
        return read == want ? buffer : buffer.AsSpan(0, Math.Max(0, read)).ToArray();
    }
}
