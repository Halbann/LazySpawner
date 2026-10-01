using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using static LazySpawner.Localisation;

namespace LazySpawner;

// Shows where vessels will appear, and lets the player point at where they want them.
//
// Previews, whenever the window is open:
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

    // What the rotation keys do: turn the heading, turn freely, or reverse the orbit.
    private enum Kind { Landed, Space, Orbit }

    public bool Placing { get; private set; }
    private Kind kind;

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
    private bool placeValid;
    private string placeInfo;
    private float placeHeading = 90;
    private Quaternion spaceTurn = Quaternion.identity;
    private bool reverseOrbit;
    private Vector3 rightClickStart;
    private float rightClickTime;
    private GUIStyle hintStyle;

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

    // Keep the camera, lose everything that would react to the clicks and keys.
    private const ControlTypes locks = ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.PAUSE | ControlTypes.MAP_UI | ControlTypes.TARGETING;

    public void Begin()
    {
        Placing = true;
        InputLockManager.SetControlLock(locks, lockID);
    }

    public void Stop()
    {
        Placing = false;
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

    // Whatever is being placed, or the orbit being set up in the map: markers and the orbit line in the map, ghosts in flight.
    protected void LateUpdate()
    {
        try
        {
            VesselTemplate template = gui.IsOpen ? gui.Template(out _) : null;
            if (template == null)
                Stop();

            bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
            List<SpawnSituation> shown = template == null ? null
                : Placing ? UpdatePlacing(template, map)
                : map && Controller.situationMode == Controller.SituationMode.Orbit ? gui.PreviewSituations(template)
                : null;

            markers.Clear();
            if (map && shown != null)
                markers.AddRange(shown.Select(Marker));

            ShowGhosts(template, map ? null : shown);
            DrawOrbit(map && shown?.Count > 0 && !shown[0].landed ? shown[0].orbit : null);
        }
        catch (Exception e)
        {
            Logger.Log($"Preview failed: {e}", LogType.Error);
            Stop();
        }
    }

    private List<SpawnSituation> UpdatePlacing(VesselTemplate template, bool map)
    {
        // Turn with the editor's keys, the same way: 90° a press, or 5° with its fine tweak key.
        float step = GameSettings.Editor_fineTweak.GetKey(true) ? 5 : 90;
        bool Pressed(KeyBinding key) => key.GetKeyDown(true);
        float roll = Pressed(GameSettings.Editor_rollLeft) ? step : Pressed(GameSettings.Editor_rollRight) ? -step : 0;

        if (kind == Kind.Orbit)
        {
            if (roll != 0)
                reverseOrbit = !reverseOrbit;
        }
        else if (kind == Kind.Landed)
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

        // Ctrl and the scroll wheel for more or fewer, without the camera zooming meanwhile.
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        ControlTypes locked = ctrl ? locks | ControlTypes.CAMERACONTROLS : locks;
        if (InputLockManager.GetControlLock(lockID) != locked)
            InputLockManager.SetControlLock(locked, lockID);
        if (ctrl && GameSettings.AXIS_MOUSEWHEEL.GetAxis() != 0)
            Controller.count.Text = Mathf.Clamp(Number + Math.Sign(GameSettings.AXIS_MOUSEWHEEL.GetAxis()), 1, 1000).ToString();

        // Stop on escape, or on a right click that wasn't a camera drag.
        if (Input.GetMouseButtonDown(1))
            (rightClickStart, rightClickTime) = (Input.mousePosition, Time.unscaledTime);

        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonUp(1) && (Input.mousePosition - rightClickStart).magnitude < 6 && Time.unscaledTime - rightClickTime < 0.4f)
        {
            Finish();
            return null;
        }

        // Whatever is under the mouse: the ground, or else space. Each says how it went.
        placeValid = false;
        placeInfo = null;
        List<SpawnSituation> placed = map ? PlaceOnMap(template) ?? PlaceOrbit(template) : PlaceOnGround(template) ?? PlaceInSpace(template);

        // Spawn on click, unless the click was for some UI.
        if (Input.GetMouseButtonDown(0) && placeValid && !MouseOverUI())
        {
            gui.Spawn(placed);

            if (!ctrl)
                Finish();
        }

        return placed;
    }

    #endregion

    #region Placing

    // Null if the mouse isn't on the ground, and the active vessel is far enough off it to place beside it instead.
    private List<SpawnSituation> PlaceOnGround(VesselTemplate template)
    {
        kind = Kind.Landed;
        Vessel active = FlightGlobals.ActiveVessel;

        // Nearby ground has colliders, buildings and all. Further away there's only the terrain. From high up,
        // pointing off the ground is for placing beside the vessel.
        CelestialBody body = active.mainBody;
        Ray ray = FlightCamera.fetch.mainCamera.ScreenPointToRay(Input.mousePosition);
        bool nearGround = active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH || active.radarAltitude <= 2000;
        bool collider = Physics.Raycast(ray, out RaycastHit hit, ghostRange, 1 << 15, QueryTriggerInteraction.Ignore);
        double distance = 0;
        if (!collider && !(nearGround && Ground(body, ray.origin, ray.direction, ghostRange, 20, out distance)))
        {
            if (!nearGround)
                return null;

            placeInfo = Loc("Placing_PointAtGround");
            return new List<SpawnSituation>();
        }

        body.GetLatLonAlt(collider ? hit.point : (Vector3d)ray.origin + (Vector3d)ray.direction * distance, out double latitude, out double longitude, out _);

        List<SpawnSituation> placed = Formations.LandedRow(template, body, latitude, longitude, placeHeading, Number, Controller.randomRotation);

        // Steep ground tips things over, and overlapping vessels explode. Only nearby ground says how steep it is.
        Vector3 up = body.GetSurfaceNVector(latitude, longitude);
        float slope = collider ? Vector3.Angle(hit.normal, up) : 0;
        float gap = Clearance(template, placed, out Vessel nearest);
        bool clear = gap >= 0.5f;

        placeValid = slope < 30 && clear;
        placeInfo = $"{Named(template)} · {HeadingText}" +
            (nearest != null && clear ? " · " + Loc("Placing_Clear", Distance(gap), nearest.GetDisplayName()) : "") +
            (slope >= 30 ? $"\n{red}{Loc("Placing_TooSteep", slope.ToString("F0"))}</color>" : "") +
            (!clear ? $"\n{red}{Loc("Placing_Touching", nearest.GetDisplayName())}</color>" : "");
        return placed;
    }

    // Beside the active vessel, on a plane through it facing the camera.
    private List<SpawnSituation> PlaceInSpace(VesselTemplate template)
    {
        kind = Kind.Space;
        Vessel active = FlightGlobals.ActiveVessel;
        Camera camera = FlightCamera.fetch.mainCamera;
        Ray ray = camera.ScreenPointToRay(Input.mousePosition);
        if (!new Plane(-camera.transform.forward, active.transform.position).Raycast(ray, out float enter))
            return new List<SpawnSituation>();

        Vector3 point = ray.GetPoint(enter);

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(active.mainBody, point, active.obt_velocity, UT);

        // Nose prograde and roof up on screen, then turned. A control part's up is its nose, and its forward its belly.
        Vector3 prograde = ((Vector3)active.obt_velocity).normalized;
        Vector3 up = Vector3.ProjectOnPlane(camera.transform.up, prograde);
        Quaternion frame = Quaternion.LookRotation(prograde, up.sqrMagnitude > 1e-4f ? up : point - (Vector3)active.mainBody.position);
        Quaternion rotation = frame * spaceTurn * Quaternion.LookRotation(Vector3.down, Vector3.forward);

        List<SpawnSituation> placed = Cluster(orbit, template, rotation);

        // Measured between the vessels' boxes, not their centres.
        float gap = Clearance(template, placed, out Vessel nearest);

        placeValid = gap >= 1f;
        placeInfo = $"{Named(template)} · " +
            (nearest == null ? "" : placeValid ? Loc("Placing_Clear", Distance(gap), nearest.GetDisplayName()) : $"{red}{Loc("Placing_Touching", nearest.GetDisplayName())}</color>");
        return placed;
    }

    // Null if the mouse isn't over a planet or moon.
    private List<SpawnSituation> PlaceOnMap(VesselTemplate template)
    {
        kind = Kind.Landed;
        // The map's planets are smooth and scaled down, so follow the same line at full size, hills and all.
        Ray ray = PlanetariumCamera.Camera.ScreenPointToRay(Input.mousePosition);
        Vector3d origin = ScaledSpace.ScaledToLocalSpace(ray.origin);
        CelestialBody hitBody = null;
        double nearest = double.MaxValue;
        foreach (CelestialBody body in FlightGlobals.Bodies)
            if (Ground(body, origin, ray.direction, double.MaxValue, 10, out double distance) && distance < nearest)
            {
                nearest = distance;
                hitBody = body;
            }

        if (hitBody == null)
            return null;

        Vector3d surface = origin + (Vector3d)ray.direction * nearest;
        double latitude = hitBody.GetLatitude(surface);
        double longitude = hitBody.GetLongitude(surface);

        string name = hitBody.displayName.LocalizeRemoveGender();

        if (hitBody.pqsController == null)
        {
            placeInfo = Loc("Placing_NoSurface", name);
            return new List<SpawnSituation>();
        }

        placeValid = true;

        string biome = ScienceUtil.GetExperimentBiomeLocalized(hitBody, latitude, longitude);
        double terrain = hitBody.TerrainAltitude(latitude, longitude, true);
        string water = hitBody.ocean && terrain < 0 && (biome ?? "").IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 ? " · " + Loc("Placing_OnWater") : "";
        placeInfo = $"{Named(template)} · {name}{(string.IsNullOrEmpty(biome) ? "" : ", " + biome)}{water}\n" +
            $"{latitude:F3}°, {longitude:F3}° · {HeadingText}";
        return Formations.LandedRow(template, hitBody, latitude, longitude, placeHeading, Number, Controller.randomRotation);
    }

    // A circular orbit through the point under the mouse, in the plane facing the camera.
    // Look down on the north pole for an equatorial orbit, from the side for a polar one.
    private List<SpawnSituation> PlaceOrbit(VesselTemplate template)
    {
        kind = Kind.Orbit;
        CelestialBody body = MapBody();
        Camera camera = PlanetariumCamera.Camera;
        Ray ray = camera.ScreenPointToRay(Input.mousePosition);
        Vector3 centre = body?.scaledBody.transform.position ?? Vector3.zero;
        Vector3 facing = camera.transform.forward;
        if (body == null || !new Plane(facing, centre).Raycast(ray, out float enter))
            return new List<SpawnSituation>();

        Vector3d radial = ((Vector3d)(ray.GetPoint(enter) - centre)) * ScaledSpace.ScaleFactor;
        double radius = radial.magnitude;
        double altitude = radius - body.Radius;
        string name = body.displayName.LocalizeRemoveGender();

        placeInfo = radius <= body.Radius ? Loc("Placing_FurtherOut", name) : radius >= body.sphereOfInfluence ? Loc("Placing_OutsideSOI", name) : null;
        if (placeInfo != null)
            return new List<SpawnSituation>();

        // Anticlockwise as seen by the camera, like most orbits seen from the north. Q/E reverse it.
        // Unity is left-handed, so a normal pointing away from the camera is anticlockwise on screen.
        Vector3d normal = reverseOrbit ? -(Vector3d)facing : (Vector3d)facing;
        Vector3d prograde = Vector3d.Cross(normal, radial).normalized;
        double speed = Math.Sqrt(body.gravParameter / radius);

        double UT = Planetarium.GetUniversalTime();
        Orbit orbit = Placement.OrbitFromWorldState(body, body.position + radial, prograde * speed, UT);

        bool inAtmosphere = body.atmosphere && altitude < body.atmosphereDepth;
        placeValid = true;
        placeInfo = $"{Named(template)} · {name} · {Loc("Placing_Altitude", Distance(altitude))} · {Loc("Placing_Inclination", orbit.inclination.ToString("F1"))}" +
            (inAtmosphere ? $"\n{red}{Loc("Placing_InAtmosphere")}</color>" : "");

        // Together where you click. Spreading them around the orbit is for the Orbit mode.
        return Cluster(orbit, template);
    }

    // The body the map is looking at, or the one in the window.
    private static CelestialBody MapBody()
    {
        MapObject target = PlanetariumCamera.fetch?.target;
        return target?.celestialBody ?? target?.vessel?.mainBody ?? (Controller.body.Valid ? Controller.body.value : null);
    }

    // Where a ray first meets a body's ground, the hills and valleys its colliders only cover close up. Steps along
    // the ray through the heights the terrain reaches, then narrows in on where it went under by how high it was
    // either side. Water counts as ground. Each height costs 10-50 µs, so the steps are as few as measured in game
    // to rarely miss a hill.
    private static bool Ground(CelestialBody body, Vector3d origin, Vector3d direction, double range, int steps, out double distance)
    {
        PQS pqs = body.pqsController;
        if (!RaySphere(origin - body.position, direction, pqs != null ? pqs.radiusMax : body.Radius, out distance, out double exit))
            return false;

        distance = Math.Max(distance, 0);
        if (pqs == null)
            return true;

        // Nothing's lower than the lowest the terrain goes, or the sea, so the ray is under it by then.
        if (RaySphere(origin - body.position, direction, Math.Max(pqs.radiusMin, body.ocean ? body.Radius : 0), out double lowest, out _) && lowest > distance)
            exit = lowest;

        double Above(double along)
        {
            Vector3d point = origin + direction * along;
            return body.GetAltitude(point) - body.TerrainAltitude(body.GetLatitude(point), body.GetLongitude(point));
        }

        double step = (Math.Min(exit, range) - distance) / steps, above = Above(distance);
        for (int i = 0; i < steps && step > 0; i++, distance += step)
        {
            double next = Above(distance + step);
            if (next > 0)
            {
                above = next;
                continue;
            }

            double over = distance, under = distance + step, below = next;
            for (int j = 0; j < 3; j++)
            {
                double middle = over + (under - over) * above / (above - below), height = Above(middle);
                if (height > 0)
                    (over, above) = (middle, height);
                else
                    (under, below) = (middle, height);
            }

            distance = over + (under - over) * above / (above - below);
            return true;
        }

        return false;
    }

    // Where a ray goes into a sphere and out again. False if it misses, or the sphere is behind it.
    private static bool RaySphere(Vector3d offset, Vector3d direction, double radius, out double enter, out double exit)
    {
        double b = Vector3d.Dot(offset, direction);
        double discriminant = b * b - offset.sqrMagnitude + radius * radius;
        enter = -b - Math.Sqrt(Math.Max(discriminant, 0));
        exit = -b + Math.Sqrt(Math.Max(discriminant, 0));
        return discriminant >= 0 && exit > 0;
    }

    // The smallest gap between any of the vessels being placed and any loaded vessel, measured between
    // their boxes. Negative if they overlap.
    internal static float Clearance(VesselTemplate template, List<SpawnSituation> situations, out Vessel nearest)
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
    private string HeadingText => Controller.randomRotation ? Loc("Placing_RandomHeading") : Loc("Placing_Heading", placeHeading.ToString("F0"));

    private const string red = "<color=#ff7766>";

    private static bool MouseOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    internal static string Distance(double metres) =>
        Math.Abs(metres) < 10000 ? $"{metres:0} m" : $"{metres / 1000:0.#} km";

    private static int Number => Controller.count.Valid ? Controller.count.value : 1;

    // What's being placed, and how many.
    private static string Named(VesselTemplate template) => Number > 1 ? Loc("Placing_Several", template.DisplayName, Number) : template.DisplayName;

    // Together, in a tidy cluster around the orbit's position.
    private static List<SpawnSituation> Cluster(Orbit orbit, VesselTemplate template, Quaternion? rotation = null) =>
        Enumerable.Range(0, Number).Select(i => SpawnSituation.Orbiting(Formations.Cluster(orbit, template, i, Controller.randomRotation), rotation)).ToList();

    #endregion

    #region Ghosts

    // A ghost for each vessel in view, as many as there are ghosts for, and the rest hidden.
    private void ShowGhosts(VesselTemplate template, List<SpawnSituation> situations)
    {
        int used = 0;
        if (situations?.Count > 0)
        {
            if (template != ghostTemplate)
            {
                ClearGhosts();
                ghostTemplate = template;
            }

            Vector3 camera = FlightCamera.fetch.mainCamera.transform.position;
            foreach (SpawnSituation situation in situations.Where(s => !s.landed || s.body == FlightGlobals.currentMainBody))
            {
                if (used >= maxGhosts)
                    break;

                (Vector3d position, Quaternion rotation) = Spawner.Pose(template, situation);
                if (((Vector3)position - camera).magnitude > ghostRange)
                    continue;

                if (used >= ghosts.Count)
                    ghosts.Add(new Ghost(template));

                Ghost ghost = ghosts[used++];
                ghost.gameObject.transform.SetPositionAndRotation(position, rotation);
                ghost.ShowLaunchClamps(situation.landed);
                ghost.SetColor(placeValid ? Ghost.validColor : Ghost.invalidColor);
                ghost.gameObject.SetActive(true);
            }
        }

        for (int i = used; i < ghosts.Count; i++)
            ghosts[i].gameObject.SetActive(false);
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

    private static (Vector3d, CelestialBody) Marker(SpawnSituation situation) =>
        (situation.landed
            ? situation.body.GetWorldSurfacePosition(situation.latitude, situation.longitude, situation.body.TerrainAltitude(situation.latitude, situation.longitude))
            : situation.orbit.getPositionAtUT(Planetarium.GetUniversalTime()), situation.body);

    // Drawn by the stock orbit renderer, the way contracts draw their target orbits, so the preview
    // takes exactly the same path as every other orbit line: same camera, same frame, same timing.
    // It's the stock component itself rather than a subclass, because Unity's script execution
    // order is set per class, and the timing is the whole point. No orbit hides it.
    private void DrawOrbit(Orbit orbit)
    {
        if (orbit == null)
        {
            if (orbitRenderer != null)
                orbitRenderer.drawMode = OrbitRendererBase.DrawMode.OFF;
            return;
        }

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
            float radius = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
            if (RaySphere(camera.transform.position - body.scaledBody.transform.position, toMarker.normalized, radius * 0.995f, out double hit, out _) && hit > 0 && hit < toMarker.magnitude)
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

        // Modifiers by the names on the keys: Shift, not LeftShift.
        string Key(KeyBinding key) => key.primary.code switch
        {
            KeyCode.LeftShift or KeyCode.RightShift => "Shift",
            KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
            KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
            _ => key.name,
        };

        string roll = Key(GameSettings.Editor_rollLeft) + "/" + Key(GameSettings.Editor_rollRight);
        string all = string.Concat(new[] { GameSettings.Editor_pitchDown, GameSettings.Editor_yawLeft, GameSettings.Editor_pitchUp, GameSettings.Editor_yawRight, GameSettings.Editor_rollLeft, GameSettings.Editor_rollRight }.Select(Key));
        string fine = Key(GameSettings.Editor_fineTweak);
        string turning = kind == Kind.Orbit ? Loc("Placing_TiltOrbit") + " · " + Loc("Placing_Reverse", roll)
            : Controller.randomRotation ? null
            : kind == Kind.Space ? Loc("Placing_Rotate", all, fine, Key(GameSettings.Editor_resetRotation))
            : Loc("Placing_Turn", roll, fine);
        // What the mouse does, then the keys that adjust it.
        string controls = string.Join(" · ", Loc("Placing_Spawn"), Loc("Placing_KeepPlacing"), Loc("Placing_Stop")) + "\n" +
            string.Join(" · ", new[] { turning, Loc("Placing_Count") }.Where(s => s != null));

        GUIContent content = new GUIContent((placeInfo ?? "") + "\n<color=#aaaaaa>" + controls + "</color>");
        Vector2 size = hintStyle.CalcSize(content);
        Vector2 mouse = Event.current.mousePosition;
        Rect rect = new Rect(mouse.x + 20, mouse.y + 20, size.x, size.y);

        if (rect.xMax > Screen.width) rect.x = mouse.x - 20 - size.x;
        if (rect.yMax > Screen.height) rect.y = mouse.y - 20 - size.y;

        GUI.Box(rect, content, hintStyle);
    }

    // A ring with a dot in the middle, 32 pixels across.
    private static Texture2D CreateMarkerTexture()
    {
        Texture2D texture = new Texture2D(32, 32, TextureFormat.ARGB32, false);
        texture.SetPixels(Enumerable.Range(0, 32 * 32).Select(i => new Vector2(i % 32 - 15.5f, i / 32 - 15.5f).magnitude)
            .Select(r => new Color(1, 1, 1, Mathf.Max(Mathf.Clamp01(1.5f - Mathf.Abs(r - 12f)), Mathf.Clamp01(4.5f - r)))).ToArray());
        texture.Apply();
        return texture;
    }

    #endregion
}
