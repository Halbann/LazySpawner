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
    public string save; // The save it's in, or null for the game's own craft and craft from elsewhere.
    public string folder; // Where it is in its Ships folder, like VAB\Drones. Null for craft from elsewhere.
    public DateTime modified;

    // Not in a save's or the game's Ships folder.
    public bool Elsewhere => folder == null;

    private string displayName;
    public string DisplayName => displayName ??= KSP.Localization.Localizer.Format(name);

    // What searching looks through: its name, and where it is.
    private string searchText;
    public string SearchText => searchText ??= $"{DisplayName} {folder} {save}";

    // What's in the craft is only read when it's first needed: in a list, when its row comes into view.
    private bool detailed;
    private int partCount;
    private List<string> missingParts;

    public int PartCount { get { Detail(); return partCount; } }
    public EditorFacility Editor { get { Detail(); return facility == "SPH" ? EditorFacility.SPH : EditorFacility.VAB; } }
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
            CraftProfileInfo info = Elsewhere
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
            if (!thumbnailLoaded)
            {
                thumbnailLoaded = true;
                string file = CraftList.ThumbnailPath(this);
                if (File.Exists(file))
                {
                    thumbnail = new Texture2D(2, 2);
                    thumbnail.LoadImage(File.ReadAllBytes(file), markNonReadable: true);
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

// The craft the list can show: in this save, in every save, the game's own, or recently used from anywhere.
// Listing them only looks at the files' names and dates, however many there are.
[Settings(category = "Craft")]
internal static class CraftList
{
    public enum Source { Recent, ThisSave, AllSaves, Stock }
    public enum Order { Newest, Name }

    public static readonly Setting<Source> source = Source.ThisSave;
    public static readonly Setting<Order> order = Order.Newest;

    // Craft picked or spawned, from anywhere, most recent first.
    public static readonly Setting<string> recent = "";
    private const int maxRecent = 50;

    // Every craft seen so far, kept until its file changes.
    private static readonly Dictionary<string, Craft> known = new Dictionary<string, Craft>(StringComparer.OrdinalIgnoreCase);

    // Lists already made, until the next refresh. Recent craft stay in the order they were used.
    private static readonly Dictionary<(Source, Order), List<Craft>> lists = new Dictionary<(Source, Order), List<Craft>>();
    private static (Source, Order) Shown => (source, source == Source.Recent ? Order.Newest : order.Value);

    public static List<Craft> List => lists.TryGetValue(Shown, out List<Craft> list) ? list : lists[Shown] = Find(Shown.Item1, Shown.Item2);

    public static void Refresh() => lists.Clear();

    private static string Root => Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static List<Craft> Find(Source from, Order by)
    {
        if (from == Source.Recent)
            return Recent().Where(File.Exists).Select(Fresh).ToList();

        string root = Root;
        string saves = Path.Combine(root, "saves");
        IEnumerable<string> ships = from == Source.Stock ? new[] { Path.Combine(root, "Ships") }
            : from == Source.AllSaves ? Directory.GetDirectories(saves).Select(save => Path.Combine(save, "Ships"))
            : HighLogic.SaveFolder != null ? new[] { Path.Combine(saves, HighLogic.SaveFolder, "Ships") }
            : new string[0];

        List<Craft> craft = ships.SelectMany(s => new[] { Path.Combine(s, "VAB"), Path.Combine(s, "SPH") })
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "*.craft", SearchOption.AllDirectories))
            .Select(Fresh)
            .ToList();

        return by == Order.Name
            ? craft.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : craft.OrderByDescending(c => c.modified).ToList();
    }

    // A listed craft, looked at again in case its file changed.
    private static Craft Fresh(string path)
    {
        DateTime modified = File.GetLastWriteTime(path);
        if (known.TryGetValue(path, out Craft craft) && craft.modified == modified)
            return craft;

        craft?.Forget();
        return known[path] = Create(path, modified);
    }

    // A craft file from anywhere, if it's still there. Asked for every frame, so once it's known it isn't read
    // again. Refreshing the list does that.
    public static Craft Get(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path = Path.GetFullPath(path)))
            return null;

        return known.TryGetValue(path, out Craft craft) ? craft : known[path] = Create(path, File.GetLastWriteTime(path));
    }

    // Where a craft is: the game's Ships/VAB/..., a save's saves/<save>/Ships/VAB/..., or anywhere else.
    private static Craft Create(string path, DateTime modified)
    {
        Craft craft = new Craft { path = path, name = Path.GetFileNameWithoutExtension(path), modified = modified };
        string root = Root;
        string[] folders = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path).Substring(root.Length).Split(Path.DirectorySeparatorChar) : new string[0];
        bool Is(int i, string name) => folders.Length > i && string.Equals(folders[i], name, StringComparison.OrdinalIgnoreCase);

        int ships = Is(0, "Ships") ? 0 : Is(0, "saves") && Is(2, "Ships") ? 2 : -1;
        if (ships >= 0 && (Is(ships + 1, "VAB") || Is(ships + 1, "SPH")))
        {
            craft.save = ships == 2 ? folders[1] : null;
            craft.facility = folders[ships + 1].ToUpperInvariant();
            craft.folder = string.Join(Path.DirectorySeparatorChar.ToString(), folders, ships + 1, folders.Length - ships - 1);
        }
        else
        {
            string[] all = path.Split(Path.DirectorySeparatorChar);
            craft.facility = all.Contains("SPH") ? "SPH" : all.Contains("VAB") ? "VAB" : null;
        }

        return craft;
    }

    // Where the editor keeps its picture of a craft, if it's in a Ships folder. Worked out here rather than by
    // ShipConstruction.GetPlayerCraftThumbnailName, which loads the whole craft file to find out which editor
    // it's from.
    public static string ThumbnailPath(Craft craft) =>
        craft.Elsewhere ? null
        : craft.save == null ? Path.Combine(KSPUtil.ApplicationRootPath, $"Ships/@thumbs/{craft.facility}/{KSPUtil.SanitizeFilename(craft.name)}.png")
        : Path.Combine(KSPUtil.ApplicationRootPath, "thumbs/" + ShipConstruction.GetPlayerCraftThumbnailName(craft.save, Path.DirectorySeparatorChar + craft.folder, craft.name) + ".png");

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

    // Craft picked or spawned go to the top of the recent ones.
    public static void Remember(Craft craft)
    {
        if (craft == null)
            return;

        recent.Value = string.Join("|", Recent().Where(p => !string.Equals(p, craft.path, StringComparison.OrdinalIgnoreCase)).Prepend(craft.path).Take(maxRecent));
        lists.Remove((Source.Recent, Order.Newest));
    }
}
