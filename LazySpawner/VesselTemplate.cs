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
    // Used to size the vessel for placement.
    public List<Vector3> partPositions = new List<Vector3>();

    // Rough radius of a sphere around the root part enclosing the vessel.
    public float radius;

    public string DisplayName => KSP.Localization.Localizer.Format(name);

    public void CalculateBounds(Vector3 craftSize)
    {
        float furthest = 0;

        foreach (Vector3 position in partPositions)
            furthest = Mathf.Max(furthest, position.magnitude);

        // Part positions are part origins, which undersizes the vessel by roughly a part's size.
        // The craft's bounding box helps with big parts, when we have it.
        radius = Mathf.Max(furthest + 2f, craftSize.magnitude * 0.5f);
    }

    // Height of the root part above the vessel's lowest part, when upright.
    public float HeightAboveBottom(Quaternion rotation)
    {
        float lowest = 0;

        foreach (Vector3 position in partPositions)
            lowest = Mathf.Min(lowest, (rotation * position).y);

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

        template.CalculateBounds(Vector3.zero);

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
