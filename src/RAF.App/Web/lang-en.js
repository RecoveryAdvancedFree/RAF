/* ==========================================================================
   המילון לאנגלית. המפתח הוא הטקסט העברי כפי שהוא בקוד, והערך — התרגום.
   {0}, {1}… — ערכים שמשתלבים בטקסט (מספרים, שמות), באותו מקום בשתי השפות.
   מסודר לפי הקבצים שבהם הטקסט מופיע.
   ========================================================================== */

'use strict';

const EN = {};

/* ------------------------------------------------------------ כללי */
Object.assign(EN, {
  'שחזור מתקדם חינם': 'Recovery Advanced Free',
  'סורק את אמצעי האחסון במערכת…': 'Scanning the storage devices…',
  'מאתחל…': 'Starting…',
  'שאלות נפוצות': 'Help',
  'שאלות נפוצות (F1)': 'Help (F1)',
  'הסתרה לאזור ההודעות': 'Hide to the notification area',
  'הסתרה לאזור ההודעות, ליד השעון — הסמל שם מראה את ההתקדמות':
    'Hide to the notification area, next to the clock — its icon shows the progress',
  'מזעור': 'Minimize',
  'הגדלה': 'Maximize',
  'סגירה': 'Close',
  'שלבי השחזור': 'Recovery steps',

  'שגיאה לא ידועה': 'Unknown error',
  'הפעולה לא הסתיימה בזמן הצפוי.': 'The operation did not finish in the expected time.',
  'ייתכן שהכונן איטי או תקוע. בדקו שהוא מחובר ונסו שוב.': 'The drive may be slow or stuck. Check that it is connected and try again.',
  'מחשב…': 'Calculating…',
  'למה?': 'Why?',
  'הקבצים המקוריים לא השתנו.': 'The original files were not changed.',
  'מה לעשות:': 'What to do:',
  'פרטים טכניים': 'Technical details',

  'ערכת נושא: לפי הגדרות Windows': 'Theme: follow Windows',
  'ערכת נושא: בהירה': 'Theme: light',
  'ערכת נושא: כהה': 'Theme: dark',

  'לחצו על שאלה כדי לראות את התשובה': 'Click a question to see the answer',

  'מחיצה': 'Partition',
  'סריקה': 'Scan',
  'בחירת קבצים': 'Select files',
  'שחזור': 'Recover',
});

/* ------------------------------------------------------------ disks.js */
Object.assign(EN, {
  'לא ניתן לרענן את רשימת הכוננים — ':
    'Couldn\'t refresh the drive list — ',
  'לא ניתן לקרוא את רשימת הכוננים':
    'Couldn\'t read the drive list',
  'טמפרטורה {0}°':
    'Temperature {0}°',
  '{0} שעות פעולה':
    '{0} power-on hours',
  '{0}% מאורך החיים נוצלו':
    '{0}% of its lifespan used',
  '{0} סקטורים שהוחלפו':
    '{0} reallocated sectors',
  '{0} סקטורים שאינם נקראים':
    '{0} unreadable sectors',
  'הכונן בסכנה':
    'Drive at risk',
  'סימני שחיקה':
    'Signs of wear',
  'בריאות תקינה':
    'Healthy',
  'יצירת תמונה של הכונן':
    'Create an image of the drive',
  'הכונן מראה סימני כשל':
    'The drive shows signs of failure',
  'הכונן מראה סימני שחיקה':
    'The drive shows signs of wear',
  'מומלץ ליצור קודם תמונה של הכונן — להעתיק אותו פעם אחת לקובץ — ולסרוק מהתמונה: כל קריאה נוספת מהכונן עלולה להחמיר את מצבו.':
    'Create an image of the drive first — copy it once to a file — and scan the image: every extra read from the drive may make it worse.',
  'כדאי לשחזר את הקבצים החשובים בהקדם. אם הסריקה נתקעת או איטית מאוד — עדיף ליצור תמונה של הכונן ולסרוק ממנה.':
    'Recover the important files soon. If the scan gets stuck or is very slow, create an image of the drive and scan that instead.',
  'הכוננים במערכת':
    'Drives on this computer',
  'לחצו על כונן כדי לראות את המחיצות שבו':
    'Click a drive to see its partitions',
  'פתיחת סריקה שמורה':
    'Open saved scan',
  'פתיחת תמונת דיסק':
    'Open disk image',
  'תיקון קבצים שלא נפתחים':
    'Repair files that won\'t open',
  'ביטול תיקון קודם':
    'Undo a previous repair',
  'רענון':
    'Refresh',
  'אין הרשאות מנהל — השחזור לא יעבוד':
    'No administrator rights — recovery won\'t work',
  'סגרו את התוכנה והפעילו אותה מחדש: לחיצה ימנית ← "הפעל כמנהל".':
    'Close the program and start it again: right-click → "Run as administrator".',
  'בלי הרשאות מנהל Windows מאפשר לראות רק את הקבצים הקיימים. קבצים שנמחקו נמצאים מתחת לרשימת הקבצים, ורק קריאה ישירה של הכונן מגיעה אליהם.':
    'Without administrator rights Windows only shows the existing files. Deleted files are below the file list, and only reading the drive directly reaches them.',
  'לא נמצאו אמצעי אחסון':
    'No storage devices found',
  'ודאו שהדיסק מחובר ונסו לרענן.':
    'Make sure the drive is connected and try refreshing.',
  '{0} כוננים · {1} מחיצות':
    '{0} drives · {1} partitions',
  'מחקתי קבצים':
    'I deleted files',
  'גם מסל המחזור':
    'Even from the Recycle Bin',
  'פרמטתי כונן':
    'I formatted a drive',
  'או כרטיס זיכרון':
    'Or a memory card',
  'Windows מבקש לפרמט':
    'Windows asks to format',
  'הכונן לא נפתח':
    'The drive won\'t open',
  'מחיצה נעלמה':
    'A partition disappeared',
  'הכונן נראה ריק':
    'The drive looks empty',
  'קובץ לא נפתח':
    'A file won\'t open',
  'תמונה, מסמך, סרטון':
    'Photo, document, video',
  'הכונן לא מופיע':
    'The drive doesn\'t appear',
  'מחובר אבל לא מזוהה':
    'Connected but not recognized',
  'מה קרה?':
    'What happened?',
  'כונן {0}':
    'Drive {0}',
  'אל תשמרו שום דבר על הכונן שממנו נמחקו הקבצים':
    'Don\'t save anything to the drive the files were deleted from',
  'כל קובץ חדש — גם הורדה או התקנה — עלול להיכתב בדיוק במקום של הקבצים שנמחקו.':
    'Every new file — even a download or an installation — may be written exactly where the deleted files are.',
  'פתחו את הכונן ברשימה ולחצו על <b>המחיצה שבה היו הקבצים</b> (לרוב C: או D:).':
    'Open the drive in the list and click <b>the partition where the files were</b> (usually C: or D:).',
  'בחרו <b>סריקה מהירה</b>. היא לוקחת שניות עד דקות, ושומרת שמות ותיקיות.':
    'Choose <b>Quick scan</b>. It takes seconds to minutes, and keeps names and folders.',
  'לא מצאתם? חזרו לאותה מחיצה ובחרו <b>סריקה עמוקה</b>.':
    'Didn\'t find them? Go back to the same partition and choose <b>Deep scan</b>.',
  'סמנו את הקבצים ושחזרו אותם — <b>לכונן אחר</b>.':
    'Mark the files and recover them — <b>to a different drive</b>.',
  'לרשימת הכוננים':
    'To the drive list',
  'אל תעתיקו קבצים חדשים לכונן שפורמט':
    'Don\'t copy new files to the formatted drive',
  'עד שהשחזור מסתיים, כל מה שנכתב אליו עלול לדרוס את מה שאפשר עוד להציל.':
    'Until the recovery is done, anything written to it may overwrite what can still be saved.',
  'פירמוט מהיר מוחק רק את רשימת הקבצים, והתוכן נשאר. פירמוט מלא (לא מהיר) ב-Windows 10 ומעלה כותב אפסים על כל הכונן, ואחריו אין מה לשחזר.':
    'A quick format erases only the file list, and the content stays. A full (not quick) format in Windows 10 and later writes zeros over the whole drive, and after it there is nothing to recover.',
  'לחצו על <b>המחיצה שפורמטה</b>.':
    'Click <b>the formatted partition</b>.',
  'בחרו <b>סריקה עמוקה</b> — לעיתים היא מוצאת גם שמות ותיקיות מלפני הפירמוט.':
    'Choose <b>Deep scan</b> — sometimes it also finds names and folders from before the format.',
  'לא נמצא מספיק? בחרו <b>סריקה מתקדמת</b>. היא מזהה קבצים לפי התוכן שלהם ועובדת גם אחרי פירמוט, אבל בלי השמות המקוריים.':
    'Not enough found? Choose <b>Advanced scan</b>. It recognizes files by their content and works even after formatting, but without the original names.',
  'שמור למערכת':
    'Reserved for the system',
  'אל תאשרו את הפירמוט':
    'Don\'t approve the format',
  'Windows מבקש לפרמט כשתחילת המחיצה נפגעה — אבל הקבצים בדרך כלל עדיין שם, שלמים.':
    'Windows asks to format when the start of the partition is damaged — but the files are usually still there, intact.',
  'לחצו על המחיצה ש-Windows לא מצליח לפתוח.':
    'Click the partition Windows can\'t open.',
  'התוכנה תבדוק אותה ותציע, לפי הסדר: <b>להעתיק את הקבצים בלי לכתוב לכונן</b>, לתקן את המחיצה, או לחפש קבצים לפי התוכן שלהם.':
    'The program will check it and suggest, in order: <b>copying the files without writing to the drive</b>, repairing the partition, or looking for files by their content.',
  'מחיצות שהתוכנה לא מצליחה לקרוא':
    'Partitions the program can\'t read',
  'כרגע כל המחיצות נקראות':
    'All partitions can be read right now',
  'אם הכונן עדיין לא נפתח ב-Windows, ייתכן שהמחיצה נמחקה מהטבלה — נסו את "מחיצה נעלמה".':
    'If the drive still won\'t open in Windows, the partition may have been deleted from the table — try "A partition disappeared".',
  'ליד הכונן לחצו <b>סריקת כונן</b>.':
    'Next to the drive click <b>Scan drive</b>.',
  'התוכנה תעבור על כל הכונן ותחפש מחיצות שנמחקו מטבלת המחיצות — גם כשתחילתן נהרסה.':
    'The program will go over the whole drive and look for partitions deleted from the partition table — even when their start was destroyed.',
  'מחיצה שנמצאה תופיע ברשימה. אפשר להעתיק ממנה קבצים בלי לכתוב לכונן, או להחזיר אותה לטבלה.':
    'A partition that is found appears in the list. You can copy files from it without writing to the drive, or restore it to the table.',
  'סריקת כונן':
    'Scan drive',
  'Windows מרגיש בהתקן אחד שלא הופעל':
    'Windows sees one device that didn\'t start',
  'Windows מרגיש ב-{0} התקנים שלא הופעלו':
    'Windows sees {0} devices that didn\'t start',
  'הם מופיעים בתחתית רשימת הכוננים, עם הסבר ועצות.':
    'They appear at the bottom of the drive list, with an explanation and tips.',
  'חברו את הכונן ישירות למחשב, בלי מפצל USB, ונסו יציאה אחרת — עדיף בגב המחשב.':
    'Connect the drive directly to the computer, without a USB hub, and try another port — preferably at the back of the computer.',
  'כונן חיצוני גדול: נסו כבל אחר, וודאו שספק הכוח שלו מחובר אם יש לו.':
    'A large external drive: try another cable, and make sure its power supply is connected if it has one.',
  'כרטיס זיכרון: נסו קורא כרטיסים אחר.':
    'A memory card: try another card reader.',
  'לחצו <b>רענון</b>.':
    'Click <b>Refresh</b>.',
  'כונן שמשמיע נקישות או רעשים — נתקו אותו מיד':
    'A drive that clicks or makes noises — disconnect it right away',
  'כל הפעלה נוספת עלולה להרוס את מה שנשאר. זה מקרה למעבדת שחזור.':
    'Every extra power-on may destroy what is left. This is a job for a data recovery lab.',
  'כונן שהמחשב כלל אינו מרגיש שחובר אינו נראה לשום תוכנה; הבעיה בחומרה.':
    'A drive the computer doesn\'t detect at all isn\'t visible to any program; the problem is in the hardware.',
  'מה עושים עכשיו':
    'What to do now',
  'הכונן מוחק מעצמו את התוכן של קבצים שנמחקו, ולכן סיכויי השחזור נמוכים יותר.':
    'The drive erases the content of deleted files by itself, so the chances of recovery are lower.',
  'TRIM פעיל':
    'TRIM on',
  'הכונן אינו מוחק מעצמו את התוכן של קבצים שנמחקו — מצב טוב לשחזור.':
    'The drive doesn\'t erase the content of deleted files by itself — good for recovery.',
  'ללא TRIM':
    'No TRIM',
  'לא מגיב':
    'Not responding',
  'אין גישה גולמית':
    'No raw access',
  'ללא מחיצות':
    'No partitions',
  '{0} מחיצות':
    '{0} partitions',
  'כונן מוצפן שנקרא דרך Windows · {0}':
    'Encrypted drive read through Windows · {0}',
  'דיסק {0}':
    'Disk {0}',
  'גודל לא ידוע':
    'Unknown size',
  'חיפוש מחיצות שנמחקו או שאבדו בכל הכונן':
    'Search the whole drive for deleted or lost partitions',
  'סגירת התמונה':
    'Close image',
  'העתקת הדיסק כולו לקובץ, וסריקה מתוכו':
    'Copy the whole disk to a file, and scan from it',
  'יצירת תמונת דיסק':
    'Create disk image',
  'הכונן מדווח על גודל 0. בקורא כרטיסים זה אומר בדרך כלל שאין כרטיס בפנים, או שהכרטיס אינו נקרא.':
    'The drive reports a size of 0. In a card reader this usually means there is no card inside, or the card can\'t be read.',
  'לא נמצאו מחיצות בטבלת המחיצות של הכונן.':
    'No partitions were found in the drive\'s partition table.',
  'היו בו מחיצות שנמחקו? לחצו על <b>סריקת כונן</b>.':
    'Did it have partitions that were deleted? Click <b>Scan drive</b>.',
  'לחצו להצגת המחיצות':
    'Click to show the partitions',
  'נמצאה מחיצה אחת':
    '1 partition found',
  'נמצאו {0} מחיצות':
    '{0} partitions found',
  'למחיצה אין אות כונן':
    'The partition has no drive letter',
  'נמצאה בסריקה':
    'Found by scan',
  'תחילתה פגומה':
    'Damaged start',
  'חופפת למחיצה קיימת':
    'Overlaps an existing partition',
  'בתוך מחיצה אחרת':
    'Inside another partition',
  'נקראת דרך הגיבוי':
    'Read through the backup',
  'לא מחוברת':
    'Not mounted',
  'נעילה פתוחה':
    'Unlocked',
  'הנעילה נפתחה ב-Windows — אפשר לסרוק':
    'Unlocked in Windows — can be scanned',
  'נעול':
    'Locked',
  'הכונן מוצפן. פתחו אותו ב-Windows כדי לסרוק':
    'The drive is encrypted. Unlock it in Windows to scan it',
  'אתחול':
    'Boot',
  'לא נתמכת לסריקה':
    'Not supported for scanning',
  'נפח לא זמין':
    'Space not available',
  '{0} בשימוש · {1}%':
    '{0} used · {1}%',
  'סריקות אחרונות':
    'Recent scans',
  'ניקוי הרשימה':
    'Clear the list',
  'ניתן לשחזור':
    'recoverable',
  'ניתנים לשחזור':
    'recoverable',
  'נעצרה באמצע — אפשר להמשיך מאותה נקודה':
    'Stopped midway — can continue from the same point',
  'נעצרה ב-{0}%':
    'Stopped at {0}%',
  'נשמרה באמצע סריקה — לא כל המחיצה נסרקה':
    'Saved in the middle of a scan — not the whole partition was scanned',
  'חלקית':
    'Partial',
  'הכונן מחובר':
    'Drive connected',
  'אפשר לעיין ברשימה, אבל לא לשחזר':
    'You can browse the list, but not recover',
  'הכונן לא מחובר':
    'Drive not connected',
  'נעצרה אחרי {0}% — המשך מאותה נקודה':
    'Stopped after {0}% — continue from the same point',
  'המשך':
    'Continue',
  'הסרה מהרשימה':
    'Remove from the list',
  'למחוק את כל הסריקות השמורות?':
    'Delete all saved scans?',
  'מחיקה':
    'Delete',
  'ביטול':
    'Cancel',
  'לא ניתן להסיר את הסריקה':
    'Couldn\'t remove the scan',
  'פותח את הסריקה השמורה…':
    'Opening the saved scan…',
  'לא ניתן לפתוח את הסריקה':
    'Couldn\'t open the scan',
  'התקנים מחוברים ש-Windows לא הצליח להפעיל':
    'Connected devices Windows couldn\'t start',
  'לא זמין לקריאה':
    'Not readable',
  'מה אפשר לנסות:':
    'What you can try:',
  'במנהל ההתקנים הוא מופיע בשם: {0}':
    'In Device Manager it appears as: {0}',
});

