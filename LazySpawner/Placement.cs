using System;
using UnityEngine;

namespace LazySpawner;

// Where and how a vessel should appear: in orbit, or landed on the surface.
public class SpawnSituation
{
    public CelestialBody body;
    public bool landed;

    // Orbit.
    public Orbit orbit;

    // Landed. The spawner finds the ground, and lifts the vessel so it stands on it.
    public double latitude;
    public double longitude;
    public float heading; // Degrees clockwise from north, for the vessel's nose.

    // World rotation of the part the vessel is controlled from, as the navball sees it.
    // By default, prograde with the roof away from the body in orbit, and upright facing the heading on the ground.
    public Quaternion? rotation;

    public static SpawnSituation Orbiting(Orbit orbit, Quaternion? rotation = null) => new SpawnSituation
    {
        body = orbit.referenceBody,
        orbit = orbit,
        rotation = rotation,
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

    // World rotation that points a control part's nose prograde, with its roof facing away from the body.
    public static Quaternion Prograde(Orbit orbit, double UT)
    {
        Vector3d position = orbit.getPositionAtUT(UT);
        Vector3d velocity = orbit.getOrbitalVelocityAtUT(UT).xzy;
        Vector3d radialOut = (position - orbit.referenceBody.position).normalized;

        if (velocity.sqrMagnitude < 1e-6)
            velocity = Vector3d.Cross(radialOut, orbit.referenceBody.transform.up);

        // A reference transform's up is its nose, and its forward points out of its belly.
        return Quaternion.LookRotation(-radialOut, velocity);
    }

    #endregion
}
