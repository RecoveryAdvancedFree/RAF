; ============================================================================
;  קובץ ההתקנה של "שחזור מתקדם חינם" (Inno Setup 6)
;
;  בנייה: installer\build-installer.ps1 — בונה את RAF.exe ומפיק dist\RAF-Setup.exe.
;
;  הקובץ חייב להישמר כ-UTF-8 עם BOM: בלי BOM, Inno קורא אותו בקידוד המערכת
;  וכל הטקסט העברי באשף משתבש. סקריפט הבנייה מוודא זאת.
;
;  התקנה שקטה (לעדכון מתוך התוכנה):
;    RAF-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH
;  ‏/RELAUNCH פותח את התוכנה מחדש בסיום — ההתקנה סוגרת אותה כדי להחליף את הקובץ.
;  (אין להתחיל שורה בקובץ הזה בתו סולמית, גם לא בהערה — המהדר קורא אותה כהוראה.)
; ============================================================================

#define AppName "שחזור מתקדם חינם"
#define AppExe "RAF.exe"

; הגרסה מגיעה מסקריפט הבנייה, כדי שתתאים תמיד לקובץ התוכנה.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; מזהה קבוע: בזכותו גרסה חדשה מעדכנת את ההתקנה הקיימת ולא נוספת כרשומה שנייה
; ב"אפליקציות ותכונות". אסור לשנות אותו.
AppId={{5B7E2F1A-9C43-4E8D-A6B1-RAF000000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=RecoveryAdvancedFree
AppPublisherURL=https://github.com/RecoveryAdvancedFree/RAF
VersionInfoVersion={#AppVersion}

; התוכנה ניגשת לכוננים ישירות ולכן רצה ממילא כמנהל.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; תיקיית ההתקנה ניתנת לבחירה — ראו האזהרה שמתווספת לדף הזה ב-InitializeWizard.
DefaultDirName={autopf}\RAF
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

OutputDir=..\dist
OutputBaseFilename=RAF-Setup
SetupIconFile=..\src\RAF.App\app.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; תוכנה פתוחה נסגרת במקום שההתקנה תיכשל על קובץ נעול. בעדכון מתוך התוכנה
; היא נסגרת בעצמה לפני שההתקנה מתחילה; זו רשת ביטחון להתקנה ידנית.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Tasks]
Name: "desktop";   Description: "שולחן העבודה";  GroupDescription: "היכן להוסיף קיצורי דרך:"
Name: "startmenu"; Description: "תפריט התחל";   GroupDescription: "היכן להוסיף קיצורי דרך:"
Name: "taskbar";   Description: "שורת המשימות"; GroupDescription: "היכן להוסיף קיצורי דרך:"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; ההצמדה לשורת המשימות. חלונות כבר לא מציעה דרך רשמית לכך (פקודת "הצמד" בוטלה, וממשק
; ה-COM הלא מתועד לא עושה כלום מאז Windows 10 21H2), והדרך היחידה שעובדת היא כתיבת רשימת
; הפריטים המוצמדים ישירות — הסקריפט של Freenitial (Pin-Taskbar). הרישיון שלו מתיר שימוש
; לא מסחרי בתנאי שהוא מצורף, ו-RAF חינמית; לכן הוא מותקן יחד עם הסקריפט.
; הוא נשאר בתיקייה גם לצורך ביטול ההצמדה בהסרה.
Source: "PinTaskbar\Pin-Taskbar.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "PinTaskbar\LICENCE";         DestDir: "{app}\tools"; DestName: "Pin-Taskbar-LICENCE.txt"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktop
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenu
; קיצור הדרך שמוצמד לשורת המשימות: הוא נותן לפריט המוצמד את השם והסמל הנכונים.
; ההצמדה מעתיקה אותו לתיקיית הפריטים המוצמדים, אבל הוא נשאר כאן כדי שיהיה מה להצמיד
; שוב בהתקנה חוזרת.
Name: "{app}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: taskbar

[Run]
; ההצמדה לא רצה בהתקנה שקטה, כלומר בעדכון מתוך התוכנה: מי שהסיר את הפריט משורת
; המשימות לא אמור למצוא אותו שם שוב אחרי כל עדכון.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Pin-Taskbar.ps1"" ""{app}\{#AppName}.lnk"" -Silent"; Flags: runhidden skipifsilent; Tasks: taskbar
Filename: "{app}\{#AppExe}"; Description: "פתח כעת"; Flags: postinstall nowait skipifsilent
; עדכון מתוך התוכנה: ההתקנה סגרה אותה, והיא נפתחת מחדש בסיום.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RelaunchRequested

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Pin-Taskbar.ps1"" -Unpin ""{app}\{#AppExe}"" -Silent"; Flags: runhidden; RunOnceId: "UnpinTaskbar"

[UninstallDelete]
Type: files; Name: "{app}\{#AppName}.lnk"

[Code]
{ הפרמטר /RELAUNCH מגיע מהעדכון שבתוך התוכנה (ראו ההערה בראש הקובץ). }
function RelaunchRequested(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/RELAUNCH') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ האזהרה בדף בחירת התיקייה. התקנה כותבת קבצים לכונן, וכל כתיבה עלולה לדרוס בדיוק את
  השטח שבו שוכבים הקבצים שנמחקו. }
procedure InitializeWizard();
var
  warn: TNewStaticText;
begin
  warn := TNewStaticText.Create(WizardForm);
  warn.Parent := WizardForm.SelectDirPage;
  warn.Left := 0;
  warn.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(16);
  warn.Width := WizardForm.SelectDirPage.Width;
  warn.AutoSize := False;
  warn.WordWrap := True;
  warn.Height := ScaleY(70);
  warn.Font.Style := [fsBold];
  warn.Font.Color := $000080C0;
  warn.Caption := 'חשוב: אין להתקין בכונן שממנו רוצים לשחזר קבצים. ' +
                  'ההתקנה כותבת לכונן, ועלולה לדרוס את הקבצים שנמחקו ולמנוע את שחזורם. ' +
                  'אם הקבצים נמחקו מכונן C, בחרו תיקייה בכונן אחר (למשל דיסק-און-קי).';
end;