/* ------------------------------------------------------------ partitions.js */
Object.assign(EN, {
  'מחפש מחיצות שנמחקו או שאבדו — אחרי מחיקה בטעות, התקנה מחדש, או כשהכונן מופיע פתאום "לא מאותחל".':
    'Looks for partitions that were deleted or lost — after an accidental deletion, a reinstall, or when the drive suddenly shows up as "not initialized".',
  'מחיצה שנמחקה מהטבלה עדיין על הכונן, עם כל הקבצים. מה שיימצא יופיע ברשימה, ואפשר יהיה להעתיק ממנו קבצים או להחזיר אותו לטבלה.':
    'A partition deleted from the table is still on the drive, with all its files. Whatever is found will appear in the list, and you will be able to copy files from it or restore it to the table.',
  'קריאה בלבד — שום דבר לא נכתב לכונן':
    'Read only — nothing is written to the drive',
  'בכונן גדול זה לוקח זמן. אפשר לעצור בכל רגע, ומה שנמצא עד אז יוצג.':
    'On a large drive this takes time. You can stop at any moment, and whatever was found so far will be shown.',
  'התחלת סריקה':
    'Start scan',
  'מחפש מחיצות בכל הכונן':
    'Looking for partitions across the whole drive',
  'מחיצות שנמצאו':
    'Partitions found',
  'נקרא מהכונן':
    'Read from the drive',
  'זמן שחלף':
    'Elapsed',
  'זמן משוער שנותר':
    'Estimated time left',
  'מצב':
    'Status',
  'פועל':
    'Running',
  'עצירה':
    'Stop',
  'סורק את הכונן…':
    'Scanning the drive…',
  ' (הסריקה נעצרה לפני סופה)':
    ' (the scan was stopped before it finished)',
  'נמצאה גם שארית אחת של מערכת קבצים בתוך מחיצה אחרת — בדרך כלל קובץ ISO שנשמר על הכונן. היא אינה מחיצה, ולכן לא הוצגה.':
    'One remnant of a file system inside another partition was also found — usually an ISO file saved on the drive. It is not a partition, so it was not shown.',
  'נמצאו גם {0} שאריות של מערכות קבצים בתוך מחיצות אחרות — בדרך כלל קבצי ISO שנשמרו על הכונן. הן אינן מחיצות, ולכן לא הוצגו.':
    '{0} remnants of file systems inside other partitions were also found — usually ISO files saved on the drive. They are not partitions, so they were not shown.',
  'נמצאה מחיצה אחת ב{0}':
    '1 partition found on {0}',
  'נמצאו {0} מחיצות ב{1}':
    '{0} partitions found on {1}',
  'הן מסומנות "נמצאה בסריקה". לחצו על מחיצה כדי להעתיק ממנה קבצים או להחזיר אותה לטבלה.':
    'They are marked "Found by scan". Click a partition to copy files from it or restore it to the table.',
  'לא נמצאו מחיצות אבודות ב{0}':
    'No lost partitions found on {0}',
  'הקבצים עדיין חסרים? נסו <b>סריקה מתקדמת</b> על אחת המחיצות — היא מוצאת קבצים לפי סוגם.':
    'Files still missing? Try an <b>Advanced scan</b> on one of the partitions — it finds files by their type.',
  'סריקת הכונן נכשלה':
    'The drive scan failed',
  'חזרה לכוננים':
    'Back to drives',
  '{0} מתוך {1}':
    '{0} of {1}',
  'החזרת המחיצה':
    'Restoring the partition',
  'החזרת המחיצה לטבלת המחיצות':
    'Restore the partition to the partition table',
  'כדי ש-Windows יראה אותה שוב, עם אות כונן. כותב לכונן — כדאי להעתיק קודם את הקבצים החשובים.':
    'So Windows sees it again, with a drive letter. Writes to the drive — copy the important files first.',
  'החזרת מחיצה לטבלה':
    'Restore partition to the table',
  'בודק את טבלת המחיצות של הכונן…':
    'Checking the drive\'s partition table…',
  'לא ניתן לבדוק אם אפשר להחזיר את המחיצה':
    'Couldn\'t check whether the partition can be restored',
  'חזרה':
    'Back',
  'הפעולה כותבת לכונן':
    'This operation writes to the drive',
  'יש במחיצה קבצים חשובים? העתיקו אותם קודם: סגרו את החלון ובחרו סריקה.':
    'Important files on the partition? Copy them first: close this window and choose a scan.',
  'לפני הכתיבה נשמר גיבוי של כל מה שעומד להשתנות בכונן. אם משהו ישתבש, התוכנה תחזיר את המצב הקודם אוטומטית.':
    'Before writing, a backup of everything about to change on the drive is saved. If something goes wrong, the program puts the previous state back automatically.',
  'תיקיית גיבוי — על כונן אחר':
    'Backup folder — on another drive',
  'לא נבחרה תיקייה':
    'No folder selected',
  'תיקיית הגיבוי':
    'Backup folder',
  'בחירה':
    'Choose',
  'החזרה לטבלה':
    'Restore to the table',
  'הגיבוי יישמר כאן.':
    'The backup will be saved here.',
  'מחזיר את המחיצה לטבלה…':
    'Restoring the partition to the table…',
  'החזרת המחיצה לא הושלמה':
    'Restoring the partition did not finish',
  'סיום':
    'Done',
});

/* ------------------------------------------------------------ disks.js */
Object.assign(EN, {
  'מחיצה אחת':
    '1 partition',
});

