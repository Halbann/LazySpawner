using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static LazySpawner.Localisation;

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

    // How many kerbals a crew mode puts aboard, besides any listed: one pilot, every command seat, or every seat.
    public int Seats(CrewMode mode)
    {
        List<Part> crewable = Parts.Select(p => p.info?.partPrefab).Where(Crewable).ToList();
        return mode switch
        {
            CrewMode.None => 0,
            CrewMode.Pilot => Math.Min(1, crewable.Count),
            CrewMode.FillCommand => crewable.Where(p => p.HasModuleImplementing<ModuleCommand>()).Sum(p => p.CrewCapacity),
            _ => crewable.Sum(p => p.CrewCapacity),
        };
    }

    // Not external seats, which need a kerbal on EVA to sit in them.
    internal static bool Crewable(Part part) =>
        part != null && part.CrewCapacity > 0 && !part.HasModuleImplementing<KerbalSeat>();

    public static VesselTemplate FromCraft(string craftPath) => CraftParser.Parse(craftPath);

    // A craft as a craft file would have it, like the one in the editor: EditorLogic.fetch.ship.SaveShip().
    public static VesselTemplate FromCraft(ConfigNode craft) => CraftParser.Parse(craft);

    // Rotation of the part the vessel will most likely be controlled from, relative to the root.
    // The spawner works out the real one once the crew are aboard. This is for previews.
    public Quaternion ReferenceRotation => referenceRotation ??= EstimateReferenceRotation();
    private Quaternion? referenceRotation;

    // A clone keeps the part it was controlled from. Otherwise, guess the crew will be wherever there are seats.
    private Quaternion EstimateReferenceRotation()
    {
        if (Parts.Count == 0)
            return Quaternion.identity;

        string reference = node.GetValue("ref");
        int index = reference == null || reference == "0" ? -1 : Array.FindIndex(node.GetNodes("PART"), p => p.GetValue("uid") == reference);
        List<Part> prefabs = Parts.Select(p => p.info?.partPrefab).ToList();
        return Parts[index >= 0 ? index : ControlPart(prefabs, i => prefabs[i].CrewCapacity > 0)].rotation;
    }

    // The part a vessel is controlled from, as launching picks it: the root part if it can control the vessel,
    // otherwise the first crewed control part, then the first control part, then the root. The parts are in
    // top-down tree order, the root first, which is the order the stock game searches in.
    internal static int ControlPart(IList<Part> prefabs, Func<int, bool> crewed) =>
        Enumerable.Range(0, prefabs.Count)
            .Where(i => prefabs[i] != null && prefabs[i].isControlSource > Vessel.ControlLevel.NONE)
            .OrderBy(i => i == 0 ? 0 : crewed(i) ? 1 : 2)
            .DefaultIfEmpty(0)
            .First();

    // Height of the root part above the vessel's lowest point, when turned this way.
    internal float HeightAboveBottom(Quaternion rotation) =>
        -Mathf.Min(0, VesselBounds.Corners(LandedBounds).Min(corner => (rotation * corner).y));

    #region From Vessel

    public static VesselTemplate FromVessel(Vessel vessel)
    {
        if (vessel == null)
            throw new ArgumentNullException(nameof(vessel));

        if (vessel.isEVA)
            throw new SpawnException(Loc("Error_CloneEVA"));

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
    public string title = Loc("Error_Title");

    public SpawnException(string message) : base(message) { }
    public SpawnException(string title, string message) : base(message) => this.title = title;
}
