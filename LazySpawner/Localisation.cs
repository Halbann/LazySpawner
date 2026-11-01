using KSP.Localization;
using System.Collections.Generic;
using System.IO;

namespace LazySpawner;

// The words are in GameData/LazySpawner/Localization, and the code asks for them by what they're for:
// Loc("Crew_Hired", 3) is #LOC_LazySpawner_Crew_Hired. Numbers go in already formatted, and the words
// choose their own plurals.
internal static class Localisation
{
    private static readonly Dictionary<string, string> words = new Dictionary<string, string>();

    public static string Loc(string key, params object[] args)
    {
        // Words without numbers don't change while the game runs, and the screens ask for them every frame.
        if (args.Length > 0)
            return Localizer.Format($"#LOC_{Meta.name}_{key}", args);
        if (!words.TryGetValue(key, out string text))
            words[key] = text = Localizer.Format($"#LOC_{Meta.name}_{key}");
        return text;
    }

    // The game only reads the words when it starts, so after a hot reload, read the English again.
    private static void OnHotLoad()
    {
        ConfigNode english = ConfigNode.Load(Path.Combine(KSPUtil.ApplicationRootPath, "GameData", Meta.name, "Localization", "en-us.cfg"))?.GetNode("Localization")?.GetNode("en-us");
        foreach (ConfigNode.Value value in english?.values ?? new ConfigNode.ValueList())
            Localizer.Tags[value.name] = value.value;
        words.Clear();
    }
}
