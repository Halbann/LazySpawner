using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
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
// Placing, after pressing Place, goes by whatever is under the mouse:
// - Flight view, the ground: the ghost follows the mouse over the terrain.
// - Flight view, the sky: the ghost follows the mouse around the active vessel, unless that's near the ground.
// - Map view, a planet or moon: a marker follows the mouse over it.
// - Map view, space: a circular orbit through the mouse.
// Click to spawn. The editor's rotation keys turn the vessel, ctrl-click keeps placing, right-click or escape stops.
public class PlacementTool : MonoBehaviour
{
    public Controller gui;

    private enum Kind { Ground, Space, Map, MapOrbit }

    public bool Placing { get; private set; }
    private Kind kind;

    // Whether the previewed vessels would land on top of another vessel.
    public bool PreviewBlocked { get; private set; }

    private const string lockID = "LazySpawnerPlacement";
    private const int maxGhosts = 25;
    private const float ghostRange = 15000;

    // Ghosts.
    private VesselTemplate ghostTemplate;
    private readonly List<Ghost> ghosts = new List<Ghost>();

    // Map view.
    private OrbitRenderer orbitRenderer;
    private OrbitDriver orbitDriver;
    // World positions, turned into screen positions as late as possible so they keep up with the camera.
    private readonly List<(Vector3d position, CelestialBody body)> markers = new List<(Vector3d, CelestialBody)>();
    private static Texture2D markerTexture;

    // Placing.
    private List<SpawnSituation> placed;
    private bool placeValid;
    private string placeInfo;
    private float placeHeading = 90;
    private Quaternion spaceTurn = Quaternion.identity;
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

