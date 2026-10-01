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
    private class PartInfo
    {
        public ConfigNode craftNode;
        public AvailablePart availablePart;
        public string partName;
        public uint craftID;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public PartInfo parent;
        public int index = -1;
        public readonly List<uint> children = new List<uint>();
        public readonly List<uint> symmetry = new List<uint>();
        public readonly List<string> attachNodes = new List<string>();
        public string srfAttachNode;
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
        VesselType vesselType = VesselType.Debris;
        VesselNaming vesselNaming = null;

        foreach (PartInfo info in sortedParts)
        {
            Vector3 position = inverseRoot * (info.position - root.position);
            Quaternion rotation = inverseRoot * info.rotation;
            ConfigNode partNode = CreatePartNode(info, position, rotation, missionFlag, out int inverseStage);
            template.node.AddNode(partNode);
            templateParts.Add(new TemplatePart { info = info.availablePart, variant = partNode.GetValue("moduleVariantName"), position = position, rotation = rotation });

            highestStage = Math.Max(highestStage, inverseStage);

            if (info.availablePart.partPrefab.vesselType > vesselType)
                vesselType = info.availablePart.partPrefab.vesselType;

            ConfigNode namingNode = info.craftNode.GetNode("VESSELNAMING");
            if (namingNode != null)
            {
                VesselNaming naming = new VesselNaming(namingNode);
                if (naming.namingPriority > (vesselNaming?.namingPriority ?? 0))
                    vesselNaming = naming;
            }
        }

        // Vessel.

        if (vesselNaming != null)
        {
            vesselType = vesselNaming.vesselType;
            template.Name = vesselNaming.vesselName;
        }

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
            };

            ReadPartValues(info);

            parts.Add(info);
            partsByCraftID.Add(cid, info);
        }

        // There were modded or DLC parts that aren't available.
        if (missingParts.Count > 0)
            throw new MissingPartsException(Localizer.Format("#autoLOC_6002425", Localizer.Format(shipName), string.Concat(missingParts.Select(p => p + "\n"))), missingParts);

        if (parts.Count == 0)
            throw LoadingError("Error_NoParts", shipName);
    }

    private void ReadPartValues(PartInfo info)
    {
        foreach (ConfigNode.Value value in info.craftNode.values)
        {
            switch (value.name)
            {
                case "pos":
                    info.position = KSPUtil.ParseVector3(value.value);
                    break;
                case "rot":
                    info.rotation = KSPUtil.ParseQuaternion(value.value);
                    break;
                case "link":
                    if (TryGetCraftID(value.value, out uint childID))
                        info.children.Add(childID);
                    break;
                case "sym":
                    if (TryGetCraftID(value.value, out uint symID))
                        info.symmetry.Add(symID);
                    break;
                case "attN":
                    info.attachNodes.Add(value.value);
                    break;
                case "srfN":
                    info.srfAttachNode = value.value;
                    break;
            }
        }
    }

    private void LinkParts()
    {
        foreach (PartInfo info in parts)
        {
            foreach (uint childID in info.children)
            {
                if (!partsByCraftID.TryGetValue(childID, out PartInfo child))
                    throw LoadingError("Error_BrokenLink", $"{info.partName}_{info.craftID}", childID);

                child.parent = info;
            }
        }
    }

    private void SortParts(PartInfo info)
    {
        if (info.index >= 0)
            return;

        info.index = sortedParts.Count;
        sortedParts.Add(info);

        foreach (uint childID in info.children)
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

        // The persistent ID from the craft is kept for now, because robotics controllers reference
        // parts by it. The spawner replaces it with a unique one and fixes up those references.
        node.AddValue("name", info.partName);
        node.AddValue("cid", info.craftID);
        node.AddValue("uid", 0);
        node.AddValue("mid", 0);
        node.AddValue("persistentId", Get("persistentId", "0"));
        node.AddValue("launchID", 0);
        node.AddValue("parent", info.parent?.index ?? 0);
        node.AddValue("position", KSPUtil.WriteVector(position));
        node.AddValue("rotation", KSPUtil.WriteQuaternion(rotation));
        node.AddValue("mirror", Get("mir", "1,1,1"));
        node.AddValue("symMethod", Get("symMethod", "Radial"));
        node.AddValue("istg", inverseStage);
        node.AddValue("resPri", Get("resPri", "0"));
        node.AddValue("dstg", Get("dstg", "0"));
        node.AddValue("sqor", Get("sqor", "-1"));
        node.AddValue("sepI", Get("sepI", "0"));
        node.AddValue("sidx", Get("sidx", "-1"));
        node.AddValue("attm", Get("attm", "0"));
        node.AddValue("sameVesselCollision", Get("sameVesselCollision", "False"));

        string customData = craft.GetValue("cData");
        if (!string.IsNullOrEmpty(customData))
            node.AddValue("cData", customData);

        foreach (uint symID in info.symmetry)
            if (partsByCraftID.TryGetValue(symID, out PartInfo counterpart))
                node.AddValue("sym", counterpart.index);

        node.AddValue("srfN", TryConvertAttachNode(info.srfAttachNode, out string srfN) ? srfN : "None, -1");

        foreach (string attachNode in info.attachNodes)
            if (TryConvertAttachNode(attachNode, out string attN))
                node.AddValue("attN", attN);

        node.AddValue("mass", VesselTemplate.Format(prefab.mass + moduleMass));
        node.AddValue("shielded", false);
        node.AddValue("temp", 300);
        node.AddValue("tempExt", 300);
        node.AddValue("tempExtUnexp", 300);
        node.AddValue("staticPressureAtm", 0);
        node.AddValue("expt", VesselTemplate.Format(prefab.explosionPotential));
        node.AddValue("state", (int)PartStates.IDLE);
        node.AddValue("PreFailState", (int)PartStates.IDLE);
        node.AddValue("attached", true);
        node.AddValue("autostrutMode", Get("autostrutMode", "Off"));
        node.AddValue("rigidAttachment", Get("rigidAttachment", "False"));
        node.AddValue("flag", missionFlag);
        node.AddValue("rTrf", "");
        node.AddValue("modCost", Get("modCost", "0"));
        node.AddValue("modMass", VesselTemplate.Format(moduleMass));
        node.AddValue("moduleVariantName", GetSelectedVariant(craft));
        node.AddValue("moduleCargoStackableQuantity", 1);

        // Part modules, resources etc. share the same format in craft files and saves.
        foreach (ConfigNode child in craft.nodes)
        {
            switch (child.name)
            {
                case "MODULE":
                case "RESOURCE":
                case "EVENTS":
                case "ACTIONS":
                case "PARTDATA":
                case "EFFECTS":
                case "VESSELNAMING":
                    node.AddNode(child.CreateCopy());
                    break;
            }
        }

        return node;
    }

    private static string GetSelectedVariant(ConfigNode craftPartNode)
    {
        foreach (ConfigNode module in craftPartNode.GetNodes("MODULE"))
            if (module.GetValue("name") == "ModulePartVariants")
                return module.GetValue("selectedVariant") ?? "";

        return "";
    }

    // Craft: "top,fuelTank_4294_0|1|0_0|1|0_0|1|0_0|1|0" or "srfAttach,fuelTank_4294,meshName,..."
    // Persistent: "top, 3" or "srfAttach, 3,meshName"
    private bool TryConvertAttachNode(string craftValue, out string persistentValue)
    {
        persistentValue = null;

        if (string.IsNullOrEmpty(craftValue))
            return false;

        string[] fields = craftValue.Split(',');
        if (fields.Length < 2)
            return false;

        string nodeID = fields[0].Trim();
        string[] partFields = fields[1].Split('_');

        if (partFields.Length < 2 || partFields[0] == "Null")
            return false;

        if (!uint.TryParse(partFields[1], out uint craftID) || !partsByCraftID.TryGetValue(craftID, out PartInfo attached))
            return false;

        persistentValue = nodeID + ", " + attached.index;

        string meshName = nodeID == "srfAttach" && fields.Length > 2 ? fields[2].Trim() : "";
        if (meshName != "")
            persistentValue += "," + meshName;

        return true;
    }

    private static bool TryGetCraftID(string nameAndCID, out uint craftID)
    {
        craftID = 0;
        int index = nameAndCID.IndexOf('_');
        return index >= 0 && uint.TryParse(nameAndCID.Substring(index + 1), out craftID) && craftID != 0;
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

        // Values the spawner always overwrites are still written, so that the node is a complete vessel.
        vesselNode.AddValue("pid", Guid.Empty.ToString("N"));
        vesselNode.AddValue("persistentId", 0);
        vesselNode.AddValue("name", name);
        vesselNode.AddValue("type", vesselType);
        vesselNode.AddValue("sit", Vessel.Situations.ORBITING);
        vesselNode.AddValue("landed", false);
        vesselNode.AddValue("skipGroundPositioning", false);
        vesselNode.AddValue("skipGroundPositioningForDroppedPart", false);
        vesselNode.AddValue("vesselSpawning", false);
        vesselNode.AddValue("launchedFrom", "");
        vesselNode.AddValue("landedAt", "");
        vesselNode.AddValue("displaylandedAt", "");
        vesselNode.AddValue("splashed", false);
        vesselNode.AddValue("met", 0);
        vesselNode.AddValue("lct", VesselTemplate.Format(UT));
        vesselNode.AddValue("lastUT", VesselTemplate.Format(UT));
        vesselNode.AddValue("distanceTraveled", 0);
        vesselNode.AddValue("root", 0);
        vesselNode.AddValue("lat", 0);
        vesselNode.AddValue("lon", 0);
        vesselNode.AddValue("alt", 0);
        vesselNode.AddValue("hgt", -1);
        vesselNode.AddValue("nrm", KSPUtil.WriteVector(Vector3.up));
        vesselNode.AddValue("rot", KSPUtil.WriteQuaternion(Quaternion.identity));
        vesselNode.AddValue("CoM", KSPUtil.WriteVector(Vector3.zero));
        vesselNode.AddValue("stg", stage);
        vesselNode.AddValue("prst", false);
        vesselNode.AddValue("ref", 0);
        vesselNode.AddValue("ctrl", true);
        vesselNode.AddValue("PQSMin", 0);
        vesselNode.AddValue("PQSMax", 0);
        vesselNode.AddValue("GroupOverride", 0);

        foreach (string copied in new[] { "OverrideDefault", "OverrideActionControl", "OverrideAxisControl", "OverrideGroupNames" })
            if (craftNode.GetValue(copied) is string value)
                vesselNode.AddValue(copied, value);

        vesselNode.AddValue("altDispState", AltimeterDisplayState.DEFAULT);

        // These can be empty for new vessels, but must be present.
        vesselNode.AddNode("ACTIONGROUPS");
        vesselNode.AddNode("FLIGHTPLAN");
        vesselNode.AddNode("CTRLSTATE");
        vesselNode.AddNode("VESSELMODULES");

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
