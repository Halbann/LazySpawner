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
// - Flight view or the space centre, the ground: the ghost follows the mouse over the terrain.
// - Flight view, the sky: the ghost follows the mouse around the active vessel, unless that's near the ground.
// - Map view, a planet or moon: a marker follows the mouse over it.
// - Map view, space: a circular orbit through the mouse.
// Click to spawn. The editor's rotation keys turn the vessel, alt-scroll lifts it off the ground, s-scroll sets how fast it flies,
// ctrl-click keeps placing, right-click or escape stops.
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
    private const int maxOrbitLines = 25;
    private readonly List<OrbitRenderer> orbitLines = new List<OrbitRenderer>();
    // World positions, turned into screen positions as late as possible so they keep up with the camera.
    private readonly List<(Vector3d position, CelestialBody body)> markers = new List<(Vector3d, CelestialBody)>();
    private static Texture2D markerTexture;

    // Placing.
    private bool placeValid;
    private string placeInfo;
    private float placeHeading = 90;
    private bool showing;
    private float placeAltitude;
    // The airspeed setting, capped for the height.
    private float placeAirspeed;
    private Quaternion spaceTurn = Quaternion.identity;
    private bool reverseOrbit;
    private Vector3 rightClickStart;
    private float rightClickTime;
    private GUIStyle hintStyle;
    private (int, bool, bool, bool, bool, bool, bool, float, float) hintKey;
    private string hintControls, hintInfo;
    private GUIContent hintContent;
    private Vector2 hintSize;

    #region Lifecycle

    protected void Start()
    {
        markerTexture ??= CreateMarkerTexture();
    }

    protected void OnDestroy()
    {
        Stop();
        ClearGhosts();

        foreach (OrbitRenderer line in orbitLines)
            if (line != null)
                Destroy(line.gameObject);
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
            if (template == null && Placing)
                Stop();

            bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
            List<SpawnSituation> shown = template == null ? null
                : Placing ? UpdatePlacing(template, map)
                : map && Controller.situationMode == Controller.SituationMode.Orbit ? gui.PreviewSituations(template)
                : null;

            // With nothing to show, once it's all hidden, there's nothing to do until there is.
            if (shown == null && !showing)
                return;
            showing = shown != null;

            markers.Clear();
            if (map && shown != null)
                markers.AddRange(shown.Select(Marker));

            ShowGhosts(template, map ? null : shown);
            DrawOrbits(map ? shown : null);
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
        float step = Fine ? 5 : 90;
        float Turn(KeyBinding one, KeyBinding other) => one.GetKeyDown(true) ? step : other.GetKeyDown(true) ? -step : 0;
        float roll = Turn(GameSettings.Editor_rollLeft, GameSettings.Editor_rollRight);

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
            float pitch = Turn(GameSettings.Editor_pitchDown, GameSettings.Editor_pitchUp);
            float yaw = Turn(GameSettings.Editor_yawLeft, GameSettings.Editor_yawRight);
            spaceTurn = Quaternion.AngleAxis(roll, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right) * Quaternion.AngleAxis(yaw, Vector3.forward) * spaceTurn;
            if (GameSettings.Editor_resetRotation.GetKeyDown(true))
                spaceTurn = Quaternion.identity;
        }

        // Ctrl and the scroll wheel for more or fewer, Alt for higher or lower, S for faster or slower in the air, without the camera zooming meanwhile.
        ControlTypes locked = Ctrl || Alt || Speed ? locks | ControlTypes.CAMERACONTROLS : locks;
        if (InputLockManager.GetControlLock(lockID) != locked)
            InputLockManager.SetControlLock(locked, lockID);

        int scroll = Math.Sign(GameSettings.AXIS_MOUSEWHEEL.GetAxis());
        if (Ctrl && scroll != 0)
            Controller.count.Text = Mathf.Clamp(Number + scroll, 1, 1000).ToString();
        else if (Alt && scroll != 0 && kind == Kind.Landed)
            placeAltitude = Mathf.Max(0, placeAltitude + scroll * Step(placeAltitude, scroll));
        else if (Speed && scroll != 0)
            Controller.airspeed.Value = Mathf.Max(0, placeAirspeed + scroll * Step(placeAirspeed, scroll));

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

            if (!Ctrl)
                Finish();
        }

        return placed;
    }

    #endregion

    #region Placing

    // Null if the mouse isn't on the ground, and the active vessel is far enough off it to place beside it instead.
    // The space centre has no active vessel, only the ground around it, on whichever world it's on.
    private List<SpawnSituation> PlaceOnGround(VesselTemplate template)
    {
        kind = Kind.Landed;
        Vessel active = FlightGlobals.ActiveVessel;

        // Nearby ground has colliders, buildings and all. Further away there's only the terrain. From high up,
        // or in orbit however low, pointing off the ground is for placing beside the vessel.
        CelestialBody body = FlightGlobals.currentMainBody;
        Ray ray = FlightCamera.fetch.mainCamera.ScreenPointToRay(Input.mousePosition);
        bool nearGround = active == null || active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH
            || active.situation is not (Vessel.Situations.ORBITING or Vessel.Situations.ESCAPING) && active.radarAltitude <= 2000;
        bool collider = Physics.Raycast(ray, out RaycastHit hit, ghostRange, 1 << 15, QueryTriggerInteraction.Ignore);
        double distance = 0;
        // The sea has no collider, so the mouse would find the seabed under it instead.
        bool sea = body.ocean && RaySphere(ray.origin - body.position, ray.direction, body.Radius, out distance, out _) && distance > 0 && distance < (collider ? hit.distance : ghostRange);
        collider &= !sea;
        if (!collider && !(sea || nearGround && Ground(body, ray.origin, ray.direction, ghostRange, 20, out distance)))
        {
            if (!nearGround)
                return null;

            // Ground further away than ghosts are drawn is for the map, which the space centre doesn't have.
            placeInfo = Loc(!RaySphere(ray.origin - body.position, ray.direction, body.Radius, out _, out _) ? "Placing_PointAtGround"
                : active == null ? "Placing_TooFar_SpaceCentre" : "Placing_TooFar");
            return new List<SpawnSituation>();
        }

        body.GetLatLonAlt(collider ? hit.point : (Vector3d)ray.origin + (Vector3d)ray.direction * distance, out double latitude, out double longitude, out double altitude);

        // Just above what the mouse is on, so it isn't put on a roof over it.
        List<SpawnSituation> placed = Row(template, body, latitude, longitude, altitude + 3);

        // Steep ground tips things over, and overlapping vessels explode. Only nearby ground says how steep it is.
        Vector3 up = body.GetSurfaceNVector(latitude, longitude);
        float slope = collider && placeAltitude == 0 ? Vector3.Angle(hit.normal, up) : 0;
        float gap = Clearance(template, placed, out Vessel nearest);
        bool clear = gap >= 0.5f;

        placeValid = slope < 30 && clear;
        placeInfo = $"{Named(template)}{(sea ? " · " + Loc("Placing_OnWater") : "")} · {HeadingText}{InTheAir}" +
            (slope >= 30 ? Warning("Placing_TooSteep", slope.ToString("F0")) : "") + (!clear ? Warning("Placing_Touching", nearest.GetDisplayName()) : "");
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

        // Nose prograde and roof up the camera's pivot axis, which doesn't tilt as the camera pitches, then turned.
        // A control part's up is its nose, and its forward its belly.
        Vector3 prograde = ((Vector3)active.obt_velocity).normalized;
        Vector3 up = Vector3.ProjectOnPlane(FlightCamera.fetch.getReferenceFrame() * Vector3.up, prograde);
        Quaternion frame = Quaternion.LookRotation(prograde, up.sqrMagnitude > 1e-4f ? up : point - (Vector3)active.mainBody.position);
        Quaternion rotation = frame * spaceTurn * Quaternion.LookRotation(Vector3.down, Vector3.forward);

        List<SpawnSituation> placed = Cluster(orbit, template, rotation);

        // Measured between the vessels' boxes, not their centres.
        float gap = Clearance(template, placed, out Vessel nearest);

        placeValid = gap >= 1f;
        placeInfo = Named(template) + (placeValid ? "" : Warning("Placing_Touching", nearest.GetDisplayName()));
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

        string name = hitBody.Name();

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
            $"{latitude:F3}°, {longitude:F3}° · {HeadingText}{InTheAir}";

        return Row(template, hitBody, latitude, longitude);
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
        string name = body.Name();

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
            (inAtmosphere ? Warning("Placing_InAtmosphere") : "");

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

    // On a line of its own, under what's being placed.
    private static string Warning(string key, params object[] args) => $"\n<color=#ff7766>{Loc(key, args)}</color>";

    private static bool MouseOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    internal static string Distance(double metres) =>
        Math.Abs(metres) < 10000 ? $"{metres:0} m" : $"{metres / 1000:0.#} km";

    private static int Number => Controller.count.Valid ? Controller.count.value : 1;

    private static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    private static bool Alt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
    private static bool Fine => GameSettings.Editor_fineTweak.GetKey(true);
    private bool Speed => Input.GetKey(KeyCode.S) && kind == Kind.Landed && placeAltitude > 0;

    // As many notches for each tenfold: 10 at a time up to 100, then 100 at a time up to 1000, and so on. An eighth of that with fine tweak.
    private static float Step(float value, int direction) =>
        Mathf.Pow(10, Mathf.Max(1, Mathf.Floor(Mathf.Log10(Mathf.Max(value + direction * 0.5f, 1))))) / (Fine ? 8 : 1);

    private string InTheAir => placeAltitude > 0 ? " · " + Loc("Placing_InTheAir", Distance(placeAltitude), placeAirspeed.ToString("F0")) : "";

    private List<SpawnSituation> Row(VesselTemplate template, CelestialBody body, double latitude, double longitude, double? groundSearchAltitude = null)
    {
        // No faster over the ground than a circular orbit at that height, flying east along the equator.
        double radius = body.Radius + placeAltitude;
        placeAirspeed = placeAltitude > 0 ? Mathf.Min(Controller.airspeed, (float)Math.Max(0, Math.Sqrt(body.gravParameter / radius) - body.angularVelocity.magnitude * radius)) : Controller.airspeed;

        SpawnSituation at = SpawnSituation.Landed(body, latitude, longitude, placeHeading, placeAltitude);
        (Vector3d position, Quaternion rotation) = Spawner.Pose(template, at);
        body.GetLatLonAlt(position - (Vector3d)(rotation * template.BoundsFor(!at.Airborne).center), out latitude, out longitude, out _);
        return Formations.LandedRow(template, body, latitude, longitude, placeHeading, Number, Controller.randomRotation, placeAltitude, placeAirspeed, groundSearchAltitude);
    }

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
            foreach (SpawnSituation situation in situations)
            {
                if (used >= maxGhosts)
                    break;
                if (situation.landed && situation.body != FlightGlobals.currentMainBody)
                    continue;

                (Vector3d position, Quaternion rotation) = Spawner.Pose(template, situation, out double height, out bool splashed);
                float distance = ((Vector3)position - camera).magnitude;
                if (distance > ghostRange)
                    continue;

                if (used >= ghosts.Count)
                    ghosts.Add(new Ghost(template));

                Ghost ghost = ghosts[used++];
                ghost.gameObject.transform.SetPositionAndRotation(position, rotation);
                ghost.ShowLaunchClamps(situation.landed && !situation.Airborne && !splashed);

                // Under the middle of the vessel, not its root part.
                if (situation.Airborne)
                {
                    Vector3 up = situation.body.GetSurfaceNVector(situation.latitude, situation.longitude);
                    Vector3 middle = rotation * template.SpaceBounds.center;
                    Vector3 ground = (Vector3)position + middle - up * (float)(height + Vector3.Dot(middle, up));
                    ghost.ShowDrop(true, ground, up, situation.radarAltitude + 0.05f, Mathf.Max(0.05f, distance * 0.002f));
                }
                else
                    ghost.ShowDrop(false);
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
    // order is set per class, and the timing is the whole point. One line for each plane: vessels spread
    // around an orbit or flying together share one. The unused lines are hidden.
    private void DrawOrbits(List<SpawnSituation> shown)
    {
        int drawn = 0;
        bool SamePlane(Orbit orbit)
        {
            for (int i = 0; i < drawn; i++)
                if (Vector3d.Angle(orbitLines[i].driver.orbit.GetOrbitNormal(), orbit.GetOrbitNormal()) < 1)
                    return true;
            return false;
        }

        if (shown != null)
            foreach (SpawnSituation situation in shown)
            {
                if (drawn == maxOrbitLines)
                    break;
                if (situation.landed || SamePlane(situation.orbit))
                    continue;

                if (drawn == orbitLines.Count)
                    orbitLines.Add(CreateOrbitLine());
                // An orbit made from its elements doesn't know where along it the vessel is yet, which the line fades from.
                situation.orbit.UpdateFromUT(Planetarium.GetUniversalTime());
                orbitLines[drawn].driver.orbit = situation.orbit;
                orbitLines[drawn++].drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
            }

        for (int i = drawn; i < orbitLines.Count; i++)
            orbitLines[i].drawMode = OrbitRendererBase.DrawMode.OFF;
    }

    private static OrbitRenderer CreateOrbitLine()
    {
        GameObject line = new GameObject("LazySpawner Orbit Preview");
        if (ScaledSpace.Instance != null)
            line.transform.parent = ScaledSpace.Instance.transform;

        // A driver that doesn't drive, just holds the orbit.
        OrbitDriver driver = line.AddComponent<OrbitDriver>();
        driver.enabled = false;

        OrbitRenderer renderer = line.AddComponent<OrbitRenderer>();
        renderer.driver = driver;
        driver.Renderer = renderer;

        // Like a vessel's orbit, the line is brightest where the (first) vessel on it will be.
        renderer.drawIcons = OrbitRendererBase.DrawIcons.NONE;
        renderer.drawNodes = false;
        renderer.autoTextureOffset = true;

        // Orbit lines are drawn at half their node colour. Never fade out with zoom.
        renderer.nodeColor = new Color(0.3f, 1.2f, 2f, 1f);
        renderer.lowerCamVsSmaRatio = 0;
        renderer.upperCamVsSmaRatio = float.MaxValue;
        return renderer;
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

        // Made afresh only when something it shows changes, rather than every frame.
        var key = ((int)kind, Ctrl, Alt, Fine, Speed, Controller.randomRotation.Value, placeAltitude > 0, Step(placeAltitude, 1), Step(placeAirspeed, 1));
        bool controlsChanged = !key.Equals(hintKey) || hintControls == null;
        if (controlsChanged)
        {
            hintKey = key;
            hintControls = Controls();
        }

        // Measuring rich text isn't cheap, and it's usually what it was last frame.
        if (hintContent == null || placeInfo != hintInfo || controlsChanged)
        {
            hintInfo = placeInfo;
            hintContent = new GUIContent((placeInfo ?? "") + "\n<color=#aaaaaa>" + hintControls + "</color>");
            hintSize = hintStyle.CalcSize(hintContent);
        }

        GUIContent content = hintContent;
        Vector2 size = hintSize;
        Vector2 mouse = Event.current.mousePosition;
        Rect rect = new Rect(mouse.x + 20, mouse.y + 20, size.x, size.y);

        if (rect.xMax > Screen.width) rect.x = mouse.x - 20 - size.x;
        if (rect.yMax > Screen.height) rect.y = mouse.y - 20 - size.y;

        GUI.Box(rect, content, hintStyle);
    }

    // Modifiers by the names on the keys: Shift, not LeftShift.
    private static string Key(KeyBinding key) => key.primary.code switch
    {
        KeyCode.LeftShift or KeyCode.RightShift => "Shift",
        KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
        KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
        _ => key.name,
    };

    // Only what the modifiers held do, or with none, what the mouse does, then the keys that adjust it.
    private string Controls()
    {
        string roll = Key(GameSettings.Editor_rollLeft) + "/" + Key(GameSettings.Editor_rollRight);
        string all = string.Concat(new[] { GameSettings.Editor_pitchDown, GameSettings.Editor_yawLeft, GameSettings.Editor_pitchUp, GameSettings.Editor_yawRight, GameSettings.Editor_rollLeft, GameSettings.Editor_rollRight }.Select(Key));
        bool turns = kind != Kind.Orbit && !Controller.randomRotation;
        string turning = kind == Kind.Orbit ? Loc("Placing_TiltOrbit") + " · " + Loc("Placing_Reverse", roll)
            : !turns ? null
            : kind == Kind.Space ? Loc("Placing_Rotate", all, Key(GameSettings.Editor_resetRotation))
            : Loc("Placing_Turn", roll);

        return Ctrl ? Loc("Placing_CtrlClick") + " · " + Loc("Placing_CtrlScroll")
            : Alt && kind == Kind.Landed ? Fine ? Loc("Placing_HeightStep", Step(placeAltitude, 1).ToString("0.##")) : Loc("Placing_AltScroll", Key(GameSettings.Editor_fineTweak))
            : Speed ? Fine ? Loc("Placing_AirspeedStep", Step(placeAirspeed, 1).ToString("0.##")) : Loc("Placing_SpeedScroll", Key(GameSettings.Editor_fineTweak))
            : Fine && turns ? kind == Kind.Space ? Loc("Placing_RotateStep", all) : Loc("Placing_TurnStep", roll)
            : string.Join(" · ", Loc("Placing_Spawn"), Loc("Placing_KeepPlacing"), Loc("Placing_Stop")) + "\n" +
                string.Join(" · ", new[] { turning, kind == Kind.Landed ? Loc("Placing_Height") : null, kind == Kind.Landed && placeAltitude > 0 ? Loc("Placing_Airspeed") : null, Loc("Placing_Count") }.Where(s => s != null));
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
