using System;
using System.Globalization;
using UnityEngine;

namespace LazySpawner;

public interface ITextField
{
    string Text { get; set; }
    bool Valid { get; }
    void Draw(ref bool ready);
}

// A labelled text box that parses its contents into a value, turns red when
// the contents don't parse, and persists its text as a setting.
public class TextField<T> : ITextField, ISetting
{
    public string title;
    public string tooltip;
    public T value;

    public Func<string, T> parser;
    public Func<T, bool> validator;

    private readonly string defaultText;
    private string text;
    private string last;

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

    public TextField(string title, string text, Func<string, T> parser, Func<T, bool> validator = null, string tooltip = null)
    {
        this.title = title;
        this.tooltip = tooltip;
        this.parser = parser;
        this.validator = validator;
        defaultText = text;
        Text = text;
    }

    public static implicit operator T(TextField<T> field) => field.value;

    public void Refresh()
    {
        last = text;

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

    public void Draw(ref bool ready)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(title + ": ", tooltip), GUILayout.Width(IMGUI.LabelWidth));

        Color previous = GUI.color;
        if (!Valid)
            GUI.color = Color.red;

        text = GUILayout.TextField(text);

        GUI.color = previous;

        if (text != last)
            Refresh();

        ready = ready && Valid;

        GUILayout.EndHorizontal();
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