/* ------------------------------------------------------------ scan-options.js */
Object.assign(EN, {
  'קריאה בלבד':
    'Read only',
  'סריקה מהירה':
    'Quick scan',
  'נמחק לאחרונה? התחילו כאן.':
    'Deleted recently? Start here.',
  'שמות ותיקיות נשמרים · שניות עד דקות':
    'Names and folders kept · seconds to minutes',
  'קוראת את טבלת הקבצים של המחיצה ומאתרת קבצים שנמחקו אך הרשומה שלהם עדיין קיימת. ב-NTFS זו טבלת ה-MFT, וב-FAT וב-exFAT — רשומות התיקיות.':
    'Reads the partition\'s file table and finds deleted files whose entry still exists. On NTFS that\'s the MFT; on FAT and exFAT, the folder entries.',
  'סריקה עמוקה':
    'Deep scan',
  'המהירה לא מצאה? נסו את זו.':
    'Quick scan didn\'t find it? Try this one.',
  'רוב השמות נשמרים · דקות עד שעה':
    'Most names kept · minutes to an hour',
  'עוברת בנוסף על כל המחיצה ומחפשת רשומות יתומות — רשומות של קבצים שהטבלה כבר אינה מצביעה עליהן — וקוראת את יומני מערכת הקבצים.':
    'Also goes over the whole partition looking for orphaned entries — entries of files the table no longer points to — and reads the file system\'s journals.',
  'סריקה מתקדמת':
    'Advanced scan',
  'אחרי פירמוט או נזק כבד.':
    'After formatting or heavy damage.',
  'בלי שמות מקוריים · שעה ומעלה':
    'No original names · an hour or more',
  'קוראת את הכונן כולו ומזהה קבצים לפי חתימות HEX — הבתים הקבועים שבתחילת כל סוג קובץ — בלי תלות במערכת הקבצים. עובדת גם אחרי פירמוט, אבל שמות ותיקיות אינם נשמרים.':
    'Reads the whole drive and recognizes files by HEX signatures — the fixed bytes at the start of each file type — independently of the file system. Works even after formatting, but names and folders are not kept.',
  'מה ההבדל בין הסריקות?':
    'What\'s the difference between the scans?',
  'הנעילה פתוחה':
    'Unlocked',
  'התוכנה תקרא את הכונן {0} דרך Windows, שמפענח אותו. הוא יופיע ברשימה ככונן נוסף, ואפשר יהיה להריץ עליו כל סוג סריקה — גם סריקה מתקדמת.':
    'The program will read drive {0} through Windows, which decrypts it. It will appear in the list as another drive, and you can run any type of scan on it — including an advanced scan.',
  'BitLocker מצפין את כל המחיצה, כולל המקום שבו יושבים קבצים שנמחקו. קריאה ישירה מהכונן מחזירה רק תוכן מוצפן; דרך Windows כל אזור נקרא מפוענח.':
    'BitLocker encrypts the whole partition, including the space where deleted files are. Reading the drive directly returns only encrypted content; through Windows every area is read decrypted.',
  'שום דבר לא ייכתב לכונן. אל תנעלו אותו מחדש ואל תנתקו אותו עד סוף השחזור.':
    'Nothing will be written to the drive. Don\'t lock it again and don\'t disconnect it until the recovery is done.',
  'הכונן נעול ב-BitLocker':
    'The drive is locked with BitLocker',
  'התוכן שלו מוצפן. הקלידו את מפתח השחזור או את הסיסמה, והתוכנה תפענח אותו בעצמה — גם אם Windows לא מצליח לפתוח אותו. הכונן המפוענח יופיע ברשימה ככונן נוסף.':
    'Its content is encrypted. Type the recovery key or the password, and the program will decrypt it itself — even if Windows can\'t unlock it. The decrypted drive will appear in the list as another drive.',
  'מפתח שחזור או סיסמה':
    'Recovery key or password',
  'הצגה':
    'Show',
  'הסתרה':
    'Hide',
  '<b>מפתח השחזור</b> הוא מספר של 48 ספרות. הוא נשמר בדרך כלל בחשבון Microsoft של מי שהגדיר את המחשב (בכתובת {0}), או הודפס ונשמר בקובץ כשההצפנה הופעלה.':
    'The <b>recovery key</b> is a 48-digit number. It\'s usually saved in the Microsoft account of whoever set up the computer (at {0}), or it was printed or saved to a file when encryption was turned on.',
  'אפשר גם לפתוח את הנעילה ב-Windows':
    'You can also unlock it in Windows',
  'המפתח משמש רק לפענוח בזמן שהתוכנה פתוחה. הוא לא נשמר בשום מקום, ושום דבר לא נכתב לכונן.':
    'The key is used only for decrypting while the program is open. It isn\'t saved anywhere, and nothing is written to the drive.',
  'פתיחה':
    'Unlock',
  'הקלידו את מפתח השחזור או את הסיסמה של הכונן.':
    'Type the drive\'s recovery key or password.',
  'בודק את המפתח… זה לוקח כמה שניות.':
    'Checking the key… this takes a few seconds.',
  'אזור הניהול של ההצפנה לא נקרא':
    'The encryption\'s management area couldn\'t be read',
  'אם הכונן גוסס, כדאי ליצור ממנו תמונת דיסק ולנסות לפתוח את התמונה. אם גם זה לא עוזר — מעבדת שחזור.':
    'If the drive is failing, create a disk image of it and try to unlock the image. If that doesn\'t help either — a data recovery lab.',
  'ההגנה על הכונן מושהית':
    'The drive\'s protection is suspended',
  'אפשר לפתוח אותו בלי מפתח — לחצו <b>פתיחה</b>.':
    'It can be opened without a key — click <b>Unlock</b>.',
  'הכונן ננעל עם: {0}.':
    'The drive is locked with: {0}.',
  'אין לכונן הזה מפתח שאפשר להקליד':
    'This drive has no key that can be typed',
  'הוא נפתח רק במחשב שבו הוצפן, או בקובץ מפתח. חברו אותו לאותו מחשב ופתחו אותו ב-Windows.':
    'It opens only on the computer where it was encrypted, or with a key file. Connect it to that computer and unlock it in Windows.',
  'כונן מוצפן שהתוכנה מפענחת · {0}':
    'Encrypted drive decrypted by the program · {0}',
  'פתחו את <b>סייר הקבצים</b> ולחצו פעמיים על הכונן <b>{0}</b>. Windows יבקש סיסמה או מפתח שחזור.':
    'Open <b>File Explorer</b> and double-click drive <b>{0}</b>. Windows will ask for a password or a recovery key.',
  'לכונן אין אות כונן, ולכן Windows לא מציע לפתוח אותו. אם הוא חיצוני — נתקו וחברו אותו מחדש; אחרת פתחו את <b>ניהול דיסקים</b> של Windows והקצו לו אות.':
    'The drive has no drive letter, so Windows doesn\'t offer to unlock it. If it\'s external, disconnect and reconnect it; otherwise open Windows <b>Disk Management</b> and assign it a letter.',
  'אחרי שהכונן נפתח, חזרו לכאן ולחצו <b>רענון</b>. ליד המחיצה יופיע "נעילה פתוחה".':
    'After the drive is unlocked, come back here and click <b>Refresh</b>. "Unlocked" will appear next to the partition.',
  'בלי המפתח אין דרך לשחזר':
    'Without the key there\'s no way to recover',
  'ההצפנה נועדה בדיוק לזה: בלי סיסמה או מפתח שחזור אף תוכנה לא יכולה לקרוא את הקבצים.':
    'That\'s exactly what encryption is for: without a password or recovery key no program can read the files.',
  'פתיחה לסריקה':
    'Open for scanning',
  'לא ניתן לפתוח את הכונן':
    'Couldn\'t open the drive',
  'מחיצה {0}':
    'Partition {0}',
  'לא זמין':
    'Not available',
  'מערכת הקבצים {0} אינה נתמכת':
    'The {0} file system is not supported',
  'מערך RAID שהתוכנה הרכיבה · {0} · {1}':
    'RAID array assembled by the program · {0} · {1}',
  'לחצו להרכבת המערך': 'Click to assemble the array',
  'הכונן הזה הוא חלק ממערך RAID של שרת לינוקס או שרת אחסון ביתי':
    'This drive is part of a RAID array from a Linux server or a home storage server (NAS)',
  'הכונן הזה הוא חלק ממערך RAID': 'This drive is part of a RAID array',
  'שרתי אחסון ביתיים ושרתי לינוקס מפזרים את הקבצים על כמה כוננים. כל כונן לבד מחזיק רק חלקים — צריך להרכיב את המערך מכל הכוננים שלו. התוכנה עושה את זה בעצמה, בלי השרת, ושום דבר לא נכתב לכוננים.':
    'Home storage servers and Linux servers spread files across several drives. Each drive alone holds only pieces — the array has to be assembled from all its drives. The program does this itself, without the server, and nothing is written to the drives.',
  'מחפש את שאר הכוננים של המערך…': 'Looking for the array\'s other drives…',
  'חסר כונן?': 'A drive is missing?',
  'חברו למחשב את כל הכוננים שהוצאו מהשרת — כל אחד בחיבור משלו או במתאם USB. אין צורך בסדר מסוים.':
    'Connect all the drives taken out of the server to this computer — each on its own port or USB adapter. The order doesn\'t matter.',
  'יצרתם קודם תמונות דיסק מהכוננים? פתחו את כולן ברשימת הכוננים.':
    'Made disk images of the drives first? Open all of them in the drive list.',
  'לחצו <b>חיפוש שוב</b>.': 'Click <b>Search again</b>.',
  'חיפוש שוב': 'Search again',
  'מרענן את רשימת הכוננים ומחפש…': 'Refreshing the drive list and searching…',
  'החיפוש נכשל': 'The search failed',
  'לא נמצא מערך': 'No array found',
  'הכותרת של המערך לא נקראה מהכונן. ייתכן שהיא ניזוקה — אז <b>סריקה מתקדמת</b> של כל כונן בנפרד עדיין תמצא קבצים קטנים.':
    'The array header couldn\'t be read from the drive. It may be damaged — an <b>Advanced scan</b> of each drive separately can still find small files.',
  '{0} כוננים': '{0} drives',
  'רצועה של {0}': '{0} stripe',
  'כונן {0} במערך:': 'Array drive {0}:',
  'לא עדכני': 'Out of date',
  'חסר: כונן {0} במערך': 'Missing: array drive {0}',
  'חסרים: כוננים {0} במערך': 'Missing: array drives {0}',
  'אי אפשר להרכיב את המערך': 'The array can\'t be assembled',
  'אפשר להרכיב גם בלי הכונן החסר': 'It can be assembled without the missing drive',
  'התוכן שלו מחושב מהכוננים האחרים. אם אפשר לחבר אותו — עדיף.':
    'Its content is computed from the other drives. If you can connect it, that\'s better.',
  'מעבר למערך': 'Go to the array',
  'הרכבת המערך': 'Assemble the array',
  'מרכיב את המערך…': 'Assembling the array…',
  'המערך לא הורכב': 'The array wasn\'t assembled',
  'אזור במאגר לוגי · {0}': 'Logical volume · {0}',
  'כוננים שהוצאו ממחשב או משרת עם כרטיס RAID — התוכנה מזהה את המבנה ומרכיבה':
    'Drives taken out of a computer or server with a RAID card — the program detects the layout and assembles it',
  'הרכבת מערך': 'Assemble array',
  'כוננים ממחשב או משרת עם כרטיס RAID': 'Drives from a computer or server with a RAID card',
  'בחרו את כל הכוננים של המערך': 'Select all the array\'s drives',
  'כרטיס RAID לא כותב על הכוננים משהו שאפשר לקרוא בלעדיו. התוכנה תנסה את כל האפשרויות — סוג, גודל רצועה וסדר הכוננים — ותבדוק כל אחת מול מערכת הקבצים שבתוכו. הסדר שבו תבחרו לא משנה. שום דבר לא נכתב לכוננים.':
    'A RAID card doesn\'t write anything on the drives that can be read without it. The program will try every option — type, stripe size and drive order — and check each against the file system inside. The order you select them in doesn\'t matter. Nothing is written to the drives.',
  'אין כוננים מתאימים ברשימה.': 'There are no suitable drives in the list.',
  'זיהוי המבנה': 'Detect the layout',
  'בחרו לפחות שני כוננים.': 'Select at least two drives.',
  'מנסה את כל האפשרויות… בכוננים גדולים זה יכול לקחת כמה דקות.': 'Trying every option… on large drives this can take a few minutes.',
  'הזיהוי נכשל': 'Detection failed',
  'המבנה לא זוהה': 'The layout wasn\'t detected',
  'אף אפשרות לא התיישבה עם מערכת הקבצים. ודאו שבחרתם את כל הכוננים של המערך, ורק אותם. הזיהוי עובד היום כשבתוך המערך יש NTFS או ext4. <b>סריקה מתקדמת</b> של כל כונן בנפרד עדיין תמצא קבצים קטנים.':
    'No option matched the file system. Make sure you selected all the array\'s drives, and only them. Detection currently works when the array contains NTFS or ext4. An <b>Advanced scan</b> of each drive separately can still find small files.',
  'יש כמה אפשרויות קרובות': 'There are several close options',
  'זו המתאימה ביותר, אבל גם האחרות שלמטה התיישבו חלקית. אם הקבצים יחזרו פגומים — נסו את הבאה.':
    'This is the best match, but the others below also partly matched. If files come back damaged, try the next one.',
  'הרכבה': 'Assemble',
  'לחצו לפתיחת האזורים': 'Click to open the volumes',
  'המחיצה מחולקת לאזורים בשכבה של לינוקס — כל אזור נפתח ככונן נוסף':
    'The partition is divided into volumes by a Linux layer — each volume opens as another drive',
  'המחיצה מחולקת לאזורים': 'The partition is divided into volumes',
  'לינוקס ושרתי אחסון ביתיים מחלקים מחיצה (או כמה מחיצות ביחד) לאזורים, וכל אזור הוא כמו כונן נפרד עם הקבצים שלו. בחרו אזור, והוא יופיע ברשימה ככונן נוסף, לקריאה בלבד.':
    'Linux and home storage servers divide a partition (or several partitions together) into volumes, and each volume is like a separate drive with its own files. Choose a volume and it will appear in the list as another drive, read-only.',
  'קורא את תיאור האזורים…': 'Reading the volume layout…',
  'תיאור האזורים לא נקרא': 'The volume layout couldn\'t be read',
  'ייתכן שהוא ניזוק. <b>סריקה מתקדמת</b> של המחיצה עדיין תמצא קבצים לפי סוג.':
    'It may be damaged. An <b>Advanced scan</b> of the partition will still find files by type.',
  'כונן שלא נמצא': 'A drive that wasn\'t found',
  'חסרים כוננים במאגר': 'Drives are missing from the pool',
  'אזורים שיושבים גם עליהם לא ייפתחו. חברו את כל הכוננים של השרת (או פתחו את התמונות שלהם) ולחצו <b>חיפוש שוב</b>.':
    'Volumes that also sit on them won\'t open. Connect all the server\'s drives (or open their images) and click <b>Search again</b>.',
  'מעבר לאזור': 'Go to the volume',
  'הכוננים של המאגר:': 'The pool\'s drives:',
  'אין במאגר אזורים.': 'The pool has no volumes.',
  'האזור לא נפתח': 'The volume didn\'t open',
  '<b>סריקה מתקדמת</b> עדיין תעבוד — היא אינה תלויה במערכת הקבצים.':
    'An <b>Advanced scan</b> will still work — it doesn\'t depend on the file system.',
  'נתמכות: NTFS, exFAT, FAT32, FAT16, FAT12, ext2/3/4, XFS, btrfs, HFS+ ו-APFS.':
    'Supported: NTFS, exFAT, FAT32, FAT16, FAT12, ext2/3/4, XFS, btrfs, HFS+ and APFS.',
  'המחיצה נקראת דרך עותק הגיבוי':
    'The partition is read through its backup copy',
  'בחרו <b>סריקה מהירה</b> — יוצגו כל הקבצים עם השמות, ולא רק קבצים שנמחקו.':
    'Choose <b>Quick scan</b> — all the files will be shown with their names, not just deleted ones.',
  'תחילת המחיצה (מגזר האתחול) פגומה, והתוכנה קוראת אותה דרך עותק הגיבוי שלה — בזיכרון בלבד. שום דבר לא נכתב לכונן.':
    'The start of the partition (the boot sector) is damaged, and the program reads it through its backup copy — in memory only. Nothing is written to the drive.',
  'בחרו סוג סריקה':
    'Choose a scan type',
  'כונן חלש או שמשמיע רעשים?':
    'A weak drive, or one that makes noises?',
  'יצירת תמונה של המחיצה לפני הסריקה':
    'Create an image of the partition before scanning',
  'מעתיקים את המחיצה פעם אחת לקובץ על כונן אחר, וסורקים את ההעתק — בלי לשחוק כונן שעלול להפסיק לעבוד.':
    'Copy the partition once to a file on another drive, and scan the copy — without wearing out a drive that may stop working.',
  'מחשב אסטרטגיית שחזור…':
    'Working out a recovery strategy…',
  'לא ניתן להכין את הסריקה':
    'Couldn\'t prepare the scan',
  'גבוהים':
    'High',
  'בינוניים':
    'Medium',
  'נמוכים':
    'Low',
  'יצירת תמונה במקום סריקה ישירה':
    'Create an image instead of scanning directly',
  'אסטרטגיה שנבחרה אוטומטית':
    'Strategy chosen automatically',
  'גודל בלוק קריאה':
    'Read block size',
  'ערוצי קריאה מקבילים':
    'Parallel read channels',
  'סדר סריקה':
    'Scan order',
  'רציף':
    'Sequential',
  'חופשי':
    'Random',
  'סוג אמצעי אחסון':
    'Storage type',
  'הערכת סיכויי שחזור:':
    'Estimated chances of recovery:',
  'הערכת סיכויי שחזור':
    'Estimated chances of recovery',
  'אילו סוגי קבצים לחפש':
    'Which file types to look for',
  'בחירה של סוגים מסוימים מקצרת את רשימת התוצאות ומתמקדת במה שמחפשים.':
    'Choosing specific types shortens the results list and focuses on what you are looking for.',
  'לסרוק רק את המקום הפנוי — מהיר בהרבה':
    'Scan only the free space — much faster',
  'קבצים שנמחקו נמצאים במקום שמערכת הקבצים סימנה כפנוי, והמקום התפוס מכיל את הקבצים הקיימים. בכונן מלא ברובו הסריקה מהירה פי כמה. כבו אם מבנה המחיצה פגום.':
    'Deleted files are in the space the file system marked as free, and the used space holds the existing files. On a mostly full drive the scan is several times faster. Turn it off if the partition structure is damaged.',
  'הצג גם קבצים קיימים, ולא רק קבצים שנמחקו':
    'Also show existing files, not just deleted ones',
  'קריאה בלבד מהדיסק המקור':
    'Read only from the source disk',
  'השחזור יתאפשר רק לכונן אחר.':
    'Recovery will only be allowed to another drive.',
  'גם סוגים שהוספתם: {0}. הם נכללים ב"אחר".':
    'Also types you added: {0}. They are included in "Other".',
  'ניהול הסוגים שהוספתם':
    'Manage the types you added',
  'הוספת סוג קובץ שהתוכנה לא מכירה':
    'Add a file type the program doesn\'t know',
  'סוגי קבצים שהתוכנה לא מכירה':
    'File types the program doesn\'t know',
  'מלמדים את הסריקה המתקדמת לחפש אותם':
    'Teach the advanced scan to look for them',
  'איך זה עובד':
    'How it works',
  'בוחרים כמה קבצים תקינים מאותו סוג — שלושה ומעלה, למשל מגיבוי או ממחשב אחר. התוכנה מוצאת מה זהה בתחילת כולם, ולפי זה הסריקה המתקדמת תמצא קבצים שנמחקו מהסוג הזה.':
    'Choose a few good files of the same type — three or more, for example from a backup or another computer. The program finds what is identical at the start of all of them, and the advanced scan uses that to find deleted files of that type.',
  'הקבצים לדוגמה רק נקראים — הם לא משתנים ולא מועתקים לשום מקום.':
    'The sample files are only read — they aren\'t changed or copied anywhere.',
  'הסוגים שהוספתם':
    'Types you added',
  'בחירת קבצים לדוגמה':
    'Choose sample files',
  'נלמד מ-{0} קבצים':
    'learned from {0} files',
  'אורך מדויק':
    'exact length',
  'הסרה':
    'Remove',
  'עדיין לא הוספתם סוגים.':
    'You haven\'t added any types yet.',
  'לא ניתן ללמוד מהקבצים':
    'Couldn\'t learn from the files',
  'שם לסוג — כך תיקרא התיקייה של הקבצים שיימצאו':
    'A name for the type — the folder of the files found will be called this',
  'שם לסוג':
    'Type name',
  'שמירה':
    'Save',
  'נשמר. מעכשיו הסריקה המתקדמת תחפש גם את הסוג הזה.':
    'Saved. From now on the advanced scan will also look for this type.',
});

