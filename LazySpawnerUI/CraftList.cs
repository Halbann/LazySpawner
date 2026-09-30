using KSP.UI.Screens;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LazySpawner;

// A craft file the player might want to spawn.
internal class Craft
{
    public string path;
    public string name;
    public string facility; // VAB or SPH
    public int partCount;
    public List<string> missingParts = new List<string>();
    public DateTime modified;
    public string thumbnailPath;
    public bool elsewhere; // Not in the save or the game's own Ships folder.

    private Texture2D thumbnail;
    private bool thumbnailLoaded;

    public string DisplayName => KSP.Localization.Localizer.Format(name);

    public Texture2D Thumbnail
    {
        get
        {
            if (!thumbnailLoaded)
            {
                thumbnailLoaded = true;
                if (thumbnailPath != null && File.Exists(thumbnailPath))
                {
                    thumbnail = new Texture2D(2, 2);
                    thumbnail.LoadImage(File.ReadAllBytes(thumbnailPath));
                }
            }

            return thumbnail;
        }
    }
}

// Every craft in the save and the game's Ships folder, plus craft from elsewhere that were spawned recently.
// Stock caches what it knows about each craft in a .loadmeta file beside it, so listing them is cheap.
[Settings(category = "Craft")]
internal static class CraftList
{
    // Craft from outside the game, most recent first.
    public static readonly Setting<string> recent = "";
    private const int maxRecent = 20;

    private static List<Craft> all;
    private static readonly Dictionary<string, Craft> elsewhere = new Dictionary<string, Craft>();

    public static List<Craft> All => all ??= Find();

    public static void Refresh() => all = null;

    private static List<Craft> Find()
    {
        List<Craft> craft = new List<Craft>();
        string root = KSPUtil.ApplicationRootPath;

        foreach (string facility in new[] { "VAB", "SPH" })
        {
            if (HighLogic.SaveFolder != null)
                AddFolder(craft, Path.Combine(root, "saves", HighLogic.SaveFolder, "Ships", facility), facility, stock: false);

            AddFolder(craft, Path.Combine(root, "Ships", facility), facility, stock: true);
        }

        foreach (string path in Recent())
            if (Get(path) is Craft c && c.elsewhere)
                craft.Add(c);

        return craft.OrderByDescending(c => c.modified).ToList();
    }

    private static void AddFolder(List<Craft> craft, string folder, string facility, bool stock)
    {
        if (!Directory.Exists(folder))
            return;

        foreach (string path in Directory.GetFiles(folder, "*.craft", SearchOption.AllDirectories))
        {
            try
            {
                CraftProfileInfo info = CraftProfileInfo.GetSaveData(path, Path.ChangeExtension(path, ".loadmeta"));
                string name = Path.GetFileNameWithoutExtension(path);

                craft.Add(new Craft
                {
                    path = Path.GetFullPath(path),
                    name = string.IsNullOrEmpty(info.shipName) ? name : info.shipName,
                    facility = facility,
                    partCount = info.partCount,
                    missingParts = Missing(info.partNames),
                    modified = File.GetLastWriteTime(path),
                    thumbnailPath = stock
                        ? Path.Combine(KSPUtil.ApplicationRootPath, $"Ships/@thumbs/{facility}/{KSPUtil.SanitizeFilename(name)}.png")
                        : Path.Combine(KSPUtil.ApplicationRootPath, "thumbs", ShipConstruction.GetPlayerCraftThumbnailName(Path.GetDirectoryName(path), name) + ".png"),
                });
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't read {path}: {e.Message}");
            }
        }
    }

    private static List<string> Missing(IEnumerable<string> partNames) =>
        (partNames ?? Enumerable.Empty<string>()).Distinct().Where(p => PartLoader.getPartInfoByName(p) == null).ToList();

    // A craft file from anywhere. Ones outside the game are read directly, without leaving a .loadmeta beside them.
    public static Craft Get(string path)
    {
        path = Path.GetFullPath(path);
        Craft known = all?.Find(c => string.Equals(c.path, path, StringComparison.OrdinalIgnoreCase));
        if (known != null)
            return known;

        if (elsewhere.TryGetValue(path, out Craft craft))
            return craft;

        if (!File.Exists(path))
            return null;

        try
        {
            ConfigNode node = ConfigNode.Load(path);
            CraftProfileInfo info = new CraftProfileInfo().LoadDetailsFromCraftFile(node, path);

            craft = new Craft
            {
                path = path,
                name = string.IsNullOrEmpty(info.shipName) ? Path.GetFileNameWithoutExtension(path) : info.shipName,
                facility = node.GetValue("type") ?? "VAB",
                partCount = info.partCount,
                missingParts = Missing(info.partNames),
                modified = File.GetLastWriteTime(path),
                elsewhere = !path.StartsWith(Path.GetFullPath(KSPUtil.ApplicationRootPath), StringComparison.OrdinalIgnoreCase),
            };

            elsewhere[path] = craft;
            return craft;
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Couldn't read {path}: {e.Message}");
            return null;
        }
    }

    // The craft file some text is a path to, if it is. Paths copied from Explorer or Everything often come quoted.
    public static Craft FromText(string text)
    {
        text = text?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(text) || !text.EndsWith(".craft", StringComparison.OrdinalIgnoreCase) || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        return Get(Path.IsPathRooted(text) ? text : Path.Combine(KSPUtil.ApplicationRootPath, text));
    }

    private static IEnumerable<string> Recent() =>
        recent.Value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

    // Craft from outside the game stay in the list once they've been used.
    public static void Remember(Craft craft)
    {
        if (!craft.elsewhere)
            return;

        recent.Value = string.Join("|", Recent().Where(p => !string.Equals(p, craft.path, StringComparison.OrdinalIgnoreCase)).Prepend(craft.path).Take(maxRecent));

        if (all != null && !all.Contains(craft))
            all.Insert(0, craft);
    }
}
