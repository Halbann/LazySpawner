using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner;

// How the window arranges several vessels at once. These are the UI's opinions, not the backend's.
internal static class Formations
{
    // A row, side by side, centred on the coordinates. Spaced for any heading if they'll be turned at random later.
    public static List<SpawnSituation> LandedRow(VesselTemplate template, CelestialBody body, double latitude, double longitude, float heading, int number, bool anyHeading)
    {
        List<SpawnSituation> situations = new List<SpawnSituation>();
        Vector3d centre = body.GetWorldSurfacePosition(latitude, longitude, 0);
        Vector3 side = Placement.SurfaceFrame(body, latitude, longitude, heading) * Vector3.right;

        // Side by side with a few metres between them, however wide the vessel is across the row.
        Bounds bounds = template.LandedBounds;
        float width = anyHeading
            ?new Vector2(bounds.extents.x, bounds.extents.z).magnitude * 2 + bounds.center.magnitude
            : Box.Of(template, SpawnSituation.Landed(body, latitude, longitude, heading)).Extent(side) * 2;
        float spacing = width + 4f;

        for (int i = 0; i < number; i++)
        {
            double lat = latitude, lon = longitude;
            if (number > 1)
                body.GetLatLonAlt(centre + (Vector3d)side * ((i - (number - 1) * 0.5f) * spacing), out lat, out lon, out _);

            situations.Add(SpawnSituation.Landed(body, lat, lon, heading));
        }

        return situations;
    }

    // A tidy formation around the reference orbit's current position, all with the same velocity.
    // Grid points nearest the middle first, lined up with the direction of travel.
    public static Orbit Cluster(Orbit reference, VesselTemplate template, int index, bool randomRotation)
    {
        if (index == 0)
            return reference;

        double UT = Planetarium.GetUniversalTime();
        CelestialBody body = reference.referenceBody;
        Vector3d position = reference.getPositionAtUT(UT);
        Vector3d velocity = reference.getOrbitalVelocityAtUT(UT).xzy;

        Vector3d prograde = velocity.normalized;
        Vector3d radial = Vector3d.Exclude(prograde, position - body.position).normalized;
        Vector3d normal = Vector3d.Cross(prograde, radial);

        // Spacing along each axis of the formation, from the vessel's size in that direction.
        // Vessels facing any which way need room to face any which way.
        const float gap = 5;
        Vector3d spacing;
        if (randomRotation)
            spacing = Vector3d.one * (template.Radius(false) * 2 + gap);
        else
        {
            Box box = Box.Of(template, SpawnSituation.Orbiting(reference));
            spacing = new Vector3d(box.Extent(prograde) * 2 + gap, box.Extent(radial) * 2 + gap, box.Extent(normal) * 2 + gap);
        }

        Vector3 cell = GridCell(index);
        Vector3d offset = prograde * (cell.x * spacing.x) + radial * (cell.y * spacing.y) + normal * (cell.z * spacing.z);

        return Placement.OrbitFromWorldState(body, position + offset, velocity, UT);
    }

    private static List<Vector3> gridCells;

