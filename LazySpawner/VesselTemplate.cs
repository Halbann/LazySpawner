using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LazySpawner;

// A vessel in persistent save format (a VESSEL node), ready to be stamped out
// any number of times by the Spawner. Built once per spawn request, from
// either a craft file or an existing vessel, so that spawning many copies
// doesn't mean parsing the craft many times.
public class VesselTemplate
{
    // VESSEL node. Never handed to the game directly, always copied first.
    public ConfigNode node;

    public string name;
    public int partCount;
    public bool fromCraft;

    // Rotation of the vessel's root part relative to a local surface frame
    // (y = up, z = north), when the vessel sits the right way up on the ground.
    // For craft this is the root part's rotation in the editor.
    public Quaternion uprightRotation = Quaternion.identity;

    // Part positions relative to the root part, in the vessel's frame.
    public List<Vector3> partPositions = new List<Vector3>();

    // The vessel's size, as boxes in its own frame. Launch clamps only come along on the ground.
    public Bounds landedBounds;
    public Bounds spaceBounds;

    public Bounds BoundsFor(bool landed) => landed ? landedBounds : spaceBounds;

    // Radius of a sphere around the root part that encloses the vessel.
    public float Radius(bool landed)
    {
        Bounds bounds = BoundsFor(landed);
        return bounds.center.magnitude + bounds.extents.magnitude;
    }

    public string DisplayName => KSP.Localization.Localizer.Format(name);

    // Rotation of the part the vessel will most likely be controlled from, relative to the root.
    // The spawner works out the real one once the crew are aboard. This is for previews.
    public Quaternion ReferenceRotation
    {
        get
        {
            if (referenceRotation == null)
                referenceRotation = EstimateReferenceRotation();

            return referenceRotation.Value;
        }
    }

    private Quaternion? referenceRotation;

    private Quaternion EstimateReferenceRotation()
    {
        ConfigNode[] parts = node.GetNodes("PART");
        if (parts.Length == 0)
            return Quaternion.identity;

        string reference = node.GetValue("ref");
        ConfigNode found = null;

        if (!string.IsNullOrEmpty(reference) && reference != "0")
            found = Array.Find(parts, p => p.GetValue("uid") == reference);

        Part Prefab(ConfigNode p) => PartLoader.getPartInfoByName(p.GetValue("name"))?.partPrefab;
        bool IsControl(ConfigNode p) => Prefab(p)?.isControlSource > Vessel.ControlLevel.NONE;

        if (found == null && IsControl(parts[0]))
            found = parts[0];

        found ??= Array.Find(parts, p => IsControl(p) && Prefab(p).CrewCapacity > 0)
            ?? Array.Find(parts, IsControl)
            ?? parts[0];

        return KSPUtil.ParseQuaternion(found.GetValue("rotation"));
    }

    public void CalculateBounds()
    {
        landedBounds = VesselBounds.FromTemplate(this, includeLaunchClamps: true);
        spaceBounds = VesselBounds.FromTemplate(this, includeLaunchClamps: false);
    }

    // Height of the root part above the vessel's lowest point, when turned this way.
    public float HeightAboveBottom(Quaternion rotation)
    {
        Bounds bounds = landedBounds;
        Vector3 min = bounds.min, max = bounds.max;
        float lowest = 0;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
            lowest = Mathf.Min(lowest, (rotation * corner).y);
        }

        return -lowest;
    }

    #region From Vessel

    public static VesselTemplate FromVessel(Vessel vessel)
    {
        if (vessel == null)
            throw new ArgumentNullException(nameof(vessel));

        if (vessel.isEVA)
            throw new SpawnException("Kerbals on EVA can't be cloned.");

        // Exactly what the game does to a vessel when saving.
        ProtoVessel proto = vessel.BackupVessel();

        VesselTemplate template = new VesselTemplate
        {
            node = new ConfigNode("VESSEL"),
            name = vessel.vesselName,
            partCount = proto.protoPartSnapshots.Count,
            fromCraft = false,
        };

        proto.Save(template.node);

        foreach (ProtoPartSnapshot snapshot in proto.protoPartSnapshots)
            template.partPositions.Add(snapshot.position);

        // A loaded vessel can be measured as it is, procedural fairings and all.
        template.landedBounds = VesselBounds.FromVessel(vessel, includeLaunchClamps: true);
        template.spaceBounds = VesselBounds.FromVessel(vessel, includeLaunchClamps: false);

        // Keep the vessel's attitude relative to the ground beneath it.
        CelestialBody body = vessel.mainBody;
        Quaternion surfaceFrame = Placement.SurfaceFrame(body, vessel.latitude, vessel.longitude, 0);
        template.uprightRotation = Quaternion.Inverse(surfaceFrame) * vessel.transform.rotation;

        CleanClonedNode(template.node);

        return template;
    }

    // Strip the things that belong to the original vessel and nobody else.
    private static void CleanClonedNode(ConfigNode vesselNode)
    {
        vesselNode.RemoveNode("TARGET");
        vesselNode.RemoveNode("WAYPOINT");

        ConfigNode flightPlan = vesselNode.GetNode("FLIGHTPLAN");
        flightPlan?.ClearData();

        vesselNode.SetValue("cln", false);
        vesselNode.RemoveValue("clnRsn");
    }

    #endregion

    #region Helpers

    internal static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    internal static string Format(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    #endregion
}

public class SpawnException : Exception
{
    public string title = "Spawning Failed";

    public SpawnException(string message) : base(message) { }
    public SpawnException(string title, string message) : base(message) => this.title = title;
}
