using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LazySpawner;

// The stock launch sites, and Making History's, as places to put landed vessels.
internal static class LaunchSites
{
    public class Site
    {
        public string name;
        public CelestialBody body;
        public double latitude, longitude;
        public float heading;
    }

    private static List<Site> all;

    public static List<Site> On(CelestialBody body) =>
        (all ??= Find()).Where(s => s.body == body).ToList();

    // The site at these coordinates, give or take a few metres.
    public static Site At(CelestialBody body, double latitude, double longitude)
    {
        if (body == null)
            return null;

        Vector3d position = body.GetRelSurfacePosition(latitude, longitude, 0);
        return On(body).FirstOrDefault(s => (body.GetRelSurfacePosition(s.latitude, s.longitude, 0) - position).magnitude < 20);
    }

    private static List<Site> Find()
    {
        List<Site> sites = new List<Site>();
        PSystemSetup setup = PSystemSetup.Instance;
        if (setup == null)
            return sites;

        foreach (PSystemSetup.SpaceCenterFacility facility in setup.SpaceCenterFacilityLaunchSites)
            foreach (PSystemSetup.SpaceCenterFacility.SpawnPoint point in facility.spawnPoints)
            {
                point.GetSpawnPointLatLonAlt(out double latitude, out double longitude, out _);
                Add(sites, KSP.Localization.Localizer.Format(facility.facilityDisplayName), facility.hostBody, latitude, longitude, point.GetSpawnPointTransform());
            }

        foreach (LaunchSite site in setup.LaunchSites)
            foreach (LaunchSite.SpawnPoint point in site.spawnPoints)
            {
                point.GetSpawnPointLatLonAlt(out double latitude, out double longitude, out _);
                Add(sites, KSP.Localization.Localizer.Format(site.launchSiteName), site.Body, latitude, longitude, point.GetSpawnPointTransform());
            }

        return sites;
    }

    private static void Add(List<Site> sites, string name, CelestialBody body, double latitude, double longitude, Transform spawn)
    {
        if (body == null || sites.Any(s => s.name == name))
            return;

        // Spawn points face the way vessels should, like along the runway.
        Vector3 up = body.GetSurfaceNVector(latitude, longitude);
        Vector3 north = Placement.SurfaceFrame(body, latitude, longitude, 0) * Vector3.forward;
        Vector3 east = Placement.SurfaceFrame(body, latitude, longitude, 90) * Vector3.forward;
        float Heading(Vector3 direction)
        {
            Vector3 flat = Vector3.ProjectOnPlane(direction, up);
            return flat.sqrMagnitude < 1e-4f ? 0 : (Mathf.Atan2(Vector3.Dot(flat, east), Vector3.Dot(flat, north)) * Mathf.Rad2Deg + 360) % 360;
        }

        sites.Add(new Site { name = name, body = body, latitude = latitude, longitude = longitude, heading = spawn != null ? Mathf.Round(Heading(spawn.forward)) : 90 });
    }
}
