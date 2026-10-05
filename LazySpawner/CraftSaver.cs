using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using static LazySpawner.Localisation;

namespace LazySpawner;

// Saves a loaded vessel as a craft file, the reverse of CraftParser. KSP's own editor save does most of it,
// since it can save any parts, not just the editor's. Flight doesn't keep everything the editor had, though:
// - How the craft sat in the editor. The control point goes back to looking up in the VAB, or forwards in the SPH.
// - Rotations from the rotate tool. A part that had one turns back to its default when picked up in the editor.
// - Rotors' torque limits, which every flight starts at 0. They go back to the default, 100%.
public static class CraftSaver
{
    // The building a vessel was launched from, or else the likelier one for what it is.
    public static EditorFacility Facility(Vessel vessel)
    {
        string site = vessel.launchedFrom;
        EditorFacility facility = PSystemSetup.Instance.GetSpaceCenterFacility(site)?.GetFacility().GetEditorFacility().ToEditor()
            ?? PSystemSetup.Instance.GetLaunchSite(site)?.editorFacility ?? EditorFacility.None;
        return facility != EditorFacility.None ? facility : vessel.vesselType is VesselType.Plane or VesselType.Rover ? EditorFacility.SPH : EditorFacility.VAB;
    }

    // Where a craft of this name goes in this game's craft for the building.
    public static string PathFor(string name, EditorFacility facility) =>
        Path.Combine(ShipConstruction.GetCurrentGameShipsPathFor(facility), KSPUtil.SanitizeString(name, '_', true) + ".craft");

    // The vessel's name, numbered if there's a craft of that name already.
    public static string FreeName(Vessel vessel, EditorFacility facility)
    {
        string name = vessel.GetDisplayName(), free = name;
        for (int i = 2; File.Exists(PathFor(free, facility)); i++)
            free = $"{name} ({i})";
        return free;
    }

    // Into this game's craft for the building, with a thumbnail like the editor's, over any craft of the same name.
    // Returns the craft file's path.
    public static string Save(Vessel vessel, EditorFacility facility, string name)
    {
        ConfigNode craft = ToCraft(vessel, facility);
        craft.SetValue("ship", name);
        string path = PathFor(name, facility);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        craft.Save(path);
        SaveThumbnail(vessel, craft, path);
        Logger.Log($"Saved {vessel.GetDisplayName()} as {path}");
        return path;
    }

    // The vessel as a craft file has it, for the building, to save anywhere or use as it is.
    public static ConfigNode ToCraft(Vessel vessel, EditorFacility facility)
    {
        if (vessel.isEVA)
            throw new SpawnException(Loc("Error_SaveCraft_Title"), Loc("Error_SaveEVA"));

        List<Part> parts = Parts(vessel);

        // Turned so the control point faces the building's way: a control part's up is its nose, and its forward its belly.
        Part reference = vessel.GetReferenceTransformPart() ?? vessel.rootPart;
        Quaternion referenceInRoot = reference.orgRot * Quaternion.Inverse(reference.transform.rotation) * vessel.ReferenceTransform.rotation;
        Quaternion rootInEditor = (facility == EditorFacility.SPH ? Quaternion.Euler(90, 0, 0) : Quaternion.identity) * Quaternion.Inverse(referenceInRoot);

        // The root part where the editor puts the first part, as stock craft have it, but lifted if that's through the floor.
        Bounds inEditor = VesselBounds.Enclose(VesselBounds.Corners(VesselBounds.FromVessel(vessel, includeLaunchClamps: false)).Select(corner => rootInEditor * corner));
        Vector3 rootPosition = new Vector3(0, Mathf.Max(facility == EditorFacility.SPH ? 10 : 15, 1 - inEditor.min.y), 0);

        ConfigNode craft = SaveShip(vessel, parts, facility);
        craft.SetValue("size", KSPUtil.WriteVector(inEditor.size));
        HashSet<string> saved = new HashSet<string>(parts.Select(p => p.partInfo.name + "_" + p.craftID));
        ConfigNode[] partNodes = craft.GetNodes("PART");
        for (int i = 0; i < parts.Count; i++)
        {
            // The parts as built, without flex, rather than where they are right now.
            ConfigNode node = partNodes[i];
            node.SetValue("pos", KSPUtil.WriteVector(rootPosition + rootInEditor * parts[i].orgPos));
            node.SetValue("rot", KSPUtil.WriteQuaternion(rootInEditor * parts[i].orgRot));
            foreach (ConfigNode.Value link in node.values.Cast<ConfigNode.Value>().Where(v => v.name == "link" && !saved.Contains(v.value)).ToList())
                node.values.Remove(link);

            foreach (ConfigNode module in node.GetNodes("MODULE"))
                switch (module.GetValue("name"))
                {
                    // Docked ports join like ports put together in the editor. The vessels they were are forgotten.
                    case "ModuleDockingNode" when module.GetValue("state")?.StartsWith("Docked") == true:
                        module.SetValue("state", "Ready");
                        module.SetValue("dockUId", 0);
                        module.RemoveNode("DOCKEDVESSEL");
                        break;
                    case "ModuleRoboticServoRotor":
                        module.SetValue("servoMotorLimit", 100);
                        break;
                }
        }

        return craft;
    }

