using KSP.Localization;
using System;
using static LazySpawner.Localisation;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LazySpawner;

// Translates a craft file (editor format) into a vessel node (persistent save format)
// without instantiating a single part. The result is a VesselTemplate that the game
// can load as an ordinary unloaded vessel.
//
// Transformation notes
// Craft: pos/rot are the part's transform.position/rotation in the editor scene.
// Persistent: position/rotation are relative to the root part, in the vessel's frame.
// Craft: link/sym/attN/srfN reference parts by name_craftID.
// Persistent: parent/sym/attN/srfN reference parts by index, and parts must be listed
// in top-down tree order so that a part's parent is always loaded before it.
internal class CraftParser
{
    // A part, and where it goes in the vessel. Everything else is read from its node as it's needed.
    private class PartInfo
    {
        public ConfigNode craftNode;
        public AvailablePart availablePart;
        public string partName;
        public uint craftID;
        public Vector3 position;
        public Quaternion rotation;
        public PartInfo parent;
        public int index = -1;
    }

    private readonly Dictionary<uint, PartInfo> partsByCraftID = new Dictionary<uint, PartInfo>();
    private readonly List<PartInfo> parts = new List<PartInfo>();
    private readonly List<PartInfo> sortedParts = new List<PartInfo>();

    public static VesselTemplate Parse(string craftPath)
    {
        if (string.IsNullOrEmpty(craftPath) || !File.Exists(craftPath))
            throw new SpawnException(Loc("Error_CraftNotFound_Title"), Loc("Error_CraftNotFound", craftPath));

        ConfigNode craftNode = ConfigNode.Load(craftPath);
        if (craftNode == null)
            throw LoadingError("Error_CraftUnreadable", craftPath);

        return Parse(craftNode);
    }

    public static VesselTemplate Parse(ConfigNode craftNode) => new CraftParser().Translate(craftNode);

    private static SpawnException LoadingError(string key, params object[] args) =>
        new SpawnException(Loc("Error_CraftLoading_Title"), Loc(key, args));

    private VesselTemplate Translate(ConfigNode craftNode)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        string shipName = craftNode.GetValue("ship") ?? Loc("Error_UnnamedVessel");
        string missionFlag = craftNode.GetValue("missionFlag");
        if (string.IsNullOrEmpty(missionFlag))
            missionFlag = HighLogic.CurrentGame?.flagURL ?? "";

        ReadParts(craftNode, shipName);
        LinkParts();

        // Find the root part by starting anywhere and going up until we find a part with no parent.
        // It's not always the first part in the file.
        PartInfo root = parts[0];
        while (root.parent != null)
            root = root.parent;

        // Parts must be given in top-down order according to the part tree.
        // A recursive sort establishes the order and gives each part its final index.
        SortParts(root);

        if (sortedParts.Count != parts.Count)
            throw LoadingError("Error_Detached", shipName);

        List<TemplatePart> templateParts = new List<TemplatePart>();
        VesselTemplate template = new VesselTemplate
        {
            node = new ConfigNode("VESSEL"),
            Name = shipName,
            Parts = templateParts,
            UprightRotation = root.rotation,
        };

        // Parts.

        Quaternion inverseRoot = Quaternion.Inverse(root.rotation);
        int highestStage = -1;
        foreach (PartInfo info in sortedParts)
        {
            Vector3 position = inverseRoot * (info.position - root.position);
            Quaternion rotation = inverseRoot * info.rotation;
            ConfigNode partNode = CreatePartNode(info, position, rotation, missionFlag, out int inverseStage);
            template.node.AddNode(partNode);
            templateParts.Add(new TemplatePart { info = info.availablePart, variant = partNode.GetValue("moduleVariantName"), position = position, rotation = rotation });
            highestStage = Math.Max(highestStage, inverseStage);
        }

        // Vessel. Named and typed by the part with the strongest naming, like a probe core, or else typed by its parts.
        VesselNaming naming = sortedParts.Select(p => p.craftNode.GetNode("VESSELNAMING")).Where(n => n != null).Select(n => new VesselNaming(n))
            .Where(n => n.namingPriority > 0).OrderByDescending(n => n.namingPriority).FirstOrDefault();
        template.Name = naming?.vesselName ?? template.Name;
        VesselType vesselType = naming?.vesselType ?? sortedParts.Max(p => p.availablePart.partPrefab.vesselType);

        AddVesselValues(craftNode, template.node, template.Name, vesselType, highestStage + 1);

        template.LandedBounds = VesselBounds.FromParts(templateParts, includeLaunchClamps: true);
        template.SpaceBounds = VesselBounds.FromParts(templateParts, includeLaunchClamps: false);

        if (craftNode.HasValue("size"))
            ApplyCraftSize(template, KSPUtil.ParseVector3(craftNode.GetValue("size")), root.rotation);

        stopwatch.Stop();
        Logger.Log($"Parsed {templateParts.Count} parts of {template.DisplayName} in {stopwatch.Elapsed.TotalMilliseconds:N1} ms, {template.SpaceBounds.size} m across.");