/* ------------------------------------------------------------ repair.js */
Object.assign(EN, {
  'מאבחן את המחיצה…':
    'Diagnosing the partition…',
  'לא ניתן לבדוק את המחיצה':
    'Couldn\'t check the partition',
  'אפשרות 1 — העתקת הקבצים בלי לגעת בכונן (מומלץ)':
    'Option 1 — copy the files without touching the drive (recommended)',
  'Windows מבקש לפרמט כי תחילת המחיצה נפגעה, אבל הקבצים עצמם בדרך כלל שלמים. התוכנה תציג את כולם <b>עם השמות והתיקיות המקוריים</b>, להעתקה לכונן אחר.':
    'Windows asks to format because the start of the partition is damaged, but the files themselves are usually intact. The program will show them all <b>with their original names and folders</b>, to copy to another drive.',
  'שום דבר לא נכתב לכונן':
    'Nothing is written to the drive',
  'התיקון קיים רק בזיכרון של התוכנה.':
    'The repair exists only in the program\'s memory.',
  'התוכנה קוראת את המחיצה דרך עותק הגיבוי של תחילתה (מגזר האתחול), והכונן נשאר בדיוק כפי שהוא.':
    'The program reads the partition through the backup copy of its start (the boot sector), and the drive stays exactly as it is.',
  'אפשרות 2 — תיקון המחיצה':
    'Option 2 — repair the partition',
  'התיקון ייכתב לקובץ התמונה בלבד':
    'The repair will be written to the image file only',
  'הכונן המקורי לא נוגע בתהליך — זו הדרך הבטוחה ביותר לנסות תיקון.':
    'The original drive isn\'t touched — this is the safest way to try a repair.',
  'הפעולה היחידה שכותבת לדיסק המקור':
    'The only operation that writes to the source disk',
  'אם התיקון יצליח, כל הקבצים יחזרו להיות נגישים כרגיל.':
    'If the repair succeeds, all the files will be accessible as usual again.',
  'אם התיקון ייכשל, התוכנה תחזיר את המצב הקודם אוטומטית מהגיבוי שנשמר לפני הכתיבה.':
    'If the repair fails, the program automatically puts back the previous state from the backup saved before writing.',
  'אפשרות 3 — סריקה מתקדמת':
    'Option 3 — Advanced scan',
  'סריקה מתקדמת מחפשת קבצים לפי התוכן שלהם, ועובדת גם במחיצה שאינה נקראת כלל.':
    'An advanced scan looks for files by their content, and works even on a partition that cannot be read at all.',
  'אבל הקבצים יימצאו בלי שמות — השתמשו בה רק אם אפשרות 1 לא מצאה את מה שחיפשתם.':
    'But the files will be found without names — use it only if option 1 didn\'t find what you were looking for.',
  'לא נמצא עותק גיבוי, ולכן זו הדרך להציל את הקבצים — בלי שמות מקוריים ובלי תיקיות.':
    'No backup copy was found, so this is the way to save the files — without original names and without folders.',
  'הכונן לא ישתנה, והקבצים יועתקו לכונן אחר.':
    'The drive won\'t change, and the files will be copied to another drive.',
  'העתקת קבצים עם שמות':
    'Copy files with names',
  'תיקון המחיצה':
    'Repair the partition',
  'קורא את המחיצה דרך עותק הגיבוי…':
    'Reading the partition through the backup copy…',
  'לא ניתן לקרוא את המחיצה דרך עותק הגיבוי':
    'Couldn\'t read the partition through the backup copy',
  'מאשר':
    'CONFIRM',
  'אישור אחרון':
    'Final confirmation',
  'כדי לכתוב לכונן, הקלידו את המילה <b>{0}</b>:':
    'To write to the drive, type the word <b>{0}</b>:',
  'תיקון מחיצה':
    'Partition repair',
  'הפעולה כותבת לדיסק':
    'This operation writes to the disk',
  'אפשר לחזור אחורה':
    'It can be undone',
  'לפני הכתיבה יישמר כאן עותק של כל מה שעומד להשתנות בכונן. אם התיקון ייכשל, המצב הקודם יוחזר אוטומטית.':
    'Before writing, a copy of everything about to change on the drive will be saved here. If the repair fails, the previous state is restored automatically.',
  'ביצוע התיקון':
    'Repair',
  'מתקן את המחיצה…':
    'Repairing the partition…',
  'תיקון המחיצה לא הושלם':
    'The partition repair did not finish',
  'קובץ ביטול:':
    'Undo file:',
  'החזרת הכונן למצב שלפני תיקון מחיצה או החזרת מחיצה לטבלה':
    'Put the drive back as it was before a partition repair or a partition restore',
  'בכל תיקון התוכנה שומרת קובץ ביטול בתיקיית הגיבוי שבחרתם. שמו מתחיל ב-RAF-undo.':
    'With every repair the program saves an undo file in the backup folder you chose. Its name starts with RAF-undo.',
  'קובץ הביטול':
    'Undo file',
  'לא נבחר קובץ':
    'No file selected',
  'ביטול התיקון':
    'Undo the repair',
  'בודק את הכונן…':
    'Checking the drive…',
  'לא ניתן לבדוק את קובץ הביטול':
    'Couldn\'t check the undo file',
  'הפעולה:':
    'Operation:',
  'מתי:':
    'When:',
  'הכונן:':
    'Drive:',
  'מחזיר את הכונן למצב הקודם…':
    'Putting the drive back to its previous state…',
  'ביטול התיקון לא הושלם':
    'Undoing the repair did not finish',
});