    // Kerbals in command seats are parts of the vessel, but never of a craft.
    private static List<Part> Parts(Vessel vessel) =>
        vessel.parts.Where(p => !p.isKerbalEVA()).ToList();

    private static ConfigNode SaveShip(Vessel vessel, List<Part> parts, EditorFacility facility)
    {
        // Copies of one craft docked together have the same part IDs.
        ShipConstruction.SanitizeCraftIDs(parts, false);

        // Docking ports join by their parts alone. Their nodes are what joins them in the editor, until the save is made.
        List<AttachNode> joined = parts.SelectMany(p => p.FindModulesImplementing<ModuleDockingNode>())
            .Where(d => d.otherNode != null && d.referenceNode != null && d.referenceNode.attachedPart == null && d.otherNode.referenceNode != null
                && (d.part.parent == d.otherNode.part || d.otherNode.part.parent == d.part))
            .Select(d => { d.referenceNode.attachedPart = d.otherNode.part; return d.referenceNode; }).ToList();

        // The constructor claims the parts, which flight doesn't use, but give them back anyway.
        List<ShipConstruct> owners = parts.Select(p => p.ship).ToList();
        ShipConstruct ship = new ShipConstruct(vessel.GetDisplayName(), "", parts)
        {
            shipFacility = facility,
            rotation = Quaternion.identity,
            missionFlag = vessel.rootPart.flagURL,
            vesselType = vessel.vesselType,
            OverrideDefault = vessel.OverrideDefault,
            OverrideActionControl = vessel.OverrideActionControl,
            OverrideAxisControl = vessel.OverrideAxisControl,
            OverrideGroupNames = vessel.OverrideGroupNames,
        };

        try
        {
            return ship.SaveShip();
        }
        finally
        {
            for (int i = 0; i < parts.Count; i++)
                parts[i].ship = owners[i];
            joined.ForEach(node => node.attachedPart = null);
        }
    }

    #region Thumbnail

    // The editor's thumbnail for the vessel's craft from ToCraft, saved at this path, where the craft browser will
    // look for it. Nothing for a path outside the saves. It's taken with the editor's camera, from the same angle on
    // the craft as it'll sit in the building. It's lit by its own light rather than the sun, which may be behind a
    // planet, and sees only this vessel.
    public static void SaveThumbnail(Vessel vessel, ConfigNode craft, string craftPath)
    {
        string name = ShipConstruction.GetPlayerCraftThumbnailName(Path.GetDirectoryName(craftPath), Path.GetFileNameWithoutExtension(craftPath));
        if (name == "")
            return;

        // The craft as it'll sit in the building, read back from the root part.
        Part root = vessel.rootPart;
        Quaternion rootInEditor = KSPUtil.ParseQuaternion(craft.GetNodes("PART").First(p => p.GetValue("part") == root.partInfo.name + "_" + root.craftID).GetValue("rot"));
        Vector3 size = KSPUtil.ParseVector3(craft.GetValue("size"));
        Vector3 center = VesselBounds.FromVessel(vessel, includeLaunchClamps: false).center;

        const int resolution = 256, layer = 3;
        (float elevation, float azimuth) = craft.GetValue("type") == nameof(EditorFacility.SPH) ? (35f, 135f) : (45f, 45f);
        Quaternion editor = vessel.transform.rotation * Quaternion.Inverse(rootInEditor);
        Quaternion angle = editor * Quaternion.AngleAxis(azimuth, Vector3.up) * Quaternion.AngleAxis(elevation, Vector3.right);

        GameObject gameObject = new GameObject("LazySpawnerThumbnail");
        Camera camera = gameObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.Color;
        camera.backgroundColor = Color.clear;
        camera.fieldOfView = 30;
        camera.cullingMask = 1 << layer;
        camera.allowHDR = false;
        float distance = KSPCameraUtil.GetDistanceToFit(size, 30 * 0.9f);
        camera.farClipPlane = distance * 3;
        camera.transform.SetPositionAndRotation(vessel.transform.TransformPoint(center) + angle * Vector3.back * distance, angle);
        gameObject.AddComponent<Light>().type = LightType.Directional;

        // Only for the moment it takes. The sun is put back before it's next updated.
        List<GameObject> moved = Parts(vessel).SelectMany(p => p.GetComponentsInChildren<Renderer>()).Select(r => r.gameObject).Where(g => g.layer == 0).ToList();
        moved.ForEach(g => g.layer = layer);
        Light sun = Sun.Instance?.sunLight;
        bool sunWasOn = sun != null && sun.enabled;
        if (sun != null)
            sun.enabled = false;

        try
        {
            Texture2D picture = ShipConstruction.RenderCamera(camera, resolution, resolution, 24, RenderTextureReadWrite.Default);
            byte[] png = picture.EncodeToPNG();
            Object.Destroy(picture);
            string thumbnail = Path.Combine(KSPUtil.ApplicationRootPath, "thumbs", name + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnail));
            File.WriteAllBytes(thumbnail, png);
        }
        finally
        {
            if (sun != null)
                sun.enabled = sunWasOn;
            moved.ForEach(g => g.layer = 0);
            Object.Destroy(gameObject);
        }
    }

    #endregion
}