    private static Vector3 GridCell(int index)
    {
        if (gridCells == null || index >= gridCells.Count)
        {
            int n = Mathf.CeilToInt(Mathf.Pow(index + 1, 1f / 3f) / 2f) + 1;
            gridCells = new List<Vector3>();

            for (int x = -n; x <= n; x++)
                for (int y = -n; y <= n; y++)
                    for (int z = -n; z <= n; z++)
                        gridCells.Add(new Vector3(x, y, z));

            gridCells = gridCells.OrderBy(c => c.sqrMagnitude).ThenBy(c => Mathf.Abs(c.y)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
        }

        return gridCells[index];
    }

    // Spread out vessels around a vessel, either around it in orbit or on the ground around it,
    // keeping them clear of it, of other vessels, and of each other.
    public static List<SpawnSituation> Nearby(Vessel vessel, VesselTemplate template, int count, float range, bool randomRotation)
    {
        const float margin = 3f;
        const int attempts = 50;

        List<SpawnSituation> situations = new List<SpawnSituation>();
        bool landed = vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH;

        Box activeBox = Box.Of(vessel);
        List<Box> taken = FlightGlobals.VesselsLoaded.Select(Box.Of).ToList();

        // Centre to centre, from where the vessels would just touch out to the range.
        float closest = activeBox.Radius() * 0.5f;
        float furthest = Mathf.Max(range, activeBox.Radius() + template.Radius(landed) + margin * 2);

        CelestialBody body = vessel.mainBody;
        double UT = Planetarium.GetUniversalTime();
        Vector3d up = body.GetSurfaceNVector(vessel.latitude, vessel.longitude);
        float heading = Placement.Heading(vessel);

        for (int i = 0; i < count; i++)
        {
            SpawnSituation best = null;
            Box bestBox = default;
            float bestGap = float.MinValue;

            // Rejection sampling. If the space is too crowded, take the roomiest spot found.
            for (int attempt = 0; attempt < attempts && bestGap < margin; attempt++)
            {
                Vector3 direction = landed ? RandomOnDisc(up) : Random.onUnitSphere;
                Vector3d position = (Vector3d)activeBox.centre + (Vector3d)direction * Random.Range(closest, furthest);
                SpawnSituation situation;

                if (landed)
                {
                    body.GetLatLonAlt(position, out double latitude, out double longitude, out _);
                    situation = SpawnSituation.Landed(body, latitude, longitude, randomRotation ? Random.Range(0f, 360f) : heading);
                }
                else
                    situation = SpawnSituation.Orbiting(Placement.OrbitFromWorldState(body, position, vessel.obt_velocity, UT), randomRotation ? Random.rotation : null);

                Box box = Box.Of(template, situation);
                float gap = taken.Min(other => box.Gap(other));

                if (gap > bestGap)
                {
                    bestGap = gap;
                    best = situation;
                    bestBox = box;
                }
            }

            situations.Add(best);
            taken.Add(bestBox);
        }

        return situations;
    }

    private static Vector3 RandomOnDisc(Vector3d normal)
    {
        Vector3 direction = Vector3.ProjectOnPlane(Random.onUnitSphere, normal);
        return direction.sqrMagnitude < 1e-4f ? RandomOnDisc(normal) : direction.normalized;
    }
}

// A vessel's bounds placed in the world, for keeping vessels clear of each other.
internal struct Box
{
    public Vector3 centre;
    public Vector3 extents;
    public Vector3 x, y, z;

    public Box(Bounds bounds, Vector3 position, Quaternion rotation)
    {
        centre = position + rotation * bounds.center;
        extents = bounds.extents;
        x = rotation * Vector3.right;
        y = rotation * Vector3.up;
        z = rotation * Vector3.forward;
    }

    public static Box Of(Vessel vessel) =>
        new Box(Measure(vessel), vessel.transform.position, vessel.transform.rotation);

    // Where a vessel would be, if it were spawned now.
    public static Box Of(VesselTemplate template, SpawnSituation situation)
    {
        (Vector3d position, Quaternion rotation) = Spawner.Pose(template, situation);
        return new Box(template.BoundsFor(situation.landed), position, rotation);
    }

    private Vector3 Axis(int i) => i == 0 ? x : i == 1 ? y : z;

    // Half the box's length along a direction.
    public float Extent(Vector3 axis) => Radius(axis.normalized);

    private float Radius(Vector3 axis) =>
        extents.x * Mathf.Abs(Vector3.Dot(x, axis)) + extents.y * Mathf.Abs(Vector3.Dot(y, axis)) + extents.z * Mathf.Abs(Vector3.Dot(z, axis));

    // The widest gap between the boxes along any separating axis, negative if they overlap.
    // Zero or more means they don't touch. It's never more than the true distance between them.
    public float Gap(Box other)
    {
        Vector3 offset = other.centre - centre;
        float gap = float.MinValue;

        // Each box's three faces, and the cross products of their edges: the separating axis theorem.
        for (int n = 0; n < 15; n++)
        {
            Vector3 axis = n < 3 ? Axis(n) : n < 6 ? other.Axis(n - 3) : Vector3.Cross(Axis((n - 6) / 3), other.Axis((n - 6) % 3));

            float length = axis.magnitude;
            if (length < 1e-4f)
                continue;

            axis /= length;
            gap = Mathf.Max(gap, Mathf.Abs(Vector3.Dot(offset, axis)) - Radius(axis) - other.Radius(axis));
        }

        return gap;
    }

    // Radius of a sphere around the centre enclosing the box.
    public float Radius() => extents.magnitude;

    // Vessels don't change shape often, so measuring once every few seconds is plenty.
    private static readonly Dictionary<uint, (int partCount, float time, Bounds bounds)> measured = new Dictionary<uint, (int, float, Bounds)>();

    private static Bounds Measure(Vessel vessel)
    {
        int partCount = vessel.loaded ? vessel.parts.Count : vessel.protoVessel.protoPartSnapshots.Count;

        if (measured.TryGetValue(vessel.persistentId, out var entry) && entry.partCount == partCount && Time.time - entry.time < 5f)
            return entry.bounds;

        Bounds bounds = VesselBounds.FromVessel(vessel);
        measured[vessel.persistentId] = (partCount, Time.time, bounds);
        return bounds;
    }
}
