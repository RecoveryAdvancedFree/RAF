using System.Text.Json.Nodes;
using RAF.Core.Signatures;

namespace RAF.App;

/// <summary>סוגי קבצים שהמשתמש מלמד את התוכנה, לסריקה המתקדמת (ראו CustomSignatures).</summary>
internal sealed partial class Bridge
{
    /// <summary>הסוג שנלמד אחרון — נשמר רק כשהמשתמש מאשר ונותן לו שם.</summary>
    private CustomType? _learned;

    /// <summary>טעינת הסוגים השמורים לסורק. נקרא פעם אחת, בפתיחת התוכנה.</summary>
    internal static void LoadCustomTypes()
        => CustomSignatures.Activate(CustomSignatures.Load(CustomSignatures.DefaultPath));

    private static object ListCustomTypes()
        => CustomSignatures.Load(CustomSignatures.DefaultPath).Select(t => new
        {
            name = t.Name,
            extension = t.Extension,
            samples = t.Samples,
            exactLength = t.Footer is not null,
        }).ToList();

    /// <summary>בחירת קבצים לדוגמה ולימוד מהם. שום דבר לא נשמר עד האישור.</summary>
    private object LearnCustomType(JsonObject? p)
    {
        // קבצים שכבר נבחרו (למשל בגרירה אל החלון) — בלי חלון בחירה.
        string[] selected = p?["paths"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        if (selected.Length == 0) _form.InvokeOnUiSync(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = L.T("בחרו כמה קבצים תקינים מאותו סוג — שלושה ומעלה"),
                Filter = L.T("כל הקבצים (*.*)|*.*"),
                Multiselect = true,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(_form) == DialogResult.OK) selected = dialog.FileNames;
        });

        if (selected.Length == 0) return new { picked = false };

        var result = CustomSignatures.Learn(selected);
        _learned = result.Type;
        return new
        {
            picked = true,
            ok = result.Ok,
            message = result.Message,
            name = result.Type?.Name,
            extension = result.Type?.Extension,
            files = selected.Length,
        };
    }

    private object SaveCustomType(JsonObject? p)
    {
        var learned = _learned ?? throw new InvalidOperationException(L.T("לא נלמד סוג חדש. בחרו קבצים לדוגמה קודם."));

        string name = (p?["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) name = learned.Name;
        if (name.Length > 60) name = name[..60];

        // סוג עם אותה סיומת מוחלף — לימוד חוזר מדוגמאות טובות יותר מעדכן אותו.
        var types = CustomSignatures.Load(CustomSignatures.DefaultPath)
            .Where(t => !t.Extension.Equals(learned.Extension, StringComparison.OrdinalIgnoreCase))
            .Append(learned with { Name = name })
            .ToList();

        CustomSignatures.Save(CustomSignatures.DefaultPath, types);
        CustomSignatures.Activate(types);
        _learned = null;
        return ListCustomTypes();
    }

    private static object RemoveCustomType(JsonObject? p)
    {
        string extension = p?["extension"]?.GetValue<string>() ?? "";
        var types = CustomSignatures.Load(CustomSignatures.DefaultPath)
            .Where(t => !t.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CustomSignatures.Save(CustomSignatures.DefaultPath, types);
        CustomSignatures.Activate(types);
        return ListCustomTypes();
    }
}