        return template;
    }

    #region Parts

    private void ReadParts(ConfigNode craftNode, string shipName)
    {
        HashSet<string> missingParts = new HashSet<string>();
        string partName = string.Empty;
        string craftID = string.Empty;

        foreach (ConfigNode partNode in craftNode.nodes)
        {
            if (partNode.name != "PART")
                continue;

            string partNameAndID = partNode.GetValue("part");
            if (string.IsNullOrEmpty(partNameAndID) || partNameAndID.IndexOf('_') < 0)
                continue;

            KSPUtil.GetPartInfo(partNameAndID, ref partName, ref craftID);

            // Check if the part is missing from the game.
            AvailablePart availablePart = PartLoader.getPartInfoByName(partName);
            if (availablePart == null || availablePart.partPrefab == null)
            {
                missingParts.Add(partName);
                continue;
            }

            if (missingParts.Count > 0)
                continue;

            if (!uint.TryParse(craftID, out uint cid) || partsByCraftID.ContainsKey(cid))
                throw LoadingError("Error_BadPartID", shipName, partNameAndID);

            PartInfo info = new PartInfo
            {
                craftNode = partNode,
                availablePart = availablePart,
                partName = partName,
                craftID = cid,
                position = KSPUtil.ParseVector3(partNode.GetValue("pos") ?? "0,0,0"),
                rotation = KSPUtil.ParseQuaternion(partNode.GetValue("rot") ?? "0,0,0,1"),
            };

            parts.Add(info);
            partsByCraftID.Add(cid, info);
        }

        // There were modded or DLC parts that aren't available.
        if (missingParts.Count > 0)
            throw new MissingPartsException(Localizer.Format("#autoLOC_6002425", Localizer.Format(shipName), string.Concat(missingParts.Select(p => p + "\n"))), missingParts);

        if (parts.Count == 0)
            throw LoadingError("Error_NoParts", shipName);
    }

    // The parts a part refers to by name_craftID in its values of this name: its children ("link") or its
    // symmetry counterparts ("sym").
    private static IEnumerable<uint> CraftIDs(PartInfo info, string name) =>
        info.craftNode.GetValues(name).Select(value => uint.TryParse(value.Substring(value.IndexOf('_') + 1), out uint id) ? id : 0).Where(id => id != 0);

    private void LinkParts()
    {
        foreach (PartInfo info in parts)
            foreach (uint childID in CraftIDs(info, "link"))
                (partsByCraftID.TryGetValue(childID, out PartInfo child) ? child : throw LoadingError("Error_BrokenLink", $"{info.partName}_{info.craftID}", childID)).parent = info;
    }

    private void SortParts(PartInfo info)
    {
        if (info.index >= 0)
            return;

        info.index = sortedParts.Count;
        sortedParts.Add(info);

        foreach (uint childID in CraftIDs(info, "link"))
            SortParts(partsByCraftID[childID]);
    }

    private ConfigNode CreatePartNode(PartInfo info, Vector3 position, Quaternion rotation, string missionFlag, out int inverseStage)
    {
        ConfigNode craft = info.craftNode;
        ConfigNode node = new ConfigNode("PART");
        Part prefab = info.availablePart.partPrefab;

        string Get(string name, string fallback) =>
            craft.GetValue(name) ?? fallback;

        float moduleMass = 0;
        float.TryParse(Get("modMass", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out moduleMass);

        inverseStage = 0;
        int.TryParse(Get("istg", "0"), out inverseStage);

        // Only what the game's defaults don't already cover. The spawner gives the part its IDs. The persistent
        // ID from the craft is kept for now, because robotics controllers reference parts by it. The spawner
        // replaces it with a unique one and fixes up those references.
        node.AddValue("name", info.partName);
        node.AddValue("cid", info.craftID);
        node.AddValue("persistentId", Get("persistentId", "0"));
        node.AddValue("parent", info.parent?.index ?? 0);
        node.AddValue("position", KSPUtil.WriteVector(position));
        node.AddValue("rotation", KSPUtil.WriteQuaternion(rotation));
        node.AddValue("mirror", Get("mir", "1,1,1"));
        node.AddValue("istg", inverseStage);
        node.AddValue("sqor", Get("sqor", "-1"));
        node.AddValue("sidx", Get("sidx", "-1"));

        // The game's defaults for these are the same as the editor's.
        foreach (string copied in new[] { "symMethod", "resPri", "dstg", "sepI", "attm", "sameVesselCollision", "autostrutMode", "rigidAttachment", "modCost", "cData" })
            if (craft.GetValue(copied) is string value)
                node.AddValue(copied, value);

        foreach (uint symID in CraftIDs(info, "sym"))
            if (partsByCraftID.TryGetValue(symID, out PartInfo counterpart))
                node.AddValue("sym", counterpart.index);

        node.AddValue("srfN", AttachNode(craft.GetValue("srfN")) ?? "None, -1");
        foreach (string attN in craft.GetValues("attN").Select(AttachNode).Where(attN => attN != null))
            node.AddValue("attN", attN);

        node.AddValue("mass", VesselTemplate.Format(prefab.mass + moduleMass));
        node.AddValue("temp", 300);
        node.AddValue("tempExt", 300);
        node.AddValue("tempExtUnexp", 300);
        node.AddValue("expt", VesselTemplate.Format(prefab.explosionPotential));
        node.AddValue("attached", true);
        node.AddValue("flag", missionFlag);
        node.AddValue("modMass", VesselTemplate.Format(moduleMass));
        node.AddValue("moduleVariantName", craft.GetNodes("MODULE").FirstOrDefault(m => m.GetValue("name") == "ModulePartVariants")?.GetValue("selectedVariant") ?? "");

        // Part modules, resources etc. share the same format in craft files and saves.
        foreach (ConfigNode child in craft.nodes)
            if (copiedNodes.Contains(child.name))
                node.AddNode(child.CreateCopy());

        return node;
    }

    private static readonly HashSet<string> copiedNodes = new HashSet<string> { "MODULE", "RESOURCE", "EVENTS", "ACTIONS", "PARTDATA", "EFFECTS", "VESSELNAMING" };

    // Craft: "top,fuelTank_4294_0|1|0_0|1|0_0|1|0_0|1|0" or "srfAttach,fuelTank_4294,meshName,..."
    // Persistent: "top, 3" or "srfAttach, 3,meshName". Null if it isn't attached to anything.
    private string AttachNode(string craftValue)
    {
        string[] fields = craftValue?.Split(',');
        string[] part = fields?.Length >= 2 ? fields[1].Split('_') : null;
        if (part == null || part.Length < 2 || part[0] == "Null" || !uint.TryParse(part[1], out uint craftID) || !partsByCraftID.TryGetValue(craftID, out PartInfo attached))
            return null;

        string nodeID = fields[0].Trim();
        string mesh = nodeID == "srfAttach" && fields.Length > 2 ? fields[2].Trim() : "";
        return $"{nodeID}, {attached.index}" + (mesh != "" ? "," + mesh : "");
    }

    #endregion

    #region Bounds

    // The part prefabs the bounds are measured from don't have procedural geometry, like fairings
    // and resizable parts. The editor did, and saved the craft's size along the editor's axes,
    // without clamps. It has no centre, so keep ours and grow the box to at least that size.
    private static void ApplyCraftSize(VesselTemplate template, Vector3 craftSize, Quaternion rootInEditor)
    {
        if (craftSize == Vector3.zero)
            return;

        // Our bounds, seen along the editor's axes.
        Bounds editor = Transform(template.SpaceBounds, rootInEditor);
        Vector3 size = Vector3.Max(editor.size, craftSize);
        if (size == editor.size)
            return;

        editor.size = size;

        // Back into the vessel's frame.
        Bounds grown = Transform(editor, Quaternion.Inverse(rootInEditor));
        Bounds landed = template.LandedBounds;
        landed.Encapsulate(grown);
        template.SpaceBounds = grown;
        template.LandedBounds = landed;
    }

    private static Bounds Transform(Bounds bounds, Quaternion rotation) =>
        VesselBounds.Enclose(VesselBounds.Corners(bounds).Select(corner => rotation * corner));

    #endregion

    #region Vessel

    private static void AddVesselValues(ConfigNode craftNode, ConfigNode vesselNode, string name, VesselType vesselType, int stage)
    {
        double UT = Planetarium.GetUniversalTime();

        // Only what the game's defaults don't already cover. The spawner gives the vessel its IDs, situation and
        // whereabouts.
        vesselNode.AddValue("name", name);
        vesselNode.AddValue("type", vesselType);
        vesselNode.AddValue("stg", stage);
        vesselNode.AddValue("ctrl", true);

        foreach (string copied in new[] { "OverrideDefault", "OverrideActionControl", "OverrideAxisControl", "OverrideGroupNames" })
            if (craftNode.GetValue(copied) is string value)
                vesselNode.AddValue(copied, value);

        // It can be empty, but the game saves it without checking it's there.
        vesselNode.AddNode("ACTIONGROUPS");

        ConfigNode discovery = vesselNode.AddNode("DISCOVERY");
        discovery.AddValue("state", (int)DiscoveryLevels.Owned);
        discovery.AddValue("lastObservedTime", VesselTemplate.Format(UT));
        discovery.AddValue("lifetime", "Infinity");
        discovery.AddValue("refTime", "Infinity");
        discovery.AddValue("size", (int)UntrackedObjectClass.C);
    }

    #endregion
}

public class MissingPartsException : SpawnException
{
    public readonly List<string> missingParts;

    public MissingPartsException(string message, IEnumerable<string> missingParts) : base(Localizer.Format("#autoLOC_6002424"), message) =>
        this.missingParts = missingParts.ToList();

    public string ShortMessage => Short(missingParts);

    public static string Short(List<string> missingParts) =>
        Loc("MissingParts", missingParts.Count, string.Join(", ", missingParts.Take(3)) + (missingParts.Count > 3 ? "…" : ""));
}
