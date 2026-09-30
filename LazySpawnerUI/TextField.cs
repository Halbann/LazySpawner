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
// localisation, and its tooltip Field_<key>_Tooltip, if there is one.
public class TextField<T> : ITextField, ISetting
{
    private readonly string key;
    public T value;

    public Func<string, T> parser;
    public Func<T, bool> validator;

    private readonly string defaultText;
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

    public TextField(string key, string text, Func<string, T> parser, Func<T, bool> validator = null)
    {
        this.key = key;
        this.parser = parser;
        this.validator = validator;
        defaultText = text;
        Text = text;
    }

    public static implicit operator T(TextField<T> field) => field.value;

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

    #region Parsers

    public static float ParseFloat(string s) =>
        float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static int ParseInt(string s) =>
        int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    #endregion

    #region ISetting

    Type ISetting.ValueType => typeof(string);

    object ISetting.BoxedValue
    {
        get => text;
        set => Text = (string)value;
    }

    Delegate ISetting.ChangedCallback => null;

    bool ISetting.Apply(bool lazy, bool silentChange) => false;

    void ISetting.Reset() { }

    void ISetting.Revert() => Text = defaultText;

    #endregion
}
