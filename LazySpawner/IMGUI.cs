using KSP.Localization;
using KSP.UI;
using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner;

[Settings(category = "UI")]
[KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
public class IMGUI : MonoBehaviour
{
    public static IMGUI Instance { get; private set; }

    // Window.
    private static string windowTitle;
    public static int windowWidth = 400;
    public static float LabelWidth => windowWidth * 0.42f;
    public static int sectionSpacing = 8;

    private int windowID;
    private Rect windowRect = new Rect(0, 0, windowWidth, 0);
    private static readonly Dictionary<GameScenes, Rect> windowRects = new Dictionary<GameScenes, Rect>();
    private GameScenes scene; // By OnDestroy, LoadedScene is already the next scene.
    private bool drawGUI = false;
    private bool showSettings = false;
    private bool showHelp = false;
    private ClickBlocker clickBlocker;
    private ApplicationLauncherButton appLauncherButton;
    private const string scrollLockID = "LazySpawnerScrollLock";
    private const string typingLockID = "LazySpawnerTypingLock";
    internal const string textFieldPrefix = "LazySpawnerField_";

    // Styles.
    private static GUIStyle topButtonStyle;
    private static GUIStyle boxStyle;
    private static GUIStyle headingStyle;
    private static GUIStyle wrapStyle;

    // Craft.
    public enum Source { Craft, Clone }
    private static readonly string[] sourceNames = { "Craft File", "Clone Vessel" };
    public static readonly Setting<Source> source = Source.Craft;
    public static readonly TextField<string> craftPath = new TextField<string>("Craft", "", s => s.Trim().Trim('"'), File.Exists, "Path to a .craft file. Paste one in, or use Select.");
    public static readonly TextField<int> count = new TextField<int>("Count", "1", TextField<int>.ParseInt, c => c > 0 && c <= 1000);
    private Coroutine craftBrowserSelection;
    private CraftInfo craftInfo;

    // Situation.
    public enum SituationMode { Nearby, Orbit, Landed }
    private static readonly string[] situationNames = { "Nearby", "Orbit", "Landed" };
    public static readonly Setting<SituationMode> situationMode = SituationMode.Nearby;
    public static readonly Setting<bool> randomRotation = false;
    public static readonly TextField<CelestialBody> body = new TextField<CelestialBody>("Body", "Kerbin", FindBody);

    // Nearby.
    public static readonly TextField<float> range = new TextField<float>("Within Range (m)", "100", TextField<float>.ParseFloat, r => r >= 0, "Vessels appear at random around the active vessel, no further away than this.");

    // Simple orbit.
    public static readonly Setting<bool> advancedOrbit = false;
    public static readonly TextField<double> altitude = new TextField<double>("Altitude (km)", "100", s => TextField<double>.ParseDouble(s) * 1000);
    public static readonly TextField<double> inclination = new TextField<double>("Inclination (°)", "0", TextField<double>.ParseDouble);

    // Advanced orbit.
    public static readonly TextField<double> sma = new TextField<double>("Semi-Major Axis (km)", "700", s => TextField<double>.ParseDouble(s) * 1000, a => a != 0);
    public static readonly TextField<double> eccentricity = new TextField<double>("Eccentricity", "0", TextField<double>.ParseDouble, e => e >= 0);
    public static readonly TextField<double> lan = new TextField<double>("Longitude of Asc. Node (°)", "0", TextField<double>.ParseDouble);
    public static readonly TextField<double> argPe = new TextField<double>("Argument of Periapsis (°)", "0", TextField<double>.ParseDouble);
    public static readonly TextField<double> meanAnomaly = new TextField<double>("Mean Anomaly (°)", "0", TextField<double>.ParseDouble, tooltip: "Where along the orbit the vessel is, right now.");
    public static readonly Setting<bool> spreadAlongOrbit = true;

    // Landed.
    public static readonly TextField<double> latitude = new TextField<double>("Latitude (°)", "-0.0972", TextField<double>.ParseDouble, l => l >= -90 && l <= 90);
    public static readonly TextField<double> longitude = new TextField<double>("Longitude (°)", "-74.5577", TextField<double>.ParseDouble);
    public static readonly TextField<float> heading = new TextField<float>("Heading (°)", "90", TextField<float>.ParseFloat, tooltip: "Which way the vessel's nose points, clockwise from north.");

    // Crew.
    private static readonly string[] crewModeNames = { "None", "Pilot", "Command", "Fill All" };
    public static readonly Setting<CrewMode> crewMode = CrewMode.Pilot;
    public static readonly Setting<bool> onlyNewKerbals = false;

    // Result.
    private readonly List<Vessel> lastSpawned = new List<Vessel>();
    private string status = "";
    private bool statusIsError = false;
    private Coroutine spawnRoutine;

    // Settings.
    public static readonly Setting<bool> showButton = true;
    public static readonly Setting<bool> useKeybind = true;
    public static readonly Setting<KeyCode> keybindModifier = KeyCode.LeftAlt;
    public static readonly Setting<KeyCode> keybind = KeyCode.F;

    private static bool SceneSupported =>
        HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

    private static bool InFlight =>
        HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;

    #region Lifecycle

    protected void Awake()
    {
        if (!SceneSupported)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    protected void Start()
    {
        windowID = GUIUtility.GetControlID(FocusType.Passive);

        // Remember where the window was in each scene. The tracking station's vessel list is on the left.
        scene = HighLogic.LoadedScene;
        if (!windowRects.TryGetValue(scene, out windowRect))
        {
            float x = HighLogic.LoadedScene == GameScenes.TRACKSTATION ? 0.25f : 0.04f;
            windowRect = new Rect(Screen.width * x, Screen.height * 0.1f, windowWidth, 0);
        }

        Version version = Assembly.GetExecutingAssembly().GetName().Version;
        windowTitle = $"Lazy Spawner v{version.Major}.{version.Minor}.{version.Build}";

        clickBlocker = ClickBlocker.Create(UIMasterController.Instance.mainCanvas, nameof(LazySpawner));
        placementTool = gameObject.AddComponent<PlacementTool>();
        placementTool.gui = this;

        // Fields that parse into game objects need the game to have loaded first.
        body.Refresh();

        if (!File.Exists(craftPath.value))
            craftPath.Text = DefaultCraftPath() ?? "";

        AddToolbarButton();
        GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
    }

    protected void Update()
    {
        if (useKeybind && (keybindModifier == KeyCode.None || Input.GetKey(keybindModifier)) && Input.GetKeyDown(keybind))
        {
            if (drawGUI)
                Close();
            else
                Open();
        }

        if (drawGUI && GameSettings.PAUSE.GetKeyUp() && placementTool.Placing == PlacementTool.Kind.None)
            Close();
    }

    protected void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        windowRects[scene] = windowRect;

        GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
        InputLockManager.RemoveControlLock(scrollLockID);
        InputLockManager.RemoveControlLock(typingLockID);

        if (appLauncherButton && ApplicationLauncher.Instance)
            ApplicationLauncher.Instance.RemoveModApplication(appLauncherButton);

        if (clickBlocker)
            Destroy(clickBlocker);

        GlobalSettings.Save();
    }

    public void Close()
    {
        GlobalSettings.Save();
        drawGUI = false;

        if (clickBlocker)
            clickBlocker.Blocking = false;

        InputLockManager.RemoveControlLock(scrollLockID);
        InputLockManager.RemoveControlLock(typingLockID);

        if (appLauncherButton && appLauncherButton.toggleButton.CurrentState == UIRadioButton.State.True)
            appLauncherButton.SetFalse(false);
    }

    public void Open()
    {
        drawGUI = true;

        if (clickBlocker)
            clickBlocker.Blocking = true;

        if (appLauncherButton && appLauncherButton.toggleButton.CurrentState == UIRadioButton.State.False)
            appLauncherButton.SetTrue(false);
    }

    public void AddToolbarButton()
    {
        if (!showButton || appLauncherButton != null || !ApplicationLauncher.Ready)
            return;

        Texture buttonTexture = GameDatabase.Instance.GetTexture("LazySpawner/Textures/Icon", false);
        ApplicationLauncher.AppScenes scenes = ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.TRACKSTATION;
        appLauncherButton = ApplicationLauncher.Instance.AddModApplication(Open, Close, null, null, null, null, scenes, buttonTexture);
    }

    public void RemoveToolbarButton()
    {
        if (appLauncherButton == null)
            return;

        ApplicationLauncher.Instance.RemoveModApplication(appLauncherButton);
        appLauncherButton = null;
    }

    #endregion

    #region Window

    private void InitStyles()
    {
        topButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Overflow,
            padding = new RectOffset(0, 0, 0, 0),
        };

        boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(6, 6, 4, 4),
        };

        headingStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
        };
        headingStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

        wrapStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
        };
    }

    protected void OnGUI()
    {
        if (!drawGUI || !UIMasterController.Instance.IsUIShowing)
        {
            if (clickBlocker)
                clickBlocker.Blocking = false;
            InputLockManager.RemoveControlLock(typingLockID);
            return;
        }

        if (topButtonStyle == null)
            InitStyles();

        // Keep window inside screen space.
        windowRect.position = new Vector2(
            Mathf.Clamp(windowRect.position.x, 0, Mathf.Max(0, Screen.width - windowRect.width)),
            Mathf.Clamp(windowRect.position.y, 0, Mathf.Max(0, Screen.height - windowRect.height))
        );

        // Clicking anywhere else stops typing.
        bool typing = GUI.GetNameOfFocusedControl().StartsWith(textFieldPrefix);
        if (typing && Event.current.type == EventType.MouseDown && !windowRect.Contains(Event.current.mousePosition))
        {
            GUIUtility.keyboardControl = 0;
            typing = false;
        }

        windowRect = GUILayout.Window(windowID, windowRect, FillWindow, windowTitle, GUILayout.Height(1), GUILayout.Width(windowWidth), GUILayout.MaxWidth(windowWidth));

        // Otherwise typing "1" into a field fires action group 1, and a space stages.
        if (Event.current.type == EventType.Repaint)
        {
            if (typing)
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, typingLockID);
            else
                InputLockManager.RemoveControlLock(typingLockID);
        }

        clickBlocker.Blocking = true;
        clickBlocker.UpdateRect(windowRect);

        // Tooltip.
        if (!string.IsNullOrEmpty(GUI.tooltip))
        {
            Vector2 mouse = Event.current.mousePosition;
            GUIContent content = new GUIContent(GUI.tooltip);
            Vector2 size = GUI.skin.box.CalcSize(content);
            float width = Mathf.Min(size.x, 300);
            float height = GUI.skin.box.CalcHeight(content, width);
            GUI.Box(new Rect(mouse.x + 16, mouse.y + 16, width, height), content);
        }
    }

    private void FillWindow(int id)
    {
        // Scroll lock when hovering over the window.
        bool hovering = windowRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
        if (hovering)
            InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, scrollLockID);
        else
            InputLockManager.RemoveControlLock(scrollLockID);

        TopButtons();

        if (showHelp)
            HelpSection();
        else if (showSettings)
            SettingsSection();
        else
            MainSection();

        // End window.
        GUI.DragWindow(new Rect(0, 0, 10000, 500));
    }

    private void TopButtons()
    {
        // Close button.
        if (GUI.Button(new Rect(windowRect.width - 18, 2, 16, 16), "x", topButtonStyle))
            Close();

        // Settings button.
        if (GUI.Button(new Rect(windowRect.width - (18 * 2), 2, 16, 16), "s", topButtonStyle))
        {
            showSettings = !showSettings;
            showHelp = false;
        }

        // Help button.
        if (GUI.Button(new Rect(windowRect.width - (18 * 3), 2, 16, 16), "i", topButtonStyle))
        {
            showHelp = !showHelp;
            showSettings = false;
        }
    }

    private void Heading(string text)
    {
        GUILayout.Label(text, headingStyle);
    }

    private static bool Toggle(Setting<bool> setting, string text, string tooltip = null)
    {
        setting.Value = GUILayout.Toggle(setting, new GUIContent("  " + text, tooltip));
        return setting;
    }

    private static T Grid<T>(Setting<T> setting, string label, string[] names, int columns = -1) where T : Enum
    {
        GUILayout.BeginHorizontal();
        if (label != null)
            GUILayout.Label(label + ": ", GUILayout.Width(LabelWidth));
        int index = Convert.ToInt32(setting.Value);
        index = GUILayout.SelectionGrid(index, names, columns < 0 ? names.Length : columns);
        setting.Value = (T)Enum.ToObject(typeof(T), index);
        GUILayout.EndHorizontal();
        return setting;
    }

    #endregion

    #region Sections

    private void MainSection()
    {
        bool ready = true;

        // Craft.
        Heading("Craft:");
        Grid(source, null, sourceNames);

        if (source == Source.Craft)
        {
            GUILayout.BeginHorizontal();
            craftPath.Draw(ref ready);

            if (GUILayout.Button("Select", GUILayout.ExpandWidth(false)) && craftBrowserSelection == null)
                craftBrowserSelection = StartCoroutine(StockCraftBrowserSelection());
            GUILayout.EndHorizontal();

            CraftInfoLabel(ref ready);
        }
        else
        {
            Vessel original = CloneSource();
            if (original == null)
            {
                Warn(HighLogic.LoadedSceneIsFlight ? "There's no active vessel to clone." : "Select a vessel to clone.");
                ready = false;
            }
            else if (original.isEVA)
            {
                Warn("Kerbals on EVA can't be cloned.");
                ready = false;
            }
            else
                GUILayout.Label($"{original.GetDisplayName()}, {original.protoVessel?.protoPartSnapshots.Count ?? original.parts.Count} parts", wrapStyle);
        }

        count.Draw(ref ready);

        // Situation.
        GUILayout.Space(sectionSpacing);
        Heading("Situation:");

        if (!InFlight && situationMode == SituationMode.Nearby)
            situationMode.Value = SituationMode.Orbit;

        GUIEnabled.Push(true);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Mode: ", GUILayout.Width(LabelWidth));
        int mode = (int)situationMode.Value;
        mode = GUILayout.SelectionGrid(mode, situationNames, situationNames.Length);
        if (mode != (int)SituationMode.Nearby || InFlight)
            situationMode.Value = (SituationMode)mode;
        GUILayout.EndHorizontal();
        GUIEnabled.Pop();

        GUILayout.BeginVertical(boxStyle);

        switch (situationMode.Value)
        {
            case SituationMode.Nearby:
                range.Draw(ref ready);
                Vessel active = FlightGlobals.ActiveVessel;
                string where = active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH ? "on the ground"
                    : active.situation == Vessel.Situations.FLYING ? "flying alongside" : "in orbit";
                GUILayout.Label($"<i>Around {active.GetDisplayName()}, {where}.</i>", wrapStyle);
                Toggle(randomRotation, "Random Rotation");
                break;

            case SituationMode.Orbit:
                body.Draw(ref ready);

                if (advancedOrbit)
                {
                    sma.Draw(ref ready);
                    eccentricity.Draw(ref ready);
                    inclination.Draw(ref ready);
                    lan.Draw(ref ready);
                    argPe.Draw(ref ready);
                    meanAnomaly.Draw(ref ready);
                }
                else
                {
                    altitude.Draw(ref ready);
                    inclination.Draw(ref ready);
                }

                ready &= OrbitWarnings();

                Toggle(advancedOrbit, "Advanced");
                if (count > 1)
                    Toggle(spreadAlongOrbit, "Spread Evenly Around Orbit", "Space the vessels out evenly around the orbit, like a constellation. Otherwise they're clustered together.");
                Toggle(randomRotation, "Random Rotation");
                break;

            case SituationMode.Landed:
                body.Draw(ref ready);
                latitude.Draw(ref ready);
                longitude.Draw(ref ready);
                heading.Draw(ref ready);

                if (InFlight && GUILayout.Button("Use Active Vessel's Position"))
                    UseActiveVesselPosition();

                if (body.Valid && body.value.pqsController == null)
                {
                    Warn($"{body.value.displayName.LocalizeRemoveGender()} has no surface.");
                    ready = false;
                }

                Toggle(randomRotation, "Random Heading");
                break;
        }

        GUILayout.EndVertical();

        // Crew.
        GUILayout.Space(sectionSpacing);
        Heading("Crew:");
        Grid(crewMode, null, crewModeNames);

        if (crewMode != CrewMode.None)
            Toggle(onlyNewKerbals, "Hire New Kerbals", "Always hire new kerbals, instead of using ones already at the space centre.");

        // Spawn.
        GUILayout.Space(sectionSpacing);

        GUILayout.BeginHorizontal();

        GUIEnabled.Push(ready && spawnRoutine == null);
        if (GUILayout.Button(spawnRoutine == null ? "Spawn" : "Spawning..."))
            spawnRoutine = StartCoroutine(SpawnRoutine());
        GUIEnabled.Pop();

        PlaceButton(ready);

        GUILayout.EndHorizontal();

        StatusSection();
    }

    private bool OrbitWarnings()
    {
        if (!body.Valid)
            return false;

        Orbit orbit;
        try
        {
            orbit = CreateOrbit(0);
        }
        catch
        {
            return false;
        }

        CelestialBody b = body.value;

        if (orbit.eccentricity < 1 && orbit.semiMajorAxis < 0)
        {
            Warn("Elliptical orbits need a positive semi-major axis.");
            return false;
        }

        if (orbit.eccentricity >= 1 && orbit.semiMajorAxis > 0)
        {
            Warn("Hyperbolic orbits need a negative semi-major axis.");
            return false;
        }

        if (orbit.PeR < b.Radius)
            Warn("This orbit intersects the surface.");
        else if (b.atmosphere && orbit.PeA < b.atmosphereDepth)
            Warn("This orbit dips into the atmosphere.");

        if (orbit.eccentricity < 1 && orbit.ApR > b.sphereOfInfluence)
            Warn($"This orbit leaves {b.displayName.LocalizeRemoveGender()}'s sphere of influence.");

        return true;
    }

    private void StatusSection()
    {
        if (!string.IsNullOrEmpty(status))
        {
            if (statusIsError)
                Warn(status);
            else
                GUILayout.Label(status, wrapStyle);
        }

        lastSpawned.RemoveAll(v => v == null || v.state == Vessel.State.DEAD);

        if (lastSpawned.Count > 0)
        {
            GUILayout.BeginHorizontal();

            Vessel target = lastSpawned[0];
            if (HighLogic.LoadedSceneIsFlight && target != FlightGlobals.ActiveVessel && GUILayout.Button($"Switch To {target.GetDisplayName()}"))
                FlightGlobals.SetActiveVessel(target);

            int removable = lastSpawned.Count(v => v != FlightGlobals.ActiveVessel);
            if (removable > 0 && GUILayout.Button(new GUIContent(removable == 1 ? "Undo" : $"Undo ({removable})", "Remove the vessels you just spawned. Their crew go home.")))
            {
                int removed = lastSpawned.Count(Spawner.Remove);
                lastSpawned.RemoveAll(v => v == null || v.state == Vessel.State.DEAD);
                status = removed == 1 ? "Removed 1 vessel." : $"Removed {removed} vessels.";
                statusIsError = false;
            }

            GUILayout.EndHorizontal();
        }
    }

    private void CraftInfoLabel(ref bool ready)
    {
        if (!craftPath.Valid)
        {
            if (!string.IsNullOrEmpty(craftPath.Text))
                Warn("There's no craft file at that path.");

            ready = false;
            return;
        }

        CraftInfo info = GetCraftInfo(craftPath.value);
        if (info.error != null)
        {
            Warn(info.error);
            ready = false;
        }
        else
            GUILayout.Label($"{Localizer.Format(info.name)}, {info.partCount} parts", wrapStyle);
    }

    private void Warn(string text)
    {
        Color previous = GUI.color;
        GUI.color = new Color(1f, 0.6f, 0.4f);
        GUILayout.Label(text, wrapStyle);
        GUI.color = previous;
    }

    private void SettingsSection()
    {
        Heading("Settings:");
        Toggle(useKeybind, $"Use Keybind ({KeyName(keybindModifier)} + {KeyName(keybind)})");

        if (Toggle(showButton, "Show Toolbar Button"))
            AddToolbarButton();
        else
            RemoveToolbarButton();
    }

    private static string KeyName(KeyCode key) =>
        key switch
        {
            KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
            KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
            KeyCode.LeftShift or KeyCode.RightShift => "Shift",
            _ => key.ToString(),
        };

    private void HelpSection()
    {
        Heading("Help:");
        GUILayout.Label(
            "<b>Craft File</b> spawns a craft from a .craft file. Paste a path, or pick one with Select.\n" +
            "<b>Clone Vessel</b> copies the active vessel, or the selected vessel in the tracking station.\n\n" +
            "<b>Nearby</b> scatters vessels around the active vessel, in orbit or on the ground.\n" +
            "<b>Orbit</b> puts vessels in the orbit you describe. Several vessels can be spread evenly around it.\n" +
            "<b>Landed</b> puts vessels on the ground at the coordinates you give, side by side if there are several. " +
            "The default coordinates are the KSC runway.\n\n" +
            "<b>Place...</b> lets you point at where you want vessels instead: at the ground or around the active vessel " +
            "in the flight view, or at any planet or moon in the map. Click to spawn, Q/E to turn, " +
            "shift-click to keep going, right-click to stop.\n\n" +
            "<b>Undo</b> removes the vessels you just spawned, and sends their crew home.\n\n" +
            "Spawned vessels start unloaded and load in like any other vessel when they come into range.",
            wrapStyle);
    }

    #endregion

    #region Spawning

    private IEnumerator SpawnRoutine() =>
        SpawnRoutine(null);

    // Spawn at the given situations, or the ones the window describes.
    private IEnumerator SpawnRoutine(List<SpawnSituation> situations)
    {
        status = "";
        statusIsError = false;
        lastSpawned.Clear();

        VesselTemplate template;

        try
        {
            template = CreateTemplate();
            situations ??= CreateSituations(template, count, false);
        }
        catch (Exception e)
        {
            ShowError(e);
            spawnRoutine = null;
            yield break;
        }

        CrewSettings crew = new CrewSettings(crewMode, onlyNewKerbals);
        Stopwatch frameTimer = new Stopwatch();
        Stopwatch totalTimer = Stopwatch.StartNew();
        int spawned = 0;

        foreach (SpawnSituation situation in situations)
        {
            // Spread big batches over several frames so the game doesn't freeze.
            if (frameTimer.ElapsedMilliseconds > 30)
            {
                status = $"Spawning... {spawned}/{situations.Count}";
                yield return null;
                frameTimer.Reset();
            }

            frameTimer.Start();

            try
            {
                Vessel vessel = Spawner.Spawn(template, situation, crew);
                if (vessel != null)
                    lastSpawned.Add(vessel);
                spawned++;
            }
            catch (Exception e)
            {
                ShowError(e);
                break;
            }

            frameTimer.Stop();
        }

        if (spawned > 0)
        {
            status = spawned == 1
                ? $"Spawned {template.DisplayName}."
                : $"Spawned {spawned} × {template.DisplayName} in {totalTimer.Elapsed.TotalSeconds:N1} s.";
            statusIsError = false;
            ScreenMessages.PostScreenMessage(status, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        spawnRoutine = null;
    }

    private VesselTemplate CreateTemplate() =>
        source == Source.Clone ? VesselTemplate.FromVessel(CloneSource()) : CraftParser.Parse(craftPath.value);

    private void ShowError(Exception e)
    {
        string title = "Spawning Failed";
        string message = e.Message;

        if (e is SpawnException spawnException)
            title = spawnException.title;
        else
            UnityEngine.Debug.LogException(e);

        // The popup has the whole message. A long one would make the window taller than the screen.
        status = message.Split('\n')[0].TrimEnd(':', ' ');
        statusIsError = true;

        PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "LazySpawnerError", title, message, Localizer.Format("#autoLOC_417274"), false, HighLogic.UISkin);
    }

    // Previews can't be random, or they'd jump about every frame.
    private List<SpawnSituation> CreateSituations(VesselTemplate template, int number, bool preview)
    {
        List<SpawnSituation> situations = new List<SpawnSituation>();

        if (situationMode != SituationMode.Nearby && !body.Valid)
            throw new SpawnException($"There's no celestial body called {body.Text}.");

        switch (situationMode.Value)
        {
            case SituationMode.Nearby:
                return preview ? null : Placement.Nearby(FlightGlobals.ActiveVessel, template, number, range, randomRotation);

            case SituationMode.Orbit:
                OrbitRotation rotation = randomRotation ? OrbitRotation.Random : OrbitRotation.Prograde;
                Orbit reference = CreateOrbit(0);

                for (int i = 0; i < number; i++)
                {
                    Orbit orbit;

                    if (number == 1)
                        orbit = reference;
                    else if (spreadAlongOrbit && reference.eccentricity < 1)
                        orbit = CreateOrbit(360.0 * i / number);
                    else
                        orbit = Cluster(reference, template, i);

                    situations.Add(SpawnSituation.Orbiting(orbit, rotation));
                }

                break;

            case SituationMode.Landed:
                return LandedRow(template, body.value, latitude, longitude, heading, number, randomRotation && !preview);
        }

        return situations;
    }

    // A row, side by side, centred on the coordinates.
    internal static List<SpawnSituation> LandedRow(VesselTemplate template, CelestialBody body, double latitude, double longitude, float heading, int number, bool randomHeading)
    {
        List<SpawnSituation> situations = new List<SpawnSituation>();
        Quaternion frame = Placement.SurfaceFrame(body, latitude, longitude, heading);
        Vector3d centre = body.GetWorldSurfacePosition(latitude, longitude, 0);
        Vector3 side = frame * Vector3.right;
        float spacing = template.radius * 2f + 4f;

        for (int i = 0; i < number; i++)
        {
            Vector3d position = number == 1 ? centre : centre + (Vector3d)side * ((i - (number - 1) * 0.5f) * spacing);
            double lat = latitude, lon = longitude;
            if (number > 1)
                body.GetLatLonAlt(position, out lat, out lon, out _);

            float h = randomHeading ? Random.Range(0f, 360f) : heading;
            situations.Add(SpawnSituation.Landed(body, lat, lon, h));
        }

        return situations;
    }

    // A tidy formation around the reference orbit's current position, all with the same velocity.
    // Grid points nearest the middle first, lined up with the direction of travel.
    internal static Orbit Cluster(Orbit reference, VesselTemplate template, int index)
    {
        if (index == 0)
            return reference;

        double UT = Planetarium.GetUniversalTime();
        CelestialBody b = reference.referenceBody;
        Vector3d position = reference.getPositionAtUT(UT);
        Vector3d velocity = reference.getOrbitalVelocityAtUT(UT).xzy;

        Vector3d prograde = velocity.normalized;
        Vector3d radial = Vector3d.Exclude(prograde, position - b.position).normalized;
        Vector3d normal = Vector3d.Cross(prograde, radial);

        Vector3 cell = GridCell(index);
        double spacing = template.radius * 2 + 5;
        Vector3d offset = (prograde * cell.x + radial * cell.y + normal * cell.z) * spacing;

        return Placement.OrbitFromWorldState(b, position + offset, velocity, UT);
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

    private Orbit CreateOrbit(double meanAnomalyOffset)
    {
        CelestialBody b = body.value;
        double UT = Planetarium.GetUniversalTime();

        // The epoch is now, so that the mean anomaly is where the vessel is when it appears.
        if (advancedOrbit)
            return Placement.CreateOrbit(b, inclination, eccentricity, sma, lan, argPe, (meanAnomaly + meanAnomalyOffset) * Mathf.Deg2Rad, UT);
        else
            return Placement.CreateOrbit(b, inclination, 0, b.Radius + altitude, 0, 0, meanAnomalyOffset * Mathf.Deg2Rad, UT);
    }

    private void UseActiveVesselPosition()
    {
        Vessel vessel = FlightGlobals.ActiveVessel;
        body.Text = vessel.mainBody.bodyName;
        latitude.Text = vessel.latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        longitude.Text = vessel.longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        heading.Text = Placement.Heading(vessel).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Vessel CloneSource()
    {
        if (HighLogic.LoadedSceneIsFlight)
            return FlightGlobals.ActiveVessel;

        if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && SpaceTracking.Instance != null)
            return SpaceTracking.Instance.SelectedVessel;

        return null;
    }

    private static CelestialBody FindBody(string name)
    {
        name = name.Trim();
        return FlightGlobals.Bodies.Find(b => b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || b.displayName.LocalizeRemoveGender().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Placement

    private PlacementTool placementTool;

    // Previews use a cached template, rebuilt when the craft file or vessel changes.
    private VesselTemplate previewTemplate;
    private string previewKey;

    internal bool IsOpen => drawGUI && UIMasterController.Instance.IsUIShowing;

    internal bool MouseOverWindow() =>
        drawGUI && windowRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));

    internal VesselTemplate PreviewTemplate()
    {
        string key;

        if (source == Source.Craft)
        {
            if (!craftPath.Valid)
                return null;

            CraftInfo info = GetCraftInfo(craftPath.value);
            return info.error == null ? info.template : null;
        }

        Vessel original = CloneSource();
        if (original == null || original.isEVA)
            return null;

        key = $"{original.id}:{(original.loaded ? original.parts.Count : original.protoVessel.protoPartSnapshots.Count)}";
        if (key != previewKey)
        {
            previewKey = key;
            try { previewTemplate = VesselTemplate.FromVessel(original); }
            catch { previewTemplate = null; }
        }

        return previewTemplate;
    }

    internal List<SpawnSituation> PreviewSituations(VesselTemplate template)
    {
        if (!count.Valid)
            return null;

        try
        {
            return CreateSituations(template, count, true);
        }
        catch
        {
            return null;
        }
    }

    internal void SpawnAt(List<SpawnSituation> situations)
    {
        if (spawnRoutine == null)
            spawnRoutine = StartCoroutine(SpawnRoutine(situations));
    }

    internal void SetLanded(CelestialBody landedBody, double lat, double lon, float newHeading, bool switchMode)
    {
        body.Text = landedBody.bodyName;
        latitude.Text = lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        longitude.Text = lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        heading.Text = newHeading.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        if (switchMode)
            situationMode.Value = SituationMode.Landed;
    }

    // Which kind of placing the Place button starts, if any.
    private PlacementTool.Kind PlaceKind()
    {
        bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

        switch (situationMode.Value)
        {
            case SituationMode.Landed:
                return map ? PlacementTool.Kind.Map : InFlight ? PlacementTool.Kind.Ground : PlacementTool.Kind.None;
            case SituationMode.Nearby:
                if (!InFlight || map)
                    return PlacementTool.Kind.None;
                Vessel active = FlightGlobals.ActiveVessel;
                return active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH || active.radarAltitude < 2000
                    ? PlacementTool.Kind.Ground : PlacementTool.Kind.Space;
            default:
                return PlacementTool.Kind.None;
        }
    }

    private void PlaceButton(bool ready)
    {
        PlacementTool.Kind kind = PlaceKind();
        if (kind == PlacementTool.Kind.None)
            return;

        bool placing = placementTool.Placing != PlacementTool.Kind.None;
        string tooltip = kind switch
        {
            PlacementTool.Kind.Map => "Click on any planet or moon to spawn there.",
            PlacementTool.Kind.Ground => "Click on the ground to spawn there.",
            _ => "Click in space around the active vessel to spawn there.",
        };

        GUIEnabled.Push(ready || placing);
        if (GUILayout.Button(new GUIContent(placing ? "Stop Placing" : "Place...", tooltip)))
        {
            if (placing)
                placementTool.Stop();
            else
                placementTool.Begin(kind);
        }
        GUIEnabled.Pop();
    }

    #endregion

    #region Craft Files

    private class CraftInfo
    {
        public string path;
        public DateTime modified;
        public string name;
        public int partCount;
        public string error;
        public VesselTemplate template;
        public float checkedTime;
    }

    // Parsing a craft is cheap enough to do on selection, and it catches missing parts up front.
    private CraftInfo GetCraftInfo(string path)
    {
        // This is asked for every frame, so only look at the file once a second.
        if (craftInfo != null && craftInfo.path == path && Time.unscaledTime - craftInfo.checkedTime < 1f)
            return craftInfo;

        DateTime modified = File.GetLastWriteTimeUtc(path);
        if (craftInfo != null && craftInfo.path == path && craftInfo.modified == modified)
        {
            craftInfo.checkedTime = Time.unscaledTime;
            return craftInfo;
        }

        craftInfo = new CraftInfo { path = path, modified = modified, checkedTime = Time.unscaledTime };

        try
        {
            VesselTemplate template = CraftParser.Parse(path);
            craftInfo.template = template;
            craftInfo.name = template.name;
            craftInfo.partCount = template.partCount;
        }
        catch (CraftParser.MissingPartsException e)
        {
            craftInfo.error = e.ShortMessage;
        }
        catch (Exception e)
        {
            craftInfo.error = e.Message.Split('\n')[0];
        }

        return craftInfo;
    }

    private IEnumerator StockCraftBrowserSelection()
    {
        bool complete = false;
        EditorFacility facility = craftPath.value != null && craftPath.value.Contains(Path.DirectorySeparatorChar + "SPH" + Path.DirectorySeparatorChar)
            ? EditorFacility.SPH : EditorFacility.VAB;

        CraftBrowserDialog craftBrowser = CraftBrowserDialog.Spawn(
            facility,
            HighLogic.SaveFolder,
            (path, loadType) => { craftPath.Text = path; complete = true; },
            () => complete = true,
            false);

        while (!complete && craftBrowser != null && craftBrowser.gameObject.activeInHierarchy)
        {
            // Merging makes no sense here. The stock dialog shows the button anyway without KSPCommunityFixes.
            if (craftBrowser.btnMerge != null && craftBrowser.btnMerge.gameObject.activeSelf)
                craftBrowser.btnMerge.gameObject.SetActive(false);

            yield return null;
        }

        if (craftBrowser != null)
            craftBrowser.Dismiss();

        craftBrowserSelection = null;
    }

    // The most recently saved craft in this save, or a stock craft.
    private static string DefaultCraftPath()
    {
        try
        {
            string ships = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships");
            if (Directory.Exists(ships))
            {
                FileInfo latest = new DirectoryInfo(ships).GetFiles("*.craft", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest != null)
                    return latest.FullName;
            }

            string stock = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "VAB", "Kerbal X.craft");
            if (File.Exists(stock))
                return Path.GetFullPath(stock);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Couldn't find a default craft: {e.Message}");
        }

        return null;
    }

    #endregion
}