/* ------------------------------------------------------------ imaging.js */
Object.assign(EN, {
  'התמונה נפתחה — בחרו מחיצה מתוכה לסריקה':
    'The image is open — choose a partition in it to scan',
  'לא ניתן לפתוח את תמונת הדיסק':
    'Couldn\'t open the disk image',
  'המחיצה תועתק לקובץ על כונן אחר, וכל הסריקות ירוצו על ההעתק — הכונן המקורי כבר לא ייקרא.':
    'The partition will be copied to a file on another drive, and all scans will run on the copy — the original drive won\'t be read again.',
  'הדיסק כולו יועתק לקובץ על כונן אחר, וכל הסריקות ירוצו על ההעתק — הכונן המקורי כבר לא ייקרא.':
    'The whole disk will be copied to a file on another drive, and all scans will run on the copy — the original drive won\'t be read again.',
  'מעבר 1 — העתקה מהירה.':
    'Pass 1 — fast copy.',
  'מדלגים על אזורים פגומים, ואוספים קודם את מה שנקרא בקלות.':
    'Damaged areas are skipped, and whatever reads easily is collected first.',
  'מעבר 2 — ניסיון חוזר.':
    'Pass 2 — retry.',
  'חוזרים לאזורים שדולגו, וקוראים אותם בחלקים קטנים ככל האפשר.':
    'The skipped areas are revisited and read in the smallest possible pieces.',
  'מעבר 3 — מהכיוון ההפוך.':
    'Pass 3 — the other direction.',
  'מה שעדיין לא נקרא נקרא שוב מהסוף להתחלה — כך מצליחים לפעמים להציל עוד סקטורים בקצה של אזור פגום.':
    'Whatever still wasn\'t read is read again from the end to the start — this sometimes saves more sectors at the edge of a damaged area.',
  'בשמירה אפשר לבחור גם <b>כונן וירטואלי (VHD)</b> — Windows יודע לחבר אותו בלחיצה כפולה, ואז מעתיקים ממנו קבצים בסייר.':
    'When saving you can also choose a <b>virtual disk (VHD)</b> — Windows can attach it with a double-click, and then you copy files from it in File Explorer.',
  'קובץ התמונה':
    'Image file',
  'קריאה בלבד מהכונן המקורי':
    'Read only from the original drive',
  'אזורים שלא ייקראו יתועדו בקובץ מפה לצד התמונה.':
    'Areas that can\'t be read will be recorded in a map file next to the image.',
  'אזור שלא נקרא נשמר בתמונה כאפסים, ואי אפשר להבחין בינו לבין אפסים אמיתיים. המפה מראה בדיוק מה חסר.':
    'An area that couldn\'t be read is saved in the image as zeros, and it can\'t be told apart from real zeros. The map shows exactly what is missing.',
  'התמונה תתפוס {0}. פנויים בכונן היעד {1}.':
    'The image will take {0}. Free on the target drive: {1}.',
  'זה כונן וירטואלי: אחרי ההעתקה אפשר לחבר אותו ב-Windows בלחיצה כפולה.':
    'It\'s a virtual disk: after copying you can attach it in Windows with a double-click.',
  'בנתיב הזה כבר יש תמונה':
    'There is already an image at this path',
  'בנתיב הזה יש תמונה קודמת של אותו מקור':
    'There is an earlier image of the same source at this path',
  'התמונה הושלמה, אבל {0} לא נקראו בה.':
    'The image was completed, but {0} couldn\'t be read in it.',
  'ההעתקה נעצרה לפני הסוף. עוד לא הועתקו: {0}.':
    'The copy stopped before the end. Not copied yet: {0}.',
  'התמונה הקודמת נוצרה מ: {0}. ודאו שזה אותו כונן.':
    'The earlier image was created from: {0}. Make sure it\'s the same drive.',
  'ניסיון חוזר באזורים שלא נקראו':
    'Retry the areas that weren\'t read',
  'רק הם נקראים שוב. לפעמים כונן מצליח לקרוא אזור פגום בניסיון מאוחר יותר.':
    'Only they are read again. Sometimes a drive manages to read a damaged area on a later attempt.',
  'המשך מהנקודה שנעצרה':
    'Continue from where it stopped',
  'רק מה שלא הועתק נקרא מהכונן. מה שכבר בתמונה נשאר כפי שהוא.':
    'Only what wasn\'t copied is read from the drive. What\'s already in the image stays as it is.',
  'התחלה מחדש':
    'Start over',
  'התמונה הקודמת תידרס, והכונן כולו ייקרא שוב.':
    'The earlier image will be overwritten, and the whole drive will be read again.',
  'לנסות שוב גם את {0} שלא נקראו בפעם הקודמת':
    'Also retry the {0} that couldn\'t be read last time',
  'מתחיל…':
    'Starting…',
  'טרם נקרא בהצלחה':
    'Not read successfully yet',
  'מה שהועתק יישמר, וגם תמונה חלקית ניתנת לסריקה.':
    'What was copied is kept, and even a partial image can be scanned.',
  'יוצר תמונה…':
    'Creating the image…',
  'יצירת התמונה נכשלה':
    'Creating the image failed',
  'ניסיון חוזר':
    'Retry',
  'מהכיוון ההפוך':
    'Reverse direction',
  'התמונה נעצרה':
    'The image was stopped',
  'התמונה נוצרה':
    'The image was created',
  'גודל התמונה':
    'Image size',
  'לא נקרא מהכונן':
    'Not read from the drive',
  'אין':
    'None',
  'לא הועתק':
    'Not copied',
  'משך':
    'Duration',
  'תמונה':
    'Image',
  'מפה':
    'Map',
  'לחבר את התמונה ב-Windows':
    'Attaching the image in Windows',
  'לחיצה כפולה על קובץ התמונה בסייר הקבצים מחברת אותו ככונן, והקבצים שבו נפתחים כרגיל — אפשר להעתיק מהם בלי לגעת שוב בכונן המקורי. כשמסיימים: לחיצה ימנית על הכונן בסייר ← הוצאה.':
    'Double-clicking the image file in File Explorer attaches it as a drive, and its files open as usual — you can copy from them without touching the original drive again. When you\'re done: right-click the drive in File Explorer → Eject.',
  'Windows מחבר את התמונה לקריאה ולכתיבה. כדי לשמור אותה כמו שהיא, עדיף להעתיק ממנה ולא לשנות בה דבר.':
    'Windows attaches the image for reading and writing. To keep it as it is, it\'s better to copy from it and not change anything in it.',
  'פתיחת התמונה לסריקה':
    'Open the image for scanning',
});

