using KSP.Localization;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LazySpawner;

// Shows where vessels will appear, and lets the player point at where they want them.
//
// Previews, whenever the window is open:
// - Landed, flight view: a ghost of each vessel at the coordinates.
// - Landed, map view: a marker on the planet for each vessel.
// - Orbit, map view: the orbit, with a marker for each vessel.
//
// Placing, after pressing Place:
// - Flight view, near the ground: the ghost follows the mouse over the terrain.
// - Flight view, in space: the ghost follows the mouse around the active vessel.
// - Map view: a marker follows the mouse over any planet or moon.
// Click to spawn. Q and E turn the vessel, shift-click keeps placing, right-click or escape stops.
public class PlacementTool : MonoBehaviour
{
    public IMGUI gui;

    public enum Kind { None, Ground, Space, Map, MapOrbit }

    private bool IsMapKind => Placing == Kind.Map || Placing == Kind.MapOrbit;
    public Kind Placing { get; private set; } = Kind.None;

    // Whether the previewed vessels would land on top of another vessel.
    public bool PreviewBlocked { get; private set; }

    private const string lockID = "LazySpawnerPlacement";
    private const int maxGhosts = 25;
    private const float ghostRange = 15000;

    // Ghosts.
    private VesselTemplate ghostTemplate;
    private readonly List<Ghost> ghosts = new List<Ghost>();

    // Map view.
    private LineRenderer orbitLine;
    private Material lineMaterial;
    private readonly List<(Vector3 position, CelestialBody body)> markers = new List<(Vector3, CelestialBody)>();
    private static Texture2D markerTexture;

    // Placing.
    private List<SpawnSituation> placed;
    private bool placeValid;
    private string placeInfo;
    private float placeHeading;
    private bool reverseOrbit;
    private Vector3 rightClickStart;
    private float rightClickTime;
    private GUIStyle hintStyle;


    private static Vector3 MousePosition => Input.mousePosition;

    #region Lifecycle

    protected void Start()
    {
        markerTexture ??= CreateMarkerTexture();
    }

    protected void OnDestroy()
    {
        Stop();
        ClearGhosts();

        if (orbitLine != null)
            Destroy(orbitLine.gameObject);
        if (lineMaterial != null)
            Destroy(lineMaterial);
    }

    public void Begin(Kind kind)
    {
        if (kind == Kind.None)
        {
            Stop();
            return;
        }

        Placing = kind;
        placeHeading = IMGUI.heading.Valid ? IMGUI.heading.value : 0;

        // Keep the camera, lose everything that would react to the clicks and keys.
        ControlTypes locks = ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.PAUSE | ControlTypes.MAP_UI | ControlTypes.TARGETING;
        InputLockManager.SetControlLock(locks, lockID);
    }

    public void Stop()
    {
        Placing = Kind.None;
        placed = null;
        InputLockManager.RemoveControlLock(lockID);
    }

    #endregion

    #region Update

    protected void LateUpdate()
    {
        markers.Clear();
        PreviewBlocked = false;
        int ghostsUsed = 0;
        bool lineUsed = false;

        try
        {
            if (!gui.IsOpen)
            {
                Stop();
                return;
            }

            VesselTemplate template = gui.PreviewTemplate();
            if (template == null)
            {
                Stop();
                return;
            }

            bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

            // Placing only makes sense in the view it started in.
            if (IsMapKind != map && Placing != Kind.None)
                Stop();

            if (Placing != Kind.None)
                UpdatePlacing(template, ref ghostsUsed, ref lineUsed);
            else
                UpdatePreview(template, map, ref ghostsUsed, ref lineUsed);
        }
        catch (Exception e)
        {
            Logger.Error($"Preview failed: {e}");
            Stop();
        }
        finally
        {
            for (int i = ghostsUsed; i < ghosts.Count; i++)
                ghosts[i].Visible = false;

            if (orbitLine != null && !lineUsed)
                orbitLine.enabled = false;
        }
    }

