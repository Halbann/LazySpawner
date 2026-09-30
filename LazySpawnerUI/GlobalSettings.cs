// Reflection-based settings system, ported from LazyPainter (itself from Rescored).
// The one change is that any static field implementing ISetting is picked up,
// not just Setting<T>, so the UI's TextFields can persist themselves too.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

using UnityEngine;

namespace LazySpawner;

public interface ISetting
{
    Type ValueType { get; }
    object BoxedValue { get; set; }
    Delegate ChangedCallback { get; }
    bool Apply(bool lazy = true, bool silentChange = false);
    void Reset();
    void Revert();
}

public class Setting<T> : ISetting
{
    public readonly T defaultValue;
    public event Action<T> OnApply;
    public event Action OnChanged;
    private T _value;

    public T Pending { get; set; }

    public T Value
    {
        get => _value;
        set
        {
            Pending = _value = value;
            Apply();
        }
    }

    public object BoxedValue
    {
        get => _value;
        set
        {
            _value = Pending = (T)value;
        }
    }

    public Type ValueType => typeof(T);

    public Delegate ChangedCallback => OnChanged;

    public Setting(T value, Action<T> onApply = null, Action onChanged = null)
    {
        _value = defaultValue = Pending = value;
        if (onApply != null) OnApply += onApply;
        if (onChanged != null) OnChanged += onChanged;
    }

    public static implicit operator T(Setting<T> setting) => setting._value;
    public static implicit operator Setting<T>(T value) => new(value);

    public void Reset() =>
        Pending = _value;

    public bool Apply(bool lazy = true, bool silentChange = false)
    {
        bool same = EqualityComparer<T>.Default.Equals(_value, Pending);
        if (lazy && same)
            return false;

        _value = Pending;
        OnApply?.Invoke(_value);

        if (!silentChange)
            OnChanged?.Invoke();

        return !same;
    }

    public void Revert() =>
        _value = Pending = defaultValue;
}

[AttributeUsage(AttributeTargets.Class)]
public class Settings : Attribute
{
    public string category = "Misc";
    public string displayName = "";
    public bool visible = true;
}

[KSPAddon(KSPAddon.Startup.Instantly, true)]
internal class GlobalSettings : MonoBehaviour
{
    private static string PluginData =>
        Path.Combine(KSPUtil.ApplicationRootPath, "GameData", Meta.name, "PluginData");

    private static string Config =>
        Path.Combine(PluginData, "settings.cfg");

    internal static int settingsVersion = 1;

    private struct CategoryInfo
    {
        public string name;
        public string displayName;
        public Dictionary<string, SettingInfo> settings;
    }

    private struct SettingInfo
    {
        public string name;
        public ISetting setting;
    }

    private static readonly Dictionary<string, CategoryInfo> categories = new Dictionary<string, CategoryInfo>();
    private static bool locatedFields = false;

    protected void Start()
    {
        Load();
        ApplyAll(lazy: false);
    }

    // After a hot reload the new copy of every setting is back at its default.
    private static void OnHotLoad()
    {
        Load();
        ApplyAll(lazy: false);
    }

    private static void Reflect()
    {
        locatedFields = true;
        categories.Clear();
        var assembly = Assembly.GetExecutingAssembly();
        Settings attribute;

        foreach (Type type in assembly.GetTypes())
        {
            attribute = (Settings)type.GetCustomAttribute(typeof(Settings), false);
            if (attribute != null)
            {
                // Create category info for this category name if it doesn't exist yet.
                if (!categories.TryGetValue(attribute.category, out CategoryInfo categoryInfo))
                {
                    categoryInfo = new CategoryInfo()
                    {
                        name = attribute.category,
                        displayName = attribute.displayName,
                        settings = new Dictionary<string, SettingInfo>(),
                    };

                    categories.Add(attribute.category, categoryInfo);
                }

                // Fallback display name.
                if (categoryInfo.displayName == "")
                    categoryInfo.displayName = attribute.displayName;

                // Add static ISetting fields to relevant category info.
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!typeof(ISetting).IsAssignableFrom(field.FieldType))
                        continue;

                    if (field.GetValue(null) is not ISetting setting)
                        continue;

                    var settingInfo = new SettingInfo { name = field.Name, setting = setting };
                    categoryInfo.settings.Add(field.Name, settingInfo);
                }
            }
        }
    }

    internal static void Save()
    {
        if (!locatedFields)
            Reflect();

        try
        {
            if (!Directory.Exists(PluginData))
                Directory.CreateDirectory(PluginData);

            ConfigNode settingsNode = new ConfigNode(nameof(GlobalSettings));
            settingsNode.AddValue("version", settingsVersion);
            ConfigNode categoryNode;

            foreach (CategoryInfo category in categories.Values)
            {
                categoryNode = new ConfigNode(category.name);

                foreach (SettingInfo settingInfo in category.settings.Values)
                    categoryNode.AddValue(settingInfo.name, FormatValue(settingInfo.setting.BoxedValue));

                settingsNode.AddNode(categoryNode);
            }

            settingsNode.Save(Config);
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to save settings: {e}");
        }
    }

    // Invariant culture, so a German locale doesn't save "0,5" and fail to
    // parse it back.
    private static string FormatValue(object value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? "";

    internal static void Load()
    {
        if (!File.Exists(Config))
            return;

        if (!locatedFields)
            Reflect();

        ConfigNode file = ConfigNode.Load(Config);
        if (file == null)
            return;

        foreach (CategoryInfo category in categories.Values)
        {
            ConfigNode categoryNode = file.GetNode(category.name);
            if (categoryNode == null)
                continue;

            foreach (SettingInfo info in category.settings.Values)
                if (TryGetValue(categoryNode, info, out object value))
                    info.setting.BoxedValue = value;
        }
    }

    private static bool TryGetValue(ConfigNode categoryNode, SettingInfo settingInfo, out object value)
    {
        Type type = settingInfo.setting.ValueType;
        bool success;
        value = null;

        if (type.IsEnum)
        {
            Enum output = null;
            success = categoryNode.TryGetEnum(settingInfo.name, type, ref output);
            value = output;
        }
        else
        {
            // TryGetValue has a million overloads and I can't be bothered writing a huge switch statement.

            object[] parameters = new object[] { settingInfo.name, null };
            MethodInfo method = typeof(ConfigNode).GetMethod("TryGetValue", new Type[] { typeof(string), type.MakeByRefType() });
            if (method == null)
                return false;

            // This is a ref, so the result is put in the second param.
            success = (bool)method.Invoke(categoryNode, parameters);
            value = parameters[1];
        }

        return success;
    }

    public static void ResetAll()
    {
        foreach (var category in categories)
            foreach (var info in category.Value.settings.Values)
                info.setting.Reset();
    }

    public static void ApplyAll(bool lazy = true)
    {
        // Apply all settings. Collate and deduplicate onChange callbacks so identical callbacks are called only once.

        HashSet<Delegate> callbacks = new HashSet<Delegate>();

        foreach (var category in categories)
        {
            foreach (var info in category.Value.settings.Values)
            {
                bool changed = info.setting.Apply(lazy, true);
                if (changed && info.setting.ChangedCallback != null)
                    callbacks.Add(info.setting.ChangedCallback);
            }
        }

        foreach (Delegate callback in callbacks)
            callback.DynamicInvoke(null);
    }
}
