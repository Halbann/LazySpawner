using KSP.Localization;
using System;
using System.Globalization;
using static LazySpawner.Localisation;

namespace LazySpawner;

public interface ITextField
{
    string Text { get; set; }
    bool Valid { get; }
    string Title { get; }
    string Tooltip { get; }
}

// Text typed into a box, parsed into a value, and persisted as a setting. Its title is Field_<key> in the
// localisation, and its tooltip Field_<key>_Tooltip, if there is one. Numbers parse by default.
public class TextField<T> : ITextField, ISetting
{
    private readonly string key;
    private readonly Func<T, bool> validator;
    private readonly Func<string, T> parser;
    public T value;
    private string text;

    public string Text
    {
        get => text;
        set
        {
            text = value ?? "";
            Refresh();
        }
    }

    public bool Valid { get; private set; } = true;
    public string Title => Loc($"Field_{key}");
    public string Tooltip => Localizer.TryGetStringByTag($"#LOC_{Meta.name}_Field_{key}_Tooltip", out string tooltip) ? tooltip : null;

    public TextField(string key, string text, Func<T, bool> validator = null, Func<string, T> parser = null)
    {
        this.key = key;
        this.validator = validator;
        this.parser = parser ?? Parse;
        Text = text;
    }

    public static implicit operator T(TextField<T> field) => field.value;

    public static T Parse(string s) => (T)Convert.ChangeType(s, typeof(T), CultureInfo.InvariantCulture);

    public void Refresh()
    {
        try
        {
            value = parser(text);
            Valid = value != null && (validator == null || validator(value));
        }
        catch
        {
            value = default;
            Valid = false;
        }
    }
}
