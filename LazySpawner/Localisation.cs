using KSP.Localization;

namespace LazySpawner;

// The words are in GameData/LazySpawner/Localization, and the code asks for them by what they're for:
// Loc("Crew_Hired", 3) is #LOC_LazySpawner_Crew_Hired. Numbers go in already formatted, and the words
// choose their own plurals.
internal static class Localisation
{
    public static string Loc(string key, params object[] args) =>
        Localizer.Format($"#LOC_{Meta.name}_{key}", args);
}