/* ------------------------------------------------------------ doctor.js */
Object.assign(EN, {
  'הבדיקה לפי מה שיש בתוך הקובץ, לא לפי השם שלו':
    'The check is based on what\'s inside the file, not on its name',
  'אי אפשר לבדוק קבצים באמצע פעולה. נסו שוב כשהיא תסתיים.':
    'Files can\'t be checked in the middle of an operation. Try again when it ends.',
  'הקבצים המקוריים לא משתנים':
    'The original files aren\'t changed',
  'התיקון נכתב לעותק חדש, ונבדק שוב אחרי הכתיבה.':
    'The repair is written to a new copy, and checked again after writing.',
  'הבדיקה משווה בין חתימת הפתיחה של כל קובץ (הבתים הראשונים שמזהים את סוגו), הסיומת שלו, והאורך שמבנה הקובץ מצהיר עליו. כל פער ביניהם הוא בעיה מזוהה.':
    'The check compares each file\'s opening signature (the first bytes that identify its type), its extension, and the length its structure declares. Any mismatch between them is a detected problem.',
  'מה אפשר לתקן':
    'What can be repaired',
  'תחילת קובץ שנמחקה או נפגעה · נתונים מיותרים בסוף הקובץ · סוף קובץ חסר · סיומת שגויה (למשל תמונה שנשמרה בשם ‎.doc) · מסמך Word, Excel או PowerPoint (או ZIP) שלא נפתח — תוכן העניינים שלו נבנה מחדש · מסמך PDF שלא נפתח, או נפתח רק עם אזהרה — טבלת המיקומים שלו נבנית מחדש · סרטון שההקלטה שלו נקטעה ולא נפתח — בעזרת סרטון תקין אחד מאותו מכשיר · הקלטת WAV שנקטעה ומתנגנת ריקה · שיר MP3 שנגנים לא מזהים בגלל נתונים זרים בתחילתו · מסד נתונים SQLite שהכותרת שלו נפגעה.':
    'A file start that was erased or damaged · extra data at the end of the file · a missing file ending · a wrong extension (for example a photo saved as .doc) · a Word, Excel or PowerPoint document (or ZIP) that won\'t open — its table of contents is rebuilt · a PDF that won\'t open, or opens only with a warning — its location table is rebuilt · a video whose recording was cut off and won\'t open — using one working video from the same device · a WAV recording that was cut off and plays empty · an MP3 players don\'t recognize because of foreign data at its start · a SQLite database whose header was damaged.',
  'קובץ שחסרים בו נתונים, או שאינו תואם לשום פורמט מוכר, לא יתוקן — התוכנה לא ממציאה נתונים.':
    'A file with missing data, or one that doesn\'t match any known format, won\'t be repaired — the program doesn\'t invent data.',
  'אפשר גם לגרור קבצים או תיקייה אל החלון.':
    'You can also drag files or a folder into the window.',
  'בודק את הקבצים…':
    'Checking the files…',
  'לא ניתן לבדוק את הקבצים':
    'Couldn\'t check the files',
  'תקין':
    'OK',
  'ניתן לתקן':
    'Can be repaired',
  'צריך תמונה לדוגמה':
    'Needs a sample photo',
  'בחירת תמונה תקינה מאותה מצלמה…':
    'Choose a working photo from the same camera…',
  'תמונה שצריכה תמונה לדוגמה':
    'photo needs a sample photo',
  'תמונות שצריכות תמונה לדוגמה':
    'photos need a sample photo',
  'בונה מחדש את תחילת התמונה…':
    'Rebuilding the beginning of the photo…',
  'בניית התמונה לא הושלמה':
    'Rebuilding the photo didn\'t finish',
  'התמונה תוקנה':
    'The photo was repaired',
  'התמונה תוקנה חלקית':
    'The photo was partially repaired',
  'התמונה לא תוקנה':
    'The photo wasn\'t repaired',
  'התמונה המקורית לא שונתה. אם הצבעים או הבהירות נראים שונים מהרגיל, המצלמה כנראה משנה את טבלאות הדחיסה מתמונה לתמונה — נסו תמונת דוגמה אחרת, רצוי כזו שצולמה סמוך לתמונה הפגומה.':
    'The original photo wasn\'t changed. If the colors or brightness look different from usual, the camera probably changes its compression tables from photo to photo — try another sample photo, preferably one taken close to the damaged photo.',
  'הקובץ המקורי לא שונה. פתחו את הקובץ המתוקן בתוכנת העריכה שלכם כדי לוודא שהוא נפתח.':
    'The original file wasn\'t changed. Open the repaired file in your editing program to make sure it opens.',
  'צריך סרטון לדוגמה':
    'Needs a sample video',
  'לא ניתן לתקן':
    'Can\'t be repaired',
  'זוהה:':
    'Detected:',
  'בחירת סרטון תקין מאותו מכשיר…':
    'Choose a working video from the same device…',
  'תקינים':
    'OK',
  'ניתנים לתיקון':
    'can be repaired',
  'סרטון שצריך סרטון לדוגמה':
    'video needs a sample video',
  'סרטונים שצריכים סרטון לדוגמה':
    'videos need a sample video',
  'לא ניתנים לתיקון':
    'can\'t be repaired',
  'אין קבצים':
    'No files',
  'תיקון':
    'Repair',
  'בחירת קבצים אחרים':
    'Choose other files',
  'מתקן ובודק מחדש…':
    'Repairing and checking again…',
  'התיקון לא הושלם':
    'The repair did not finish',
  'תוקן — תקין':
    'Repaired — OK',
  'תוקן חלקית':
    'Partly repaired',
  'לא תוקן':
    'Not repaired',
  'נכתב אל:':
    'written to:',
  'נכתבו אל:':
    'written to:',
  'כל עותק מתוקן נבדק שוב אחרי הכתיבה, והתוצאה המוצגת היא של הבדיקה החוזרת. הקבצים המקוריים לא שונו.':
    'Every repaired copy is checked again after writing, and the result shown is from that check. The original files were not changed.',
  'בונה אינדקס חדש לסרטון — תמונה אחר תמונה…':
    'Building a new index for the video — frame by frame…',
  'בניית האינדקס לא הושלמה':
    'Building the index did not finish',
  'חזרה לרשימה':
    'Back to the list',
  'הסרטון תוקן':
    'The video was repaired',
  'הסרטון תוקן חלקית':
    'The video was partly repaired',
  'הסרטון לא תוקן':
    'The video was not repaired',
  'הסרטון המקורי לא שונה. אם התמונה בסרטון המתוקן משובשת, כנראה שסרטון הדוגמה צולם בהגדרות אחרות (רזולוציה או קצב תמונות) — נסו סרטון אחר מאותו מכשיר.':
    'The original video was not changed. If the picture in the repaired video is garbled, the sample video was probably recorded with different settings (resolution or frame rate) — try another video from the same device.',
  'פתיחת התיקייה':
    'Open the folder',
});

/* ------------------------------------------------------------ scanning.js */
Object.assign(EN, {
  'שגיאה':
    'Error',
  'הועתק':
    'Copied',
  'אפשר לעצור בכל רגע':
    'You can stop at any moment',
  'עוצר…':
    'Stopping…',
  '/שנייה':
    '/s',
  '· המשך מהנקודה שבה נעצרה':
    '· continuing from where it stopped',
  'קבצים שנמצאו':
    'Files found',
  'נקרא מהדיסק':
    'Read from the disk',
  'השהיה':
    'Pause',
  'עצירת הסריקה':
    'Stop the scan',
  'אפשר להשהות ולהמשיך אחר כך':
    'You can pause and continue later',
  'בהשהיה הסריקה נשמרת עם הנקודה שבה עצרה — אפשר להמשיך עכשיו, או גם אחרי סגירת התוכנה, מ"סריקות אחרונות". בעצירה מוצג מה שנמצא עד אז.':
    'When paused, the scan is saved with the point where it stopped — you can continue now, or even after closing the program, from "Recent scans". Stopping shows what was found so far.',
  'מה שנמצא עד אז יוצג, ואפשר יהיה לשחזר אותו.':
    'Whatever was found so far will be shown, and you can recover it.',
  'משהה…':
    'Pausing…',
  'סורק…':
    'Scanning…',
  'אי אפשר להמשיך את הסריקה כרגע':
    'The scan can\'t be continued right now',
  'הסריקה נכשלה':
    'The scan failed',
  'ניסיון נוסף':
    'Try again',
  'הסריקה עצמה שמורה, עם הנקודה שבה נעצרה — אפשר להמשיך גם אחר כך, מ"סריקות אחרונות".':
    'The scan itself is saved, with the point where it stopped — you can also continue later, from "Recent scans".',
  'אי אפשר להמשיך כרגע':
    'Can\'t continue right now',
  'הכונן נותק באמצע הסריקה':
    'The drive was disconnected during the scan',
  'הסריקה מושהית':
    'The scan is paused',
  'נסרקו {0}% · {1} נמצאו עד כה':
    '{0}% scanned · {1} found so far',
  'הסריקה נשמרה עם הנקודה שבה עצרה':
    'The scan was saved with the point where it stopped',
  'חברו את הכונן שוב ולחצו "המשך הסריקה" — היא תמשיך מאותה נקודה. אפשר גם לסגור את התוכנה ולהמשיך אחר כך, מ"סריקות אחרונות" במסך הכוננים.':
    'Connect the drive again and click "Continue scan" — it will continue from the same point. You can also close the program and continue later, from "Recent scans" on the drives screen.',
  'אפשר להמשיך עכשיו, או לסגור את התוכנה ולהמשיך אחר כך — מ"סריקות אחרונות" במסך הכוננים.':
    'You can continue now, or close the program and continue later — from "Recent scans" on the drives screen.',
  'המשך הסריקה':
    'Continue scan',
  'הצגת מה שנמצא עד כה':
    'Show what was found so far',
  'הכונן נותק — הסריקה נשמרה':
    'The drive was disconnected — the scan was saved',
  'נסרק':
    'Scanned',
  'נמצאו קבצים':
    'Files found',
  'קבצים קיימים (דולג)':
    'Existing files (skipped)',
  'לא ניתן לקריאה':
    'Unreadable',
  'טרם נסרק':
    'Not scanned yet',
  'נבדק':
    'Checked',
  'נמצאה מחיצה':
    'Partition found',
  'טרם נבדק':
    'Not checked yet',
  'ממתין לניסיון חוזר':
    'Waiting for retry',
  'טרם הועתק':
    'Not copied yet',
  'מפת הסקטורים':
    'Sector map',
});

/* ------------------------------------------------------------ recovery.js */
Object.assign(EN, {
  'שחזור קבצים':
    'Recover files',
  'יעד על כונן אחר בלבד':
    'Target on another drive only',
  'שחזור לאותו כונן ידרוס קבצים שעוד לא שוחזרו.':
    'Recovering to the same drive would overwrite files that haven\'t been recovered yet.',
  'קובץ שנמחק עדיין יושב באזור שמסומן "פנוי". כל קובץ חדש שנכתב לאותו כונן עלול לתפוס בדיוק את האזור הזה. התוכנה חוסמת זאת אוטומטית.':
    'A deleted file still sits in an area marked "free". Any new file written to the same drive may take exactly that area. The program blocks this automatically.',
  'תיקיית יעד':
    'Target folder',
  'תיקיית היעד לשחזור':
    'Recovery target folder',
  'שמירה על מבנה התיקיות המקורי':
    'Keep the original folder structure',
  'בודק…':
    'Checking…',
  'תיקיית היעד תקינה':
    'The target folder is OK',
  'ייתכן שאין מספיק מקום פנוי':
    'There may not be enough free space',
  'פנוי: {0}':
    'Free: {0}',
  'משחזר קבצים…':
    'Recovering files…',
  'מתחיל':
    'Starting',
  'השחזור נעצר':
    'The recovery stopped',
  'קבצים שנכשלו':
    'Files that failed',
  'שוחזר חלקית':
    'partly recovered',
  'שוחזרו חלקית':
    'partly recovered',
  'הם הועברו לתיקייה <b>_חלקיים</b>, כדי שיהיה ברור על אילו קבצים לא לסמוך.':
    'They were moved to the <b>_partial</b> folder, so it is clear which files not to rely on.',
  'חלק מהנתונים שלהם כבר נדרס, או שלא ניתן היה לקרוא אותם מהדיסק. ייתכן שלא ייפתחו כראוי.':
    'Part of their data was already overwritten, or couldn\'t be read from the disk. They may not open properly.',
  'נשמר דוח שחזור':
    'A recovery report was saved',
  '{0} בתיקיית היעד — נפתח ב-Excel.':
    '{0} in the target folder — opens in Excel.',
  'שורה לכל קובץ: הנתיב המקורי, לאן נכתב, איכות, תוצאה, וגיבוב SHA-256 של מה שנכתב — כדי לוודא בעתיד שהקובץ לא השתנה.':
    'A row for every file: the original path, where it was written, quality, result, and a SHA-256 hash of what was written — so you can check later that the file has not changed.',
  'השחזור הושלם':
    'Recovery complete',
  'שוחזר בהצלחה':
    'recovered successfully',
  'שוחזרו בהצלחה':
    'recovered successfully',
  '{0} נכתבו אל:':
    '{0} written to:',
  'נשמרה תמונה מוקטנת אחת מתוך תמונות פגומות':
    'One thumbnail was saved from a damaged photo',
  'נשמרו {0} תמונות מוקטנות מתוך תמונות פגומות':
    '{0} thumbnails were saved from damaged photos',
  'בתוך רוב התמונות ממצלמה או מטלפון שמורה גרסה מוקטנת. כשהתמונה עצמה חזרה פגומה, הגרסה המוקטנת נשמרה לצדה — בשם "(תמונה מוקטנת)" — ונבדקה שהיא שלמה.':
    'Most photos from a camera or phone contain a smaller version. When the photo itself came back damaged, the smaller version was saved next to it — named "(thumbnail)" — and checked to be complete.',
  'לא נכתב':
    'not written',
  'לא נכתבו':
    'not written',
  'התוכן שלהם כבר לא קיים על הדיסק.':
    'Their content no longer exists on the disk.',
  'אזור הנתונים שלהם מכיל אפסים בלבד. לא נוצר עבורם קובץ, כדי שלא יתקבלו קבצים ריקים שנראים תקינים.':
    'Their data area contains only zeros. No file was created for them, so you won\'t get empty files that look fine.',
  'נכשל מסיבות אחרות.':
    'failed for other reasons.',
  'נכשלו מסיבות אחרות.':
    'failed for other reasons.',
  'פתיחת תיקיית היעד':
    'Open the target folder',
  'בדיקת הקבצים ששוחזרו':
    'Check the recovered files',
});

