namespace RAF.Core.Text;

/// <summary>
/// התרגום לאנגלית של הודעות המנוע. המפתח הוא הטקסט העברי בדיוק כפי שהוא בקוד
/// (כולל {0}, רווחים וסימני פיסוק). tests/engine-i18n-check.mjs מוודא שלכל L.T יש כאן תרגום.
/// </summary>
internal static class EnglishTexts
{
    internal static readonly Dictionary<string, string> Texts = new()
    {
        // ------------------------------------------------ RAF.App — הודעות החלון והגשר
        ["בקשה ריקה"] =
            "Empty request",
        ["הפעולה בוטלה."] =
            "The operation was cancelled.",
        ["בחרו תיקייה לגיבוי — חייבת להיות על כונן אחר"] =
            "Choose a backup folder — it must be on a different drive",
        ["בחרו תיקייה לשמירת הקבצים המתוקנים"] =
            "Choose a folder for the repaired files",
        ["שיטה לא מוכרת: {0}"] =
            "Unknown method: {0}",
        ["המחיצה לא נמצאה. רענן את רשימת הדיסקים."] =
            "The partition was not found. Refresh the disk list.",
        ["מערכת הקבצים {0} אינה נתמכת לסריקת מטא-דאטה. נסו סריקה מתקדמת, שאינה תלויה במערכת הקבצים."] =
            "The {0} file system is not supported for a metadata scan. Try an advanced scan, which does not depend on the file system.",
        ["כונן "] =
            "Drive ",
        ["מחיצה "] =
            "Partition ",
        ["קובץ הסריקה שממנו ממשיכים לא נמצא."] =
            "The scan file to resume from was not found.",
        ["הסריקה הזו הסתיימה, או שאי אפשר להמשיך אותה."] =
            "This scan has finished, or it cannot be resumed.",
        ["הכונן שנסרק אינו מחובר. חברו אותו, רעננו את רשימת הכוננים ונסו שוב."] =
            "The scanned drive is not connected. Connect it, refresh the drive list and try again.",
        ["הכונן שנסרק אינו מחובר, ולכן אפשר רק לעיין ברשימה — בלי תצוגה מקדימה ובלי שחזור. חברו את הכונן, חזרו לרשימת הכוננים ופתחו את הסריקה שוב."] =
            "The scanned drive is not connected, so you can only browse the list — no preview and no recovery. Connect the drive, go back to the drive list and open the scan again.",
        ["זו נקודת ביניים שנשמרה באמצע סריקה, ולא כל המחיצה נסרקה. הקבצים שברשימה ניתנים לשחזור; כדי למצוא את השאר — הריצו את הסריקה שוב."] =
            "This is a checkpoint saved in the middle of a scan, and not the whole partition was scanned. The files in the list can be recovered; to find the rest, run the scan again.",
        ["חסרה תצוגה בבקשה."] =
            "The request is missing a view.",
        ["הקובץ לא נמצא בתוצאות הסריקה."] =
            "The file was not found in the scan results.",
        ["לא ניתן לקרוא את תוכן הקובץ."] =
            "Cannot read the file's content.",
        ["\n\n… (התצוגה נקטעה)"] =
            "\n\n… (preview truncated)",
        ["בחרו תיקיית יעד לשחזור — חייבת להיות על כונן אחר"] =
            "Choose a destination folder for recovery — it must be on a different drive",
        ["לא נבחרו קבצים לשחזור."] =
            "No files were selected for recovery.",
        ["{0} · תמונת דיסק {1}"] =
            "{0} · disk image {1}",
        ["התיקייה לא נמצאה."] =
            "The folder was not found.",
        ["מחיצה שנמצאה · "] =
            "Found partition · ",
        ["זו אינה מחיצה שנמצאה בסריקת כונן."] =
            "This is not a partition found by a drive scan.",
        ["יש לבחור תיקייה לגיבוי לפני הכתיבה."] =
            "Choose a backup folder before writing.",
        ["{0} (דיסק {1}, {2})"] =
            "{0} (disk {1}, {2})",
        ["{0} (דיסק {1}) · {2} · {3}"] =
            "{0} (disk {1}) · {2} · {3}",
        ["שמירת תמונת הדיסק — בחרו כונן אחר מהכונן המקורי"] =
            "Save the disk image — choose a drive other than the original one",
        ["תמונת דיסק גולמית (*.img)|*.img|כונן וירטואלי שאפשר לחבר ב-Windows (*.vhd)|*.vhd"] =
            "Raw disk image (*.img)|*.img|Virtual drive that Windows can attach (*.vhd)|*.vhd",
        ["תמונת דיסק גולמית (*.img)|*.img"] =
            "Raw disk image (*.img)|*.img",
        ["פתיחת תמונת דיסק"] =
            "Open a disk image",
        ["תמונות דיסק וכוננים וירטואליים (*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01)|*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01|כוננים וירטואליים של Windows (*.vhd;*.vhdx)|*.vhd;*.vhdx|כוננים של VirtualBox ו-VMware (*.vmdk)|*.vmdk|תמונות של כלי חקירה (*.e01)|*.e01|כל הקבצים (*.*)|*.*"] =
            "Disk images and virtual drives (*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01)|*.img;*.dd;*.raw;*.bin;*.vhd;*.vhdx;*.vmdk;*.e01|Windows virtual drives (*.vhd;*.vhdx)|*.vhd;*.vhdx|VirtualBox and VMware drives (*.vmdk)|*.vmdk|Forensic images (*.e01)|*.e01|All files (*.*)|*.*",
        ["המחיצה אינה כונן BitLocker עם אות כונן."] =
            "The partition is not a BitLocker drive with a drive letter.",
        ["כונן {0}"] =
            "Drive {0}",
        ["יש לבחור תיקייה לגיבוי לפני התיקון."] =
            "Choose a backup folder before the repair.",
        ["בחרו את קובץ הביטול שנשמר בזמן התיקון"] =
            "Choose the undo file that was saved during the repair",
        ["קובצי ביטול|RAF-undo-*.bin|כל הקבצים|*.*"] =
            "Undo files|RAF-undo-*.bin|All files|*.*",
        ["החזרת מחיצה לטבלת המחיצות"] =
            "Restoring a partition to the partition table",
        ["תיקון מחיצה"] =
            "Partition repair",
        ["בחרו קבצים לבדיקה ולתיקון"] =
            "Choose files to check and repair",
        ["יש לבחור תיקייה לשמירת הקבצים המתוקנים."] =
            "Choose a folder for the repaired files.",
        ["בחרו סרטון תקין שצולם באותו מכשיר ובאותן הגדרות"] =
            "Choose a working video shot on the same device with the same settings",
        ["סרטונים|*.mp4;*.mov;*.m4v;*.3gp;*.3g2|כל הקבצים|*.*"] =
            "Videos|*.mp4;*.mov;*.m4v;*.3gp;*.3g2|All files|*.*",
        ["יש לבחור תיקייה לשמירת הסרטון המתוקן."] =
            "Choose a folder for the repaired video.",
        ["לא ניתן לקרוא את הקובץ. "] =
            "Cannot read the file. ",
        ["הדיסק לא נמצא. רענן את רשימת הדיסקים."] =
            "The disk was not found. Refresh the disk list.",
        ["לא בוצעה סריקה עדיין."] =
            "No scan has been run yet.",
        ["כדי לכתוב לכונן יש להקליד את המילה \"{0}\". שום דבר לא נכתב."] =
            "To write to the drive, type the word \"{0}\". Nothing was written.",
        ["שחזור מתקדם חינם"] =
            "Recovery Advanced Free",
        ["פתיחת החלון"] =
            "Open window",
        ["יציאה"] =
            "Exit",
        ["הפעולה נעצרה בגלל שגיאה — הפרטים בחלון."] =
            "The operation stopped because of an error — details in the window.",
        ["הפעולה הסתיימה."] =
            "The operation has finished.",
        ["לא ניתן לוודא שהתיקייה אינה על הדיסק שממנו משחזרים."] =
            "Cannot verify that the folder is not on the disk you are recovering from.",
        ["התיקייה נמצאת על הדיסק שממנו משחזרים — כתיבה אליו עלולה לדרוס קבצים שעוד לא שוחזרו."] =
            "The folder is on the disk you are recovering from — writing to it may overwrite files that have not been recovered yet.",
        ["השמירה האוטומטית נכשלה. "] =
            "Automatic saving failed. ",
        ["שמירת הסריקה — בחרו כונן אחר מהכונן שנסרק"] =
            "Save the scan — choose a drive other than the scanned one",
        ["סריקת RAF (*{0})|*{1}"] =
            "RAF scan (*{0})|*{1}",
        [" בחרו כונן אחר."] =
            " Choose another drive.",
        ["אפשר להסיר רק סריקות מהרשימה של הסריקות האחרונות."] =
            "Only scans from the recent scans list can be removed.",
        ["פתיחת סריקה שמורה"] =
            "Open a saved scan",
        ["הכונן שנסרק אינו מחובר. חברו אותו, חזרו לרשימת הכוננים ופתחו את הסריקה שוב."] =
            "The scanned drive is not connected. Connect it, go back to the drive list and open the scan again.",
        ["בחרו כמה קבצים תקינים מאותו סוג — שלושה ומעלה"] =
            "Choose several working files of the same type — three or more",
        ["כל הקבצים (*.*)|*.*"] =
            "All files (*.*)|*.*",
        ["לא נלמד סוג חדש. בחרו קבצים לדוגמה קודם."] =
            "No new type was learned. Choose sample files first.",
        ["שחררו כאן כדי לבדוק את הקבצים"] =
            "Drop here to check the files",
        ["קבצים ותיקיות · הקבצים המקוריים לא ישתנו"] =
            "Files and folders · the original files will not change",
        ["אם זה חוזר, ייתכן שהכונן מתחיל להיכשל. מומלץ ליצור ממנו תמונת דיסק ולעבוד מהתמונה — כך כל קריאה נוספת לא תסכן אותו."] =
            "If this keeps happening, the drive may be starting to fail. It is best to create a disk image of it and work from the image — that way further reads will not put it at risk.",
        ["Windows לא אישר גישה לקובץ או לתיקייה."] =
            "Windows denied access to the file or folder.",
        ["ודאו שהתוכנה פועלת עם הרשאות מנהל, ושהקובץ אינו פתוח בתוכנה אחרת. אם זו תיקיית מערכת — בחרו תיקייה אחרת."] =
            "Make sure the program is running as administrator and the file is not open in another program. If this is a system folder, choose another folder.",
        ["הנתיב ארוך מדי."] =
            "The path is too long.",
        ["בחרו תיקיית יעד קרובה יותר לשורש הכונן, למשל D:\\שחזור."] =
            "Choose a destination folder closer to the root of the drive, for example D:\\Recovery.",
        ["הקובץ או התיקייה לא נמצאו."] =
            "The file or folder was not found.",
        ["ייתכן שהם נמחקו, הועברו או שהכונן נותק. רעננו ונסו שוב."] =
            "They may have been deleted or moved, or the drive was disconnected. Refresh and try again.",
        ["הכונן לא מגיב — ייתכן שהוא נותק."] =
            "The drive is not responding — it may have been disconnected.",
        ["בדקו שהכונן מחובר היטב (ב-USB נסו יציאה אחרת, בלי מפצל), ורעננו את רשימת הכוננים."] =
            "Check that the drive is firmly connected (for USB, try another port without a hub), and refresh the drive list.",
        ["הכונן לא הצליח לקרוא חלק מהנתונים."] =
            "The drive could not read some of the data.",
        ["אין מספיק מקום פנוי בכונן היעד."] =
            "There is not enough free space on the destination drive.",
        ["פנו מקום או בחרו כונן אחר, ונסו שוב. מה שכבר נכתב נשאר במקומו."] =
            "Free up space or choose another drive, and try again. What was already written stays in place.",
        ["הקובץ פתוח בתוכנה אחרת."] =
            "The file is open in another program.",
        ["סגרו את התוכנה שמשתמשת בו ונסו שוב."] =
            "Close the program that is using it and try again.",
        ["Windows לא אישר גישה."] =
            "Windows denied access.",
        ["ודאו שהתוכנה פועלת עם הרשאות מנהל, ושאף תוכנה אחרת אינה נועלת את הכונן."] =
            "Make sure the program is running as administrator and that no other program is locking the drive.",
        ["אירעה שגיאה בקריאה או בכתיבה."] =
            "A read or write error occurred.",
        ["נסו שוב. "] =
            "Try again. ",
        ["אירעה שגיאה לא צפויה."] =
            "An unexpected error occurred.",
        ["נסו שוב. אם זה חוזר, סגרו את התוכנה ופתחו אותה מחדש."] =
            "Try again. If this keeps happening, close the program and open it again.",
        ["שחזור מתקדם חינם — RAF"] =
            "Recovery Advanced Free — RAF",
        ["לא ניתן לאתחל את מנוע התצוגה WebView2.\n\nב-Windows 11 המנוע מותקן מראש. אם המחשב מריץ Windows 10 ישן, יש להתקין את WebView2 Runtime מאתר Microsoft.\n\nפירוט: "] =
            "Cannot start the WebView2 display engine.\n\nOn Windows 11 the engine is preinstalled. If the computer runs an old Windows 10, install the WebView2 Runtime from the Microsoft website.\n\nDetails: ",
        ["סריקה פועלת כעת. אם תסגרו את התוכנה, הסריקה תיעצר באמצע והתוצאות שלה יאבדו.\n\nבסריקה מתקדמת נשמרת נקודת ביניים כל 5 דקות, ואפשר לפתוח אותה אחר כך מ\"סריקות אחרונות\"."] =
            "A scan is running. If you close the program, the scan will stop midway and its results will be lost.\n\nAn advanced scan saves a checkpoint every 5 minutes, which you can open later from \"Recent scans\".",
        ["שחזור פועל כעת. אם תסגרו את התוכנה, השחזור ייעצר וחלק מהקבצים לא ישוחזרו."] =
            "A recovery is running. If you close the program, the recovery will stop and some files will not be recovered.",
        ["סריקת כונן פועלת כעת. אם תסגרו את התוכנה, היא תיעצר ולא יוצגו המחיצות שנמצאו."] =
            "A drive scan is running. If you close the program, it will stop and the partitions found will not be shown.",
        ["יצירת תמונת דיסק פועלת כעת. אם תסגרו את התוכנה, היא תיעצר. מה שכבר הועתק נשמר, ואפשר להמשיך מאותה נקודה בפעם הבאה."] =
            "A disk image is being created. If you close the program, it will stop. What was already copied is saved, and you can continue from the same point next time.",
        ["\n\nלסגור בכל זאת?"] =
            "\n\nClose anyway?",
        ["שחזור מתקדם חינם — שגיאה"] =
            "Recovery Advanced Free — Error",
        ["אירעה שגיאה בלתי צפויה:\n\n"] =
            "An unexpected error occurred:\n\n",
        ["שגיאה לא ידועה"] =
            "Unknown error",
        ["\n\nפירוט טכני:\n"] =
            "\n\nTechnical details:\n",
        ["קורא את מפת המקום הפנוי"] =
            "Reading the free space map",
        ["מפת המקום הפנוי של מערכת הקבצים אינה נקראת, ולכן נסרקה המחיצה כולה."] =
            "The file system's free space map could not be read, so the whole partition was scanned.",
        ["נסרק רק המקום הפנוי — {0} מהמחיצה. קבצים שנמחקו נמצאים שם; המקום התפוס מכיל את הקבצים הקיימים. אחרי פירמוט, או כשמבנה המחיצה פגום, אפשר לסרוק את כולה."] =
            "Only the free space was scanned — {0} of the partition. Deleted files are there; the used space holds the existing files. After a format, or when the partition structure is damaged, you can scan all of it.",
        ["הכונן נותק באמצע הסריקה, אחרי {0}% מהמחיצה. הסריקה נשמרה — חברו את הכונן שוב והמשיכו מאותה נקודה."] =
            "The drive was disconnected during the scan, after {0}% of the partition. The scan was saved — reconnect the drive and continue from the same point.",
        ["סורק את המחיצה אחר חתימות קבצים"] =
            "Scanning the partition for file signatures",
        ["נקודת ביניים: נשמרה אחרי {0}% מהמחיצה. קבצים שאחרי נקודה זו אינם ברשימה."] =
            "Checkpoint: saved after {0}% of the partition. Files past this point are not in the list.",
        ["סריקת חתימות"] =
            "Signature scan",
        ["מאמת את התמונות שנמצאו"] =
            "Verifying the images found",
        ["הקובץ היה מפוצל לשני חלקים על הכונן. החלק השני נמצא {0} אחרי סוף הראשון, והחיבור אומת בפענוח של כל התמונה עד סופה."] =
            "The file was split into two parts on the drive. The second part was found {0} after the end of the first, and the join was verified by decoding the whole image to its end.",
        ["נתוני התמונה משתבשים אחרי כ-{0} ממנה. כנראה הקובץ היה מפוצל והמשכו לא נמצא, או שחלקו נדרס. החלק העליון של התמונה ייפתח, והשאר יוצג משובש."] =
            "The image data becomes corrupt after about {0} of it. The file was probably split and its continuation was not found, or part of it was overwritten. The top of the image will open, and the rest will look garbled.",
        ["הקובץ זוהה כ{0} לפי חתימתו, ומבנהו נקרא מתחילתו ועד סופו — כלומר גם הזיהוי וגם האורך מאומתים."] =
            "The file was identified as {0} by its signature, and its structure was read from start to end — so both the type and the length are verified.",
        ["הקובץ זוהה כ{0} לפי חתימתו, ואורכו נקרא משדה הגודל שבכותרת. לקובץ אמיתי האורך מדויק."] =
            "The file was identified as {0} by its signature, and its length was read from the size field in its header. For a genuine file the length is exact.",
        ["הקובץ זוהה כ{0} לפי חתימתו, ואורכו נקבע לפי חתימת הסיום שנמצאה."] =
            "The file was identified as {0} by its signature, and its length was set by the end signature that was found.",
        ["הקובץ זוהה כ{0} לפי חתימת הפתיחה בלבד. הפורמט אינו נושא את אורכו, ולכן נלקח גודל מרבי סביר — ייתכן שהקובץ יכיל נתונים עודפים בסופו."] =
            "The file was identified as {0} by its opening signature only. The format does not record its length, so a reasonable maximum size was used — the file may contain extra data at its end.",
        [" נתוני התמונה עצמם פוענחו מתחילתם ועד סופם — התמונה שלמה."] =
            " The image data itself was decoded from start to end — the image is complete.",
        ["בסריקה מתקדמת השמות והתיקיות המקוריים אינם נשמרים: הם היו רשומים במערכת הקבצים, והסריקה הזו אינה נעזרת בה. הקבצים מסודרים בתיקיות לפי סוגם, ומקבלים שם לפי מה ששמור בתוכם — תאריך הצילום ודגם המצלמה, כותרת המסמך או שם השיר. קובץ שאין בו מידע כזה מקבל מספר."] =
            "An advanced scan does not keep the original names and folders: they were recorded in the file system, which this scan does not use. Files are sorted into folders by type and named from what is stored inside them — the date taken and camera model, the document title or the song name. A file without such information gets a number.",
        ["לא אותרו חתימות קבצים מוכרות במחיצה."] =
            "No known file signatures were found in the partition.",
        ["ב-{0} קבצים לא ניתן היה לקבוע את האורך המדויק מתוך מבנה הקובץ. הם ישוחזרו בגודל מרבי, וייתכן שיכילו נתונים עודפים בסופם — ברוב הפורמטים הדבר אינו מפריע לפתיחת הקובץ."] =
            "For {0} files the exact length could not be determined from the file structure. They will be recovered at a maximum size and may contain extra data at the end — in most formats this does not prevent opening the file.",
        ["קובץ שהיה מפוצל על הכונן: תמונת JPEG בשני חלקים מחוברת ומאומתת בפענוח. בשאר סוגי הקבצים הסריקה מניחה שהקובץ רציף — קובץ מפוצל ישוחזר חלקית בלבד."] =
            "Files that were split on the drive: a JPEG image in two parts is joined and verified by decoding. For other file types the scan assumes the file is contiguous — a split file will be only partly recovered.",
        ["הכונן ענה באיחור, אך לא ניתן היה לקרוא ממנו את טבלת המחיצות."] =
            "The drive responded late, but its partition table could not be read.",
        ["הכונן מחובר, אך אינו עונה אפילו לשאילתת המאפיינים הבסיסית. זה סימן מובהק לכונן פגום, או לחיבור (כבל / מתאם USB) שאינו תקין."] =
            "The drive is connected but does not answer even the basic properties query. This is a clear sign of a failing drive, or of a faulty connection (cable / USB adapter).",
        ["הכונן זוהה, אך אינו עונה לבקשות קריאה. זה סימן לכונן פגום. התוכנה ממשיכה לנסות ברקע, והרשימה תתעדכן אם הוא יענה."] =
            "The drive was detected but does not answer read requests. This is a sign of a failing drive. The program keeps trying in the background, and the list will update if it responds.",
        ["Windows מדווח שהכונן צפוי להיכשל"] =
            "Windows reports that the drive is expected to fail",
        ["שטח הרזרבה להחלפת תאים שנשחקו כמעט נגמר ({0}%)"] =
            "The spare area for replacing worn cells is almost used up ({0}%)",
        ["הכונן מדווח שהאמינות שלו נפגעה"] =
            "The drive reports that its reliability is degraded",
        ["הכונן עבר למצב קריאה בלבד — כך כוננים מגינים על עצמם לפני כשל"] =
            "The drive switched to read-only mode — this is how drives protect themselves before failing",
        ["הכונן מדווח על טמפרטורה חריגה"] =
            "The drive reports an abnormal temperature",
        ["{0} שגיאות נתונים שלא תוקנו"] =
            "{0} uncorrected data errors",
        ["הכונן עבר את אורך החיים המתוכנן ({0}% נוצלו)"] =
            "The drive has passed its rated lifetime ({0}% used)",
        ["הכונן קרוב לסוף אורך החיים המתוכנן ({0}% נוצלו)"] =
            "The drive is near the end of its rated lifetime ({0}% used)",
        ["הכונן עצמו מדווח שהוא צפוי להיכשל"] =
            "The drive itself reports that it is expected to fail",
        ["{0} סקטורים שאינם נקראים כרגע"] =
            "{0} sectors that currently cannot be read",
        ["{0} סקטורים שלא ניתן היה לתקן"] =
            "{0} sectors that could not be corrected",
        ["{0} סקטורים פגומים כבר הוחלפו ברזרבה"] =
            "{0} bad sectors were already replaced from the spare area",
        ["{0} שגיאות קריאה שדווחו למחשב"] =
            "{0} read errors reported to the computer",
        ["התקן לא מזוהה"] =
            "Unrecognized device",
        ["התקן USB שנכשל בזיהוי"] =
            "USB device that failed to be recognized",
        ["משהו מחובר ליציאת ה-USB, אך הוא לא הצליח אפילו להציג את עצמו ל-Windows. כך נראה לרוב כונן שהבקר שלו או המתאם שלו תקולים."] =
            "Something is connected to the USB port, but it could not even identify itself to Windows. This is usually what a drive with a faulty controller or adapter looks like.",
        ["Windows מרגיש שהכונן מחובר, אך לא הצליח להפעיל אותו (קוד 10)."] =
            "Windows senses the drive is connected but could not start it (code 10).",
        ["Windows עצר את הכונן כי הוא דיווח על תקלה (קוד 43)."] =
            "Windows stopped the drive because it reported a problem (code 43).",
        ["לא מותקן לכונן מנהל התקן (קוד 28)."] =
            "No driver is installed for the drive (code 28).",
        ["הכונן מושבת ב-Windows (קוד 22)."] =
            "The drive is disabled in Windows (code 22).",
        ["יש בעיה במנהל ההתקן של הכונן (קוד {0})."] =
            "There is a problem with the drive's driver (code {0}).",
        ["Windows מדווח על בעיה בכונן (קוד {0})."] =
            "Windows reports a problem with the drive (code {0}).",
        ["אפשר להפעיל אותו מחדש במנהל ההתקנים: קליק ימני על הכונן ← \"הפעל התקן\"."] =
            "You can re-enable it in Device Manager: right-click the drive → \"Enable device\".",
        ["נתקו את הכונן, המתינו כמה שניות וחברו אותו מחדש. "] =
            "Disconnect the drive, wait a few seconds and connect it again. ",
        ["נסו יציאת USB אחרת — עדיף יציאה בגב המחשב — וכבל או מתאם אחר. "] =
            "Try another USB port — preferably one on the back of the computer — and another cable or adapter. ",
        ["בכונן קשיח פנימי: חיבור ישיר בכבל SATA במקום מתאם USB מצליח לעיתים קרובות גם כשהמתאם נכשל. כשהכונן יזוהה — צרו ממנו תמונה לפני כל דבר אחר."] =
            "For an internal hard disk: connecting it directly with a SATA cable instead of a USB adapter often works even when the adapter fails. Once the drive is detected, create an image of it before anything else.",
        ["הכונן"] =
            "the drive",
        ["קובץ התמונה לא נמצא."] =
            "The image file was not found.",
        ["הקובץ קטן מכדי להיות תמונת דיסק."] =
            "The file is too small to be a disk image.",
        ["לא ניתן לפתוח את קובץ התמונה לקריאה. ייתכן שהוא בשימוש בתוכנה אחרת."] =
            "Cannot open the image file for reading. It may be in use by another program.",
        ["תמונה של כלי חקירה (E01) — נקראת ישירות מהקובץ, ושום דבר לא נכתב אליה."] =
            "A forensic image (E01) — read directly from the file, and nothing is written to it.",
        [" הכלי שיצר אותה שמר בה טביעת אצבע של הכונן המקורי."] =
            " The tool that created it stored a fingerprint of the original drive in it.",
        ["כונן וירטואלי ({0}) — נקרא ישירות מהקובץ, בלי לחבר אותו ל-Windows, ושום דבר לא נכתב אליו."] =
            "A virtual drive ({0}) — read directly from the file without attaching it to Windows, and nothing is written to it.",
        [" הכונן לא נסגר כראוי בפעם האחרונה, ולכן ייתכן שהשינויים האחרונים שנעשו בו חסרים."] =
            " The drive was not closed properly last time, so the most recent changes made to it may be missing.",
        ["הכונן עדיין נעול. פתחו אותו קודם ב-Windows, עם הסיסמה או מפתח השחזור."] =
            "The drive is still locked. Unlock it in Windows first, with the password or the recovery key.",
        ["{0} — BitLocker פתוח"] =
            "{0} — unlocked BitLocker",
        ["המחיצה אינה ext2/3/4 תקינה, או שהכותרת שלה פגומה."] =
            "The partition is not a valid ext2/3/4 file system, or its header is damaged.",
        ["המחיצה אינה XFS תקינה, או שהכותרת שלה פגומה."] =
            "The partition is not a valid XFS file system, or its header is damaged.",
        ["קורא את היומן של מערכת הקבצים"] =
            "Reading the file system journal",
        ["בחלק מהתיקיות במחיצה הזו הופעלה הצפנה של לינוקס. שמות ותוכן בתיקיות כאלה מוצפנים ולא ייקראו."] =
            "Linux encryption is enabled on some folders of this partition. Names and content in such folders are encrypted and will not be read.",
        ["תיקיית השורש של המחיצה אינה נקראת."] =
            "The partition's root folder cannot be read.",
        ["{0} קבצים שנמחקו שוחזרו בעזרת עותק ישן של הרשומה שלהם, שנשמר ביומן של מערכת הקבצים."] =
            "{0} deleted files were recovered using an old copy of their record, kept in the file system journal.",
        ["ל-{0} קבצים שנמחקו נמצא השם, אבל לינוקס מחק את המידע על מיקום התוכן ולא נשאר ממנו עותק ביומן. סריקה מתקדמת עשויה למצוא את התוכן שלהם לפי סוג הקובץ, בלי השם."] =
            "For {0} deleted files the name was found, but Linux erased the information on where the content is, and no copy of it remained in the journal. An advanced scan may find their content by file type, without the name.",
        ["ל-{0} קבצים שנמחקו נמצא השם, אבל המידע על מיקום התוכן כבר לא קיים. סריקה מתקדמת עשויה למצוא את התוכן שלהם לפי סוג הקובץ, בלי השם."] =
            "For {0} deleted files the name was found, but the information on where the content is no longer exists. An advanced scan may find their content by file type, without the name.",
        ["הקובץ קיים, אבל המידע על מיקום התוכן שלו פגום."] =
            "The file exists, but the information on where its content is, is damaged.",
        ["מיקום התוכן נלקח מעותק של הרשומה שנשמר ביומן של מערכת הקבצים לפני המחיקה."] =
            "The content's location was taken from a copy of the record that the file system journal kept before the deletion.",
        ["הרשומה של הקובץ שמרה את מיקום התוכן גם אחרי המחיקה."] =
            "The file's record kept the content's location even after the deletion.",
        ["השם נשאר, אבל לינוקס מחק את המידע על מיקום התוכן, ולא נשאר ממנו עותק ביומן. סריקה מתקדמת עשויה למצוא את התוכן לפי סוג הקובץ."] =
            "The name remains, but Linux erased the information on where the content is, and no copy of it remained in the journal. An advanced scan may find the content by file type.",
        ["השם נשאר, אבל המידע על מיקום התוכן כבר לא קיים. סריקה מתקדמת עשויה למצוא את התוכן לפי סוג הקובץ."] =
            "The name remains, but the information on where the content is no longer exists. An advanced scan may find the content by file type.",
        ["המידע על מיקום התוכן פגום."] =
            "The information on where the content is, is damaged.",
        ["מחפש קבצים שנמחקו בלי שם"] =
            "Looking for deleted files without a name",
        ["המיקום נשאר ברשומת הקובץ. הגודל המקורי נמחק, ולכן הקובץ משוחזר עד סוף הבלוק האחרון שלו."] =
            "The location remained in the file's record. The original size was erased, so the file is recovered up to the end of its last block.",
        ["{0} — BitLocker מפוענח"] =
            "{0} — decrypted BitLocker",
        ["מחיצה {0}"] =
            "Partition {0}",
        ["המחיצה אינה מוצפנת ב-BitLocker."] =
            "The partition is not encrypted with BitLocker.",
        ["אזור הניהול של ההצפנה ניזוק בכל שלושת העותקים שלו, ולכן אי אפשר לפתוח את הכונן גם עם המפתח הנכון."] =
            "The encryption's management area is damaged in all three of its copies, so the drive cannot be opened even with the right key.",
        ["הכונן הוצפן ב-Windows Vista, בגרסה ישנה של BitLocker שהתוכנה אינה קוראת. פתחו אותו ב-Windows, ואז אפשר לסרוק אותו כאן."] =
            "The drive was encrypted on Windows Vista, with an old version of BitLocker that the program does not read. Unlock it in Windows, and then it can be scanned here.",
        ["מפתח שחזור"] =
            "Recovery key",
        ["סיסמה"] =
            "Password",
        ["שבב האבטחה של המחשב"] =
            "The computer's security chip (TPM)",
        ["שבב האבטחה של המחשב וקוד"] =
            "The computer's security chip and a PIN",
        ["קובץ מפתח בדיסק-און-קי"] =
            "A key file on a USB drive",
        ["ההגנה מושהית"] =
            "Protection suspended",
        ["אחר"] =
            "Other",
        ["הקלידו את מפתח השחזור או את הסיסמה של הכונן."] =
            "Type the drive's recovery key or password.",
        ["מפתח השחזור לא הוקלד נכון: יש בו 8 קבוצות של 6 ספרות, וכל קבוצה מתחלקת ב-11. בדקו שוב את הספרות."] =
            "The recovery key was not typed correctly: it has 8 groups of 6 digits, and each group is divisible by 11. Check the digits again.",
        ["המפתח או הסיסמה אינם נכונים לכונן הזה. בדקו שהמפתח שייך לכונן הזה — לכל כונן מוצפן מפתח שחזור משלו."] =
            "The key or password is not correct for this drive. Check that the key belongs to this drive — every encrypted drive has its own recovery key.",
        ["המפתח נכון והכונן פוענח, אבל תחילת המחיצה שבתוכו פגומה. סריקה מתקדמת תמצא את הקבצים לפי סוג. "] =
            "The key is correct and the drive was decrypted, but the start of the partition inside it is damaged. An advanced scan will find the files by type. ",
        ["התוכנה מפענחת את הכונן בעצמה ({0}), בלי Windows. הקריאה בלבד — שום דבר לא נכתב אליו."] =
            "The program decrypts the drive itself ({0}), without Windows. Read only — nothing is written to it.",
        ["הכונן המוצפן {0} נקרא דרך Windows, שמפענח אותו. הקריאה בלבד — שום דבר לא נכתב אליו. אל תנעלו אותו מחדש עד סוף השחזור."] =
            "The encrypted drive {0} is read through Windows, which decrypts it. Read only — nothing is written to it. Do not lock it again until the recovery is finished.",
        ["תמונה ללא קובץ מפה — לא ידוע אם הכונן כולו נקרא בעת יצירתה."] =
            "An image without a map file — it is not known whether the whole drive was read when it was created.",
        ["נוצרה מ: {0}. "] =
            "Created from: {0}. ",
        ["התמונה חלקית: {0} לא הועתקו. קבצים שישבו באזורים האלה לא ישוחזרו."] =
            "The image is partial: {0} were not copied. Files that were in those areas will not be recovered.",
        ["{0} לא נקראו מהכונן בעת היצירה ומולאו באפסים. קבצים שישבו בהם יחזרו פגומים חלקית."] =
            "{0} could not be read from the drive when it was created and were filled with zeros. Files that were there will come back partly damaged.",
        ["הכונן כולו נקרא בהצלחה."] =
            "The whole drive was read successfully.",
        ["סוג 0x{0}"] =
            "Type 0x{0}",
        ["לא ניתן לפתוח את הכונן לקריאה."] =
            "Cannot open the drive for reading.",
        ["גבולות המחיצה שנמצאה אינם תקינים, ולכן אי אפשר לרשום אותה בטבלה."] =
            "The boundaries of the found partition are invalid, so it cannot be added to the table.",
        ["המחיצה חורגת מסוף הכונן."] =
            "The partition extends beyond the end of the drive.",
        ["המחיצה שנמצאה חופפת למחיצה שכבר קיימת בטבלה"] =
            "The found partition overlaps a partition already in the table",
        [". רישום שתיהן היה גורם לכך שכתיבה לאחת תהרוס את השנייה. אפשר עדיין לסרוק אותה ולהעתיק ממנה קבצים — בלי לכתוב לכונן."] =
            ". Listing both would mean that writing to one destroys the other. You can still scan it and copy files from it — without writing to the drive.",
        ["לא ניתן לקרוא את תחילת הכונן."] =
            "Cannot read the beginning of the drive.",
        ["המחיצה מתחילה בתחילת הכונן, ולכן היא אינה זקוקה לטבלת מחיצות. אם Windows אינו מזהה אותה, השתמשו בתיקון המחיצה."] =
            "The partition starts at the beginning of the drive, so it does not need a partition table. If Windows does not recognize it, use partition repair.",
        ["בתחילת הכונן יש מערכת קבצים פעילה. יצירת טבלת מחיצות הייתה דורסת אותה."] =
            "There is an active file system at the beginning of the drive. Creating a partition table would overwrite it.",
        ["המחיצה נמצאת מעבר ל-2TB הראשונים של הכונן, ולכן טבלת MBR אינה יכולה לתאר אותה."] =
            "The partition lies beyond the first 2TB of the drive, so an MBR table cannot describe it.",
        ["כל ארבע הרשומות בטבלת המחיצות של הכונן תפוסות, ואין מקום לרשום מחיצה נוספת."] =
            "All four entries in the drive's partition table are in use, and there is no room for another partition.",
        ["לכונן אין טבלת מחיצות. תיווצר טבלה חדשה, ובה המחיצה שנמצאה."] =
            "The drive has no partition table. A new table will be created containing the found partition.",
        ["בטבלת המחיצות של הכונן יש רשומה פנויה, והמחיצה תירשם בה."] =
            "The drive's partition table has a free entry, and the partition will be recorded in it.",
        ["תיכתב טבלת מחיצות חדשה בתחילת הכונן. המחיצה עצמה והקבצים לא ישתנו."] =
            "A new partition table will be written at the beginning of the drive. The partition itself and the files will not change.",
        ["תשתנה רשומה אחת בטבלת המחיצות, בתחילת הכונן. המחיצה עצמה והקבצים לא ישתנו."] =
            "One entry in the partition table, at the beginning of the drive, will change. The partition itself and the files will not change.",
        ["כותרת טבלת ה-GPT של הכונן פגומה, ולכן לא ניתן לרשום בה מחיצה בבטחה."] =
            "The drive's GPT header is damaged, so a partition cannot be safely added to it.",
        ["כותרת טבלת ה-GPT מכילה ערכים לא תקינים."] =
            "The GPT header contains invalid values.",
        ["המחיצה חורגת מהאזור שטבלת ה-GPT מאפשרת למחיצות."] =
            "The partition extends beyond the area the GPT allows for partitions.",
        ["לא ניתן לקרוא את רשומות טבלת ה-GPT."] =
            "Cannot read the GPT entries.",
        ["כל הרשומות בטבלת ה-GPT תפוסות."] =
            "All GPT entries are in use.",
        ["בטבלת ה-GPT של הכונן יש רשומה פנויה, והמחיצה תירשם בה."] =
            "The drive's GPT has a free entry, and the partition will be recorded in it.",
        ["תתווסף רשומה אחת לטבלת ה-GPT — בעותק הראשי שבתחילת הכונן ובעותק המשני שבסופו. המחיצה עצמה והקבצים לא ישתנו."] =
            "One entry will be added to the GPT — in the primary copy at the beginning of the drive and in the backup copy at its end. The partition itself and the files will not change.",
        ["תיקיית הגיבוי חייבת להיות על כונן אחר מהכונן שמשתנה."] =
            "The backup folder must be on a drive other than the one being changed.",
        ["לא ניתן לקרוא את מה שעומד להשתנות בכונן כדי לגבות אותו, ולכן לא נכתב דבר."] =
            "Cannot read what is about to change on the drive in order to back it up, so nothing was written.",
        ["לא ניתן ליצור קובץ גיבוי, ולכן דבר לא נכתב: {0}"] =
            "Cannot create a backup file, so nothing was written: {0}",
        ["הכתיבה לכונן נכשלה (שגיאת Windows {0}). "] =
            "Writing to the drive failed (Windows error {0}). ",
        ["המצב הקודם הוחזר."] =
            "The previous state was restored.",
        ["קובץ הגיבוי שמור ב: {0}"] =
            "The backup file is saved at: {0}",
        ["המחיצה הוחזרה לטבלת המחיצות. "] =
            "The partition was restored to the partition table. ",
        ["תחילת המחיצה (מגזר האתחול) עדיין פגומה, ולכן Windows יציג אותה כמחיצה שדורשת פירמוט — אל תפרמט. השלב הבא הוא תיקון המחיצה, או העתקת הקבצים דרך עותק הגיבוי. "] =
            "The beginning of the partition (boot sector) is still damaged, so Windows will show it as a partition that needs formatting — do not format. The next step is partition repair, or copying the files through the backup copy. ",
        ["אם היא עדיין לא מופיעה ב-Windows, נתקו וחברו את הכונן או הפעילו מחדש את המחשב. "] =
            "If it still does not appear in Windows, disconnect and reconnect the drive or restart the computer. ",
        ["גיבוי המצב הקודם נשמר ב: {0}"] =
            "A backup of the previous state was saved at: {0}",
        ["הטבלה נכתבה, אך המחיצה לא הופיעה בה כמצופה — ולכן המצב הקודם הוחזר אוטומטית. הכונן נותר כפי שהיה."] =
            "The table was written, but the partition did not appear in it as expected — so the previous state was restored automatically. The drive is as it was.",
        ["הטבלה נכתבה, המחיצה לא הופיעה בה, וגם החזרת המצב הקודם נכשלה. קובץ הגיבוי שמור ב: {0}"] =
            "The table was written, the partition did not appear in it, and restoring the previous state also failed. The backup file is saved at: {0}",
        ["בדיקת הדיסק של Windows שמרה את הקובץ הזה בלי השם המקורי. לפי התוכן זה {0}."] =
            "Windows disk check saved this file without its original name. Judging by its content, it is {0}.",
        // ------------------------------------------------ סורקים, תמונות דיסק, כוננים
        ["המחיצה אינה exFAT תקין, או שתחילת המחיצה (מגזר האתחול) פגומה."] =
            "The partition is not a valid exFAT, or the beginning of the partition (boot sector) is damaged.",
        ["רשומת הקובץ אינה מציינת היכן בכונן מתחיל התוכן שלו."] =
            "The file's entry does not say where on the drive its content starts.",
        ["הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו."] =
            "The file exists in the file system and its location is fully known.",
        ["הרשומה מציינת שהקובץ היה רציף על הכונן, ולכן מיקומו ידוע בוודאות."] =
            "The entry says the file was contiguous on the drive, so its location is known for certain.",
        ["הקובץ היה מפוצל ל-{0} חלקים, והמפה של חלקיו שרדה במלואה — מיקום כל חלק ידוע."] =
            "The file was split into {0} parts, and the map of its parts survived intact — the location of every part is known.",
        ["המפה של חלקי הקובץ שרדה במלואה, ולכן מיקומו ידוע."] =
            "The map of the file's parts survived intact, so its location is known.",
        ["הקובץ לא נשמר ברצף, והמפה של חלקיו אינה שלמה עוד. השחזור מניח רצף — ייתכן שחלק מהתוכן יהיה של קובץ אחר."] =
            "The file was not stored contiguously, and the map of its parts is no longer complete. Recovery assumes it was contiguous — some of the content may belong to another file.",
        ["נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. {0}"] =
            "Data was found, and all the space the file occupied on the drive is still free. {0}",
        ["נמצאו נתונים. כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. {1}"] =
            "Data was found. About {0} of the space the file occupied on the drive is already used by other files. {1}",
        ["נמצאו נתונים, אבל {0}"] =
            "Data was found, but {0}",
        ["קורא את ספריית השורש"] =
            "Reading the root directory",
        ["{0} קבצים נמצאו ברשומות הספרייה אך אזור הנתונים שלהם מכיל אפסים. הם סומנו כלא ניתנים לשחזור."] =
            "{0} files were found in the directory entries, but their data area contains only zeros. They were marked as unrecoverable.",
        ["עובר על ספריות מערכת הקבצים"] =
            "Walking the file system's directories",
        ["סורק ספריות יתומות על פני המחיצה"] =
            "Scanning the partition for orphaned directories",
        ["הסריקה העמוקה איתרה {0} שרידי תיקיות שאינם מקושרים עוד לעץ התיקיות."] =
            "The deep scan found {0} folder remnants that are no longer linked to the folder tree.",
        ["הקובץ ריק ואין לו תוכן לשחזר."] =
            "The file is empty and has no content to recover.",
        ["אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר."] =
            "The file's data area contains only zeros — the content was erased. It cannot be recovered.",
        ["לא ניתן היה לקרוא את אזור הנתונים של הקובץ."] =
            "The file's data area could not be read.",
        ["כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים."] =
            "Almost all the space the file occupied on the drive is already used by other files.",
        ["המחיצה אינה FAT תקין, או שתחילת המחיצה (מגזר האתחול) פגומה."] =
            "The partition is not a valid FAT, or the beginning of the partition (boot sector) is damaged.",
        ["ב-FAT מחיקת קובץ מוחקת את המפה של חלקיו. השחזור מניח שהקובץ נשמר ברצף מתחילתו — הנחה נכונה ברוב הקבצים, אך קובץ שהיה מפוצל על פני הכונן ישוחזר פגום."] =
            "In FAT, deleting a file erases the map of its parts. Recovery assumes the file was stored contiguously from its start — true for most files, but a file that was split across the drive will be recovered damaged.",
        [" שימו לב: שם הקובץ נשמר בתבנית הקצרה בלבד, ומחיקה ב-FAT דורסת את האות הראשונה שלו. התוכן שלם, אך האות הראשונה בשם הוחלפה בקו תחתון."] =
            " Note: the file name was stored in short form only, and deletion in FAT overwrites its first letter. The content is complete, but the first letter of the name was replaced by an underscore.",
        ["רשומת הקובץ אינה מציינת היכן בכונן מתחיל התוכן שלו, ולכן לא ניתן לאתר אותו."] =
            "The file's entry does not say where on the drive its content starts, so it cannot be located.",
        ["הקובץ קיים במערכת הקבצים, והמפה של חלקיו שלמה."] =
            "The file exists in the file system, and the map of its parts is complete.",
        ["נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. השחזור מניח שהקובץ היה רציף על הכונן."] =
            "Data was found, and all the space the file occupied on the drive is still free. Recovery assumes the file was contiguous on the drive.",
        ["נמצאו נתונים. כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים."] =
            "Data was found. About {0} of the space the file occupied on the drive is already used by other files.",
        ["כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום."] =
            "About {0} of the space the file occupied on the drive is already used by other files. The file will be recovered damaged.",
        ["המחיצה אינה NTFS תקין, או שתחילת המחיצה (מגזר האתחול) פגומה."] =
            "The partition is not a valid NTFS, or the beginning of the partition (boot sector) is damaged.",
        ["הסריקה העמוקה קראה {0} GB מהמחיצה ואיתרה {1} קבצים נוספים שרשומתם כבר אינה בטבלת הקבצים (MFT)."] =
            "The deep scan read {0} GB of the partition and found {1} more files whose records are no longer in the file table (MFT).",
        ["{0} קבצים נמצאו ברשומות המטא-דאטה אך תוכנם כבר אינו קיים על הכונן. "] =
            "{0} files were found in the metadata records, but their content no longer exists on the drive. ",
        ["הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM). "] =
            "This drive erases the content of deleted files by itself (TRIM). ",
        ["המקום שלהם בכונן נדרס או אופס. "] =
            "Their space on the drive was overwritten or zeroed. ",
        ["קבצים אלה סומנו כלא ניתנים לשחזור ולא יוצעו לשחזור."] =
            "These files were marked as unrecoverable and will not be offered for recovery.",
        ["קורא את טבלת הקבצים"] =
            "Reading the file table",
        ["סורק רשומות יתומות על פני המחיצה"] =
            "Scanning the partition for orphaned records",
        ["תוכן הקובץ שמור בתוך רשומת המטא-דאטה ונקרא במלואו."] =
            "The file's content is stored inside its metadata record and was read in full.",
        ["לא נמצא מידע על מיקום תוכן הקובץ על הדיסק."] =
            "No information was found about where the file's content is on the disk.",
        ["הקובץ קיים במערכת הקבצים ותוכנו שלם."] =
            "The file exists in the file system and its content is complete.",
        ["רשומת הקובץ שרדה, אך אזור הנתונים שלו מכיל אפסים בלבד. הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM), והתוכן כבר אינו קיים. לא ניתן לשחזר."] =
            "The file's record survived, but its data area contains only zeros. This drive erases the content of deleted files by itself (TRIM), and the content no longer exists. It cannot be recovered.",
        ["אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק או אופס. לא ניתן לשחזר."] =
            "The file's data area contains only zeros — the content was erased or zeroed. It cannot be recovered.",
        ["לא ניתן היה לקרוא את אזור הנתונים של הקובץ לצורך בדיקה."] =
            "The file's data area could not be read for checking.",
        ["תוכן הקובץ לא אומת מול הדיסק. ייתכן שהשחזור יניב קובץ ריק."] =
            "The file's content was not verified against the disk. The recovery may produce an empty file.",
        ["נמצאו נתונים בקובץ. לא ניתן היה לבדוק אם קבצים אחרים נכתבו במקומו."] =
            "Data was found in the file. It could not be checked whether other files were written in its place.",
        ["נמצאו נתונים בקובץ."] =
            "Data was found in the file.",
        ["נמצאו נתונים בקובץ, וכל המקום שהוא תפס בכונן עדיין פנוי."] =
            "Data was found in the file, and all the space it occupied on the drive is still free.",
        ["נמצאו נתונים בקובץ. כ-{0} מהמקום שהוא תפס בכונן כבר תפוס על ידי קבצים אחרים."] =
            "Data was found in the file. About {0} of the space it occupied on the drive is already used by other files.",
        ["כמעט כל המקום שהקובץ תפס בכונן נדרס על ידי קבצים אחרים."] =
            "Almost all the space the file occupied on the drive was overwritten by other files.",
        ["יומן השינויים ($UsnJrnl) אינו קיים במחיצה זו, ולכן לא נסרק."] =
            "The change journal ($UsnJrnl) does not exist on this partition, so it was not scanned.",
        ["יומן השינויים אותר אך זרם הנתונים שלו אינו קריא."] =
            "The change journal was found, but its data stream cannot be read.",
        ["קורא את יומן השינויים של מערכת הקבצים"] =
            "Reading the file system's change journal",
        ["הקובץ אותר ביומן השינויים של מערכת הקבצים: הוא היה קיים ונמחק. היומן שומר את שמו ואת תיקיית האב שלו, אך אינו שומר היכן תוכנו ישב על הדיסק, ולכן לא ניתן לשחזר אותו. זוהי עדות לקיומו בלבד."] =
            "The file was found in the file system's change journal: it existed and was deleted. The journal keeps its name and parent folder, but not where its content was on the disk, so it cannot be recovered. This is only evidence that it existed.",
        ["יומן השינויים נסרק: {0} רשומות נבדקו, ומתוכן {1} קבצים שנמחקו ואינם קיימים עוד ברשומות המטא-דאטה. שמותיהם ידועים, אך תוכנם אינו ניתן לאיתור."] =
            "The change journal was scanned: {0} records were checked, including {1} deleted files that no longer exist in the metadata records. Their names are known, but their content cannot be located.",
        ["יומן הטרנזקציות ($LogFile) אינו קריא במחיצה זו."] =
            "The transaction log ($LogFile) cannot be read on this partition.",
        ["סורק את יומן הטרנזקציות"] =
            "Scanning the transaction log",
        ["הקובץ אותר בשריד רשומה ביומן הטרנזקציות. ידועים שמו, גודלו ותאריכיו, אך לא נשמר בו מיקום תוכנו על הדיסק, ולכן לא ניתן לשחזר אותו. זוהי עדות לקיומו בלבד."] =
            "The file was found in a record remnant in the transaction log. Its name, size and dates are known, but the location of its content on the disk was not kept, so it cannot be recovered. This is only evidence that it existed.",
        ["יומן הטרנזקציות נסרק: {0} שמות קבצים נוספים חולצו משרידי רשומות. גם עבורם ידוע השם בלבד, ללא מיקום התוכן."] =
            "The transaction log was scanned: {0} more file names were extracted from record remnants. For these too only the name is known, without the content's location.",
        ["לא אותרו שרידי רשומות ביומן הטרנזקציות."] =
            "No record remnants were found in the transaction log.",
        ["משחזר את עץ התיקיות"] =
            "Rebuilding the folder tree",
        ["חלק קטן מהקובץ ישוחזר עם תוכן זר."] =
            "A small part of the file will be recovered with foreign content.",
        ["הקובץ ישוחזר פגום."] =
            "The file will be recovered damaged.",
        ["כמעט כל התוכן שלו נדרס."] =
            "Almost all of its content was overwritten.",
        ["קובץ מחוק אחר שנכתב אחריו — {0} — נכתב על כ-{1} מהמקום של הקובץ הזה, ולכן התוכן שם כבר אינו שלו. {2}"] =
            "Another deleted file written after it — {0} — was written over about {1} of this file's space, so the content there is no longer its own. {2}",
        [" קובץ מחוק אחר ({0}) נכתב על חלק מאותו מקום, ולא ידוע מי מהשניים נכתב אחרון — ייתכן שחלק מהתוכן שלו."] =
            " Another deleted file ({0}) was written over part of the same space, and it is not known which of the two was written last — some of the content may be its.",
        ["{0} ועוד {1}"] =
            "{0} and {1} more",
        ["{0} קבצים ותיקיות שנמחקו דרך סל המחזור קיבלו בחזרה את השם והתיקייה המקוריים."] =
            "{0} files and folders deleted through the Recycle Bin got back their original name and folder.",
        ["{0} קבצים שבדיקת הדיסק של Windows השאירה בתיקיית FOUND בלי שם זוהו לפי התוכן שלהם וקיבלו בחזרה את הסוג הנכון."] =
            "{0} files that Windows disk check left nameless in a FOUND folder were identified by their content and got back their correct type.",
        ["{0} קבצים מחוקים דורגו מחדש: קובץ מחוק אחר, שנכתב אחריהם, נכתב במקום שלהם בכונן — גם אם עכשיו המקום נראה פנוי."] =
            "{0} deleted files were re-rated: another deleted file, written after them, was written in their place on the drive — even if the space now looks free.",
        ["מערכת הקבצים {0} אינה נתמכת לסריקה בגרסה זו."] =
            "The {0} file system is not supported for scanning in this version.",
        ["לא נבחר קובץ יעד לתמונה."] =
            "No destination file was chosen for the image.",
        ["כונן וירטואלי (VHD) אפשר ליצור רק מכונן שלם — Windows לא יודע לחבר מחיצה בודדת בלי טבלת מחיצות. בחרו תמונה רגילה, או צרו תמונה של הכונן כולו."] =
            "A virtual drive (VHD) can only be created from a whole drive — Windows cannot attach a single partition without a partition table. Choose a regular image, or create an image of the whole drive.",
        ["כונן וירטואלי (VHD) מוגבל ל-2040GB, והכונן הזה בגודל {0}. בחרו תמונה רגילה."] =
            "A virtual drive (VHD) is limited to 2040GB, and this drive is {0}. Choose a regular image.",
        ["זו כבר תמונת דיסק. ניתן לסרוק ולשחזר ממנה ישירות."] =
            "This is already a disk image. You can scan and recover from it directly.",
        ["תיקיית היעד אינה קיימת."] =
            "The destination folder does not exist.",
        ["התמונה חייבת להישמר על כונן אחר מזה שממנו היא נוצרת: כתיבה לאותו כונן הייתה דורסת בדיוק את הנתונים שמנסים להציל."] =
            "The image must be saved on a drive other than the one it is created from: writing to the same drive would overwrite exactly the data you are trying to save.",
        ["כונן היעד מפורמט ב-FAT32, שאינו מאפשר קובץ גדול מ-4GB, והתמונה תהיה בגודל {0}. בחרו כונן NTFS או exFAT."] =
            "The destination drive is formatted as FAT32, which does not allow files larger than 4GB, and the image will be {0}. Choose an NTFS or exFAT drive.",
        ["אין מספיק מקום בכונן היעד: התמונה דורשת {0}, ופנויים {1}."] =
            "There is not enough space on the destination drive: the image needs {0}, and {1} is free.",
        ["לא נמצאה תמונה קודמת להמשיך ממנה."] =
            "No previous image was found to continue from.",
        ["בנתיב הזה יש תמונה של מקור אחר (בגודל שונה). התחלה מחדש תדרוס אותה."] =
            "There is an image of a different source (with a different size) at this path. Starting over will overwrite it.",
        ["קובץ התמונה קצר מהצפוי — ייתכן שנקטע. אי אפשר להמשיך ממנו."] =
            "The image file is shorter than expected — it may have been cut off. It cannot be continued.",
        ["התמונה הזו כבר שלמה, והכונן כולו נקרא."] =
            "This image is already complete, and the whole drive was read.",
        ["מעבר 1 — השלמת מה שלא הועתק"] =
            "Pass 1 — completing what was not copied",
        ["מעבר 1 — העתקה מהירה"] =
            "Pass 1 — fast copy",
        ["מעבר 2 — ניסיון חוזר באזורים פגומים"] =
            "Pass 2 — retrying damaged areas",
        ["מעבר 3 — קריאה מהכיוון ההפוך באזורים פגומים"] =
            "Pass 3 — reading damaged areas in reverse",
        ["הכונן נותק באמצע ההעתקה. מה שהועתק נשמר — חברו אותו שוב ובחרו באותה תמונה כדי להמשיך מאותה נקודה. "] =
            "The drive was disconnected during copying. What was copied is saved — reconnect it and choose the same image to continue from the same point. ",
        ["ההעתקה נעצרה. הועתקו: {0}; לא הועתקו: {1}. אפשר לסרוק את התמונה החלקית, או להמשיך אותה — בחרו שוב את אותו קובץ ביצירת תמונה, ורק מה שחסר ייקרא מהכונן."] =
            "Copying stopped. Copied: {0}; not copied: {1}. You can scan the partial image, or continue it — choose the same file again when creating an image, and only what is missing will be read from the drive.",
        ["התמונה הושלמה. הכונן כולו נקרא בהצלחה — התמונה זהה לכונן."] =
            "The image is complete. The whole drive was read successfully — the image is identical to the drive.",
        ["התמונה הושלמה. {0} לא נקראו מהכונן גם בניסיון החוזר ומולאו באפסים. כל השאר הועתק. קבצים שישבו באזורים האלה יחזרו פגומים חלקית; כל השאר ישוחזרו כרגיל."] =
            "The image is complete. {0} could not be read from the drive even on retry and were filled with zeros. Everything else was copied. Files that were in those areas will come back partly damaged; everything else will be recovered normally.",
        ["דיסק {0}"] =
            "Disk {0}",
        ["דיסק מגנטי מסתובב: הסריקה תתבצע ברצף מתחילת הדיסק ועד סופו, בבלוקים של 4MB ובזרם קריאה יחיד, כדי למנוע תנועות ראש מיותרות. בדיסק מסוג זה נתונים שנמחקו נשארים על הצלחת עד לדריסה — סיכויי השחזור גבוהים."] =
            "Spinning magnetic disk: the scan runs sequentially from the start of the disk to its end, in 4MB blocks with a single read stream, to avoid unnecessary head movement. On this kind of disk, deleted data stays on the platter until overwritten — recovery chances are high.",
        ["כונן NVMe: אין עלות גישה אקראית, ולכן הסריקה תרוץ ב-8 ערוצים מקבילים כדי לנצל את התורים הפנימיים של הבקר ולהגיע למהירות מרבית."] =
            "NVMe drive: random access costs nothing, so the scan runs in 8 parallel streams to use the controller's internal queues and reach maximum speed.",
        ["כונן SSD: אין עלות גישה אקראית, ולכן הסריקה תרוץ ב-4 ערוצים מקבילים."] =
            "SSD drive: random access costs nothing, so the scan runs in 4 parallel streams.",
        ["שימו לב: הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM), לרוב תוך דקות. שחזור אפשרי בעיקר לקבצים שנמחקו לאחרונה מאוד. מומלץ לכבות את המחשב ולסרוק בהקדם האפשרי."] =
            "Note: this drive erases the content of deleted files by itself (TRIM), usually within minutes. Recovery is mainly possible for very recently deleted files. Shut down the computer and scan as soon as possible.",
        ["כרטיס זיכרון: רוב הכרטיסים אינם מוחקים מעצמם תוכן של קבצים שנמחקו (TRIM), ולכן הוא נשאר על הכרטיס. הסריקה תתבצע ברצף, בקצב שמתאים לכרטיס."] =
            "Memory card: most cards do not erase the content of deleted files by themselves (TRIM), so it stays on the card. The scan runs sequentially, at a pace that suits the card.",
        ["התקן USB נייד: רוב ההתקנים אינם מוחקים מעצמם תוכן של קבצים שנמחקו (TRIM), ולכן הוא נשאר על ההתקן. הסריקה תתבצע ברצף, בקצב שמתאים להתקן."] =
            "Portable USB device: most devices do not erase the content of deleted files by themselves (TRIM), so it stays on the device. The scan runs sequentially, at a pace that suits the device.",
        ["תמונת דיסק: הסריקה קוראת את קובץ התמונה בלבד, ברצף ובבלוקים של 4MB. הכונן המקורי אינו נקרא כלל — כך ניתן לסרוק שוב ושוב בלי לסכן כונן חלש."] =
            "Disk image: the scan reads only the image file, sequentially in 4MB blocks. The original drive is not read at all — so you can scan again and again without risking a weak drive.",
        ["בתמונה זו יש אזורים שלא נקראו מהכונן המקורי. קבצים שישבו בהם יחזרו פגומים חלקית."] =
            "This image has areas that were not read from the original drive. Files that were there will come back partly damaged.",
        ["תקליטור: קריאה רציפה איטית בבלוקים קטנים, עם סבלנות לשגיאות קריאה."] =
            "Optical disc: slow sequential reading in small blocks, tolerant of read errors.",
        ["סוג ההתקן לא זוהה בוודאות — נבחרה אסטרטגיית סריקה מאוזנת."] =
            "The device type could not be identified for certain — a balanced scan strategy was chosen.",
        ["[דליל × {0}]"] =
            "[sparse × {0}]",
        ["חסר קובץ של התמונה: \"{0}\". כל קובצי התמונה (E01, E02 וכן הלאה) צריכים להיות באותה תיקייה."] =
            "An image file is missing: \"{0}\". All the image files (E01, E02 and so on) must be in the same folder.",
        ["הקובץ \"{0}\" אינו חלק של תמונת E01, או שתחילתו פגומה."] =
            "The file \"{0}\" is not part of an E01 image, or its beginning is damaged.",
        ["המידע על הכונן שבתוך תמונת ה-E01 פגום."] =
            "The drive information inside the E01 image is damaged.",
        ["בתמונה חסרים חלקים: נמצאו {0} מתוך {1}. ייתכן שחסר אחד מקובצי התמונה, או שהתמונה לא הושלמה."] =
            "Parts of the image are missing: {0} of {1} were found. One of the image files may be missing, or the image was not completed.",
        ["הדיסק"] =
            "the disk",
        ["הכונן אינו מחובר. חברו אותו שוב, רעננו את רשימת הכוננים ונסו שוב."] =
            "The drive is not connected. Reconnect it, refresh the drive list and try again.",
        ["אין הרשאה לקרוא את {0}. ודאו שהתוכנה פועלת בהרשאות מנהל."] =
            "No permission to read {0}. Make sure the program is running as administrator.",
        ["{0} בשימוש של תוכנה אחרת שנעלה אותו. סגרו אותה ונסו שוב."] =
            "Another program is using {0} and has locked it. Close it and try again.",
        ["לא ניתן לפתוח את {0} לקריאה. ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל."] =
            "Cannot open {0} for reading. Make sure it is connected and the program is running as administrator.",
        ["לא ניתן לפתוח את {0} לקריאה (שגיאה {1}). ודאו שהוא מחובר ושהתוכנה פועלת בהרשאות מנהל."] =
            "Cannot open {0} for reading (error {1}). Make sure it is connected and the program is running as administrator.",
        ["כונן שנפתח דרך Windows הוא לקריאה בלבד — התוכנה אינה כותבת אליו."] =
            "A drive opened through Windows is read-only — the program does not write to it.",
        ["כונן וירטואלי נפתח לקריאה בלבד — התוכנה אינה כותבת אליו."] =
            "A virtual drive is opened read-only — the program does not write to it.",
        ["זו תמונה בפורמט Ex01 — הגרסה החדשה של E01, שהתוכנה עוד לא קוראת. אם אפשר, צרו את התמונה מחדש בפורמט E01 הרגיל, או המירו אותה לתמונה גולמית (dd)."] =
            "This is an Ex01 image — the newer version of E01, which the program cannot read yet. If possible, create the image again in the regular E01 format, or convert it to a raw (dd) image.",
        ["סוג כונן VHD לא מוכר ({0})."] =
            "Unknown VHD drive type ({0}).",
        ["הכותרת של הכונן הווירטואלי פגומה."] =
            "The virtual drive's header is damaged.",
        ["טבלת הבלוקים של הכונן הווירטואלי פגומה."] =
            "The virtual drive's block table is damaged.",
        ["הכותרות של הכונן הווירטואלי פגומות."] =
            "The virtual drive's headers are damaged.",
        ["טבלת האזורים של הכונן הווירטואלי פגומה."] =
            "The virtual drive's region table is damaged.",
        ["המידע על הכונן הווירטואלי פגום."] =
            "The virtual drive's information is damaged.",
        ["כונן VMDK בשיטת דחיסה שהתוכנה לא מכירה ({0})."] =
            "A VMDK drive with a compression method the program does not know ({0}).",
        ["סוף הכונן הווירטואלי הדחוס חסר או פגום — ייתכן שהייצוא לא הושלם."] =
            "The end of the compressed virtual drive is missing or damaged — the export may not have completed.",
        ["כונן VMDK מסוג שהתוכנה לא מכירה ({0})."] =
            "A VMDK drive of a type the program does not know ({0}).",
        ["חסר קובץ של הכונן הווירטואלי: \"{0}\". כל קובצי ה-VMDK של הכונן צריכים להיות באותה תיקייה."] =
            "A virtual drive file is missing: \"{0}\". All of the drive's VMDK files must be in the same folder.",
        ["לא ניתן לפתוח את \"{0}\". ייתכן שהוא בשימוש בתוכנה אחרת."] =
            "Cannot open \"{0}\". It may be in use by another program.",
        ["החלק \"{0}\" של הכונן הווירטואלי פגום."] =
            "The \"{0}\" part of the virtual drive is damaged.",
        ["קובץ התיאור של הכונן הווירטואלי לא מפרט אף קובץ נתונים."] =
            "The virtual drive's descriptor file does not list any data file.",
        ["זה כונן {0} מסוג \"הפרשים\": הוא שומר רק את מה שהשתנה, והשאר נמצא בקובץ אחר (קובץ ההורה). פתחו את קובץ ההורה במקום — או חברו את שניהם ב-Windows (ניהול דיסקים ← צירוף VHD) וסרקו את הכונן שנוסף."] =
            "This is a \"differencing\" {0} drive: it keeps only what changed, and the rest is in another file (the parent file). Open the parent file instead — or attach both in Windows (Disk Management → Attach VHD) and scan the added drive.",
        ["המחיצה אינה מחוברת; אין צורך בנעילה."] =
            "The partition is not mounted; no lock is needed.",
        ["לא ניתן לפתוח את אמצעי האחסון {0}: לנעילה."] =
            "Cannot open volume {0}: for locking.",
        ["לא ניתן לנעול את כונן {0}: — ככל הנראה קובץ כלשהו עליו פתוח. סגרו את כל החלונות והתוכנות שמשתמשות בכונן ונסו שוב."] =
            "Cannot lock drive {0}: — some file on it is probably open. Close all windows and programs that use the drive and try again.",
        ["כונן {0}: ננעל ונותק לצורך הכתיבה."] =
            "Drive {0}: was locked and dismounted for writing.",
        ["הקובץ נשמר בגרסה חדשה יותר של התוכנה. עדכנו את RAF כדי לפתוח אותו."] =
            "The file was saved by a newer version of the program. Update RAF to open it.",
        ["זה אינו קובץ סריקה של RAF, או שהקובץ פגום."] =
            "This is not a RAF scan file, or the file is damaged.",
        // ------------------------------------------------ שחזור ותיקון
        ["לא נבחרה תיקיית יעד לשחזור."] =
            "No destination folder was chosen for recovery.",
        ["נתיב היעד אינו תקין: {0}"] =
            "The destination path is invalid: {0}",
        ["לא ניתן לוודא על איזה דיסק יושב הכונן שממנו משחזרים, ולכן גם לא שהיעד אינו עליו. ודאו שהכונן עדיין מחובר ופתוח, ונסו שוב."] =
            "Cannot determine which disk the source drive is on, so it cannot be verified that the destination is not on it. Make sure the drive is still connected and open, and try again.",
        ["לא ניתן לשחזר לאותו דיסק שממנו משחזרים. כתיבה לדיסק המקור תדרוס את הקבצים שטרם שוחזרו ותמנע את שחזורם. בחרו כונן אחר, למשל התקן USB חיצוני."] =
            "You cannot recover to the same disk you are recovering from. Writing to the source disk would overwrite files not yet recovered and prevent their recovery. Choose another drive, for example an external USB device.",
        ["כונן היעד אינו זמין."] =
            "The destination drive is not available.",
        ["דיסק המקור"] =
            "the source disk",
        ["לא ניתן לקרוא את מבנה המחיצה לצורך השחזור."] =
            "Cannot read the partition structure for the recovery.",
        ["לא נמצא מידע על מיקום תוכן הקובץ — רשומת המטא-דאטה שלו נדרסה."] =
            "No information was found about where the file's content is — its metadata record was overwritten.",
        ["התוכן נבדק בזמן הסריקה ונמצא ריק — הקובץ לא נכתב."] =
            "The content was checked during the scan and found empty — the file was not written.",
        ["הקובץ סומן כבלתי ניתן לשחזור."] =
            "The file was marked as unrecoverable.",
        ["תוכן הקובץ כבר אינו קיים על הדיסק — אזור הנתונים שלו מכיל אפסים בלבד."] =
            "The file's content no longer exists on the disk — its data area contains only zeros.",
        ["{0} בתים לא נקראו מהדיסק ונכתבו כאפסים."] =
            "{0} bytes could not be read from the disk and were written as zeros.",
        ["נכתבו {0} מתוך {1} בתים."] =
            "{0} of {1} bytes were written.",
        ["תמונה מוקטנת שלמה ({0}×{1}) מתוך התמונה הפגומה"] =
            "Complete thumbnail ({0}×{1}) from the damaged image",
        ["_חלקיים"] =
            "_partial",
        ["{0} (תמונה מוקטנת).jpg"] =
            "{0} (thumbnail).jpg",
        ["{0} (תמונה מוקטנת {1}).jpg"] =
            "{0} (thumbnail {1}).jpg",
        ["נתיב מקורי,נכתב אל,גודל (בתים),איכות,תוצאה,פירוט,SHA-256,מיקום בכונן"] =
            "Original path,Written to,Size (bytes),Quality,Result,Details,SHA-256,Location on drive",
        ["קרא אותי.txt"] =
            "Read me.txt",
        ["קרא אותי - שחזור מתקדם חינם.txt"] =
            "Read me - Recovery Advanced Free.txt",
        ["(תמונה מוקטנת"] =
            "(thumbnail",
        ["שחזור מ-{0} בשעה {1}"] =
            "Recovery from {0} at {1}",
        ["המקור: {0}"] =
            "Source: {0}",
        ["שוחזר קובץ אחד"] =
            "One file was recovered",
        ["שוחזרו {0} קבצים"] =
            "{0} files were recovered",
        [", חלקית."] =
            ", partly.",
        [", מהם {0} חלקית."] =
            ", {0} of them partly.",
        ["קובץ אחד לא נכתב — הסיבה בדוח."] =
            "One file was not written — the reason is in the report.",
        ["{0} קבצים לא נכתבו — הסיבה לכל אחד מהם בדוח."] =
            "{0} files were not written — the reason for each is in the report.",
        ["השחזור נעצר באמצע, ולכן לא כל הקבצים שנבחרו נמצאים כאן."] =
            "The recovery stopped midway, so not all the selected files are here.",
        ["משך השחזור: {0} דקות ו-{1} שניות."] =
            "Recovery time: {0} minutes and {1} seconds.",
        ["מה יש בתיקייה"] =
            "What is in this folder",
        ["• הקבצים ששוחזרו במלואם — באותן תיקיות שבהן היו במקור."] =
            "• Fully recovered files — in the same folders they were originally in.",
        ["• הקבצים ששוחזרו במלואם — כולם ישירות בתיקייה הזו, בלי מבנה התיקיות המקורי."] =
            "• Fully recovered files — all directly in this folder, without the original folder structure.",
        ["  קבצים שנמצאו בסריקה מתקדמת מסודרים בתיקיות לפי הסוג שלהם, ובלי השמות המקוריים —"] =
            "  Files found by an advanced scan are sorted into folders by type, without their original names —",
        ["  הסריקה הזו מוצאת קבצים לפי התוכן שלהם, והשם לא נשמר בתוכן."] =
            "  that scan finds files by their content, and the name is not stored in the content.",
        ["• {0} — קבצים שחלק מהם לא נקרא מהכונן. החלק החסר נכתב כאפסים:"] =
            "• {0} — files part of which could not be read from the drive. The missing part was written as zeros:",
        ["  בתמונה זה נראה כפס אפור או כתמונה שנקטעת, בסרטון — כקפיצה או כסוף מוקדם."] =
            "  in a picture it looks like a gray band or a cut-off image, in a video like a jump or an early end.",
        ["  לפעמים הם נפתחים בכל זאת. אל תמחקו אותם לפני שבדקתם."] =
            "  Sometimes they open anyway. Do not delete them before checking.",
        ["• קבצים ששמם מסתיים ב\"(תמונה מוקטנת)\" — גרסה קטנה ושלמה של תמונה פגומה, שנמצאה בתוך התמונה עצמה."] =
            "• Files whose name ends with \"(thumbnail)\" — a small, complete version of a damaged picture, found inside the picture itself.",
        ["• {0} — דוח מלא: לכל קובץ, מאיפה הגיע, לאן נכתב ומה קרה לו."] =
            "• {0} — full report: for each file, where it came from, where it was written and what happened to it.",
        ["  נפתח באקסל. העמודה SHA-256 היא \"טביעת אצבע\" של הקובץ — לבדיקה שהוא לא השתנה מאז."] =
            "  Opens in Excel. The SHA-256 column is a \"fingerprint\" of the file — to check it has not changed since.",
        ["קובץ לא נפתח?"] =
            "A file does not open?",
        ["1. נסו לפתוח אותו בתוכנה אחרת — לפעמים תוכנה אחת מוותרת ואחרת מצליחה."] =
            "1. Try opening it in another program — sometimes one program gives up and another succeeds.",
        ["2. בתוכנת השחזור, במסך הכוננים: \"תיקון קבצים שלא נפתחים\". אפשר פשוט לגרור את הקבצים לחלון."] =
            "2. In the recovery program, on the drives screen: \"Repair files that won't open\". You can simply drag the files onto the window.",
        ["   שם יש גם תיקון לסרטון שלא מתנגן, בעזרת סרטון תקין שצולם באותו מכשיר."] =
            "   There is also a fix there for a video that won't play, using a working video shot on the same device.",
        ["3. עדיין חסרים קבצים? נסו סוג סריקה אחר — עמוקה או מתקדמת."] =
            "3. Still missing files? Try another scan type — deep or advanced.",
        ["חשוב: עד שתסיימו לשחזר, אל תשמרו שום דבר על הכונן שממנו שחזרתם —"] =
            "Important: until you finish recovering, do not save anything on the drive you recovered from —",
        ["כל קובץ חדש שנכתב אליו עלול לדרוס קבצים שעוד אפשר להציל."] =
            "every new file written to it may overwrite files that can still be saved.",
        ["שחזור מתקדם חינם — הסבר על התיקייה הזו"] =
            "Recovery Advanced Free — about this folder",
        ["בתים"] =
            "bytes",
        ["{0} בתים"] =
            "{0} bytes",
        ["סקטור {0}"] =
            "Sector {0}",
        ["שוחזר"] =
            "Recovered",
        ["שוחזר חלקית"] =
            "Partly recovered",
        ["לא נכתב — ריק"] =
            "Not written — empty",
        ["דולג"] =
            "Skipped",
        ["נכשל"] =
            "Failed",
        ["מצוין"] =
            "Excellent",
        ["טוב"] =
            "Good",
        ["חלש"] =
            "Poor",
        ["לא ניתן לשחזור"] =
            "Unrecoverable",
        ["קובץ_{0}"] =
            "file_{0}",
        ["לא נמצא שם פנוי לקובץ בתיקיית היעד."] =
            "No free name for the file was found in the destination folder.",
        ["הקובץ ריק או מכיל אפסים בלבד. אין בו תוכן לתקן."] =
            "The file is empty or contains only zeros. There is no content to repair.",
        ["בתחילת הקובץ יש {0} בתים שאינם שייכים לשיר — השמע עצמו מתחיל רק אחריהם, ולכן נגנים לא מזהים את הקובץ. אפשר להסיר אותם."] =
            "At the start of the file there are {0} bytes that do not belong to the song — the audio itself starts only after them, so players do not recognize the file. They can be removed.",
        ["תחילת הקובץ נפגעה: חתימת הפתיחה של {0} — הבתים שמזהים את סוג הקובץ — חלקית או מאופסת. זה סימן לנזק, ולא לסוג קובץ אחר, ולכן ניתן לשחזר אותה."] =
            "The start of the file is damaged: the opening signature of {0} — the bytes that identify the file type — is partial or zeroed. This is a sign of damage, not of a different file type, so it can be restored.",
        ["הסיומת מציינת {0}, אך תחילת הקובץ אינה דומה לפורמט הזה ואינה מזוהה כפורמט אחר. ייתכן שהתוכן נדרס. לא ניתן לתקן בביטחון."] =
            "The extension indicates {0}, but the start of the file does not resemble that format and is not recognized as another format. The content may have been overwritten. It cannot be repaired with confidence.",
        ["התוכן הוא {0}, אך הסיומת היא .{1}. הסיומת הנכונה היא .{2}."] =
            "The content is {0}, but the extension is .{1}. The correct extension is .{2}.",
        ["(ללא)"] =
            "(none)",
        ["גם הבית שאחרי חתימת ה-JPEG (סמן המקטע הראשון) נפגע. המידע שאחריו שרד, ולכן ניתן לשחזר אותו במדויק."] =
            "The byte after the JPEG signature (the first segment marker) is also damaged. The data after it survived, so it can be restored exactly.",
        ["גם הבית שאחרי חתימת ה-JPEG (סמן המקטע הראשון) נפגע, וגם המידע שממנו ניתן היה לשחזר אותו אבד. לא ניתן לשחזר אותו בוודאות, והתמונה עלולה שלא להיפתח."] =
            "The byte after the JPEG signature (the first segment marker) is also damaged, and the data it could be restored from is lost too. It cannot be restored with certainty, and the image may not open.",
        [" לא נמצאו במסמך החלק הראשי שלו ולא רשימת העמודים, ולכן אי אפשר לבנות את הטבלה מחדש."] =
            " Neither the document's main part nor its page list was found, so the table cannot be rebuilt.",
        [" גם החלק הראשי של המסמך אבד, אבל רשימת העמודים שרדה: ייבנו טבלה וחלק ראשי חדשים מתוך {0} החלקים שנמצאו. העמודים יוצגו; תוכן עניינים וסימניות עלולים לחסור."] =
            " The document's main part is also lost, but the page list survived: a new table and main part will be built from the {0} parts found. The pages will display; the table of contents and bookmarks may be missing.",
        [" נמצאו {0} חלקים שלמים במסמך, ואפשר לבנות ממנו טבלה חדשה."] =
            " {0} complete parts were found in the document, and a new table can be built from them.",
        ["הסרטון לא נסגר כראוי: חסר בו האינדקס — החלק שאומר לנגן היכן כל תמונה וכל קטע קול. זה קורה כשההקלטה נקטעת (סוללה שנגמרה, כרטיס שנשלף, מכשיר שנתקע). "] =
            "The video was not closed properly: its index is missing — the part that tells the player where each frame and each piece of sound is. This happens when a recording is cut off (a dead battery, a removed card, a frozen device). ",
        ["התמונות והקול עצמם נמצאים בקובץ (⁦{0}⁩). אפשר לבנות אינדקס חדש בעזרת סרטון תקין אחד שצולם באותו מכשיר ובאותן הגדרות."] =
            "The frames and sound themselves are in the file (⁦{0}⁩). A new index can be built with the help of one working video shot on the same device with the same settings.",
        ["ההקלטה לא נסגרה כראוי — כך קורה כשהמכשיר נכבה או שהאפליקציה נסגרה באמצע. "] =
            "The recording was not closed properly — this happens when the device turns off or the app closes midway. ",
        ["בכותרת רשום שאין בה שמע כלל, "] =
            "The header says it contains no audio at all, ",
        ["בכותרת רשום אורך שגוי, "] =
            "The header records a wrong length, ",
        ["אבל בקובץ יש {0} של שמע. תיקון הכותרת יאפשר לנגן את כל ההקלטה."] =
            "but the file contains {0} of audio. Repairing the header will make the whole recording playable.",
        ["הגודל הכללי שרשום בכותרת ההקלטה אינו תואם לתוכן שלה. השמע עצמו שלם, ואפשר לתקן את הכותרת."] =
            "The overall size recorded in the recording's header does not match its content. The audio itself is complete, and the header can be repaired.",
        ["אחרי סוף ההקלטה יש {0} בתים עודפים שאינם חלק ממנה. ניתן להסיר אותם."] =
            "After the end of the recording there are {0} extra bytes that are not part of it. They can be removed.",
        ["אחרי סוף השיר יש {0} בתים עודפים שאינם חלק ממנו. ניתן להסיר אותם."] =
            "After the end of the song there are {0} extra bytes that are not part of it. They can be removed.",
        ["מבנה הקובץ מצהיר על {0} בתים, אך הקובץ מכיל {1}. {2} הבתים העודפים אינם חלק מהקובץ וניתן להסיר אותם."] =
            "The file structure declares {0} bytes, but the file contains {1}. The {2} extra bytes are not part of the file and can be removed.",
        ["הסרטון נקטע: הוא מצהיר על {0} בתים, ויש בו {1} — כ-{2}% ממנו. החלק שנשאר מתנגן כמו שהוא — ב-VLC, ב-Edge ובנגן של Windows — עד המקום שבו נקטע. החלק החסר אינו נמצא בקובץ, ולכן אין מה לתקן בו."] =
            "The video is cut off: it declares {0} bytes, and has {1} — about {2}% of it. The remaining part plays as it is — in VLC, in Edge and in the Windows player — up to where it was cut. The missing part is not in the file, so there is nothing to repair.",
        ["מבנה הקובץ מצהיר על {0} בתים, אך רק {1} קיימים. {2} בתים חסרים ואינם ניתנים לשחזור מתוך הקובץ עצמו. ייתכן שהקובץ ייפתח חלקית."] =
            "The file structure declares {0} bytes, but only {1} exist. {2} bytes are missing and cannot be restored from the file itself. The file may open partly.",
        ["סוף הקובץ חסר (חתימת הסיום של {0}) — ככל הנראה הקובץ נקטע. השלמת החתימה מאפשרת לרוב התוכנות לפתוח את החלק הקיים."] =
            "The end of the file is missing (the end signature of {0}) — the file was probably cut off. Completing the signature lets most programs open the existing part.",
        ["אחרי סוף הקובץ יש {0} בתים עודפים שאינם חלק ממנו. ניתן להסיר אותם."] =
            "After the end of the file there are {0} extra bytes that are not part of it. They can be removed.",
        ["התמונה פגומה: רק כ-{0} ממנה מתפענח, ומשם והלאה הנתונים אינם של התמונה — הקובץ נקטע, נדרס, או שחלקו נלקח מקובץ אחר. את החלק החסר אי אפשר להשלים."] =
            "The image is damaged: only about {0} of it decodes, and from there on the data is not the image's — the file was cut off, overwritten, or part of it came from another file. The missing part cannot be completed.",
        ["בתוך הקובץ שמורה תמונה מוקטנת שלמה, בגודל {0}×{1}. אפשר לשמור אותה כקובץ נפרד — גם אם התמונה עצמה לא תיפתח, היא תישאר."] =
            "A complete thumbnail, {0}×{1}, is stored inside the file. It can be saved as a separate file — even if the image itself will not open, the thumbnail will remain.",
        ["הטקסט של המסמך שרד — כ-{0} מילים. אפשר לשמור אותו כקובץ טקסט פשוט: העיצוב, התמונות והטבלאות לא יישמרו, אבל התוכן כן — גם אם המסמך עצמו לא ייפתח."] =
            "The document's text survived — about {0} words. It can be saved as a plain text file: the formatting, images and tables will not be kept, but the content will — even if the document itself will not open.",
        ["{0}:{1}:{2} שעות"] =
            "{0}:{1}:{2} hours",
        ["{0}:{1} דקות"] =
            "{0}:{1} minutes",
        ["הקובץ קצר מכותרת המסמך עצמה — כמעט כולו חסר."] =
            "The file is shorter than the document header itself — almost all of it is missing.",
        ["המסמך נקטע: חלק מטבלת ההקצאה שלו — המפה שאומרת היכן כל חלק של המסמך — נמצא מעבר לסוף הקובץ. Word לא יפתח אותו."] =
            "The document is cut off: part of its allocation table — the map that says where each part of the document is — lies beyond the end of the file. Word will not open it.",
        ["טבלת ההקצאה של המסמך — המפה שאומרת היכן כל חלק שלו — אופסה או נדרסה. Word לא יפתח אותו."] =
            "The document's allocation table — the map that says where each of its parts is — was zeroed or overwritten. Word will not open it.",
        ["המסמך נקטע: הוא אמור להיות באורך {0} בתים לפחות, ויש בו {1}. Word לא יפתח אותו."] =
            "The document is cut off: it should be at least {0} bytes long, and has {1}. Word will not open it.",
        ["{0} (תוקן)"] =
            "{0} (repaired)",
        ["נתיב היעד זהה לקובץ המקורי. התיקון בוטל."] =
            "The destination path is the same as the original file. The repair was cancelled.",
        ["בחרו תמונה תקינה שצולמה באותה מצלמה ובאותן הגדרות"] =
            "Choose a working photo taken with the same camera and the same settings",
        ["תמונות JPEG|*.jpg;*.jpeg|כל הקבצים|*.*"] =
            "JPEG photos|*.jpg;*.jpeg|All files|*.*",
        ["יש לבחור תיקייה לשמירת התמונה המתוקנת."] =
            "Choose a folder for the repaired photo.",
        ["תחילת התמונה נהרסה: הטבלאות וההגדרות שדרושות כדי לפענח אותה אבדו, ולכן היא לא נפתחת. "] =
            "The beginning of the photo is destroyed: the tables and settings needed to decode it are lost, so it won't open. ",
        ["נתוני התמונה עצמם שרדו (⁦{0}⁩). אפשר לבנות את התחילה מחדש בעזרת תמונה תקינה אחת שצולמה באותה מצלמה ובאותן הגדרות."] =
            "The image data itself survived ({0}). The beginning can be rebuilt using one working photo taken with the same camera and the same settings.",
        [" העותק נבדק מחדש — התמונה אמורה להיפתח."] =
            " The copy was checked again — the photo should open.",
        ["תמונת הדוגמה גדולה מדי."] =
            "The sample photo is too large.",
        ["הקובץ שנבחר אינו תמונת JPEG."] =
            "The selected file is not a JPEG photo.",
        ["תמונת הדוגמה שמורה בשיטה שאינה נתמכת (למשל JPEG מדורג, שנוצר בעריכה). בחרו תמונה כפי שיצאה מהמצלמה."] =
            "The sample photo is saved in an unsupported way (for example progressive JPEG, created by editing). Choose a photo as it came out of the camera.",
        ["תמונת הדוגמה עצמה פגומה. בחרו תמונה תקינה."] =
            "The sample photo itself is damaged. Choose a working photo.",
        ["תמונת הדוגמה היא התמונה הפגומה עצמה. בחרו תמונה תקינה אחרת מאותה מצלמה."] =
            "The sample photo is the damaged photo itself. Choose another working photo from the same camera.",
        ["התמונה גדולה מדי לבנייה מחדש."] =
            "The photo is too large to rebuild.",
        ["לא ניתן לקרוא את תחילת תמונת הדוגמה."] =
            "The beginning of the sample photo can't be read.",
        ["לא נמצאו בתמונה הפגומה הנתונים הדחוסים שלה — הם נדרסו יחד עם תחילת הקובץ."] =
            "The compressed data of the damaged photo wasn't found — it was overwritten along with the beginning of the file.",
        ["תמונת הדוגמה אינה מתאימה לתמונה הפגומה: הנתונים של התמונה אינם מתפענחים עם ההגדרות שלה. בחרו תמונה שצולמה באותה מצלמה ובאותן הגדרות (גודל התמונה ואיכותה)."] =
            "The sample photo doesn't match the damaged photo: the photo's data doesn't decode with its settings. Choose a photo taken with the same camera and the same settings (image size and quality).",
        ["פרטי הצילום (תאריך, מצלמה, כיוון) אבדו עם תחילת הקובץ. אם התמונה מוצגת שוכבת, סובבו אותה."] =
            "The shooting details (date, camera, orientation) were lost with the beginning of the file. If the photo appears sideways, rotate it.",
        ["תחילת התמונה נבנתה מחדש, וכל התמונה מתפענחת."] =
            "The beginning of the photo was rebuilt, and the whole photo decodes.",
        ["תחילת התמונה נבנתה מחדש, אבל רק כ-{0} ממנה מתפענח — משם והלאה הנתונים פגומים."] =
            "The beginning of the photo was rebuilt, but only about {0} of it decodes — from there on the data is damaged.",
        ["הכותרת נבנתה מחדש מהחלקים ששרדו בתמונה עצמה."] =
            "The header was rebuilt from the parts that survived in the photo itself.",
        ["מתמונת הדוגמה נלקחו: {0}. השאר נלקח מהתמונה עצמה."] =
            "Taken from the sample photo: {0}. The rest was taken from the photo itself.",
        ["טבלאות הדחיסה"] =
            "the compression tables",
        ["טבלאות הפענוח"] =
            "the decoding tables",
        ["מידות התמונה"] =
            "the image dimensions",
        ["מרווח ההתחלה מחדש"] =
            "the restart interval",
        ["כל הכותרת (טבלאות, מידות והגדרות)"] =
            "the whole header (tables, dimensions and settings)",
        ["בחרו קובץ RAW תקין שצולם באותה מצלמה ובאותן הגדרות"] =
            "Choose a working RAW file shot with the same camera and the same settings",
        ["קובצי {0}|*.{1}|כל הקבצים|*.*"] =
            "{0} files|*.{1}|All files|*.*",
        ["תחילת קובץ ה-RAW נהרסה: הרשימה שאומרת היכן נמצאים נתוני החיישן והתצוגה המקדימה אבדה, ולכן תוכנות עריכה לא פותחות אותו, אף שהנתונים עצמם בקובץ. אפשר לבנות את התחילה מחדש בעזרת קובץ RAW תקין אחד שצולם באותה מצלמה ובאותן הגדרות."] =
            "The beginning of the RAW file is destroyed: the list that says where the sensor data and the preview are is lost, so editing programs won't open it, although the data itself is in the file. The beginning can be rebuilt using one working RAW file shot with the same camera and the same settings.",
        ["קובץ הדוגמה גדול מדי."] =
            "The sample file is too large.",
        ["קובץ הדוגמה אינו קובץ RAW תקין. בחרו קובץ RAW תקין שצולם באותה מצלמה."] =
            "The sample file is not a working RAW file. Choose a working RAW file shot with the same camera.",
        ["קובץ הדוגמה הוא הקובץ הפגום עצמו. בחרו קובץ תקין אחר מאותה מצלמה."] =
            "The sample file is the damaged file itself. Choose another working file from the same camera.",
        ["הקובץ גדול מדי לבנייה מחדש."] =
            "The file is too large to rebuild.",
        ["מקובץ הדוגמה נלקחו {0} הבתים הראשונים: רשימות התגיות שנהרסו. מיקומי התצוגה המקדימה ונתוני החיישן ואורכיהם תוקנו לפי הקובץ עצמו, והשאר נשאר של הקובץ עצמו."] =
            "The first {0} bytes were taken from the sample file: the tag lists that were destroyed. The positions and lengths of the preview and the sensor data were corrected to match the file itself, and the rest remains the file's own.",
        ["פרטי הצילום (תאריך, חשיפה, איזון לבן) נלקחו מקובץ הדוגמה, כי אלה של הקובץ נדרסו. אם הצבעים נראים שונים, כוונו את איזון הלבן בתוכנת העריכה. אם הקובץ לא נפתח, קובץ הדוגמה צולם כנראה בהגדרת דחיסה אחרת (למשל בניקון: דחיסה ללא אובדן מול דחיסה רגילה) — נסו קובץ דוגמה אחר."] =
            "The shooting details (date, exposure, white balance) were taken from the sample file, because the file's own were overwritten. If the colors look different, adjust the white balance in your editing program. If the file doesn't open, the sample was probably shot with a different compression setting (for example on Nikon: lossless versus regular compression) — try another sample file.",
        ["תחילת הקובץ נבנתה מחדש, וכל החלקים שלו נמצאו במקומם."] =
            "The beginning of the file was rebuilt, and all its parts were found in place.",
        ["קובץ הדוגמה אינו מתאים לקובץ הפגום: המבנה שלו שונה, או שחלקים מהקובץ הפגום לא נמצאו. בחרו קובץ שצולם באותה מצלמה ובאותן הגדרות (סוג ה-RAW, גודלו ועומק הצבע). אם יש כמה, נסו קובץ אחר."] =
            "The sample file doesn't match the damaged file: its structure is different, or parts of the damaged file weren't found. Choose a file shot with the same camera and the same settings (RAW type, size and bit depth). If you have several, try another one.",
        ["בחרו מסד נתונים תקין של אותה אפליקציה"] =
            "Choose a working database of the same app",
        ["מסדי נתונים|*.db;*.sqlite;*.sqlite3|כל הקבצים|*.*"] =
            "Databases|*.db;*.sqlite;*.sqlite3|All files|*.*",
        ["הדף הראשון של מסד הנתונים נהרס: הכותרת ורשימת הטבלאות — מה שאומר איזה נתון שייך לאיזו טבלה — אבדו, ולכן המסד לא נפתח. שאר הדפים, עם השורות עצמן, נמצאים בקובץ. אפשר לשחזר את המסד בעזרת מסד תקין של אותה אפליקציה (למשל גיבוי ישן, או מסד ממכשיר אחר)."] =
            "The first page of the database is destroyed: the header and the table list — what says which data belongs to which table — are lost, so the database won't open. The other pages, with the rows themselves, are in the file. The database can be restored using a working database of the same app (for example an old backup, or a database from another device).",
        ["מסד הדוגמה עצמו פגום. בחרו מסד תקין."] =
            "The sample database itself is damaged. Choose a working database.",
        ["במסד הדוגמה אין טבלאות."] =
            "The sample database has no tables.",
        ["הקובץ שנבחר אינו מסד נתונים SQLite תקין."] =
            "The selected file is not a working SQLite database.",
        ["מסד הדוגמה הוא המסד הפגום עצמו. בחרו מסד תקין אחר מאותה אפליקציה."] =
            "The sample database is the damaged database itself. Choose another working database from the same app.",
        ["המסד גדול מדי לשחזור."] =
            "The database is too large to restore.",
        ["לא נמצאו במסד הפגום דפים של נתונים — גודל הדף שלו אינו ניתן לזיהוי."] =
            "No data pages were found in the damaged database — its page size can't be identified.",
        ["אף טבלה של מסד הדוגמה לא נמצאה במסד הפגום. בחרו מסד של אותה אפליקציה (ורצוי מאותה גרסה)."] =
            "None of the sample database's tables were found in the damaged database. Choose a database of the same app (preferably the same version).",
        ["רשימת הטבלאות ארוכה מדי כדי לבנות אותה מחדש."] =
            "The table list is too long to rebuild.",
        ["שוחזרו {0} טבלאות עם {1} שורות. הגדרות הטבלאות שלא שרדו נלקחו ממסד הדוגמה, והנתונים — מהמסד עצמו."] =
            "{0} tables with {1} rows were restored. Table definitions that didn't survive were taken from the sample database, and the data from the database itself.",
        ["{0} טבלאות לא נמצאו במסד הפגום ונוצרו ריקות: {1}."] =
            "{0} tables weren't found in the damaged database and were created empty: {1}.",
        ["{0} טבלאות נמצאו אבל לא ניתן היה לקרוא אותן (נתונים פגומים), והן ריקות: {1}."] =
            "{0} tables were found but couldn't be read (damaged data), and are empty: {1}.",
        ["{0} אינדקסים לא נבנו מחדש (הנתונים אינם מאפשרים אותם)."] =
            "{0} indexes weren't rebuilt (the data doesn't allow them).",
        ["המסד המשוחזר לא עבר את בדיקת השלמות של SQLite."] =
            "The restored database didn't pass SQLite's integrity check.",
        ["המסד שוחזר, ועבר את בדיקת השלמות של SQLite."] =
            "The database was restored and passed SQLite's integrity check.",
        ["בחרו הקלטה תקינה מאותו מכשיר ובאותן הגדרות"] =
            "Choose a working recording from the same device with the same settings",
        ["הקלטות WAV|*.wav|כל הקבצים|*.*"] =
            "WAV recordings|*.wav|All files|*.*",
        ["כותרת ההקלטה נדרסה: ההגדרות שאומרות איך לקרוא את השמע (ערוצים, קצב דגימה, עומק) אבדו, ולכן נגנים לא פותחים אותה. השמע עצמו נמצא בקובץ. אפשר לבנות את הכותרת מחדש בעזרת הקלטה תקינה אחת מאותו מכשיר ובאותן הגדרות."] =
            "The recording's header was overwritten: the settings that say how to read the audio (channels, sample rate, depth) are lost, so players won't open it. The audio itself is in the file. The header can be rebuilt using one working recording from the same device with the same settings.",
        ["הקובץ שנבחר אינו הקלטת WAV תקינה."] =
            "The selected file is not a working WAV recording.",
        ["הקלטת הדוגמה היא ההקלטה הפגומה עצמה. בחרו הקלטה תקינה אחרת מאותו מכשיר."] =
            "The sample recording is the damaged recording itself. Choose another working recording from the same device.",
        ["אין בהקלטה הפגומה שמע אחרי מקום הכותרת."] =
            "The damaged recording has no audio after the header position.",
        ["השמע בהקלטה הפגומה אינו נקרא נכון בפורמט של הדוגמה (ערוצים, קצב או עומק דגימה שונים). בחרו הקלטה מאותו מכשיר ובאותן הגדרות."] =
            "The audio in the damaged recording doesn't read correctly in the sample's format (different channels, rate or sample depth). Choose a recording from the same device with the same settings.",
        ["מהקלטת הדוגמה נלקחו הגדרות השמע: {0} ערוצים, {1} הרץ, {2} ביט. השמע עצמו — מההקלטה, ⁦{3}⁩."] =
            "The audio settings were taken from the sample recording: {0} channels, {1} Hz, {2} bit. The audio itself is from the recording, {3}.",
        ["הסימן של תחילת השמע נדרס, ולכן השמע נקרא מהמקום שבו הוא מתחיל בדוגמה. אם יש רעש קצר בהתחלה — זה מה שנשאר מהכותרת."] =
            "The marker of the audio start was overwritten, so the audio is read from where it starts in the sample. A short noise at the start is what remains of the header.",
        ["הכותרת נבנתה מחדש. אם ההקלטה מתנגנת מהר או לאט מדי, הדוגמה הוקלטה בקצב דגימה אחר — נסו הקלטה אחרת."] =
            "The header was rebuilt. If the recording plays too fast or too slow, the sample was recorded at a different sample rate — try another recording.",
        ["הסרטון קיבל אינדקס חדש ונבדק מחדש — הוא אמור להיפתח ולהתנגן."] =
            "The video got a new index and was checked again — it should open and play.",
        ["האינדקס נכתב, אך הבדיקה החוזרת מצאה בעיות: "] =
            "The index was written, but the recheck found problems: ",
        ["הארכיון פגום, ולא נמצא בו אף קובץ פנימי שלם שאפשר להציל."] =
            "The archive is damaged, and no complete inner file that can be saved was found in it.",
        ["תוכן העניינים שבסוף הקובץ חסר או פגום, ולכן התוכנה שיצרה את הקובץ לא תפתח אותו. {0} קבצים פנימיים שלמים נמצאו בגוף הקובץ ונבדקו בסכום ביקורת — אפשר לבנות מהם תוכן עניינים חדש."] =
            "The directory at the end of the file is missing or damaged, so the program that created the file will not open it. {0} complete inner files were found in the body of the file and verified by checksum — a new directory can be built from them.",
        ["{0} קבצים פנימיים פגומים ויושמטו מהעותק המתוקן: "] =
            "{0} inner files are damaged and will be left out of the repaired copy: ",
        [" ועוד."] =
            " and more.",
        ["החלק [Content_Types].xml של המסמך חסר או פגום. בלעדיו Office לא יפתח את הקובץ גם אחרי התיקון — אבל התוכן (למשל word/document.xml) יישאר נגיש בפתיחה כ-ZIP."] =
            "The document's [Content_Types].xml part is missing or damaged. Without it Office will not open the file even after the repair — but the content (for example word/document.xml) stays accessible when opened as a ZIP.",
        ["הקובץ תקין ואינו זקוק לתיקון."] =
            "The file is fine and does not need repair.",
        ["הבעיות שנמצאו אינן ניתנות לתיקון מכני: "] =
            "The problems found cannot be repaired automatically: ",
        ["שוחזרה תחילת הקובץ (חתימת הפתיחה של {0})"] =
            "The start of the file was restored (the opening signature of {0})",
        ["הוסרו נתונים עודפים — הקובץ קוצר ל-{0} בתים"] =
            "Extra data was removed — the file was shortened to {0} bytes",
        ["הושלם סוף הקובץ (חתימת הסיום של {0})"] =
            "The end of the file was completed (the end signature of {0})",
        ["הסיומת תוקנה ל-.{0}"] =
            "The extension was corrected to .{0}",
        ["תוקנו שדות הגודל בכותרת לפי התוכן שבקובץ"] =
            "The size fields in the header were corrected to match the file's content",
        ["הוסרו {0} בתים זרים מתחילת הקובץ"] =
            "{0} foreign bytes were removed from the start of the file",
        ["{0} (תמונה מוקטנת)"] =
            "{0} (thumbnail)",
        ["נשמרה התמונה המוקטנת שבתוך הקובץ ({0}×{1}) כקובץ נפרד"] =
            "The thumbnail inside the file ({0}×{1}) was saved as a separate file",
        ["התמונה עצמה פגומה, ואת החלק החסר בה אי אפשר להשלים. התמונה המוקטנת שבתוכה ({0}×{1}) נשמרה כקובץ נפרד ונבדקה — היא שלמה."] =
            "The image itself is damaged, and its missing part cannot be completed. The thumbnail inside it ({0}×{1}) was saved as a separate file and checked — it is complete.",
        ["{0} (טקסט)"] =
            "{0} (text)",
        ["נשמר הטקסט של המסמך (כ-{0} מילים) בקובץ טקסט: {1}"] =
            "The document's text (about {0} words) was saved in a text file: {1}",
        ["המסמך עצמו פגום, ואי אפשר לתקן אותו כך שייפתח. הטקסט שבו נשמר כקובץ טקסט פשוט — אפשר לפתוח אותו בפנקס הרשימות או להעתיק ממנו לוורד."] =
            "The document itself is damaged and cannot be repaired so that it opens. Its text was saved as a plain text file — you can open it in Notepad or copy from it into Word.",
        ["הקובץ תוקן ונבדק מחדש — לא נמצאו בו בעיות."] =
            "The file was repaired and checked again — no problems were found.",
        ["כל מה שניתן לתקן תוקן. נותרו בעיות שאינן ניתנות לתיקון מכני: "] =
            "Everything that could be repaired was repaired. Problems that cannot be repaired automatically remain: ",
        ["התיקון נכתב, אך הבדיקה החוזרת עדיין מוצאת בעיות הניתנות לתיקון."] =
            "The repair was written, but the recheck still finds repairable problems.",
        ["הקובץ המקורי התקצר בזמן התיקון."] =
            "The original file got shorter during the repair.",
        ["נבנתה טבלת מיקומים חדשה ל-{0} חלקי המסמך"] =
            "A new location table was built for the document's {0} parts",
        ["נבנה תוכן עניינים חדש מ-{0} קבצים פנימיים שלמים"] =
            "A new directory was built from {0} complete inner files",
        ["; {0} קבצים פגומים הושמטו"] =
            "; {0} damaged files were left out",
        ["{0} שניות"] =
            "{0} seconds",
        ["גם בסרטון הזה אין אינדקס, ולכן אי אפשר ללמוד ממנו. בחרו סרטון שנפתח ומתנגן כרגיל."] =
            "This video has no index either, so nothing can be learned from it. Choose a video that opens and plays normally.",
        ["בסרטון הזה אין תמונה בקידוד H.264 או H.265 — הקידודים שטלפונים ומצלמות משתמשים בהם, ושאפשר לבנות להם אינדקס."] =
            "This video has no H.264 or H.265 frames — the encodings phones and cameras use, for which an index can be built.",
        ["לסרטון הזה לא חסר אינדקס — אין מה לבנות מחדש."] =
            "This video is not missing its index — there is nothing to rebuild.",
        ["בסרטון הייחוס לא נמצא אינדקס. בחרו סרטון שנפתח ומתנגן כרגיל."] =
            "No index was found in the reference video. Choose a video that opens and plays normally.",
        ["בסרטון הייחוס אין תמונה בקידוד H.264 או H.265 — אלה הקידודים שאפשר לבנות להם אינדקס."] =
            "The reference video has no H.264 or H.265 frames — those are the encodings an index can be built for.",
        ["לא נמצאו בסרטון תמונות שמתאימות להגדרות של סרטון הייחוס. ודאו שסרטון הייחוס צולם באותו מכשיר ובאותן הגדרות (רזולוציה, קצב תמונות)."] =
            "No frames matching the reference video's settings were found in the video. Make sure the reference video was shot on the same device with the same settings (resolution, frame rate).",
        ["נבנה אינדקס חדש: {0} תמונות"] =
            "A new index was built: {0} frames",
        [" וקול"] =
            " and sound",
        [" (הקול לא נמצא)"] =
            " (the sound was not found)",
        [" חלק קטן ({0}) לא זוהה — בדרך כלל התמונה האחרונה, שנקטעה באמצע ההקלטה — והוא נשאר מחוץ לסרטון."] =
            " A small part ({0}) was not recognized — usually the last frame, cut off mid-recording — and it was left out of the video.",
        [" {0} לא זוהו כתמונה או כקול — אזורים פגומים, או נתונים שהמכשיר שומר בנוסף — ונשארו מחוץ לסרטון."] =
            " {0} were not recognized as frames or sound — damaged areas, or extra data the device stores — and were left out of the video.",
        [" דקות"] =
            " minutes",
        ["הסרטון המקורי התקצר בזמן הבנייה."] =
            "The original video got shorter during the rebuild.",
        ["לא ניתן לפתוח את הדיסק לקריאה. ודאו שהתוכנה פועלת בהרשאות מנהל."] =
            "Cannot open the disk for reading. Make sure the program is running as administrator.",
        ["תחילת המחיצה (מגזר האתחול) תקינה ומזהה מערכת קבצים {0}. אין צורך בתיקון."] =
            "The beginning of the partition (boot sector) is fine and identifies a {0} file system. No repair is needed.",
        ["תחילת המחיצה (מגזר האתחול) פגומה, אך נמצא עותק גיבוי תקין של {0} {1}. העותק נבדק והתפענח בהצלחה."] =
            "The beginning of the partition (boot sector) is damaged, but a valid {0} backup copy was found {1}. The copy was checked and decoded successfully.",
        ["התיקון יעתיק {0} בתים מעותק הגיבוי אל תחילת המחיצה. זו כתיבה לדיסק המקור. התוכנה תשמור תחילה עותק של מה שיוחלף, כדי שניתן יהיה לבטל את הפעולה."] =
            "The repair will copy {0} bytes from the backup copy to the beginning of the partition. This writes to the source disk. The program will first save a copy of what is replaced, so the operation can be undone.",
        ["תחילת המחיצה (מגזר האתחול) פגומה, ולא נמצא עותק גיבוי תקין במקומות שבהם הוא נשמר. לא ניתן לתקן את המחיצה, אך עדיין אפשר לשחזר ממנה קבצים בסריקה מתקדמת, שאינה תלויה במערכת הקבצים."] =
            "The beginning of the partition (boot sector) is damaged, and no valid backup copy was found where it is kept. The partition cannot be repaired, but you can still recover files from it with an advanced scan, which does not depend on the file system.",
        ["בסוף המחיצה, במקום שבו NTFS שומר את הגיבוי"] =
            "at the end of the partition, where NTFS keeps the backup",
        ["סמוך לסוף המחיצה, במקום שבו NTFS שומר את הגיבוי"] =
            "near the end of the partition, where NTFS keeps the backup",
        ["בתחילת המחיצה, במקום שבו FAT32 שומר את הגיבוי"] =
            "at the beginning of the partition, where FAT32 keeps the backup",
        ["בתחילת המחיצה, באזור הגיבוי של exFAT"] =
            "at the beginning of the partition, in the exFAT backup area",
        ["האבחון לא מצא עותק גיבוי תקין, ולכן אין מה לתקן."] =
            "The diagnosis found no valid backup copy, so there is nothing to repair.",
        ["תיקיית הגיבוי חייבת להיות על כונן אחר מהדיסק שמתוקן."] =
            "The backup folder must be on a drive other than the disk being repaired.",
        ["לא ניתן לפתוח את הדיסק לקריאה."] =
            "Cannot open the disk for reading.",
        ["לא ניתן לקרוא את עותק הגיבוי במלואו."] =
            "Cannot read the whole backup copy.",
        ["לא ניתן לקרוא את תחילת המחיצה כדי לגבות אותה לפני הכתיבה, ולכן לא נכתב דבר."] =
            "Cannot read the beginning of the partition to back it up before writing, so nothing was written.",
        ["לא ניתן ליצור קובץ גיבוי, ולכן התיקון לא בוצע: {0}"] =
            "Cannot create a backup file, so the repair was not done: {0}",
        ["לא ניתן לפתוח את הדיסק לכתיבה. ודאו שהתוכנה פועלת בהרשאות מנהל ושהמחיצה אינה בשימוש."] =
            "Cannot open the disk for writing. Make sure the program is running as administrator and the partition is not in use.",
        ["הכתיבה לדיסק נכשלה (שגיאת Windows {0}). {1} המחיצה לא שונתה."] =
            "Writing to the disk failed (Windows error {0}). {1} The partition was not changed.",
        ["המחיצה תוקנה. מערכת הקבצים {0} נקראת כעת בהצלחה. ייתכן שיהיה צורך לנתק ולחבר מחדש את הכונן, או להפעיל מחדש את המחשב, כדי ש-Windows יזהה את השינוי. גיבוי המצב הקודם נשמר ב: {1}"] =
            "The partition was repaired. The {0} file system now reads successfully. You may need to disconnect and reconnect the drive, or restart the computer, for Windows to notice the change. A backup of the previous state was saved at: {1}",
        ["התיקון נכתב אך המחיצה עדיין אינה נקראת, ולכן המצב הקודם הוחזר אוטומטית. הדיסק נותר כפי שהיה. נסו לשחזר קבצים בסריקה מתקדמת."] =
            "The repair was written but the partition still cannot be read, so the previous state was restored automatically. The disk is as it was. Try recovering files with an advanced scan.",
        ["התיקון נכתב, המחיצה עדיין אינה נקראת, וגם החזרת המצב הקודם נכשלה. קובץ הגיבוי שמור ב: {0}"] =
            "The repair was written, the partition still cannot be read, and restoring the previous state also failed. The backup file is saved at: {0}",
        ["סוף הקובץ חסר — ככל הנראה הקובץ נקטע, ואיתו טבלת המיקומים של המסמך."] =
            "The end of the file is missing — the file was probably cut off, and with it the document's location table.",
        ["ההפניה לטבלת המיקומים של המסמך שבורה."] =
            "The reference to the document's location table is broken.",
        ["טבלת המיקומים של המסמך אינה תואמת את תוכנו — היא מצביעה למקומות שבהם אין את החלקים."] =
            "The document's location table does not match its content — it points to places where the parts are not.",
        ["ההפניה לטבלת המיקומים של המסמך מצביעה למקום שאין בו טבלה."] =
            "The reference to the document's location table points to a place with no table.",
        ["אין במסמך מספיק מבנה כדי לבנות אותו מחדש."] =
            "The document does not have enough structure to rebuild it.",
        ["כותרת מסד הנתונים פגומה, וגם הדף הראשון שלו אינו במבנה הצפוי. לא ניתן לשחזר את הכותרת בוודאות."] =
            "The database header is damaged, and its first page is not in the expected structure either. The header cannot be restored with certainty.",
        ["כותרת מסד הנתונים פגומה: גודל הדף — הנתון שקובע איך המסד נקרא — אבד, ואי אפשר לגלות אותו מתוך המסד. לא ניתן לתקן בוודאות."] =
            "The database header is damaged: the page size — the value that determines how the database is read — is lost, and cannot be worked out from the database. It cannot be repaired with certainty.",
        ["גודל הדף — הנתון שקובע איך המסד נקרא — אבד. לפי מבנה המסד עצמו הוא {0} בתים"] =
            "the page size — the value that determines how the database is read — was lost. By the database's own structure it is {0} bytes",
        ["כמה שדות קבועים בכותרת נפגעו"] =
            "some fixed fields in the header were damaged",
        ["כותרת מסד הנתונים פגומה: {0}. אפשר לשחזר את הכותרת, והנתונים עצמם לא ישתנו."] =
            "The database header is damaged: {0}. The header can be restored, and the data itself will not change.",
        ["— שקופית {0} —\n"] =
            "— Slide {0} —\n",
        ["זה לא קובץ ביטול של התוכנה, או שהקובץ נפגם. לא נכתב דבר."] =
            "This is not an undo file of the program, or the file is damaged. Nothing was written.",
        ["זה קובץ ביטול מגרסה קודמת של התוכנה. אין בו את הזהות של הכונן ואת מה שנכתב אליו, ולכן אי אפשר לוודא שהביטול ייכתב לכונן הנכון. לא נכתב דבר."] =
            "This is an undo file from an earlier version of the program. It does not hold the drive's identity and what was written to it, so it cannot be verified that the undo will be written to the right drive. Nothing was written.",
        ["הכונן שהתיקון נעשה בו לא מחובר עכשיו. חברו אותו, לחצו על רענון ונסו שוב."] =
            "The drive the repair was made on is not connected now. Connect it, click refresh and try again.",
        ["יותר מכונן אחד מתאים לקובץ הזה, ואין דרך לדעת בוודאות לאיזה מהם הוא שייך. נתקו את הכוננים האחרים ונסו שוב."] =
            "More than one drive matches this file, and there is no way to know for sure which one it belongs to. Disconnect the other drives and try again.",
        ["לא ניתן לקרוא מהכונן את האזור שהתיקון שינה, ולכן לא נכתב דבר."] =
            "Cannot read the area the repair changed from the drive, so nothing was written.",
        ["הכונן נמצא בדיוק במצב שהתיקון השאיר, ואפשר להחזיר אותו למצב שלפניו."] =
            "The drive is exactly in the state the repair left it, and it can be returned to the state before it.",
        ["הכונן כבר במצב שלפני התיקון — אין מה לבטל."] =
            "The drive is already in its state from before the repair — there is nothing to undo.",
        ["הכונן השתנה מאז התיקון (למשל פורמט, או תיקון נוסף). ביטול עכשיו היה דורס את מה שנכתב אחר כך, ולכן לא נכתב דבר."] =
            "The drive has changed since the repair (for example a format, or another repair). Undoing now would overwrite what was written afterwards, so nothing was written.",
        ["טבלת המחיצות הוחזרה למצב שלפני ההחזרה. "] =
            "The partition table was returned to its state before the restore. ",
        ["תחילת המחיצה הוחזרה למצב שלפני התיקון. "] =
            "The beginning of the partition was returned to its state before the repair. ",
        ["ייתכן שיהיה צורך לנתק ולחבר מחדש את הכונן כדי ש-Windows יזהה את השינוי."] =
            "You may need to disconnect and reconnect the drive for Windows to notice the change.",
        ["הכתיבה לכונן נכשלה (שגיאת Windows {0}). {1} הכונן נשאר כמו שהתיקון השאיר אותו."] =
            "Writing to the drive failed (Windows error {0}). {1} The drive remains as the repair left it.",
        ["הכתיבה לכונן נכשלה באמצע, וגם החזרת המצב נכשלה. אל תכתבו לכונן — קובץ הביטול עדיין שמור, ואפשר לנסות שוב אחרי ניתוק וחיבור של הכונן."] =
            "Writing to the drive failed midway, and restoring the state also failed. Do not write to the drive — the undo file is still saved, and you can try again after disconnecting and reconnecting the drive.",
        ["לא ניתן לקרוא את עותק הגיבוי של תחילת המחיצה (מגזר האתחול)."] =
            "Cannot read the backup copy of the beginning of the partition (boot sector).",
        ["ארכיון Zip64 (קבצים מעל 4GB) — מחוץ לתחום התיקון."] =
            "A Zip64 archive (files over 4GB) — outside the scope of the repair.",
        ["סוף הקובץ הפנימי לא נמצא — ככל הנראה נקטע."] =
            "The end of the inner file was not found — it was probably cut off.",
        ["הקובץ הפנימי נקטע באמצע."] =
            "The inner file is cut off midway.",
        ["הנתונים הדחוסים אינם באורך הצפוי."] =
            "The compressed data is not the expected length.",
        ["הנתונים הדחוסים פגומים."] =
            "The compressed data is damaged.",
        ["הקובץ הפנימי אינו באורך הצפוי."] =
            "The inner file is not the expected length.",
        ["סכום הביקורת (CRC) אינו תואם — התוכן השתנה."] =
            "The checksum (CRC) does not match — the content has changed.",
        ["צריך לפחות שני קבצים מאותו סוג — ועדיף שלושה ומעלה — כדי לדעת מה משותף לכולם."] =
            "At least two files of the same type are needed — preferably three or more — to know what they all have in common.",
        ["הקובץ \"{0}\" קטן מדי כדי ללמוד ממנו."] =
            "The file \"{0}\" is too small to learn from.",
        ["לא ניתן לקרוא את \"{0}\": {1}"] =
            "Cannot read \"{0}\": {1}",
        ["התוכנה כבר מכירה את הקבצים האלה — הם בנויים כמו {0}, והסריקה המתקדמת כבר מוצאת אותם. אין צורך להוסיף סוג חדש."] =
            "The program already knows these files — they are built like {0}, and the advanced scan already finds them. There is no need to add a new type.",
        ["לקבצים לדוגמה אין סיומת. בחרו קבצים עם הסיומת שהם אמורים לקבל בשחזור."] =
            "The sample files have no extension. Choose files with the extension they should get when recovered.",
        ["לא נמצא מספיק משותף בתחילת הקבצים: "] =
            "Not enough in common was found at the start of the files: ",
        ["כל קובץ מתחיל אחרת. "] =
            "each file starts differently. ",
        ["רק {0} בתים זהים בכולם. "] =
            "only {0} bytes are identical in all of them. ",
        ["ייתכן שהם לא באמת מאותו סוג, או שלסוג הזה אין תחילה קבועה — ואז אי אפשר לחפש אותו כך."] =
            "They may not really be of the same type, or this type has no fixed start — and then it cannot be searched for this way.",
        ["קובץ {0}"] =
            "{0} file",
        ["בתחילת כל {0} הקבצים יש {1} בתים זהים — לפיהם הסריקה המתקדמת תמצא קבצים מהסוג הזה."] =
            "All {0} files start with {1} identical bytes — the advanced scan will find files of this type by them.",
        [" גם סוף הקבצים זהה, ולכן גם האורך של כל קובץ שיימצא ייקבע במדויק."] =
            " The ends of the files are identical too, so the length of every file found will also be exact.",
        [" לסוג הזה אין סוף קבוע, ולכן כל קובץ שיימצא ישוחזר באורך של עד {0} — ייתכן שבסופו יהיו נתונים מיותרים. בדרך כלל התוכנה שפותחת אותו מתעלמת מהם."] =
            " This type has no fixed end, so every file found will be recovered at a length of up to {0} — there may be extra data at its end. Usually the program that opens it ignores it.",
        [" עם שלושה קבצים ומעלה הזיהוי מדויק יותר."] =
            " With three or more files the identification is more accurate.",
        // ------------------------------------------------ שמות סוגי קבצים — משולבים בתוך משפטים
        ["דיסק קשיח מגנטי"] = "Magnetic hard disk",
        ["כונן SSD"] = "SSD drive",
        ["כונן NVMe SSD"] = "NVMe SSD drive",
        ["התקן USB נייד"] = "Portable USB device",
        ["כרטיס זיכרון"] = "Memory card",
        ["כונן אופטי"] = "Optical drive",
        ["דיסק וירטואלי"] = "Virtual disk",
        ["כונן רשת או זיכרון"] = "Network or RAM drive",
        ["תמונת דיסק"] = "Disk image",
        ["סוג לא ידוע"] = "Unknown type",
        ["לא מזוהה"] = "Unrecognized",
        ["ללא טבלת מחיצות"] = "No partition table",
        ["לא נקרא"] = "Not read",
        ["TRIM לא ידוע"] = "TRIM unknown",
        ["קובץ תמונה"] = "Image file",
        ["דרך Windows"] = "Through Windows",
        ["נתונים בסיסיים"] = "Basic data",
        ["מחיצת מערכת EFI"] = "EFI system partition",
        ["שחזור Windows"] = "Windows recovery",
        ["נתוני Linux"] = "Linux data",
        ["מחיצה מוסתרת"] = "Hidden partition",
        ["וירטואלי"] = "Virtual",
        ["פגום חלקית"] = "Partly damaged",
        ["טבלת הקבצים"] = "File table",
        ["שריד בטבלת הקבצים"] = "File table remnant",
        ["יומן שינויים"] = "Change journal",
        ["יומן מערכת הקבצים"] = "File system log",
        ["זיהוי לפי תוכן"] = "Found by content",
        ["ללא תאריך"] = "No date",
        ["ארכיון 7-Zip"] = "7-Zip archive",
        ["ארכיון GZIP"] = "GZIP archive",
        ["ארכיון RAR"] = "RAR archive",
        ["הקלטת קול AMR"] = "AMR voice recording",
        ["וידאו 3GP"] = "3GP video",
        ["וידאו AVI"] = "AVI video",
        ["וידאו MP4"] = "MP4 video",
        ["וידאו MPEG / DVD"] = "MPEG / DVD video",
        ["וידאו MPEG-TS"] = "MPEG-TS video",
        ["וידאו Matroska"] = "Matroska video",
        ["וידאו QuickTime"] = "QuickTime video",
        ["וידאו Windows Media"] = "Windows Media video",
        ["וידאו ממצלמת וידאו (AVCHD)"] = "Camcorder video (AVCHD)",
        ["מסד נתונים SQLite"] = "SQLite database",
        ["מסמך Office או ארכיון ZIP"] = "Office document or ZIP archive",
        ["מסמך Office ישן"] = "Old Office document",
        ["מסמך PDF"] = "PDF document",
        ["מסמך RTF"] = "RTF document",
        ["סמל ICO"] = "ICO icon",
        ["קובץ הרצה של Windows"] = "Windows executable",
        ["שמע FLAC"] = "FLAC audio",
        ["שמע MP3"] = "MP3 audio",
        ["שמע OGG"] = "OGG audio",
        ["שמע WAV"] = "WAV audio",
        ["תמונת AVIF"] = "AVIF image",
        ["תמונת BMP"] = "BMP image",
        ["תמונת GIF"] = "GIF image",
        ["תמונת HEIC"] = "HEIC image",
        ["תמונת HEIF"] = "HEIF image",
        ["תמונת JPEG"] = "JPEG image",
        ["תמונת PNG"] = "PNG image",
        ["תמונת Photoshop"] = "Photoshop image",
        ["תמונת RAW של Canon"] = "Canon RAW image",
        ["תמונת RAW של Olympus"] = "Olympus RAW image",
        ["תמונת RAW של Panasonic"] = "Panasonic RAW image",
        ["תמונת TIFF"] = "TIFF image",
        ["תמונת WebP"] = "WebP image",
        ["שמע M4A"] = "M4A audio",
        ["שמע Windows Media"] = "Windows Media audio",
        ["וידאו OGG"] = "OGG video",
        ["מסמך Word"] = "Word document",
        ["גיליון Excel"] = "Excel spreadsheet",
        ["מצגת PowerPoint"] = "PowerPoint presentation",
        ["שרטוט Visio"] = "Visio drawing",
        ["מסמך OpenDocument"] = "OpenDocument document",
        ["גיליון OpenDocument"] = "OpenDocument spreadsheet",
        ["מצגת OpenDocument"] = "OpenDocument presentation",
        ["ספר אלקטרוני"] = "E-book",
        ["אפליקציית אנדרואיד"] = "Android app",
        ["ארכיון Java"] = "Java archive",
        ["מסמך Word ישן"] = "Old Word document",
        ["גיליון Excel ישן"] = "Old Excel spreadsheet",
        ["מצגת PowerPoint ישנה"] = "Old PowerPoint presentation",
        ["הודעת Outlook"] = "Outlook message",

        // מערכי RAID
        ["{0} · מחיצה {1}"] = "{0} · partition {1}",
        ["{0} — מערך מורכב"] = "{0} — assembled array",
        ["אחד הכוננים נפל מהמערך לפני האחרים, והנתונים בו אינם עדכניים — קבצים שנכתבו אחרי שנפל עלולים לחזור פגומים."] =
            "One of the drives dropped out of the array before the others and its data is out of date — files written after it dropped out may come back damaged.",
        ["אחד מכונני המערך"] = "one of the array's drives",
        ["בתוך המערך יש מאגר לוגי — כמו ברוב שרתי האחסון הביתיים. לחצו עליו כדי לפתוח את האזורים שבו."] =
            "The array contains a logical volume pool — like most home storage servers. Click it to open its volumes.",
        ["המאגר לא נמצא. חפשו שוב."] = "The pool wasn't found. Search again.",
        ["חלק מהאזור יושב על כונן שלא נמצא. חברו את כל הכוננים של השרת ולחצו חיפוש שוב."] =
            "Part of the volume sits on a drive that wasn't found. Connect all the server's drives and click Search again.",
        ["האזור לא נמצא. חפשו שוב."] = "The volume wasn't found. Search again.",
        ["אחד מכונני המאגר"] = "one of the pool's drives",
        ["אזור \"{0}\" במאגר הלוגי \"{1}\" של לינוקס, שהתוכנה פתחה מ-{2} כוננים. הקריאה בלבד — שום דבר לא נכתב לכוננים."] =
            "Volume \"{0}\" in the Linux logical volume pool \"{1}\", opened by the program from {2} drives. Read-only — nothing is written to the drives.",
        ["האזור \"דליל\" (thin) — המיקום של כל קטע בו רשום במבנה נפרד, שעוד לא נקרא בגרסה זו. סריקה מתקדמת של המאגר תמצא את הקבצים לפי סוג."] =
            "The volume is \"thin\" — the location of each piece is recorded in a separate structure this version can't read yet. An Advanced scan of the pool will find the files by type.",
        ["סוג האזור ({0}) עוד לא נתמך. סריקה מתקדמת תמצא את הקבצים לפי סוג."] =
            "The volume type ({0}) isn't supported yet. An Advanced scan will find the files by type.",
        ["תיאור האזור פגום."] = "The volume description is damaged.",

        ["אחד הכוננים שנבחרו כבר לא ברשימה. רעננו ובחרו שוב."] = "One of the selected drives is no longer in the list. Refresh and select again.",
        ["המבנה לא נמצא. זהו שוב."] = "The layout wasn't found. Detect again.",
        ["מערך {0} בלי כותרת (של כרטיס RAID), שהתוכנה זיהתה והרכיבה מ-{1} כוננים: רצועה של {2}KB. הקריאה בלבד — שום דבר לא נכתב לכוננים."] =
            "{0} array without a header (from a RAID card), detected and assembled by the program from {1} drives: {2}KB stripe. Read-only — nothing is written to the drives.",

        // מק
        ["המחיצה אינה מכולת APFS תקינה, או שהכותרת שלה פגומה."] = "The partition isn't a valid APFS container, or its header is damaged.",
        ["הכרך \"{0}\" מוצפן (FileVault, או מק עם שבב אבטחה של Apple). בלי המפתח של המחשב הזה אין דרך לקרוא אותו."] =
            "The volume \"{0}\" is encrypted (FileVault, or a Mac with an Apple security chip). Without that computer's key there's no way to read it.",
        ["קורא את הכרך {0}"] = "Reading the volume {0}",
        ["קבצים שנמחקו מ-APFS נמצאים רק בסריקה עמוקה: היא עוברת על כל הכונן ומחפשת עותקים ישנים של הרשומות."] =
            "Deleted files on APFS are found only by a Deep scan: it goes over the whole drive looking for old copies of the records.",
        ["הקובץ דחוס בדחיסה של macOS (בדרך כלל קובצי מערכת ותוכנות), שעוד לא נתמכת."] =
            "The file uses macOS compression (usually system files and apps), which isn't supported yet.",
        ["נמצאו נתונים, והמקום שהקובץ תפס לא משמש קובץ קיים. {0}"] = "Data was found, and the space the file occupied isn't used by an existing file. {0}",
        ["המחיצה אינה HFS+ תקינה, או שהכותרת שלה פגומה."] = "The partition isn't a valid HFS+, or its header is damaged.",
        ["הקטלוג של המחיצה אינו נקרא."] = "The partition's catalog can't be read.",
        ["קורא את הקטלוג"] = "Reading the catalog",
        ["מחפש קבצים שנמחקו בקטלוג ובעותקים הישנים של הרשומות"] = "Looking for deleted files in the catalog and in old copies of the records",
        ["{0} קבצים שנמחקו נמצאו ברשומות ישנות שנשארו בקטלוג וביומן של מערכת הקבצים."] =
            "{0} deleted files were found in old records left in the file system's catalog and journal.",

        // btrfs
        ["מיקום התוכן פגום."] = "The content location is damaged.",
        ["לא ניתן היה לקרוא את התוכן מהכונן."] = "The content couldn't be read from the drive.",
        ["הקובץ דחוס בשיטת zstd, שעוד לא נתמכת. סריקה מתקדמת לא תעזור כאן — התוכן בדיסק דחוס."] =
            "The file is compressed with zstd, which isn't supported yet. An Advanced scan won't help here — the content on the drive is compressed.",
        ["התוכן הדחוס פגום — לא ניתן לפרוס אותו."] = "The compressed content is damaged — it can't be decompressed.",
        ["המחיצה אינה btrfs תקינה, או שהכותרת שלה פגומה."] = "The partition isn't a valid btrfs, or its header is damaged.",
        ["מערכת הקבצים מפוזרת על כמה כוננים בעצמה (בלי מערך מתחת). זה עוד לא נתמך — חלק מהקבצים עלולים לחזור פגומים."] =
            "The file system spreads itself across several drives (without an array underneath). This isn't supported yet — some files may come back damaged.",
        ["קורא את עץ השורשים"] = "Reading the root tree",
        ["עץ הקבצים הראשי של המחיצה אינו נקרא."] = "The partition's main file tree can't be read.",
        ["{0} קבצים דחוסים לא נפרסו — הסיבה ליד כל אחד מהם."] = "{0} compressed files weren't decompressed — the reason is next to each.",
        ["קורא את התיקיות"] = "Reading the folders",
        ["מחפש קבצים שנמחקו בעותקים הישנים של הרשומות"] = "Looking for deleted files in old copies of the records",
        ["הגודל המקורי לא נשמר, והקובץ משוחזר עד סוף הבלוק האחרון שלו, בלי האפסים שבסופו."] =
            "The original size wasn't kept, so the file is recovered up to the end of its last block, without the trailing zeros.",
        ["{0} קבצים שנמחקו נמצאו בעותקים הישנים של הרשומות, שמערכת הקבצים משאירה בדיסק אחרי כל שינוי."] =
            "{0} deleted files were found in old copies of the records, which the file system leaves on the drive after every change.",
        ["אצל {0} מהם המקום שהתוכן תפס כבר נתפס מחדש, ולכן הם עלולים לחזור פגומים."] =
            "For {0} of them the space the content occupied has been taken again, so they may come back damaged.",
        ["הרשומה, השם והמיקום נלקחו מעותק ישן שנשאר בדיסק."] = "The record, name and location were taken from an old copy left on the drive.",
        ["הקובץ דחוס וגדול מדי לפריסה בזיכרון בגרסה זו."] = "The file is compressed and too large to decompress in memory in this version.",
        ["גודל הרצועה של המערך אינו ידוע."] = "The array's stripe size is unknown.",
        ["המערך היה באמצע שינוי מבנה (הוספת כונן או שינוי סוג) כשנעצר. מערך כזה עוד לא נתמך."] =
            "The array was in the middle of a reshape (adding a drive or changing type) when it stopped. Such an array isn't supported yet.",
        ["הסידור של המערך ({0}) אינו נתמך. נתמכים ארבעת הסידורים הרגילים וזוגיות בכונן הראשון או האחרון."] =
            "The array's layout ({0}) isn't supported. The four standard layouts and parity on the first or last drive are supported.",
        ["חסר כונן אחד — התוכן שלו מחושב מהזוגיות שבכוננים האחרים. הקריאה איטית יותר, וכל פגם נוסף באחד הכוננים יפגע בקבצים."] =
            "One drive is missing — its content is computed from the parity on the other drives. Reading is slower, and any further fault in one of the drives will damage files.",
        ["חסר כונן במערך. במערך מסוג זה כל כונן מחזיק חלק מהנתונים ואין עותק — בלי כל הכוננים הקבצים יחזרו חלקיים. סריקה מתקדמת של הכוננים שנמצאו עדיין יכולה למצוא קבצים קטנים."] =
            "A drive is missing from the array. In this kind of array each drive holds part of the data with no copy — without all the drives files will come back partial. An Advanced scan of the drives that were found can still find small files.",
        ["חסרים {0} כוננים במערך — יותר ממה שהוא יכול לאבד."] = "{0} drives are missing from the array — more than it can lose.",
        ["חסרים {0} כוננים — הנתונים נקראים מהעותקים שבכוננים האחרים."] = "{0} drives are missing — the data is read from the copies on the other drives.",
        ["חסרים שני כוננים במערך RAID 6. שחזור שני כוננים חסרים עוד לא נתמך — חברו לפחות אחד מהם."] =
            "Two drives are missing from the RAID 6 array. Rebuilding two missing drives isn't supported yet — connect at least one of them.",
        ["מספר העותקים במערך אינו תקין."] = "The array's copy count is invalid.",
        ["מערך RAID 10 בסידור \"רחוק\" או \"היסט\" אינו נתמך עדיין — רק הסידור הרגיל (\"קרוב\")."] =
            "A RAID 10 array in the \"far\" or \"offset\" layout isn't supported yet — only the standard (\"near\") layout.",
        ["מערך {0} של לינוקס, שהתוכנה הרכיבה מ-{1} כוננים. הקריאה בלבד — שום דבר לא נכתב לכוננים."] =
            "Linux {0} array, assembled by the program from {1} drives. Read-only — nothing is written to the drives.",
        ["סוג המערך (RAID {0}) אינו נתמך."] = "The array type (RAID {0}) isn't supported.",
        ["שרשור (JBOD)"] = "Concatenation (JBOD)",
        ["המערך לא נמצא. חפשו שוב."] = "The array wasn't found. Search again.",
        ["מערך RAID שהורכב בתוכנה הוא לקריאה בלבד — התוכנה אינה כותבת אליו."] =
            "A RAID array assembled by the program is read-only — the program doesn't write to it.",
    };
}
