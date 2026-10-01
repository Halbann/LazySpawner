using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using static LazySpawner.Localisation;
using Random = UnityEngine.Random;

namespace LazySpawner;

// What to spawn, where, and with whom, and the spawning itself. The screen in the debug console shows it,
// and the placement tool points at places for it. Lives in flight and the tracking station.
[Settings(category = "UI")]
[KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
public class Controller : MonoBehaviour
{
    public static Controller Instance { get; private set; }

    // The screen's name in the console, which it's found by. What it's called there is localised.
    public const string ScreenName = "Spawn Vessels";

    // Craft.
    public enum Source { Craft, Clone }
    public static readonly Setting<Source> source = Source.Craft;
    public static readonly Setting<string> craftPath = "";
    // Whatever was copied before the game started isn't picked up, only what's copied while it runs.
    private static string lastClipboard = GUIUtility.systemCopyBuffer;
    public static readonly TextField<int> count = new TextField<int>("Count", "1", c => c > 0 && c <= 1000);

    // Situation.
    public enum SituationMode { Place, Nearby, Orbit, LaunchSite }
    public static readonly Setting<SituationMode> situationMode = SituationMode.Place;
    public static readonly Setting<bool> randomRotation = false;
    public static readonly TextField<CelestialBody> body = new TextField<CelestialBody>("Body", "Kerbin", parser: FindBody);

    // Nearby.
    public static readonly TextField<float> range = new TextField<float>("Range", "100", r => r >= 0);

    // Orbit.
    public static readonly Setting<bool> advancedOrbit = false;
    public static readonly TextField<double> altitude = new TextField<double>("Altitude", "100", parser: s => TextField<double>.Parse(s) * 1000);
    public static readonly TextField<double> inclination = new TextField<double>("Inclination", "0");
    public static readonly TextField<double> sma = new TextField<double>("SemiMajorAxis", "700", a => a != 0, s => TextField<double>.Parse(s) * 1000);
    public static readonly TextField<double> eccentricity = new TextField<double>("Eccentricity", "0", e => e >= 0);
    public static readonly TextField<double> lan = new TextField<double>("AscendingNode", "0");
    public static readonly TextField<double> argPe = new TextField<double>("ArgumentOfPeriapsis", "0");
    public static readonly TextField<double> meanAnomaly = new TextField<double>("MeanAnomaly", "0");
    public static readonly Setting<bool> spreadAlongOrbit = true;

    // Launch site, by name.
    public static readonly Setting<string> launchSite = "";

    // Crew.
    public static readonly Setting<CrewMode> crewMode = CrewMode.FillCommand;
    public static readonly Setting<bool> onlyNewKerbals = true;

    // Settings, only in settings.cfg.
    public static readonly Setting<bool> useKeybind = true;
    public static readonly Setting<KeyCode> keybindModifier = KeyCode.LeftAlt;
    public static readonly Setting<KeyCode> keybind = KeyCode.F;

    // Result.
    internal readonly List<ProtoVessel> lastSpawned = new List<ProtoVessel>();
    internal string status = "";
    internal bool statusIsError;
    internal Coroutine spawnRoutine;

    internal PlacementTool placementTool;

    internal static bool InFlight => HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;

    internal static bool CanPlace => InFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

    // In the editor, it's the craft being edited that's spawned, into orbit or on a launch site.
    internal static bool InEditor => HighLogic.LoadedSceneIsEditor;
    private int editorChanges;
    private void OnShipModified(ShipConstruct ship) => editorChanges++;

    // Leave the editor to fly a vessel spawned from it, keeping the craft for when you come back, as launching would.
    internal static void FlyFromEditor(ProtoVessel spawned)
    {
        ShipConstruction.ShipConfig = EditorLogic.fetch.ship.SaveShip();
        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE, GameScenes.FLIGHT);
        FlightDriver.StartAndFocusVessel("persistent", HighLogic.CurrentGame.flightState.protoVessels.IndexOf(spawned));
    }

    // The screen is showing, or vessels are being placed with it out of the way.
    internal bool IsOpen => SpawnScreen.Visible || placementTool.Placing;

    #region Lifecycle

    protected void Awake()
    {
        if (!HighLogic.LoadedSceneIsFlight && HighLogic.LoadedScene != GameScenes.TRACKSTATION && !InEditor)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    protected void Start()
    {
        // A hot reload brings the old one across.
        placementTool = GetComponent<PlacementTool>() ?? gameObject.AddComponent<PlacementTool>();
        placementTool.gui = this;

        // Fields that parse into game objects need the game to have loaded first.
        body.Refresh();

        // There's nothing to be near in the tracking station, and nowhere to point in the editor.
        if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && situationMode == SituationMode.Nearby)
            situationMode.Value = SituationMode.Place;
        if (InEditor && (situationMode == SituationMode.Nearby || situationMode == SituationMode.Place))
            situationMode.Value = SituationMode.Orbit;

        GameEvents.onEditorShipModified.Add(OnShipModified);
    }

    protected void Update()
    {
        if (useKeybind && (keybindModifier == KeyCode.None || Input.GetKey(keybindModifier)) && Input.GetKeyDown(keybind))
        {
            if (SpawnScreen.Visible)
                Close();
            else
                Open();
        }
    }

    protected void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        GameEvents.onEditorShipModified.Remove(OnShipModified);
        GlobalSettings.Save();
    }

    public void Open() => DebugUI.Show(ScreenName);

    public void Close()
    {
        GlobalSettings.Save();
        placementTool.Stop();
        DebugUI.Hide();
    }

    #endregion

    #region Craft

    // The craft that's selected, whether it's there or not.
    internal static Craft SelectedCraft => CraftList.Get(craftPath.Value);

    internal static void Select(Craft craft)
    {
        source.Value = Source.Craft;
        craftPath.Value = craft.path;
        CraftList.Remember(craft);
    }

    // Copy a craft's path somewhere, come back to the game, and it's selected.
    internal static bool CheckClipboard()
    {
        string clipboard = GUIUtility.systemCopyBuffer;
        if (clipboard == lastClipboard || Instance == null || InEditor)
            return false;

        lastClipboard = clipboard;
        Craft craft = CraftList.FromText(clipboard);
        if (craft == null)
            return false;

        Select(craft);
        Instance.status = Loc("Status_Clipboard", craft.DisplayName);
        Instance.statusIsError = false;
        return true;
    }

    internal static Vessel CloneSource()
    {
        if (HighLogic.LoadedSceneIsFlight)
            return FlightGlobals.ActiveVessel;

        if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && SpaceTracking.Instance != null)
            return SpaceTracking.Instance.SelectedVessel;

        return null;
    }

    // The stock craft browser, for anyone who prefers it. The console would cover it, so it goes away meanwhile.
    internal IEnumerator StockCraftBrowser()
    {
        bool complete = false;
        EditorFacility facility = SelectedCraft?.facility == "SPH" ? EditorFacility.SPH : EditorFacility.VAB;
        DebugUI.Hide();

        CraftBrowserDialog craftBrowser = CraftBrowserDialog.Spawn(facility, HighLogic.SaveFolder,
            (path, loadType) => { if (CraftList.Get(path) is Craft craft) Select(craft); complete = true; },
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

        Open();
    }

    #endregion

    #region Spawning

    // Spawn at the places picked, or where the screen says. Placed vessels face the default way while
    // they're previewed, so they're turned at random last.
    internal void Spawn(List<SpawnSituation> placed = null)
    {
        if (spawnRoutine != null)
            return;

        if (placed != null && randomRotation)
            foreach (SpawnSituation situation in placed)
            {
                if (situation.landed)
                    situation.heading = Random.Range(0f, 360f);
                else
                    situation.rotation = Random.rotation;
            }

        spawnRoutine = StartCoroutine(SpawnRoutine(placed));
    }

    private IEnumerator SpawnRoutine(List<SpawnSituation> situations)
    {
        status = "";
        statusIsError = false;
        lastSpawned.Clear();

        if (source == Source.Craft && !InEditor)
            CraftList.Remember(SelectedCraft);

        VesselTemplate template = Template(out Exception error, fresh: true);
        try
        {
            if (template != null)
                situations ??= CreateSituations(template, count, false);
        }
        catch (Exception e)
        {
            error = e;
        }

        if (error != null)
        {
            ShowError(error);
            spawnRoutine = null;
            yield break;
        }

        Stopwatch timer = Stopwatch.StartNew();
        IEnumerator batch = Spawner.SpawnAll(template, situations, new CrewSettings(crewMode, onlyNewKerbals), lastSpawned);

        // Step through it here rather than as its own coroutine, to catch whatever goes wrong.
        while (true)
        {
            try
            {
                if (!batch.MoveNext())
                    break;
            }
            catch (Exception e)
            {
                ShowError(e);
                break;
            }

            status = Loc("Status_Spawning", lastSpawned.Count, situations.Count);
            yield return null;
        }

        int spawned = lastSpawned.Count;
        if (spawned > 0)
        {
            status = spawned == 1
                ? Loc("Status_SpawnedOne", template.DisplayName)
                : Loc("Status_Spawned", spawned, template.DisplayName, timer.Elapsed.TotalSeconds.ToString("N1"));
            statusIsError = false;
            ScreenMessages.PostScreenMessage(status, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        spawnRoutine = null;
    }

    internal void Undo()
    {
        int removed = lastSpawned.RemoveAll(Spawner.Remove);
        status = Loc("Status_Removed", removed);
        statusIsError = false;
    }

    // The craft being edited as it is now, saved or not, the vessel to clone, or the craft picked.
    private static VesselTemplate CreateTemplate() =>
        InEditor ? VesselTemplate.FromCraft(EditorLogic.fetch.ship.SaveShip())
        : source == Source.Clone ? VesselTemplate.FromVessel(CloneSource() ?? throw new SpawnException(Loc(HighLogic.LoadedSceneIsFlight ? "Craft_NoActiveVessel" : "Craft_SelectVessel")))
        : VesselTemplate.FromCraft(SelectedCraft?.path ?? throw new SpawnException(Loc("Craft_Choose")));

    // The first line, for the status and the summary. A long message would make them too tall.
    internal static string ShortMessage(Exception e) =>
        e is MissingPartsException missing ? missing.ShortMessage : e.Message.Split('\n')[0].TrimEnd(':', ' ');

    // The popup has the whole message.
    private void ShowError(Exception e)
    {
        if (e is not SpawnException)
            UnityEngine.Debug.LogException(e);

        status = ShortMessage(e);
        statusIsError = true;
        PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "LazySpawnerError", (e as SpawnException)?.title ?? Loc("Error_Title"), e.Message, KSP.Localization.Localizer.Format("#autoLOC_417274"), false, HighLogic.UISkin);
    }

    // Previews can't be random, or they'd jump about every frame.
    private List<SpawnSituation> CreateSituations(VesselTemplate template, int number, bool preview)
    {
        List<SpawnSituation> situations = new List<SpawnSituation>();

        if (situationMode == SituationMode.Orbit && !body.Valid)
            throw new SpawnException(Loc("Error_NoBody", body.Text));

        switch (situationMode.Value)
        {
            case SituationMode.Nearby:
                return preview ? null : Formations.Nearby(FlightGlobals.ActiveVessel, template, number, range, randomRotation);

            case SituationMode.Orbit:
                Orbit reference = CreateOrbit(0);

                for (int i = 0; i < number; i++)
                {
                    Orbit orbit;

                    if (number == 1)
                        orbit = reference;
                    else if (spreadAlongOrbit && reference.eccentricity < 1)
                        orbit = CreateOrbit(360.0 * i / number);
                    else
                        orbit = Formations.Cluster(reference, template, i, randomRotation);

                    situations.Add(SpawnSituation.Orbiting(orbit, randomRotation && !preview ? Random.rotation : null));
                }

                break;

            case SituationMode.LaunchSite:
                LaunchSites.Site site = LaunchSites.Named(launchSite) ?? throw new SpawnException(Loc("Summary_NoLaunchSites"));
                return Formations.LandedRow(template, site.body, site.latitude, site.longitude, site.heading, number, false);
        }

        return situations;
    }

    internal static Orbit CreateOrbit(double meanAnomalyOffset)
    {
        CelestialBody b = body.value;
        double UT = Planetarium.GetUniversalTime();

        // The epoch is now, so that the mean anomaly is where the vessel is when it appears.
        if (advancedOrbit)
            return new Orbit(inclination, eccentricity, sma, lan, argPe, (meanAnomaly + meanAnomalyOffset) * Mathf.Deg2Rad, UT, b);
        else
            return new Orbit(inclination, 0, b.Radius + altitude, 0, 0, meanAnomalyOffset * Mathf.Deg2Rad, UT, b);
    }

    private static CelestialBody FindBody(string name)
    {
        name = name.Trim();
        return FlightGlobals.Bodies.Find(b => b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || b.displayName.LocalizeRemoveGender().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Placement

    // The template for the selected craft or vessel, or null with the reason it can't be spawned.
    // Previews keep it until the craft file or vessel changes. Spawning makes it afresh, so clones are up to date.
    private (string key, VesselTemplate template, Exception error) cached;

    internal VesselTemplate Template(out Exception error, bool fresh = false)
    {
        Vessel original = CloneSource();
        Craft craft = SelectedCraft;
        string key = InEditor ? $"editor {editorChanges} {EditorLogic.fetch.ship.parts.Count} {EditorLogic.fetch.ship.shipName}"
            : source == Source.Clone ? $"clone {original?.id} {(original == null ? 0 : original.loaded ? original.parts.Count : original.protoVessel.protoPartSnapshots.Count)}"
            : $"craft {craft?.path} {(craft == null ? 0 : File.GetLastWriteTimeUtc(craft.path).Ticks)}";

        if (fresh || cached.key != key)
        {
            cached = (key, null, null);
            try
            {
                cached.template = CreateTemplate();
            }
            catch (Exception e)
            {
                cached.error = e;
            }
        }

        error = cached.error;
        return cached.template;
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

    // Describe an orbit in the advanced orbit fields.
    internal static void SetOrbit(Orbit orbit)
    {
        string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        body.Text = orbit.referenceBody.bodyName;
        advancedOrbit.Value = true;
        sma.Text = F(orbit.semiMajorAxis / 1000);
        eccentricity.Text = F(orbit.eccentricity);
        inclination.Text = F(orbit.inclination);
        lan.Text = F(orbit.LAN);
        argPe.Text = F(orbit.argumentOfPeriapsis);

        // The fields' epoch is always now.
        double meanAnomalyNow = orbit.getObtAtUT(Planetarium.GetUniversalTime()) * 2 * Math.PI / orbit.period;
        meanAnomaly.Text = F((meanAnomalyNow * Mathf.Rad2Deg % 360 + 360) % 360);
        situationMode.Value = SituationMode.Orbit;
    }

    // Point at where the vessels go, with the console out of the way until done.
    internal void Place()
    {
        DebugUI.Hide();
        placementTool.Begin();
    }

    #endregion
}