/* ------------------------------------------------------------ main.js */
Object.assign(EN, {
  '⁦Recovery Advanced Free⁩ · גרסה {0}':
    '⁦Recovery Advanced Free⁩ · version {0}',
  'הרשאות מנהל':
    'Administrator',
  'ללא הרשאות מנהל':
    'No administrator rights',
  'בעיית תצוגה · חלון':
    'Display problem · window',
  '· ציור':
    '· drawing',
  '· יחס':
    '· ratio',
  'אי-התאמה בין גודל החלון לאזור הציור. פירוט בקובץ RAF-diagnostics.txt בתיקיית הזמניים.':
    'Mismatch between the window size and the drawing area. Details in RAF-diagnostics.txt in the temp folder.',
});

/* ------------------------------------------------------------ a11y.js */
Object.assign(EN, {
  'התקדמות':
    'Progress',
});

/* ------------------------------------------------------------ results.js */
Object.assign(EN, {
  'מחיצות':
    'Partitions',
  'מחוק':
    'deleted',
  'מחוקים':
    'deleted',
  'ריקים':
    'empty',
  'קבצים שאותרו ביומני מערכת הקבצים: שמם ידוע, תוכנם אינו ניתן לאיתור':
    'Files found in the file system journals: their name is known, their content can\'t be located',
  'עדות בלבד':
    'Evidence only',
  'נעצרה':
    'Stopped',
  'רשומות שאותרו ביומני מערכת הקבצים: שמן ידוע, אך תוכנן אינו ניתן לאיתור ולא ניתן לשחזר אותן':
    'Entries found in the file system journals: their name is known, but their content can\'t be located and they can\'t be recovered',
  'הצג {0} רשומות יומן':
    'Show {0} journal entries',
  'שמירת הסריקה לקובץ — כדי לחזור אליה בלי לסרוק שוב':
    'Save the scan to a file — to come back to it without scanning again',
  'שמירת הסריקה':
    'Save scan',
  'חיפוש בשם קובץ…':
    'Search by file name…',
  'חיפוש בשם קובץ':
    'Search by file name',
  'הסריקה נעצרה אחרי {0}% מהמחיצה':
    'The scan stopped after {0}% of the partition',
  'מוצג מה שנמצא עד כה. אפשר להמשיך את הסריקה מאותה נקודה.':
    'Showing what was found so far. You can continue the scan from the same point.',
  'תיקיות — חיצים למעבר, אנטר לפתיחה, רווח לסימון':
    'Folders — arrows to move, Enter to open, Space to mark',
  'סימון כל הקבצים ברשימה, גם אלה שלא נגללו':
    'Mark all the files in the list, including ones you haven\'t scrolled to',
  'הכל':
    'All',
  'אופן התצוגה':
    'View',
  'רשימה':
    'List',
  'גלריה':
    'Gallery',
  'מיון':
    'Sort',
  'מהחדש לישן, לפי חודשים':
    'Newest first, by month',
  'מהישן לחדש, לפי חודשים':
    'Oldest first, by month',
  'לפי שם':
    'By name',
  'מהגדול לקטן':
    'Largest first',
  'לפי איכות':
    'By quality',
  'סינון לפי תאריך':
    'Filter by date',
  'מתאריך':
    'From date',
  'עד':
    'to',
  'עד תאריך':
    'To date',
  'סינון לפי גודל':
    'Filter by size',
  'רק ניתנים לשחזור':
    'Recoverable only',
  'קבצים עם תוכן זהה בדיוק מוצגים פעם אחת — העותק הטוב ביותר. העותקים שמוסתרים גם לא ישוחזרו.':
    'Files with exactly the same content are shown once — the best copy. Hidden copies won\'t be recovered either.',
  'הסתר כפילויות':
    'Hide duplicates',
  'הקבצים — חיצים למעבר, רווח לסימון לשחזור, אנטר לתצוגה מקדימה':
    'Files — arrows to move, Space to mark for recovery, Enter to preview',
  'בחרו קובץ לתצוגה מקדימה':
    'Choose a file to preview',
  'לא נבחרו קבצים':
    'No files selected',
  'שחזור לכונן אחר':
    'Recover to another drive',
  'כל הקבצים':
    'All files',
  'סימון התיקייה וכל מה שבתוכה':
    'Mark the folder and everything in it',
  'שם הקובץ':
    'File name',
  'גודל':
    'Size',
  'שונה':
    'Modified',
  'איכות':
    'Quality',
  'כל התאריכים':
    'All dates',
  '30 הימים האחרונים':
    'Last 30 days',
  'השנה':
    'This year',
  'השנה שעברה':
    'Last year',
  'טווח לבחירה…':
    'Custom range…',
  'כל הגדלים':
    'All sizes',
  'מעל 10KB':
    'Over 10 KB',
  'מעל 100KB':
    'Over 100 KB',
  'מעל 1MB':
    'Over 1 MB',
  'מעל 10MB':
    'Over 10 MB',
  'כל הסוגים':
    'All types',
  'תמונות':
    'Photos',
  'מסמכים':
    'Documents',
  'וידאו':
    'Video',
  'שמע':
    'Audio',
  'ארכיונים':
    'Archives',
  'אחר':
    'Other',
  'אין קבצים להצגה':
    'No files to show',
  'נמצאו {0} תוצאות עבור "{1}".':
    'Found {0} results for "{1}".',
  'קובץ אחד בלי תאריך אינו מוצג בסינון לפי תאריך.':
    '1 file without a date isn\'t shown when filtering by date.',
  '{0} קבצים בלי תאריך אינם מוצגים בסינון לפי תאריך.':
    '{0} files without a date aren\'t shown when filtering by date.',
  'אין קבצים שמתאימים לסינון.':
    'No files match the filter.',
  'לא נמצאו תוצאות לחיפוש.':
    'No results for the search.',
  'הקבצים נמצאים בתיקיות המשנה — בחרו תיקייה בעץ.':
    'The files are in the subfolders — choose a folder in the tree.',
  'התיקייה הזו ריקה.':
    'This folder is empty.',
  'ריק — נמחק':
    'Empty — erased',
  'ריק':
    'empty',
  'נמחק':
    'Deleted',
  'לא ניתן לשחזור':
    'not recoverable',
  'דחוס':
    'Compressed',
  'נדגם תוכן אמיתי מהדיסק':
    'Real content was sampled from the disk',
  'אומת':
    'Verified',
  'נמחק דרך סל המחזור ב-{0}. השם והתיקייה המקוריים הוחזרו מתוך הסל.':
    'Deleted through the Recycle Bin on {0}. The original name and folder were restored from the bin.',
  'מסל המחזור':
    'From Recycle Bin',
  'ב-FAT מחיקה דורסת את האות הראשונה של שם קצר. התוכן שלם, השם חסר אות אחת.':
    'On FAT, deleting overwrites the first letter of a short name. The content is complete; the name is missing one letter.',
  'שם חלקי':
    'Partial name',
  'מיון מהרשימה':
    'Sort from the list',
  'מחפש קבצים כפולים…':
    'Looking for duplicate files…',
  'לא נמצאו קבצים כפולים.':
    'No duplicate files were found.',
  'הוסתרו {0} כפולים ({1}) — מכל קובץ מוצג העותק הטוב ביותר.':
    '{0} duplicates hidden ({1}) — the best copy of each file is shown.',
  'עותק אחד שסומן הוסר מהבחירה.':
    'One marked copy was removed from the selection.',
  '{0} עותקים שסומנו הוסרו מהבחירה.':
    '{0} marked copies were removed from the selection.',
  'לא ניתן לחפש כפילויות':
    'Couldn\'t look for duplicates',
  'הסריקה נשמרה: {0}':
    'Scan saved: {0}',
  'הסריקה לא נשמרה':
    'The scan was not saved',
  'הערה אחת על הסריקה':
    'One note about the scan',
  '{0} הערות על הסריקה':
    '{0} notes about the scan',
  'נקודת ביניים של הסריקה נשמרה':
    'A checkpoint of the scan was saved',
  'הסריקה נשמרה אוטומטית':
    'The scan was saved automatically',
  'הסריקה לא נשמרה אוטומטית':
    'The scan was not saved automatically',
  'הסריקה לא נשמרה אוטומטית:':
    'The scan was not saved automatically:',
  'כדי לחזור אליה בלי לסרוק שוב, שמרו אותה בכפתור השמירה לכונן אחר.':
    'To come back to it without scanning again, save it with the save button to another drive.',
  'נבחר':
    'selected',
  'נבחרו':
    'selected',
  'הכונן שנסרק אינו מחובר':
    'The scanned drive isn\'t connected',
  'קורא…':
    'Reading…',
  'לא ניתן להציג את הקובץ':
    'Couldn\'t show the file',
  'תוכן הקובץ אינו תואם לסיומת שלו. זוהה בפועל:':
    'The file\'s content doesn\'t match its extension. Actually detected:',
  'לא ידוע':
    'Unknown',
  'אין תצוגה מקדימה לסוג קובץ זה':
    'No preview for this file type',
  'התוכן הגולמי (HEX)':
    'Raw content (HEX)',
  'הנגן לא מצליח לנגן את הקובץ. ייתכן שהוא פגום, או שהוא בפורמט שהנגן המובנה אינו מכיר (למשל חלק מקובצי MKV ו-MOV). אפשר לשחזר אותו ולנסות לפתוח אותו בנגן אחר.':
    'The player can\'t play the file. It may be damaged, or in a format the built-in player doesn\'t know (for example some MKV and MOV files). You can recover it and try opening it in another player.',
});