        if (orbitRenderer != null)
            Destroy(orbitRenderer.gameObject);
    }

    public void Begin()
    {
        Placing = true;

        // Keep the camera, lose everything that would react to the clicks and keys.
        ControlTypes locks = ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.PAUSE | ControlTypes.MAP_UI | ControlTypes.TARGETING;
        InputLockManager.SetControlLock(locks, lockID);
    }

    public void Stop()
    {
        Placing = false;
        placed = null;
        InputLockManager.RemoveControlLock(lockID);
    }

    // Done placing: bring the screen back.
    private void Finish()
    {
        Stop();
        gui.Open();
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

            if (Placing)
                UpdatePlacing(template, map, ref ghostsUsed, ref lineUsed);
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

            if (orbitRenderer != null && !lineUsed)
                orbitRenderer.drawMode = OrbitRendererBase.DrawMode.OFF;
        }
    }

    private void UpdatePreview(VesselTemplate template, bool map, ref int ghostsUsed, ref bool lineUsed)
    {
        switch (Controller.situationMode.Value)
        {
            case Controller.SituationMode.Landed:
                List<SpawnSituation> landed = gui.PreviewSituations(template);
                if (landed == null)
                    return;

                if (map)
                    AddMarkers(landed);
                else
                {
                    PreviewBlocked = Clearance(template, landed, out _) < 0.5f;
                    ShowGhosts(template, landed, ref ghostsUsed, PreviewBlocked ? Ghost.invalidColor : Ghost.validColor);
                }
                break;

            case Controller.SituationMode.Orbit:
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

    private void UpdatePlacing(VesselTemplate template, bool map, ref int ghostsUsed, ref bool lineUsed)
    {
        // Turn with the editor's keys, the same way: 90° a press, or 5° with its fine tweak key.
        float step = GameSettings.Editor_fineTweak.GetKey(true) ? 5 : 90;
        bool Pressed(KeyBinding key) => key.GetKeyDown(true);
        float roll = Pressed(GameSettings.Editor_rollLeft) ? step : Pressed(GameSettings.Editor_rollRight) ? -step : 0;

        if (kind == Kind.MapOrbit)
        {
            if (roll != 0)
                reverseOrbit = !reverseOrbit;
        }
        else if (kind == Kind.Ground)
            placeHeading = (placeHeading - roll + 360f) % 360f;
        else
        {
            // Around fixed axes, as in the editor: the camera's up, its right, and prograde.
            float pitch = Pressed(GameSettings.Editor_pitchUp) ? -step : Pressed(GameSettings.Editor_pitchDown) ? step : 0;
            float yaw = Pressed(GameSettings.Editor_yawLeft) ? step : Pressed(GameSettings.Editor_yawRight) ? -step : 0;
            spaceTurn = Quaternion.AngleAxis(roll, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right) * Quaternion.AngleAxis(yaw, Vector3.forward) * spaceTurn;
            if (Pressed(GameSettings.Editor_resetRotation))
                spaceTurn = Quaternion.identity;
        }

        // Stop on escape, or on a right click that wasn't a camera drag.
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Finish();
            return;
        }

        if (Input.GetMouseButtonDown(1))
        {
            rightClickStart = Input.mousePosition;
            rightClickTime = Time.unscaledTime;
        }
        else if (Input.GetMouseButtonUp(1) && (Input.mousePosition - rightClickStart).magnitude < 6 && Time.unscaledTime - rightClickTime < 0.4f)
        {
            Finish();
            return;
        }

        placed = null;
        placeValid = false;
        placeInfo = null;

        // Whatever is under the mouse: the ground, or else space.
        if (map)
            kind = PlaceOnMap(template) ? Kind.Map : PlaceOrbit(template);
        else
            kind = PlaceOnGround(template) ? Kind.Ground : PlaceInSpace(template);

        if (placed == null)
            return;

        if (kind == Kind.MapOrbit)
        {
            DrawOrbit(placed[0].orbit);
            lineUsed = true;
            AddMarkers(placed);
        }
        else if (kind == Kind.Map)
            AddMarkers(placed);
        else
            ShowGhosts(template, placed, ref ghostsUsed, placeValid ? Ghost.validColor : Ghost.invalidColor);

        // Spawn on click, unless the click was for some UI.
        bool clicked = Input.GetMouseButtonDown(0);

        if (clicked && placeValid && !MouseOverUI())
        {
            gui.SpawnAt(placed);

            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
                Finish();
        }
    }

    #endregion

    #region Placing

    // False if the mouse isn't on the ground, and the active vessel is far enough off it to place beside it instead.
    private bool PlaceOnGround(VesselTemplate template)
    {
        Vessel active = FlightGlobals.ActiveVessel;
        if (active == null)
            return true;

        Ray ray = FlightCamera.fetch.mainCamera.ScreenPointToRay(MousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, ghostRange, 1 << 15, QueryTriggerInteraction.Ignore))
        {
            if (!active.LandedOrSplashed && active.situation != Vessel.Situations.PRELAUNCH && active.radarAltitude > 2000)
                return false;

            placeInfo = "Point at the ground.";
            return true;
        }

        CelestialBody body = active.mainBody;
        body.GetLatLonAlt(hit.point, out double latitude, out double longitude, out _);

        placed = Formations.LandedRow(template, body, latitude, longitude, placeHeading, Controller.count.Valid ? Controller.count.value : 1, false);

        // Steep ground tips things over, and overlapping vessels explode.
        Vector3 up = body.GetSurfaceNVector(latitude, longitude);
        float slope = Vector3.Angle(hit.normal, up);
        float gap = Clearance(template, placed, out Vessel nearest);
        bool clear = gap >= 0.5f;

        placeValid = slope < 30 && clear;
        placeInfo = $"{template.DisplayName} · {HeadingText}" +
            (nearest != null && clear ? $" · {FormatDistance(gap)} clear of {nearest.GetDisplayName()}" : "") +
            (slope >= 30 ? $"\n<color=#ff7766>Too steep ({slope:F0}°)</color>" : "") +
            (!clear ? $"\n<color=#ff7766>Touching {nearest.GetDisplayName()}</color>" : "");
        return true;
    }

    private Kind PlaceInSpace(VesselTemplate template)
    {
        Vessel active = FlightGlobals.ActiveVessel;
        if (active == null)
            return Kind.Space;

        // The ghost moves over a plane through the active vessel, facing the camera.
        Camera camera = FlightCamera.fetch.mainCamera;
        Ray ray = camera.ScreenPointToRay(MousePosition);
        Vector3 centre = active.transform.position;
        Plane plane = new Plane(-camera.transform.forward, centre);

        if (!plane.Raycast(ray, out float enter))
            return Kind.Space;

        Vector3 point = ray.GetPoint(enter);

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(active.mainBody, point, active.obt_velocity, UT);

        // Nose prograde and roof up on screen, then turned. A control part's up is its nose, and its forward its belly.
        Vector3 prograde = ((Vector3)active.obt_velocity).normalized;
        Vector3 up = Vector3.ProjectOnPlane(camera.transform.up, prograde);
        Quaternion frame = Quaternion.LookRotation(prograde, up.sqrMagnitude > 1e-4f ? up : point - (Vector3)active.mainBody.position);
        Quaternion rotation = frame * spaceTurn * Quaternion.LookRotation(Vector3.down, Vector3.forward);

        placed = new List<SpawnSituation>();
        int number = Controller.count.Valid ? Controller.count.value : 1;
        for (int i = 0; i < number; i++)
            placed.Add(SpawnSituation.Orbiting(Formations.Cluster(orbit, template, i, Controller.randomRotation), rotation));

        // Measured between the vessels' boxes, not their centres.
        float gap = Clearance(template, placed, out Vessel nearest);

        placeValid = gap >= 1f;
        placeInfo = $"{template.DisplayName} · " +
            (nearest == null ? "" : placeValid ? $"{FormatDistance(gap)} clear of {nearest.GetDisplayName()}" : $"<color=#ff7766>Touching {nearest.GetDisplayName()}</color>");
        return Kind.Space;
    }

    // False if the mouse isn't over a planet or moon.
    private bool PlaceOnMap(VesselTemplate template)
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
            return false;

        Vector3d direction = (hitPoint - hitBody.scaledBody.transform.position).normalized;
        Vector3d surface = hitBody.position + direction * hitBody.Radius;
        double latitude = hitBody.GetLatitude(surface);
        double longitude = hitBody.GetLongitude(surface);

        string name = hitBody.displayName.LocalizeRemoveGender();

        if (hitBody.pqsController == null)
        {
            placeInfo = $"{name} has no surface.";
            return true;
        }

        placed = Formations.LandedRow(template, hitBody, latitude, longitude, placeHeading, Controller.count.Valid ? Controller.count.value : 1, false);
        placeValid = true;

        string biome = ScienceUtil.GetExperimentBiomeLocalized(hitBody, latitude, longitude);
        double terrain = hitBody.TerrainAltitude(latitude, longitude, true);
        string water = hitBody.ocean && terrain < 0 && (biome ?? "").IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 ? " · on the water" : "";
        placeInfo = $"{template.DisplayName} · {name}{(string.IsNullOrEmpty(biome) ? "" : ", " + biome)}{water}\n" +
            $"{latitude:F3}°, {longitude:F3}° · {HeadingText}";
        return true;
    }

    // A circular orbit through the point under the mouse, in the plane facing the camera.
    // Look down on the north pole for an equatorial orbit, from the side for a polar one.
    private Kind PlaceOrbit(VesselTemplate template)
    {
        CelestialBody body = MapBody();
        if (body == null)
            return Kind.MapOrbit;

        Camera camera = PlanetariumCamera.Camera;
        Ray ray = camera.ScreenPointToRay(MousePosition);
        Vector3 centre = body.scaledBody.transform.position;
        Vector3 facing = camera.transform.forward;

        if (!new Plane(facing, centre).Raycast(ray, out float enter))
            return Kind.MapOrbit;

        Vector3d radial = ((Vector3d)(ray.GetPoint(enter) - centre)) * ScaledSpace.ScaleFactor;
        double radius = radial.magnitude;
        double altitude = radius - body.Radius;
        string name = body.displayName.LocalizeRemoveGender();

        if (radius <= body.Radius)
        {
            placeInfo = $"Point further out from {name}.";
            return Kind.MapOrbit;
        }

        if (radius >= body.sphereOfInfluence)
        {
            placeInfo = $"That's outside {name}'s sphere of influence.";
            return Kind.MapOrbit;
        }

        // Anticlockwise as seen by the camera, like most orbits seen from the north. Q/E reverse it.
        // Unity is left-handed, so a normal pointing away from the camera is anticlockwise on screen.
        Vector3d normal = reverseOrbit ? -(Vector3d)facing : (Vector3d)facing;
        Vector3d prograde = Vector3d.Cross(normal, radial).normalized;
        double speed = Math.Sqrt(body.gravParameter / radius);

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(body, body.position + radial, prograde * speed, UT);

        // Together where you click. Spreading them around the orbit is for the Orbit mode.
        int number = Controller.count.Valid ? Controller.count.value : 1;
        placed = new List<SpawnSituation>();
        for (int i = 0; i < number; i++)
            placed.Add(SpawnSituation.Orbiting(Formations.Cluster(orbit, template, i, Controller.randomRotation)));

        bool inAtmosphere = body.atmosphere && altitude < body.atmosphereDepth;
        placeValid = true;
        placeInfo = $"{template.DisplayName} · {name} · altitude {FormatDistance((float)altitude)} · inclination {orbit.inclination:F1}°" +
            (inAtmosphere ? "\n<color=#ff7766>Inside the atmosphere</color>" : "");
        return Kind.MapOrbit;
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

        return Controller.body.Valid ? Controller.body.value : null;
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

    // The smallest gap between any of the vessels being placed and any loaded vessel, measured between
    // their boxes. Negative if they overlap.
    private static float Clearance(VesselTemplate template, List<SpawnSituation> situations, out Vessel nearest)
    {
        nearest = null;
        float smallest = float.MaxValue;

        foreach (SpawnSituation situation in situations)
        {
            Box box = Box.Of(template, situation);

            foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
            {
                float gap = box.Gap(Box.Of(vessel));
                if (gap < smallest)
                {
                    smallest = gap;
                    nearest = vessel;
                }
            }
        }

        return smallest;
    }

    // Random headings are picked on spawning, so the ghost doesn't spin about.
    private string HeadingText => Controller.randomRotation ? "random heading" : $"heading {placeHeading:F0}°";

    private static bool MouseOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

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

            if (situation.landed && situation.body != FlightGlobals.currentMainBody)
                continue;

            (Vector3d position, Quaternion rotation) = Spawner.Pose(template, situation);
            if (((Vector3)position - camera).magnitude > ghostRange)
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

            markers.Add((world, situation.body));
        }
    }

    // Drawn by the stock orbit renderer, the way contracts draw their target orbits, so the preview
    // takes exactly the same path as every other orbit line: same camera, same frame, same timing.
    // It's the stock component itself rather than a subclass, because Unity's script execution
    // order is set per class, and the timing is the whole point.
    private void DrawOrbit(Orbit orbit)
    {
        if (orbitRenderer == null)
        {
            GameObject line = new GameObject("LazySpawner Orbit Preview");
            if (ScaledSpace.Instance != null)
                line.transform.parent = ScaledSpace.Instance.transform;

            // A driver that doesn't drive, just holds the orbit.
            orbitDriver = line.AddComponent<OrbitDriver>();
            orbitDriver.orbit = orbit;
            orbitDriver.enabled = false;

            orbitRenderer = line.AddComponent<OrbitRenderer>();
            orbitRenderer.driver = orbitDriver;
            orbitDriver.Renderer = orbitRenderer;

            // Like a vessel's orbit, the line is brightest where the (first) vessel will be.
            orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.NONE;
            orbitRenderer.drawNodes = false;
            orbitRenderer.autoTextureOffset = true;

            // Orbit lines are drawn at half their node colour. Never fade out with zoom.
            orbitRenderer.nodeColor = new Color(0.3f, 1.2f, 2f, 1f);
            orbitRenderer.lowerCamVsSmaRatio = 0;
            orbitRenderer.upperCamVsSmaRatio = float.MaxValue;
        }

        orbitDriver.orbit = orbit;
        orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
    }

    #endregion

    #region GUI

    protected void OnGUI()
    {
        if (!gui.IsOpen || Event.current.type != EventType.Repaint)
            return;

        if (markers.Count > 0)
            DrawMarkers();

        if (Placing)
            DrawHint();
    }

    private void DrawMarkers()
    {
        Camera camera = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION ? PlanetariumCamera.Camera : null;
        if (camera == null)
            return;

        Color previous = GUI.color;
        GUI.color = !Placing || placeValid ? new Color(0.45f, 0.85f, 1f) : new Color(1f, 0.4f, 0.3f);

        foreach ((Vector3d world, CelestialBody body) in markers)
        {
            Vector3 marker = ScaledSpace.LocalToScaledSpace(world);
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

        string roll = GameSettings.Editor_rollLeft.name + "/" + GameSettings.Editor_rollRight.name;
        string all = string.Concat(new[] { GameSettings.Editor_pitchDown, GameSettings.Editor_yawLeft, GameSettings.Editor_pitchUp, GameSettings.Editor_yawRight, GameSettings.Editor_rollLeft, GameSettings.Editor_rollRight }.Select(k => k.name));
        string fine = $"{GameSettings.Editor_fineTweak.name} for 5°";
        string controls = "Click to spawn · " + (kind == Kind.MapOrbit ? $"Turn the camera to tilt the orbit · {roll} to reverse · "
            : Controller.randomRotation ? ""
            : kind == Kind.Space ? $"{all} to rotate, {fine}, {GameSettings.Editor_resetRotation.name} to reset · "
            : $"{roll} to turn, {fine} · ")
            + "Ctrl-click to keep going · Right-click to stop";

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
