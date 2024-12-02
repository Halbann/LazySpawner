using HarmonyLib;
using KSP.Localization;
using KSP.UI;
using KSP.UI.Screens;
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner
{
    [Settings(category = "UI")]
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class IMGUI : MonoBehaviour
    {
        // GUI.
        private static string windowTitle;
        public static int windowWidth = 400;
        public static float fieldNameProportion = 0.5f; // 0.18f
        public static int sectionSpacing = 10;

        private int windowID;
        private Rect windowRect = new Rect(Screen.width * 0.04f, Screen.height * 0.1f, windowWidth, 0);
        private bool drawGUI = false;
        private bool showSettings = false;
        private Coroutine stockCraftBrowserSelection;
        private ApplicationLauncherButton appLauncherButton;

        // Styles.
        private static GUIStyle topButtonStyle;
        private static GUIStyle boxStyle;

        // Craft.
        public static bool cloneActiveVessel = false;
        public static string craftURL = @"G:\Games\KSP_win64\saves\default\Ships\SPH\HKA Aegis II (Spartwo).craft";
        public readonly static TextField<int> countField = new TextField<int>("Count", "1", int.Parse);

        // Orbit.
        public readonly static TextField<CelestialBody> body = new TextField<CelestialBody>("Body", "Kerbin", t => FlightGlobals.Bodies.Find(b => b.bodyName.Equals(t, StringComparison.OrdinalIgnoreCase)));

        // Nearby.
        public readonly static TextField<float> rangeField = new TextField<float>("Within Range (m)", "100", float.Parse);

        // Simple orbit.
        public readonly static TextField<float> altitude = new TextField<float>("Altitude (km)", "70", s => float.Parse(s) * 1000);
        public readonly static TextField<float> inclination = new TextField<float>("Inclination (°)", "0", float.Parse);

        // Advanced orbit.
        public readonly static TextField<float> sma = new TextField<float>("Semi-Major Axis (km)", "700", s => float.Parse(s) * 1000);
        public readonly static TextField<float> eccentricity = new TextField<float>("Eccentricity (ratio)", "0", float.Parse);
        public readonly static TextField<float> lan = new TextField<float>("Longitude of Ascending Node (°)", "0", float.Parse);
        public readonly static TextField<float> argPe = new TextField<float>("Argument of Periapsis (°)", "0", float.Parse);
        public readonly static TextField<float> mna = new TextField<float>("Mean Anomaly at Epoch (rad)", "0", float.Parse);
        public readonly static TextField<float> epoch = new TextField<float>("Epoch (seconds)", "0", float.Parse);
        public static bool advanced = false;

        // Situation mode.
        private string[] sitModeNames = new string[] { "Nearby", "Orbit" };
        public enum SituationMode { Nearby, Orbit }
        public SituationMode situationMode = SituationMode.Nearby;

        // Rotation.
        private static bool randomRotation = false;

        // Crew.
        private static readonly string[] crewModeNames = new string[] { "None", "Pilot", "Fill Command", "Fill All" };
        public static Spawner.CrewMode crewMode = Spawner.CrewMode.Pilot;
        public static bool onlyNewHires = false;

        // Patch.
        private static bool patchesApplied = false;

        // Settings.
        [Setting] public static bool showButton = true;
        [Setting] public static bool useKeybind = true;
        [Setting] public static KeyCode keybindModifier = KeyCode.LeftAlt;
        [Setting] public static KeyCode keybind = KeyCode.F;

        protected void Start()
        {
            windowID = GUIUtility.GetControlID(FocusType.Passive);

            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            windowTitle = $"Lazy Spawner v{version.Major}.{version.Minor}.{version.Build} by Halban";

            if (!patchesApplied)
            {
                patchesApplied = true;
                Harmony harmony = new Harmony("LazySpawner");
                harmony.PatchAll();
            }

            AddToolbarButton();
        }

        private void InitStyles()
        {
            topButtonStyle = new GUIStyle(GUI.skin.GetStyle("Button"))
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Overflow
            };

            boxStyle = GUI.skin.box;
        }

        protected void Update()
        {
            if (!useKeybind)
                return;

            if ((keybindModifier == KeyCode.None || Input.GetKey(keybindModifier)) && Input.GetKeyDown(keybind))
            {
                if (drawGUI)
                    Close();
                else
                    Open();
            }
        }

        protected void OnDestroy()
        {
            if (appLauncherButton)
                ApplicationLauncher.Instance.RemoveModApplication(appLauncherButton);
        }

        private void Close()
        {
            GlobalSettings.Save();
            drawGUI = false;

            if (appLauncherButton && appLauncherButton.toggleButton.CurrentState == UIRadioButton.State.True)
                appLauncherButton.SetFalse(false);
        }

        private void Open()
        {
            drawGUI = true;

            if (appLauncherButton && appLauncherButton.toggleButton.CurrentState == UIRadioButton.State.False)
                appLauncherButton.SetTrue(false);
        }

        protected void OnGUI()
        {
            if (topButtonStyle == null)
                InitStyles();

            if (drawGUI)
                windowRect = GUILayout.Window(windowID, windowRect, FillWindow, windowTitle, GUILayout.Height(1), GUILayout.Width(windowWidth));
        }

        private void FillWindow(int id)
        {
            TopButtons();

            if (showSettings)
                SettingsSection();
            else
                MainSection();

            // End window.
            GUI.DragWindow(new Rect(0, 0, 10000, 500));
        }

        private void MainSection()
        {
            bool ready = true;

            // Divider.
            GUI.color = Color.grey;
            GUILayout.Label("<b>Craft:</b>");
            GUI.color = Color.white;

            cloneActiveVessel = GUILayout.Toggle(cloneActiveVessel, "  Clone Current Vessel");

            // Paste craft URL or select via stock craft browser.
            if (!cloneActiveVessel)
            {
                GUILayout.BeginHorizontal();

                GUILayout.Label("Craft URL: ", GUILayout.Width(windowWidth * fieldNameProportion));
                craftURL = GUILayout.TextField(craftURL);

                string select = "Select";
                GUI.skin.button.CalcMinMaxWidth(new GUIContent(select), out float selectWidth, out float _);
                if (GUILayout.Button(select, GUILayout.Width(selectWidth)) && stockCraftBrowserSelection == null)
                    stockCraftBrowserSelection = StartCoroutine(StockCraftBrowserSelection());

                GUILayout.EndHorizontal();
            }

            craftURL = craftURL.Trim('"');

            countField.Draw(ref ready);

            // Divider.
            GUILayout.Space(sectionSpacing);
            GUI.color = Color.grey;
            GUILayout.Label("<b>Situation:</b>");
            GUI.color = Color.white;

            randomRotation = GUILayout.Toggle(randomRotation, "  Random Rotation");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode: ", GUILayout.Width(windowWidth * fieldNameProportion));
            situationMode = (SituationMode)GUILayout.SelectionGrid((int)situationMode, sitModeNames, 2);
            GUILayout.EndHorizontal();

            GUILayout.BeginVertical(boxStyle);

            switch (situationMode)
            {
                case SituationMode.Nearby:

                    rangeField.Draw(ref ready);

                    break;
                case SituationMode.Orbit:

                    advanced = GUILayout.Toggle(advanced, "  Advanced");
                    if (advanced)
                    {
                        body.Draw(ref ready);
                        sma.Draw(ref ready);
                        inclination.Draw(ref ready);
                        eccentricity.Draw(ref ready);
                        lan.Draw(ref ready);
                        argPe.Draw(ref ready);
                        mna.Draw(ref ready);
                    }
                    else
                    {
                        body.Draw(ref ready);
                        altitude.Draw(ref ready);
                        inclination.Draw(ref ready);
                    }

                    break;
                //case SituationMode.Land:

                //    body.Draw(ref ready);

                //    break;
            }

            GUILayout.EndVertical();

            // Divider.
            GUILayout.Space(sectionSpacing);
            GUI.color = Color.grey;
            GUILayout.Label("<b>Crew:</b>");
            GUI.color = Color.white;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Selection: ", GUILayout.Width(windowWidth * fieldNameProportion));
            crewMode = (Spawner.CrewMode)GUILayout.SelectionGrid((int)crewMode, crewModeNames, 4);
            GUILayout.EndHorizontal();

            if (crewMode != Spawner.CrewMode.None)
                onlyNewHires = GUILayout.Toggle(onlyNewHires, "  Use Only New Kerbals");

            // Divider.
            GUILayout.Space(sectionSpacing);

            if (!ready)
                GUI.enabled = false;

            if (GUILayout.Button("Spawn"))
                CallSpawn();

            GUI.enabled = true;
        }

        private void TopButtons()
        {
            // Close button.
            if (GUI.Button(new Rect(windowRect.width - 18, 2, 16, 16), "x", topButtonStyle))
                Close();

            // Settings button.
            if (GUI.Button(new Rect(windowRect.width - (18 * 2), 2, 16, 16), "s", topButtonStyle))
                showSettings = !showSettings;
        }

        private void SettingsSection()
        {
            useKeybind = GUILayout.Toggle(useKeybind, $"Use Keybind ({keybindModifier} + {keybind})");
            showButton = GUILayout.Toggle(showButton, "Show Toolbar Button (after next scene load)");
        }

        private void CallSpawn()
        {
            if (!cloneActiveVessel && string.IsNullOrEmpty(craftURL) || cloneActiveVessel && FlightGlobals.ActiveVessel == null)
                return;

            float range = rangeField.value;
            int count = countField.value;

            for (int i = 0; i < count; i++)
            {
                Orbit orbit = HighLogic.LoadedSceneIsFlight && situationMode == SituationMode.Nearby ? NearbyOrbit(range) : CreateOrbit();
                if (orbit == null)
                    continue;

                Spawner.SituationInfo info = new Spawner.SituationInfo()
                {
                    orbit = orbit,
                    rotation = !randomRotation ? Quaternion.identity : Quaternion.LookRotation(Random.onUnitSphere, Random.onUnitSphere),
                    situation = Vessel.Situations.ORBITING
                };

                Spawner.onlyHireNewKerbals = onlyNewHires;

                try
                {
                    if (cloneActiveVessel)
                        Spawner.Spawn(FlightGlobals.ActiveVessel, info, crewMode);
                    else
                        Spawner.Spawn(craftURL, info, crewMode);
                }
                catch (CraftParser.MissingPartsException e)
                {
                    PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "Craft Loading Error", Localizer.Format("#autoLOC_6002424"), e.Message, Localizer.Format("#autoLOC_417274"), persistAcrossScenes: true, HighLogic.UISkin);
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    return;
                }
            }
        }

        private Orbit NearbyOrbit(float range)
        {
            double UT = Planetarium.GetUniversalTime();
            FlightGlobals.ActiveVessel.orbit.GetOrbitalStateVectorsAtUT(UT, out Vector3d pos, out Vector3d vel);

            pos += Random.onUnitSphere * Random.Range(30, Mathf.Max(range, 50));

            Orbit orbit = new Orbit(FlightGlobals.ActiveVessel.orbit);
            orbit.UpdateFromStateVectors(pos, vel, FlightGlobals.ActiveVessel.orbit.referenceBody, UT);

            return orbit;
        }

        private Orbit CreateOrbit()
        {
            if (advanced)
                return new Orbit(inclination.value, eccentricity.value, sma.value, lan.value, argPe.value, mna.value, epoch.value, body.value);
            else
                return new Orbit(inclination.value, eccentricity.value, body.value.Radius + altitude.value, lan.value, argPe.value, mna.value, epoch.value, body.value);
        }

        private IEnumerator StockCraftBrowserSelection()
        {
            bool complete = false;
            CraftBrowserDialog craftBrowser = CraftBrowserDialog.Spawn(
                EditorFacility.SPH,
                HighLogic.SaveFolder,
                (path, loadType) => { craftURL = path; complete = true; },
                () => complete = true, false);

            while (!complete && craftBrowser != null && craftBrowser.gameObject.activeInHierarchy)
                yield return null;

            craftBrowser?.Dismiss();
            stockCraftBrowserSelection = null;
        }

        public void AddToolbarButton()
        {
            if (!showButton)
                return;

            if (appLauncherButton != null)
                return;

            Texture buttonTexture = GameDatabase.Instance.GetTexture("LazySpawner/Textures/icon", false);
            appLauncherButton = ApplicationLauncher.Instance.AddModApplication(Open, Close, null, null, null, null, ApplicationLauncher.AppScenes.ALWAYS, buttonTexture);
        }
    }
}