/* ------------------------------------------ תוויות קצרות שמגיעות מהמנוע */
Object.assign(EN, {
  'דיסק קשיח מגנטי': 'Magnetic hard disk',
  'כונן SSD': 'SSD drive',
  'כונן NVMe SSD': 'NVMe SSD drive',
  'התקן USB נייד': 'Portable USB device',
  'כרטיס זיכרון': 'Memory card',
  'כונן אופטי': 'Optical drive',
  'דיסק וירטואלי': 'Virtual disk',
  'כונן רשת או זיכרון': 'Network or RAM drive',
  'תמונת דיסק': 'Disk image',
  'סוג לא ידוע': 'Unknown type',
  'לא מזוהה': 'Unrecognized',
  'ללא טבלת מחיצות': 'No partition table',
  'לא נקרא': 'Not read',
  'TRIM לא ידוע': 'TRIM unknown',
  'קובץ תמונה': 'Image file',
  'דרך Windows': 'Through Windows',
  'מערך RAID': 'RAID array',
  'חלק ממערך RAID': 'Part of a RAID array',
  'מאגר לוגי': 'Logical volume pool',
  'נתונים בסיסיים': 'Basic data',
  'מחיצת מערכת EFI': 'EFI system partition',
  'שחזור Windows': 'Windows recovery',
  'נתוני Linux': 'Linux data',
  'מחיצה מוסתרת': 'Hidden partition',
  'וירטואלי': 'Virtual',
  'מצוין': 'Excellent',
  'טוב': 'Good',
  'פגום חלקית': 'Partly damaged',
  'טבלת הקבצים': 'File table',
  'שריד בטבלת הקבצים': 'File table remnant',
  'יומן שינויים': 'Change journal',
  'יומן מערכת הקבצים': 'File system log',
  'זיהוי לפי תוכן': 'Found by content',
  'ללא תאריך': 'No date',
  // תיקיות לפי סוג בסריקה מתקדמת (CarvedMetadata.cs)
  'שמע M4A': 'M4A audio', 'שמע Windows Media': 'Windows Media audio', 'וידאו OGG': 'OGG video',
  'מסמך Word': 'Word document', 'גיליון Excel': 'Excel spreadsheet', 'מצגת PowerPoint': 'PowerPoint presentation',
  'שרטוט Visio': 'Visio drawing', 'מסמך OpenDocument': 'OpenDocument document',
  'גיליון OpenDocument': 'OpenDocument spreadsheet', 'מצגת OpenDocument': 'OpenDocument presentation',
  'ספר אלקטרוני': 'E-book', 'אפליקציית אנדרואיד': 'Android app', 'ארכיון Java': 'Java archive',
  'מסמך Word ישן': 'Old Word document', 'גיליון Excel ישן': 'Old Excel spreadsheet',
  'מצגת PowerPoint ישנה': 'Old PowerPoint presentation', 'הודעת Outlook': 'Outlook message',
  'ארכיון 7-Zip': '7-Zip archive',
  'ארכיון GZIP': 'GZIP archive',
  'ארכיון RAR': 'RAR archive',
  'הקלטת קול AMR': 'AMR voice recording',
  'וידאו 3GP': '3GP video',
  'וידאו AVI': 'AVI video',
  'וידאו MP4': 'MP4 video',
  'וידאו MPEG / DVD': 'MPEG / DVD video',
  'וידאו MPEG-TS': 'MPEG-TS video',
  'וידאו Matroska': 'Matroska video',
  'וידאו QuickTime': 'QuickTime video',
  'וידאו Windows Media': 'Windows Media video',
  'וידאו ממצלמת וידאו (AVCHD)': 'Camcorder video (AVCHD)',
  'מסד נתונים SQLite': 'SQLite database',
  'מסמך Office או ארכיון ZIP': 'Office document or ZIP archive',
  'מסמך Office ישן': 'Old Office document',
  'מסמך PDF': 'PDF document',
  'מסמך RTF': 'RTF document',
  'סמל ICO': 'ICO icon',
  'קובץ הרצה של Windows': 'Windows executable',
  'שמע FLAC': 'FLAC audio',
  'שמע MP3': 'MP3 audio',
  'שמע OGG': 'OGG audio',
  'שמע WAV': 'WAV audio',
  'תמונת AVIF': 'AVIF image',
  'תמונת BMP': 'BMP image',
  'תמונת GIF': 'GIF image',
  'תמונת HEIC': 'HEIC image',
  'תמונת HEIF': 'HEIF image',
  'תמונת JPEG': 'JPEG image',
  'תמונת PNG': 'PNG image',
  'תמונת Photoshop': 'Photoshop image',
  'תמונת RAW של Canon': 'Canon RAW image',
  'תמונת RAW של Olympus': 'Olympus RAW image',
  'תמונת RAW של Panasonic': 'Panasonic RAW image',
  'תמונת TIFF': 'TIFF image',
  'תמונת WebP': 'WebP image',
  'NTFS': 'NTFS',
  'exFAT': 'exFAT',
  'FAT32': 'FAT32',
  'FAT16': 'FAT16',
  'FAT12': 'FAT12',
  'ReFS': 'ReFS',
  'ext2/3/4': 'ext2/3/4',
  'APFS': 'APFS',
  'HFS+': 'HFS+',
  'BitLocker': 'BitLocker',
  'GPT': 'GPT',
  'MBR': 'MBR',
  'NVMe': 'NVMe',
  'USB': 'USB',
  'SATA': 'SATA',
  'SAS': 'SAS',
  'SCSI': 'SCSI',
  'RAID': 'RAID',
  'SD': 'SD',
  'HDD': 'HDD',
  'SSD': 'SSD',
  'IMG': 'IMG',
  'VHD': 'VHD',
  'ODD': 'ODD',
});

/* --------------------------------------------------- שאלות נפוצות */
const EN_FAQ = [
  ['What should I avoid doing now?', `
    <ul>
      <li><b>Don't save anything</b> to the drive you are recovering from — no files, no programs. Every new file may overwrite files that can still be saved.</li>
      <li><b>Don't format it</b>, even if Windows asks you to.</li>
      <li><b>Don't run "Error checking"</b> (chkdsk) — it "repairs" by deleting whatever doesn't fit.</li>
      <li><b>Always recover to a different drive</b> — the program won't let you recover to the same drive.</li>
    </ul>`],
  ['Which scan should I start with?', `
    <ol>
      <li><b>Quick scan</b> — seconds to minutes. Finds recently deleted files, with their names and folders.</li>
      <li>Didn't find it? <b>Deep scan</b> — takes longer and also finds older files. Most names are kept.</li>
      <li>After formatting or heavy damage: <b>Advanced scan</b> — looks for files by their content, across the whole drive. It finds the most, but without names and folders.</li>
    </ol>`],
  ['Windows says the drive needs to be formatted. What do I do?', `
    Click <b>Cancel</b>. In most cases the files are still there — only the start of the partition was damaged.
    In the drive list, click the partition: the program checks what happened and suggests the safe way — first copying the files with their names, without writing to the drive,
    and only then, if you want, repairing the partition itself.`],
  ['Why don\'t files from the advanced scan have names?', `
    A file's name and folder are kept in the file system's table, not in the file itself. Formatting erases that table,
    and the advanced scan finds files by their content — so they get a number instead of a name, and are sorted into folders by type.
    The gallery view is a good way to go through them.`],
  ['A recovered file doesn\'t open, or opens partially. Why?', `
    Probably part of the space the file used has already been overwritten by another file. What you can do:
    <ul>
      <li>In the drive list: <b>Repair files that won't open</b> — drag the files into the window.</li>
      <li>A video that won't play can be repaired with a working video recorded on the same device.</li>
      <li>A damaged photo sometimes contains a small complete version of itself, saved next to the file.</li>
    </ul>`],
  ['What does the quality next to each file mean?', `
    <ul>
      <li><b>Excellent</b> — the space the file used is still free. It should come back in full.</li>
      <li><b>Good</b> — a small part may have been overwritten.</li>
      <li><b>Partly damaged</b> — a large part was overwritten. The file will come back, but probably damaged.</li>
      <li><b>Not recoverable</b> — the file is known to have existed, but there is no information left about where its content was.</li>
    </ul>`],
  ['Why are deleted files almost never found on an SSD?', `
    Most SSDs erase the content of deleted files by themselves, shortly after deletion (the feature is called TRIM).
    Once that has happened, no program can bring them back. Such a drive shows the chip "TRIM on".
    On USB sticks, memory cards and regular hard disks this usually doesn't happen.`],
  ['The drive is slow, makes noises or gets stuck', `
    That's a sign the drive is failing, and every extra read may make it worse. Instead of scanning it again and again:
    click <b>Create disk image</b> — the program copies the drive once to a file on another drive, easy parts first,
    and then you can scan the copy as much as you like. If the drive reports its health, it is shown next to its name.`],
  ['Can I stop a scan and continue later?', `
    Yes. The advanced scan has a <b>Pause</b> button, and you can continue even after closing the program — or if the drive was disconnected midway.
    Every scan is saved automatically and appears under <b>Recent scans</b> on the drives screen.`],
  ['Can I recover from my phone?', `
    From the phone's internal memory — no. Phones don't let a computer read their memory like a drive.
    If the phone has a <b>memory card</b>, take it out and connect it to the computer with a card reader — that can be scanned.`],
  ['The drive is locked with BitLocker', `
    Click the partition in the list and type the recovery key (48 digits) or the password. The program decrypts the drive itself,
    and it appears in the list as another drive — even when Windows can't unlock it, even a deleted partition or a disk image.
    If it is already unlocked in Windows, choose <b>Open for scanning</b>. Without the password or key there is no way to read the files.`],
  ['I have a Windows backup or a virtual machine (a VHD, VHDX or VMDK file)', `
    On the drives screen click <b>Open disk image</b> and choose the file. It opens as another drive in the list —
    without attaching it to Windows and without writing to it — and you can scan and recover from it like any drive.
    <ul>
      <li><b>VirtualBox and VMware</b> — choose the main VMDK file (without -s001 or -flat in its name).
      When the disk is split into several files, they all need to be in the same folder.</li>
      <li>For a machine with a snapshot — open the original disk, not the changes file.</li>
    </ul>`],
  ['I got a disk image from a lab or a technician (an E01 file)', `
    That's the format of forensic tools such as FTK Imager and EnCase. On the drives screen click <b>Open disk image</b> and choose the E01 file.
    When the image is split into several files (E01, E02, E03…), they all need to be in the same folder — the program opens them together.`],
  ['Keyboard shortcuts', `
    On the file selection screen:
    <ul>
      <li><b>Arrows</b> — move between files (in the gallery also left and right). <b>Page Up / Page Down</b>, <b>Home / End</b> — jump.</li>
      <li><b>Enter</b> — preview the file.</li>
      <li><b>Space</b> — mark the file for recovery, or unmark it.</li>
      <li><b>Ctrl + A</b> — mark all the files in the list; press again to unmark.</li>
      <li><b>Ctrl + F</b> — search by name. <b>Esc</b> clears the search.</li>
    </ul>
    And everywhere: <b>F1</b> — these questions.`],
  ['Does the program write anything to the drive?', `
    No, except for two operations you ask for explicitly: <b>repairing the partition</b> and <b>restoring</b> a deleted partition to the table.
    Before each of them the program backs up what it changes, and puts it back automatically if something fails.
    Scanning, recovery and creating a disk image only read from the drive.`],
];
