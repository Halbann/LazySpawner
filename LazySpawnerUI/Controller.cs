using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner;

// What to spawn, where, and with whom, and the spawning itself. The screen in the debug console shows it,
// and the placement tool points at places for it. Lives in flight and the tracking station.
[Settings(category = "UI")]
[KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
public class Controller : MonoBehaviour
{
    public static Controller Instance { get; private set; }

    public const string ScreenName = "Spawn Vessels";

    // Craft.
    public enum Source { Craft, Clone }
    public static readonly Setting<Source> source = Source.Craft;
    public static readonly Setting<string> craftPath = "";
    public static readonly Setting<string> lastClipboard = "";
    public static readonly TextField<int> count = new TextField<int>("Count", "1", TextField<int>.ParseInt, c => c > 0 && c <= 1000);

    // Situation.
    public enum SituationMode { Place, Nearby, Orbit, Landed }
    public static readonly Setting<SituationMode> situationMode = SituationMode.Place;
    public static readonly Setting<bool> randomRotation = false;
    public static readonly TextField<CelestialBody> body = new TextField<CelestialBody>("Body", "Kerbin", FindBody);

    // Nearby.
    public static readonly TextField<float> range = new TextField<float>("Within (m)", "100", TextField<float>.ParseFloat, r => r >= 0, "Vessels appear at random around the active vessel, no further away than this.");

    // Orbit.
    public static readonly Setting<bool> advancedOrbit = false;
    public static readonly TextField<double> altitude = new TextField<double>("Altitude (km)", "100", s => TextField<double>.ParseDouble(s) * 1000);
    public static readonly TextField<double> inclination = new TextField<double>("Inclination (°)", "0", TextField<double>.ParseDouble);
    public static readonly TextField<double> sma = new TextField<double>("Semi-Major Axis (km)", "700", s => TextField<double>.ParseDouble(s) * 1000, a => a != 0);
    public static readonly TextField<double> eccentricity = new TextField<double>("Eccentricity", "0", TextField<double>.ParseDouble, e => e >= 0);
    public static readonly TextField<double> lan = new TextField<double>("Asc. Node (°)", "0", TextField<double>.ParseDouble, tooltip: "Longitude of the ascending node.");
    public static readonly TextField<double> argPe = new TextField<double>("Arg. of Periapsis (°)", "0", TextField<double>.ParseDouble);
    public static readonly TextField<double> meanAnomaly = new TextField<double>("Mean Anomaly (°)", "0", TextField<double>.ParseDouble, tooltip: "Where along the orbit the vessel is, right now.");
    public static readonly Setting<bool> spreadAlongOrbit = true;

    // Landed.
    public static readonly TextField<double> latitude = new TextField<double>("Latitude (°)", "-0.0486", TextField<double>.ParseDouble, l => l >= -90 && l <= 90);
    public static readonly TextField<double> longitude = new TextField<double>("Longitude (°)", "-74.7200", TextField<double>.ParseDouble);
    public static readonly TextField<float> heading = new TextField<float>("Heading (°)", "90", TextField<float>.ParseFloat, tooltip: "Which way the vessel's nose points, clockwise from north.");

    // Crew.
    public static readonly Setting<CrewMode> crewMode = CrewMode.Pilot;
    public static readonly Setting<bool> onlyNewKerbals = false;

    // Settings, only in settings.cfg.
    public static readonly Setting<bool> useKeybind = true;
    public static readonly Setting<KeyCode> keybindModifier = KeyCode.LeftAlt;
    public static readonly Setting<KeyCode> keybind = KeyCode.F;

    // Result.
    internal readonly List<Vessel> lastSpawned = new List<Vessel>();
    internal string status = "";
    internal bool statusIsError;
    internal Coroutine spawnRoutine;

    internal PlacementTool placementTool;

    internal static bool InFlight => HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null;

    internal static bool CanPlace => InFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION;

    // The screen is showing, or vessels are being placed with it out of the way.
    internal bool IsOpen => SpawnScreen.Visible || placementTool.Placing;

    #region Lifecycle

    protected void Awake()
    {
        if (!HighLogic.LoadedSceneIsFlight && HighLogic.LoadedScene != GameScenes.TRACKSTATION)
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

        // There's nothing to be near in the tracking station.
        if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && situationMode == SituationMode.Nearby)
            situationMode.Value = SituationMode.Place;

        if (!File.Exists(craftPath.Value))
            craftPath.Value = CraftList.All.FirstOrDefault(c => c.MissingParts.Count == 0)?.path ?? "";
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
        if (clipboard == lastClipboard.Value || Instance == null)
            return false;

        lastClipboard.Value = clipboard;
        Craft craft = CraftList.FromText(clipboard);
        if (craft == null)
            return false;

        Select(craft);
        Instance.status = $"Picked up {craft.DisplayName} from the clipboard.";
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

    internal void Spawn()
    {
        if (spawnRoutine == null)
            spawnRoutine = StartCoroutine(SpawnRoutine());
    }

    private IEnumerator SpawnRoutine() =>
        SpawnRoutine(null);

    // Spawn at the given situations, or the ones the screen describes.
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

            status = $"Spawning... {lastSpawned.Count}/{situations.Count}";
            yield return null;
        }

        int spawned = lastSpawned.Count;
        if (spawned > 0)
        {
            status = spawned == 1
                ? $"Spawned {template.DisplayName}."
                : $"Spawned {spawned} × {template.DisplayName} in {timer.Elapsed.TotalSeconds:N1} s.";
            statusIsError = false;
            ScreenMessages.PostScreenMessage(status, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        spawnRoutine = null;
    }

    internal void Undo()
    {
        int removed = lastSpawned.Count(Spawner.Remove);
        lastSpawned.RemoveAll(v => v == null || v.state == Vessel.State.DEAD);
        status = removed == 1 ? "Removed 1 vessel." : $"Removed {removed} vessels.";
        statusIsError = false;
    }

    private VesselTemplate CreateTemplate()
    {
        if (source == Source.Clone)
            return VesselTemplate.FromVessel(CloneSource());

        CraftList.Remember(SelectedCraft);
        return VesselTemplate.FromCraft(craftPath.Value);
    }

    private void ShowError(Exception e)
    {
        string title = "Spawning Failed";
        string message = e.Message;

        if (e is SpawnException spawnException)
            title = spawnException.title;
        else
            UnityEngine.Debug.LogException(e);

        // The popup has the whole message. A long one would make the status too tall.
        status = e is MissingPartsException missing ? missing.ShortMessage : message.Split('\n')[0].TrimEnd(':', ' ');
        statusIsError = true;

        PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "LazySpawnerError", title, message, KSP.Localization.Localizer.Format("#autoLOC_417274"), false, HighLogic.UISkin);
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

            case SituationMode.Landed:
                return Formations.LandedRow(template, body.value, latitude, longitude, heading, number, randomRotation && !preview);
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

    internal static void UseActiveVesselPosition()
    {
        Vessel vessel = FlightGlobals.ActiveVessel;
        SetLanded(vessel.mainBody, vessel.latitude, vessel.longitude, Placement.Heading(vessel));
    }

    private static CelestialBody FindBody(string name)
    {
        name = name.Trim();
        return FlightGlobals.Bodies.Find(b => b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || b.displayName.LocalizeRemoveGender().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Placement

    // Previews use a cached template, rebuilt when the craft file or vessel changes.
    // The key and template live together so that a hot reload resets both, not just the template.
    private class PreviewCache
    {
        public string key;
        public VesselTemplate template;
        public string error;
    }

    private PreviewCache previewCache;

    // The template for the selected craft or vessel, or null with the reason it can't be spawned.
    internal VesselTemplate PreviewTemplate() => PreviewTemplate(out _);

    internal VesselTemplate PreviewTemplate(out string error)
    {
        string key;
        Vessel original = null;

        if (source == Source.Craft)
        {
            Craft craft = SelectedCraft;
            if (craft == null)
            {
                error = "Choose a craft.";
                return null;
            }

            key = craft.path + File.GetLastWriteTimeUtc(craft.path).Ticks;
        }
        else
        {
            original = CloneSource();
            if (original == null)
            {
                error = HighLogic.LoadedSceneIsFlight ? "There's no active vessel to clone." : "Select a vessel to clone.";
                return null;
            }

            key = $"{original.id}:{(original.loaded ? original.parts.Count : original.protoVessel.protoPartSnapshots.Count)}";
        }

        if (previewCache?.key != key)
        {
            previewCache = new PreviewCache { key = key };

            try
            {
                previewCache.template = original != null ? VesselTemplate.FromVessel(original) : VesselTemplate.FromCraft(craftPath.Value);
            }
            catch (MissingPartsException e)
            {
                previewCache.error = e.ShortMessage;
            }
            catch (Exception e)
            {
                previewCache.error = e.Message.Split('\n')[0];
            }
        }

        error = previewCache.error;
        return previewCache.template;
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

    // Previews face the default way. Randomness comes last, so the preview doesn't jump about.
    internal void SpawnAt(List<SpawnSituation> situations)
    {
        if (randomRotation)
            foreach (SpawnSituation situation in situations)
            {
                if (situation.landed)
                    situation.heading = Random.Range(0f, 360f);
                else
                    situation.rotation = Random.rotation;
            }

        if (spawnRoutine == null)
            spawnRoutine = StartCoroutine(SpawnRoutine(situations));
    }

    internal static void SetLanded(CelestialBody landedBody, double lat, double lon, float newHeading)
    {
        body.Text = landedBody.bodyName;
        latitude.Text = lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        longitude.Text = lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        heading.Text = newHeading.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
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
