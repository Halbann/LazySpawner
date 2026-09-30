using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner;

// Where and how a vessel should appear.
public class SpawnSituation
{
    public CelestialBody body;

    // On the surface, otherwise in orbit.
    public bool landed;

    // Orbit.
    public Orbit orbit;
    public OrbitRotation orbitRotation = OrbitRotation.Prograde;
    public Quaternion worldRotation = Quaternion.identity; // OrbitRotation.Fixed only.

    // Landed.
    public double latitude;
    public double longitude;
    public float heading;

    public static SpawnSituation Orbiting(Orbit orbit, OrbitRotation rotation = OrbitRotation.Prograde) => new SpawnSituation
    {
        body = orbit.referenceBody,
        orbit = orbit,
        orbitRotation = rotation,
    };

    public static SpawnSituation Landed(CelestialBody body, double latitude, double longitude, float heading) => new SpawnSituation
    {
        body = body,
        landed = true,
        latitude = latitude,
        longitude = longitude,
        heading = heading,
    };
}

public enum OrbitRotation
{
    Prograde,
    Random,
    Fixed,
}

public static class Placement
{
    #region Frames

    // Rotation of a frame on the surface with y = up and z = the heading (degrees clockwise from north).
    public static Quaternion SurfaceFrame(CelestialBody body, double latitude, double longitude, float heading)
    {
        Vector3d up = body.GetSurfaceNVector(latitude, longitude);
        Vector3d north = North(body, up);
        Vector3d east = Vector3d.Cross(up, north);

        // Make sure east really is the direction of increasing longitude, whatever the handedness.
        Vector3d towardsEast = body.GetWorldSurfacePosition(latitude, longitude + 0.001, 0) - body.GetWorldSurfacePosition(latitude, longitude, 0);
        if (Vector3d.Dot(east, towardsEast) < 0)
            east = -east;

        double h = heading * Mathf.Deg2Rad;
        Vector3d forward = north * Math.Cos(h) + east * Math.Sin(h);

        return Quaternion.LookRotation(forward, up);
    }

    private static Vector3d North(CelestialBody body, Vector3d up)
    {
        Vector3d axis = body.transform.up;
        Vector3d north = Vector3d.Exclude(up, axis);

        // At the poles any direction will do.
        if (north.sqrMagnitude < 1e-8)
            north = Vector3d.Exclude(up, body.transform.forward);

        return north.normalized;
    }

    public static float Heading(Vessel vessel)
    {
        if (vessel == null || vessel.ReferenceTransform == null)
            return 0;

        CelestialBody body = vessel.mainBody;
        Vector3d up = body.GetSurfaceNVector(vessel.latitude, vessel.longitude);
        Vector3 forward = Vector3.ProjectOnPlane(vessel.ReferenceTransform.up, up);
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.ProjectOnPlane(-vessel.ReferenceTransform.forward, up);

        Vector3 north = SurfaceFrame(body, vessel.latitude, vessel.longitude, 0) * Vector3.forward;
        Vector3 east = SurfaceFrame(body, vessel.latitude, vessel.longitude, 90) * Vector3.forward;
        float angle = Mathf.Atan2(Vector3.Dot(forward, east), Vector3.Dot(forward, north)) * Mathf.Rad2Deg;

        return (angle + 360) % 360;
    }

    #endregion

    #region Orbits

    public static Orbit CreateOrbit(CelestialBody body, double inclination, double eccentricity, double sma, double lan, double argPe, double meanAnomalyAtEpoch, double epoch) =>
        new Orbit(inclination, eccentricity, sma, lan, argPe, meanAnomalyAtEpoch, epoch, body);

    // An orbit that passes through the given world position with the given world velocity.
    public static Orbit OrbitFromWorldState(CelestialBody body, Vector3d worldPosition, Vector3d worldVelocity, double UT)
    {
        Orbit orbit = new Orbit();
        orbit.UpdateFromStateVectors((worldPosition - body.position).xzy, worldVelocity.xzy, body, UT);
        return orbit;
    }