    private void UpdatePreview(VesselTemplate template, bool map, ref int ghostsUsed, ref bool lineUsed)
    {
        switch (IMGUI.situationMode.Value)
        {
            case IMGUI.SituationMode.Landed:
                List<SpawnSituation> landed = gui.PreviewSituations(template);
                if (landed == null)
                    return;

                if (map)
                    AddMarkers(landed);
                else
                {
                    PreviewBlocked = !IsClear(template, landed);
                    ShowGhosts(template, landed, ref ghostsUsed, PreviewBlocked ? Ghost.invalidColor : Ghost.validColor);
                }
                break;

            case IMGUI.SituationMode.Orbit:
                if (!map)
                    return;

                List<SpawnSituation> orbits = gui.PreviewSituations(template);
                if (orbits == null || orbits.Count == 0)
                    return;

                DrawOrbit(orbits[0].orbit);
                lineUsed = true;
                AddMarkers(orbits);
                break;
        }
    }

    private void UpdatePlacing(VesselTemplate template, ref int ghostsUsed, ref bool lineUsed)
    {
        // Turn, or reverse an orbit.
        if (Placing == Kind.MapOrbit)
        {
            if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.E))
                reverseOrbit = !reverseOrbit;
        }
        else
        {
            float turn = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            placeHeading = (placeHeading + turn * 90f * Time.unscaledDeltaTime + 360f) % 360f;
        }

        // Stop on escape, or on a right click that wasn't a camera drag.
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Stop();
            return;
        }

        if (Input.GetMouseButtonDown(1))
        {
            rightClickStart = Input.mousePosition;
            rightClickTime = Time.unscaledTime;
        }
        else if (Input.GetMouseButtonUp(1) && (Input.mousePosition - rightClickStart).magnitude < 6 && Time.unscaledTime - rightClickTime < 0.4f)
        {
            Stop();
            return;
        }

        placed = null;
        placeValid = false;
        placeInfo = null;

        switch (Placing)
        {
            case Kind.Ground: PlaceOnGround(template); break;
            case Kind.Space: PlaceInSpace(template); break;
            case Kind.Map: PlaceOnMap(template); break;
            case Kind.MapOrbit: PlaceOrbit(template); break;
        }

        if (placed == null)
            return;

        if (Placing == Kind.MapOrbit)
        {
            DrawOrbit(placed[0].orbit);
            lineUsed = true;
            AddMarkers(placed);
        }
        else if (Placing == Kind.Map)
            AddMarkers(placed);
        else
            ShowGhosts(template, placed, ref ghostsUsed, placeValid ? Ghost.validColor : Ghost.invalidColor);

        // Spawn on click, unless the click was for some UI.
        bool clicked = Input.GetMouseButtonDown(0);

        if (clicked && placeValid && !MouseOverUI())
        {
            List<SpawnSituation> situations = placed;
            bool keepPlacing = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (Placing == Kind.MapOrbit)
                gui.SetOrbit(situations[0].orbit);
            else if (situations[0].landed)
                gui.SetLanded(situations[0].body, situations[0].latitude, situations[0].longitude, placeHeading, Placing != Kind.Ground || IMGUI.situationMode != IMGUI.SituationMode.Nearby);

            gui.SpawnAt(situations);

            if (!keepPlacing)
                Stop();
        }
    }

    #endregion

    #region Placing

    private void PlaceOnGround(VesselTemplate template)
    {
        Vessel active = FlightGlobals.ActiveVessel;
        if (active == null)
            return;

        Ray ray = FlightCamera.fetch.mainCamera.ScreenPointToRay(MousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, ghostRange, 1 << 15, QueryTriggerInteraction.Ignore))
        {
            placeInfo = "Point at the ground.";
            return;
        }

        CelestialBody body = active.mainBody;
        body.GetLatLonAlt(hit.point, out double latitude, out double longitude, out _);

        placed = IMGUI.LandedRow(template, body, latitude, longitude, placeHeading, IMGUI.count.Valid ? IMGUI.count.value : 1, false);

        // Steep ground tips things over, and overlapping vessels explode.
        Vector3 up = body.GetSurfaceNVector(latitude, longitude);
        float slope = Vector3.Angle(hit.normal, up);
        float distance = (float)(hit.point - active.transform.position).magnitude;
        bool clear = IsClear(template, placed);

        placeValid = slope < 30 && clear;
        placeInfo = $"{Localizer.Format(template.name)} · heading {placeHeading:F0}° · {FormatDistance(distance)} from {active.GetDisplayName()}" +
            (slope >= 30 ? $"\n<color=#ff7766>Too steep ({slope:F0}°)</color>" : "") +
            (!clear ? "\n<color=#ff7766>Too close to another vessel</color>" : "");
    }

    private void PlaceInSpace(VesselTemplate template)
    {
        Vessel active = FlightGlobals.ActiveVessel;
        if (active == null)
            return;

        // The ghost moves over a plane through the active vessel, facing the camera.
        Camera camera = FlightCamera.fetch.mainCamera;
        Ray ray = camera.ScreenPointToRay(MousePosition);
        Vector3 centre = active.transform.position;
        Plane plane = new Plane(-camera.transform.forward, centre);

        if (!plane.Raycast(ray, out float enter))
            return;

        Vector3 point = ray.GetPoint(enter);
        float distance = (point - centre).magnitude;
        float minimum = Placement.VesselRadius(active) + template.radius;

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(active.mainBody, point, active.obt_velocity, UT);

        placed = new List<SpawnSituation>();
        int number = IMGUI.count.Valid ? IMGUI.count.value : 1;
        for (int i = 0; i < number; i++)
            placed.Add(SpawnSituation.Orbiting(i == 0 ? orbit : IMGUI.Cluster(orbit, template, i), IMGUI.randomRotation ? OrbitRotation.Random : OrbitRotation.Prograde));

        placeValid = distance >= minimum;
        placeInfo = $"{Localizer.Format(template.name)} · {FormatDistance(distance)} from {active.GetDisplayName()}" +
            (!placeValid ? "\n<color=#ff7766>Too close</color>" : "");
    }

    private void PlaceOnMap(VesselTemplate template)
    {
        Camera camera = PlanetariumCamera.Camera;
        Ray ray = camera.ScreenPointToRay(MousePosition);

        // The planets in the map are spheres in scaled space.
        CelestialBody hitBody = null;
        float nearest = float.MaxValue;
        Vector3 hitPoint = Vector3.zero;

        foreach (CelestialBody body in FlightGlobals.Bodies)
        {
            if (body.scaledBody == null)
                continue;

            Vector3 centre = body.scaledBody.transform.position;
            float radius = (float)(body.Radius * ScaledSpace.InverseScaleFactor);

            if (RaySphere(ray, centre, radius, out float distance) && distance < nearest)
            {
                nearest = distance;
                hitBody = body;
                hitPoint = ray.GetPoint(distance);
            }
        }

        if (hitBody == null)
        {
            placeInfo = "Point at a planet or moon.";
            return;
        }

        Vector3d direction = (hitPoint - hitBody.scaledBody.transform.position).normalized;
        Vector3d surface = hitBody.position + direction * hitBody.Radius;
        double latitude = hitBody.GetLatitude(surface);
        double longitude = hitBody.GetLongitude(surface);

        string name = hitBody.displayName.LocalizeRemoveGender();

        if (hitBody.pqsController == null)
        {
            placeInfo = $"{name} has no surface.";
            return;
        }

        placed = IMGUI.LandedRow(template, hitBody, latitude, longitude, placeHeading, IMGUI.count.Valid ? IMGUI.count.value : 1, false);
        placeValid = true;

        string biome = ScienceUtil.GetExperimentBiomeLocalized(hitBody, latitude, longitude);
        double terrain = hitBody.TerrainAltitude(latitude, longitude, true);
        string water = hitBody.ocean && terrain < 0 && (biome ?? "").IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 ? " · on the water" : "";
        placeInfo = $"{Localizer.Format(template.name)} · {name}{(string.IsNullOrEmpty(biome) ? "" : ", " + biome)}{water}\n" +
            $"{latitude:F3}°, {longitude:F3}° · heading {placeHeading:F0}°";
    }

    // A circular orbit through the point under the mouse, in the plane facing the camera.
    // Look down on the north pole for an equatorial orbit, from the side for a polar one.
    private void PlaceOrbit(VesselTemplate template)
    {
        CelestialBody body = MapBody();
        if (body == null)
            return;

        Camera camera = PlanetariumCamera.Camera;
        Ray ray = camera.ScreenPointToRay(MousePosition);
        Vector3 centre = body.scaledBody.transform.position;
        Vector3 facing = camera.transform.forward;

        if (!new Plane(facing, centre).Raycast(ray, out float enter))
            return;

        Vector3d radial = ((Vector3d)(ray.GetPoint(enter) - centre)) * ScaledSpace.ScaleFactor;
        double radius = radial.magnitude;
        double altitude = radius - body.Radius;
        string name = body.displayName.LocalizeRemoveGender();

        if (radius <= body.Radius)
        {
            placeInfo = $"Point further out from {name}.";
            return;
        }

        if (radius >= body.sphereOfInfluence)
        {
            placeInfo = $"That's outside {name}'s sphere of influence.";
            return;
        }

        // Anticlockwise as seen by the camera, like most orbits seen from the north. Q/E reverse it.
        // Unity is left-handed, so a normal pointing away from the camera is anticlockwise on screen.
        Vector3d normal = reverseOrbit ? -(Vector3d)facing : (Vector3d)facing;
        Vector3d prograde = Vector3d.Cross(normal, radial).normalized;
        double speed = Math.Sqrt(body.gravParameter / radius);

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(body, body.position + radial, prograde * speed, UT);

        int number = IMGUI.count.Valid ? IMGUI.count.value : 1;
        placed = new List<SpawnSituation>();
        for (int i = 0; i < number; i++)
        {
            Orbit spread = i == 0 ? orbit : new Orbit(orbit.inclination, orbit.eccentricity, orbit.semiMajorAxis, orbit.LAN, orbit.argumentOfPeriapsis,
                orbit.meanAnomalyAtEpoch + 2 * Math.PI * i / number, orbit.epoch, body);
            placed.Add(SpawnSituation.Orbiting(spread, IMGUI.randomRotation ? OrbitRotation.Random : OrbitRotation.Prograde));
        }

        bool inAtmosphere = body.atmosphere && altitude < body.atmosphereDepth;
        placeValid = true;
        placeInfo = $"{Localizer.Format(template.name)} · {name} · altitude {FormatDistance((float)altitude)} · inclination {orbit.inclination:F1}°" +
            (inAtmosphere ? "\n<color=#ff7766>Inside the atmosphere</color>" : "");
    }

    // The body the map is looking at, or the one in the window.
    private static CelestialBody MapBody()
    {
        MapObject target = PlanetariumCamera.fetch?.target;
        if (target != null)
        {
            if (target.celestialBody != null)
                return target.celestialBody;
            if (target.vessel != null)
                return target.vessel.mainBody;
        }

        return IMGUI.body.Valid ? IMGUI.body.value : null;
    }

    private static bool RaySphere(Ray ray, Vector3 centre, float radius, out float distance)
    {
        distance = 0;
        Vector3 offset = ray.origin - centre;
        float b = Vector3.Dot(offset, ray.direction);
        float c = offset.sqrMagnitude - radius * radius;
        float discriminant = b * b - c;

        if (discriminant < 0)
            return false;

        distance = -b - Mathf.Sqrt(discriminant);
        return distance > 0;
    }

    private static bool IsClear(VesselTemplate template, List<SpawnSituation> situations)
    {
        foreach (SpawnSituation situation in situations)
        {
            Vector3d position = situation.body.GetWorldSurfacePosition(situation.latitude, situation.longitude, situation.body.TerrainAltitude(situation.latitude, situation.longitude, true));

            foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                if ((vessel.transform.position - (Vector3)position).magnitude < Placement.VesselRadius(vessel) + template.radius * 0.8f)
                    return false;
        }

        return true;
    }

    private static bool MouseOverUI()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return true;

        return IMGUI.Instance != null && IMGUI.Instance.MouseOverWindow();
    }

    private static string FormatDistance(float metres) =>
        metres < 1000 ? $"{metres:F0} m" : $"{metres / 1000:F2} km";

    #endregion

    #region Ghosts

    private void ShowGhosts(VesselTemplate template, List<SpawnSituation> situations, ref int ghostsUsed, Color color)
    {
        if (template != ghostTemplate)
        {
            ClearGhosts();
            ghostTemplate = template;
        }

        Vector3 camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera.transform.position : Vector3.zero;

        foreach (SpawnSituation situation in situations)
        {
            if (ghostsUsed >= maxGhosts)
                return;

            Vector3 position;
            Quaternion rotation;

            if (situation.landed)
            {
                if (situation.body != FlightGlobals.currentMainBody)
                    continue;

                Spawner.LandedPose pose = Spawner.GetLandedPose(template, situation, template.ReferenceRotation);
                position = pose.position;
                rotation = pose.rotation;
            }
            else
            {
                double UT = Planetarium.GetUniversalTime();
                position = situation.orbit.getPositionAtUT(UT);
                rotation = situation.orbitRotation == OrbitRotation.Prograde
                    ? Placement.Prograde(situation.orbit, UT, template.ReferenceRotation)
                    : Quaternion.identity;
            }

            if ((position - camera).magnitude > ghostRange)
                continue;

            if (ghostsUsed >= ghosts.Count)
                ghosts.Add(new Ghost(template));

            Ghost ghost = ghosts[ghostsUsed++];
            ghost.SetPose(position, rotation);
            ghost.ShowLaunchClamps(situation.landed);
            ghost.SetColor(color);
            ghost.Visible = true;
        }
    }

    private void ClearGhosts()
    {
        foreach (Ghost ghost in ghosts)
            ghost.Destroy();

        ghosts.Clear();
        ghostTemplate = null;
    }

    #endregion

    #region Map

    private void AddMarkers(List<SpawnSituation> situations)
    {
        double UT = Planetarium.GetUniversalTime();

        foreach (SpawnSituation situation in situations)
        {
            Vector3d world = situation.landed
                ? situation.body.GetWorldSurfacePosition(situation.latitude, situation.longitude, Math.Max(0, situation.body.TerrainAltitude(situation.latitude, situation.longitude)))
                : situation.orbit.getPositionAtUT(UT);

            markers.Add((ScaledSpace.LocalToScaledSpace(world), situation.body));
        }
    }

    private void DrawOrbit(Orbit orbit)
    {
        if (orbitLine == null)
        {
            GameObject line = new GameObject("LazySpawner Orbit Preview");
            line.layer = 10; // Scaled scenery, which the map camera draws.
            orbitLine = line.AddComponent<LineRenderer>();
            orbitLine.useWorldSpace = true;
            orbitLine.loop = false;
            orbitLine.positionCount = 0;
            orbitLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            orbitLine.receiveShadows = false;

            lineMaterial = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default"));
            orbitLine.material = lineMaterial;
        }

        const int segments = 180;
        List<Vector3> points = new List<Vector3>(segments + 1);
        CelestialBody body = orbit.referenceBody;

        if (orbit.eccentricity < 1)
        {
            for (int i = 0; i <= segments; i++)
            {
                double trueAnomaly = 2 * Math.PI * i / segments;
                points.Add(ScaledSpace.LocalToScaledSpace(body.position + orbit.getRelativePositionFromTrueAnomaly(trueAnomaly).xzy));
            }
        }
        else
        {
            // Just the part of a hyperbola that's inside the sphere of influence.
            double limit = Math.Acos(-1 / orbit.eccentricity) - 0.01;
            for (int i = 0; i <= segments; i++)
            {
                double trueAnomaly = -limit + 2 * limit * i / segments;
                Vector3d relative = orbit.getRelativePositionFromTrueAnomaly(trueAnomaly);
                if (relative.magnitude > body.sphereOfInfluence)
                    continue;

                points.Add(ScaledSpace.LocalToScaledSpace(body.position + relative.xzy));
            }
        }

        orbitLine.positionCount = points.Count;
        orbitLine.SetPositions(points.ToArray());

        // Constant width on screen.
        float distance = (PlanetariumCamera.Camera.transform.position - body.scaledBody.transform.position).magnitude;
        orbitLine.widthMultiplier = distance * 0.004f;

        Color color = new Color(0.45f, 0.85f, 1f, 0.9f);
        orbitLine.startColor = orbitLine.endColor = color;
        orbitLine.enabled = true;
    }

    #endregion

    #region GUI

    protected void OnGUI()
    {
        if (!gui.IsOpen || Event.current.type != EventType.Repaint)
            return;

        if (markers.Count > 0)
            DrawMarkers();

        if (Placing != Kind.None)
            DrawHint();
    }

    private void DrawMarkers()
    {
        Camera camera = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION ? PlanetariumCamera.Camera : null;
        if (camera == null)
            return;

        Color previous = GUI.color;
        GUI.color = Placing == Kind.None || placeValid ? new Color(0.45f, 0.85f, 1f) : new Color(1f, 0.4f, 0.3f);

        foreach ((Vector3 marker, CelestialBody body) in markers)
        {
            Vector3 screen = camera.WorldToScreenPoint(marker);
            if (screen.z < 0)
                continue;

            // Hidden behind the planet?
            Vector3 toMarker = marker - camera.transform.position;
            Ray ray = new Ray(camera.transform.position, toMarker);
            float radius = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
            if (RaySphere(ray, body.scaledBody.transform.position, radius * 0.995f, out float hit) && hit < toMarker.magnitude)
                continue;

            GUI.DrawTexture(new Rect(screen.x - 8, Screen.height - screen.y - 8, 16, 16), markerTexture);
        }

        GUI.color = previous;
    }

    private void DrawHint()
    {
        hintStyle ??= new GUIStyle(HighLogic.Skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            richText = true,
            wordWrap = false,
            fontSize = 13,
            padding = new RectOffset(8, 8, 6, 6),
        };

        string controls = Placing == Kind.MapOrbit
            ? "Click to spawn · Turn the camera to tilt the orbit · Q/E to reverse · Right-click to stop"
            : Placing == Kind.Space
            ? "Click to spawn · Shift-click to keep going · Right-click to stop"
            : "Click to spawn · Q/E to turn · Shift-click to keep going · Right-click to stop";

        GUIContent content = new GUIContent((placeInfo ?? "") + "\n<color=#aaaaaa>" + controls + "</color>");
        Vector2 size = hintStyle.CalcSize(content);
        Vector2 mouse = Event.current.mousePosition;
        Rect rect = new Rect(mouse.x + 20, mouse.y + 20, size.x, size.y);

        if (rect.xMax > Screen.width) rect.x = mouse.x - 20 - size.x;
        if (rect.yMax > Screen.height) rect.y = mouse.y - 20 - size.y;

        GUI.Box(rect, content, hintStyle);
    }

    private static Texture2D CreateMarkerTexture()
    {
        // A ring with a dot in the middle.
        const int size = 32;
        Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
        float c = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x - c, y - c).magnitude;
                float ring = Mathf.Clamp01(1.5f - Mathf.Abs(r - 12f));
                float dot = Mathf.Clamp01(4.5f - r);
                texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Max(ring, dot)));
            }
        }

        texture.Apply();
        return texture;
    }

    #endregion
}
