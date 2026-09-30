using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace LazySpawner;

// A vessel ready to be spawned any number of times, from a craft file or an existing vessel.
// Build one per kind of vessel, not one per spawn: the expensive work happens here, once.
// Templates don't change after they're made, so one can be spawned from anywhere, any time.
public class VesselTemplate
{
    // VESSEL node in persistent save format. Never handed to the game directly, always copied first.
    internal ConfigNode node;

    public string Name { get; internal set; }
    public string DisplayName => KSP.Localization.Localizer.Format(Name);

    // In the same order as the vessel's parts will be, the root part first.
    public IReadOnlyList<TemplatePart> Parts { get; internal set; }

    // Rotation of the root part relative to a surface frame (y = up, z = north)
    // when the vessel sits the right way up on the ground. For craft, the root part's rotation in the editor.
    public Quaternion UprightRotation { get; internal set; } = Quaternion.identity;

    // The vessel's size, as boxes in the root part's frame. Launch clamps only come along on the ground.
    public Bounds LandedBounds { get; internal set; }
    public Bounds SpaceBounds { get; internal set; }

    public Bounds BoundsFor(bool landed) => landed ? LandedBounds : SpaceBounds;

    // Radius of a sphere around the root part that encloses the vessel.
    public float Radius(bool landed)
    {
        Bounds bounds = BoundsFor(landed);
        return bounds.center.magnitude + bounds.extents.magnitude;
    }

    public static VesselTemplate FromCraft(string craftPath) => CraftParser.Parse(craftPath);

    // Rotation of the part the vessel will most likely be controlled from, relative to the root.
    // The spawner works out the real one once the crew are aboard. This is for previews.
    public Quaternion ReferenceRotation => referenceRotation ??= EstimateReferenceRotation();
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

    // Height of the root part above the vessel's lowest point, when turned this way.
    internal float HeightAboveBottom(Quaternion rotation)
    {
        Vector3 min = LandedBounds.min, max = LandedBounds.max;
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
            Name = vessel.vesselName,
            Parts = TemplatePart.From(proto),
            // A loaded vessel can be measured as it is, procedural fairings and all.
            LandedBounds = VesselBounds.FromVessel(vessel, includeLaunchClamps: true),
            SpaceBounds = VesselBounds.FromVessel(vessel, includeLaunchClamps: false),
            // Keep the vessel's attitude relative to the ground beneath it.
            UprightRotation = Quaternion.Inverse(Placement.SurfaceFrame(vessel.mainBody, vessel.latitude, vessel.longitude, 0)) * vessel.transform.rotation,
        };

        proto.Save(template.node);

        // Strip the things that belong to the original vessel and nobody else.
        template.node.RemoveNode("TARGET");
        template.node.RemoveNode("WAYPOINT");
        template.node.GetNode("FLIGHTPLAN")?.ClearData();
        template.node.SetValue("cln", false);
        template.node.RemoveValue("clnRsn");

        return template;
    }

    #endregion

    internal static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    internal static string Format(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

// One part of a template, where it will be relative to the root part.
public struct TemplatePart
{
    public AvailablePart info;
    public string variant;
    public Vector3 position;
    public Quaternion rotation;

    internal static List<TemplatePart> From(ProtoVessel proto) =>
        proto.protoPartSnapshots.Select(s => new TemplatePart { info = s.partInfo, variant = s.moduleVariantName, position = s.position, rotation = s.rotation }).ToList();
}

public class SpawnException : Exception
{
    public string title = "Spawning Failed";

    public SpawnException(string message) : base(message) { }
    public SpawnException(string title, string message) : base(message) => this.title = title;
}