    public static Vessel.Situations OrbitSituation(Orbit orbit)
    {
        CelestialBody body = orbit.referenceBody;

        if (orbit.eccentricity >= 1 || orbit.ApR > body.sphereOfInfluence)
            return Vessel.Situations.ESCAPING;

        if (orbit.PeA < (body.atmosphere ? body.atmosphereDepth : 0))
            return Vessel.Situations.SUB_ORBITAL;

        return Vessel.Situations.ORBITING;
    }

    // World rotation that points the reference part's nose prograde with its roof facing away from the body.
    public static Quaternion Prograde(Orbit orbit, double UT, Quaternion referenceRelative)
    {
        Vector3d position = orbit.getPositionAtUT(UT);
        Vector3d velocity = orbit.getOrbitalVelocityAtUT(UT).xzy;
        Vector3d radialOut = (position - orbit.referenceBody.position).normalized;

        if (velocity.sqrMagnitude < 1e-6)
            velocity = Vector3d.Cross(radialOut, orbit.referenceBody.transform.up);

        // A reference transform's up is its nose, and its forward points out of its belly.
        Quaternion reference = Quaternion.LookRotation(-radialOut, velocity);
        return reference * Quaternion.Inverse(referenceRelative);
    }

    #endregion

    #region Nearby

    // Spread out vessels around a vessel, either around it in orbit or on the ground around it,
    // keeping them clear of it, of other vessels, and of each other.
    public static List<SpawnSituation> Nearby(Vessel vessel, VesselTemplate template, int count, float range, bool randomRotation)
    {
        const float margin = 3f;
        const int attempts = 50;

        List<SpawnSituation> situations = new List<SpawnSituation>();
        bool landed = vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH;
        Bounds bounds = template.BoundsFor(landed);

        VesselBounds.Box activeBox = VesselBounds.Box.Of(vessel);
        List<VesselBounds.Box> taken = new List<VesselBounds.Box> { activeBox };
        foreach (Vessel other in FlightGlobals.VesselsLoaded)
            if (other != vessel)
                taken.Add(VesselBounds.Box.Of(other));

        // Centre to centre, from where the vessels would just touch out to the range.
        float closest = activeBox.Radius() * 0.5f;
        float furthest = Mathf.Max(range, activeBox.Radius() + template.Radius(landed) + margin * 2);

        CelestialBody body = vessel.mainBody;
        double UT = Planetarium.GetUniversalTime();
        Vector3d up = body.GetSurfaceNVector(vessel.latitude, vessel.longitude);
        float heading = Heading(vessel);

        for (int i = 0; i < count; i++)
        {
            SpawnSituation best = null;
            VesselBounds.Box bestBox = default;
            float bestGap = float.MinValue;

            // Rejection sampling. If the space is too crowded, take the roomiest spot found.
            for (int attempt = 0; attempt < attempts && bestGap < margin; attempt++)
            {
                Vector3 direction = landed ? RandomOnDisc(up) : Random.onUnitSphere;
                Vector3d position = (Vector3d)activeBox.centre + (Vector3d)direction * Random.Range(closest, furthest);

                SpawnSituation situation;
                VesselBounds.Box box;

                if (landed)
                {
                    body.GetLatLonAlt(position, out double latitude, out double longitude, out _);
                    situation = SpawnSituation.Landed(body, latitude, longitude, randomRotation ? Random.Range(0f, 360f) : heading);
                    Spawner.LandedPose pose = Spawner.GetLandedPose(template, situation, template.ReferenceRotation);
                    box = new VesselBounds.Box(bounds, pose.position, pose.rotation);
                }
                else
                {
                    Orbit orbit = OrbitFromWorldState(body, position, vessel.obt_velocity, UT);
                    Quaternion rotation = randomRotation ? Random.rotation : Prograde(orbit, UT, template.ReferenceRotation);
                    situation = SpawnSituation.Orbiting(orbit, OrbitRotation.Fixed);
                    situation.worldRotation = rotation;
                    box = new VesselBounds.Box(bounds, position, rotation);
                }

                float gap = float.MaxValue;
                foreach (VesselBounds.Box other in taken)
                    gap = Mathf.Min(gap, box.Gap(other));

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

    #endregion
}
