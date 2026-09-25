namespace RAF.Core.Text;

/// <summary>
/// תווים שאסור שיהיו בשם קובץ — הרשימה של Windows, בכל מערכת. Path.GetInvalidFileNameChars
/// במק מחזירה רק "/", אבל קבצים משוחזרים נשמרים הרבה פעמים לדיסק-און-קי או לכונן של
/// Windows, ושם ": * ? \" < > |" אסורים. שם שמתאים לכולם עובר לכל מקום.
/// </summary>
public static class FileNames
{
    public static readonly char[] Invalid =
        [.. Enumerable.Range(0, 32).Select(i => (char)i), '"', '<', '>', '|', ':', '*', '?', '\\', '/'];
}
