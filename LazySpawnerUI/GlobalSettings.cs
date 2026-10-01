// Settings saved in settings.cfg: every public static ISetting field of a class marked [Settings], in a node
// for its category. After LazyPainter's (itself from Rescored's), cut down to what this mod uses.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace LazySpawner;

// A setting as it's saved.
public interface ISetting
{
    string Text { get; set; }
}

public class Setting<T> : ISetting
{
    public T Value;

    public Setting(T value) => Value = value;

    public static implicit operator T(Setting<T> setting) => setting.Value;
    public static implicit operator Setting<T>(T value) => new(value);

    // Invariant culture, so a German locale doesn't save "0,5" and fail to parse it back.
    public string Text
    {
        get => Convert.ToString(Value, CultureInfo.InvariantCulture);
        set => Value = typeof(T).IsEnum ? (T)Enum.Parse(typeof(T), value) : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}

[AttributeUsage(AttributeTargets.Class)]
public class Settings : Attribute
{
    public string category;
}

[KSPAddon(KSPAddon.Startup.Instantly, true)]
internal class GlobalSettings : MonoBehaviour
{
    private static string Config => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", Meta.name, "PluginData", "settings.cfg");

    private static IEnumerable<(string category, string name, ISetting setting)> All() =>
        from type in Assembly.GetExecutingAssembly().GetTypes()
        let attribute = type.GetCustomAttribute<Settings>()
        where attribute != null
        from field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
        let setting = field.GetValue(null) as ISetting
        where setting != null
        select (attribute.category, field.Name, setting);

    protected void Start() => Load();

    // After a hot reload the new copy of every setting is back at its default.
    private static void OnHotLoad() => Load();

    internal static void Save()
    {
        try
        {
            ConfigNode file = new ConfigNode();
            foreach ((string category, string name, ISetting setting) in All())
                (file.GetNode(category) ?? file.AddNode(category)).AddValue(name, setting.Text);

            Directory.CreateDirectory(Path.GetDirectoryName(Config));
            file.Save(Config);
        }
        catch (Exception e)
        {
            Logger.Log($"Failed to save settings: {e}", LogType.Error);
        }
    }

    private static void Load()
    {
        ConfigNode file = File.Exists(Config) ? ConfigNode.Load(Config) : null;
        foreach ((string category, string name, ISetting setting) in All())
        {
            string text = file?.GetNode(category)?.GetValue(name);
            try
            {
                if (text != null)
                    setting.Text = text;
            }
            catch (Exception e)
            {
                Logger.Log($"Couldn't read the setting {name}: {e.Message}", LogType.Warning);
            }
        }
    }
}
