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
    public string name; // The file's name, as the stock craft browser shows it.
    public string facility; // VAB or SPH
    public DateTime modified;
    public string thumbnailPath;
    public bool elsewhere; // Not in the save or the game's own Ships folder.

    private string displayName;
    public string DisplayName => displayName ??= KSP.Localization.Localizer.Format(name);

    // What's in the craft is only read when it's first needed: in a list, when its row comes into view.
    private bool detailed;
    private int partCount;
    private List<string> missingParts;

    public int PartCount { get { Detail(); return partCount; } }
    public List<string> MissingParts { get { Detail(); return missingParts; } }

    private void Detail()
    {
        if (detailed)
            return;

        detailed = true;
        missingParts = new List<string>();

        try
        {
            // Stock keeps what's in a craft in a .loadmeta beside it, and rebuilds it if the craft is newer.
            // Craft from elsewhere are read directly, without leaving one behind.
            CraftProfileInfo info = elsewhere
                ? new CraftProfileInfo().LoadDetailsFromCraftFile(ConfigNode.Load(path), path)
                : CraftProfileInfo.GetSaveData(path, Path.ChangeExtension(path, ".loadmeta"));

            partCount = info.partCount;
            missingParts = (info.partNames ?? new List<string>()).Distinct().Where(p => PartLoader.getPartInfoByName(p) == null).ToList();
            facility ??= info.shipFacility == EditorFacility.SPH ? "SPH" : "VAB";
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Couldn't read {path}: {e.Message}");
        }
    }

    private Texture2D thumbnail;
    private bool thumbnailLoaded;

    public Texture2D Thumbnail
    {
        get
        {
            if (!thumbnailLoaded && thumbnailPath != null)
            {
                thumbnailLoaded = true;
                if (File.Exists(thumbnailPath))
                {
                    thumbnail = new Texture2D(2, 2);
                    thumbnail.LoadImage(File.ReadAllBytes(thumbnailPath), markNonReadable: true);
                }
            }

            return thumbnail;
        }
    }

    // The file changed, so this is out of date.
    public void Forget()
    {
        if (thumbnail != null)
            UnityEngine.Object.Destroy(thumbnail);
    }
}

// Every craft in the save and the game's Ships folder, plus craft from elsewhere that were spawned recently.
// Listing them only looks at the files' names and dates, however many there are.
[Settings(category = "Craft")]
internal static class CraftList
{
    // Craft from outside the game, most recent first.
    public static readonly Setting<string> recent = "";
    private const int maxRecent = 20;

    private static List<Craft> all;

    // Every craft seen so far, kept until its file changes.
    private static readonly Dictionary<string, Craft> known = new Dictionary<string, Craft>(StringComparer.OrdinalIgnoreCase);

    public static List<Craft> All => all ??= Find();

    public static void Refresh() => all = null;

    private static List<Craft> Find()
    {
        List<Craft> craft = new List<Craft>();
        string root = KSPUtil.ApplicationRootPath;

        foreach (string facility in new[] { "VAB", "SPH" })
        {
            if (HighLogic.SaveFolder != null)
                AddFolder(craft, Path.Combine(root, "saves", HighLogic.SaveFolder, "Ships"), facility, stock: false);

            AddFolder(craft, Path.Combine(root, "Ships"), facility, stock: true);
        }

        foreach (string path in Recent())
            if (Get(path) is Craft c && c.elsewhere)
                craft.Add(c);

        return craft.OrderByDescending(c => c.modified).ToList();
    }

    private static void AddFolder(List<Craft> craft, string ships, string facility, bool stock)
    {
        ships = Path.GetFullPath(ships);
        string folder = Path.Combine(ships, facility);
        if (!Directory.Exists(folder))
            return;

        foreach (string path in Directory.GetFiles(folder, "*.craft", SearchOption.AllDirectories))
        {
            DateTime modified = File.GetLastWriteTime(path);
            if (!known.TryGetValue(path, out Craft entry) || entry.modified != modified)
            {
                entry?.Forget();
                entry = known[path] = new Craft { path = path, name = Path.GetFileNameWithoutExtension(path), facility = facility, modified = modified };
            }

            // Worked out here rather than by ShipConstruction.GetPlayerCraftThumbnailName, which loads
            // the whole craft file to find out which editor it's from.
            entry.thumbnailPath ??= Path.Combine(KSPUtil.ApplicationRootPath, stock
                ? $"Ships/@thumbs/{facility}/{KSPUtil.SanitizeFilename(entry.name)}.png"
                : "thumbs/" + ShipConstruction.GetPlayerCraftThumbnailName(HighLogic.SaveFolder, Path.GetDirectoryName(path).Substring(ships.Length), entry.name) + ".png");

            craft.Add(entry);
        }
    }

    // A craft file from anywhere. Asked for every frame, so it doesn't look at the file again once it's
    // known. Refreshing the list does.
    public static Craft Get(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        path = Path.GetFullPath(path);
        if (known.TryGetValue(path, out Craft craft))
            return craft;

        if (!File.Exists(path))
            return null;

        string[] folders = path.Split(Path.DirectorySeparatorChar);
        return known[path] = new Craft
        {
            path = path,
            name = Path.GetFileNameWithoutExtension(path),
            facility = folders.Contains("SPH") ? "SPH" : folders.Contains("VAB") ? "VAB" : null,
            modified = File.GetLastWriteTime(path),
            elsewhere = !path.StartsWith(Path.GetFullPath(KSPUtil.ApplicationRootPath), StringComparison.OrdinalIgnoreCase),
        };
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
