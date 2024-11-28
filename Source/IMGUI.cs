using FinePrint.Utilities;
using KSP.Localization;
using KSP.UI.Screens;
using System;
using System.Collections;
using System.Reflection.Emit;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner
{
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class IMGUI : MonoBehaviour
    {
        private readonly string windowTitle = "Lazy Spawner";
        private int windowID;
        private static int windowWidth = 400;
        private Rect windowRect = new Rect(Screen.width * 0.04f, Screen.height * 0.1f, windowWidth, 0);
        private static string craftURL = @"G:\Games\KSP_win64\saves\default\Ships\SPH\HKA Aegis II (Spartwo).craft";
        private bool drawGUI = false;
        private static float fieldNameProportion = 0.18f;

        public static double altitude = 0;
        public static double inclination = 0;
        public static double eccentricity = 0;
        public static string bodyString = "Kerbin";

        private static string rangeString = "0";
        private static string countString = "1";

        private static bool cloneActiveVessel = false;
        private static bool randomRotation = false;

        private string[] crewModeNames = new string[] { "None", "Pilot", "Fill Command", "Fill All" };
        public Spawner.CrewMode crewMode = Spawner.CrewMode.Pilot;
        public bool onlyNewHires = false;

        private Coroutine stockCraftBrowserSelection;

        protected void Start()
        {
            windowID = GUIUtility.GetControlID(FocusType.Passive);
        }

        protected void Update()
        {
            if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.F))
                drawGUI = !drawGUI;
        }

        protected void OnGUI()
        {
            if (drawGUI)
                windowRect = GUILayout.Window(windowID, windowRect, FillWindow, windowTitle, GUILayout.Height(1), GUILayout.Width(windowWidth));
        }

        private static int sectionSpacing = 10;

        private void FillWindow(int id)
        {
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

                var select = "Select";
                GUI.skin.button.CalcMinMaxWidth(new GUIContent(select), out float width, out float _);

                if (GUILayout.Button(select, GUILayout.Width(width)) && stockCraftBrowserSelection == null)
                    stockCraftBrowserSelection = StartCoroutine(StockCraftBrowserSelection());

                GUILayout.EndHorizontal();
            }

            craftURL = craftURL.Trim('"');

            // Divider.
            GUILayout.Space(sectionSpacing);
            GUI.color = Color.grey;
            GUILayout.Label("<b>Situation:</b>");
            GUI.color = Color.white;

            randomRotation = GUILayout.Toggle(randomRotation, "  Random Rotation");
            TextFieldSetting("Range", ref rangeString);
            TextFieldSetting("Count", ref countString);
            TextFieldSetting("Body", ref bodyString);

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

            if (GUILayout.Button("Spawn"))
                CallSpawn();

            //SkipCraftThumbnails.enabled = GUILayout.Toggle(SkipCraftThumbnails.enabled, "Thumb Patch");
            //LimitPlayerCraftListRebuilds.enabled = GUILayout.Toggle(LimitPlayerCraftListRebuilds.enabled, "Rebuild Limit");

            // End window and release scroll lock.
            GUI.DragWindow(new Rect(0, 0, 10000, 500));
        }

        private void TextFieldSetting(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": ", GUILayout.Width(windowWidth * fieldNameProportion));
            value = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
        }

        private void CallSpawn()
        {
            if (!cloneActiveVessel && string.IsNullOrEmpty(craftURL) || cloneActiveVessel && FlightGlobals.ActiveVessel == null)
                return;

            float range = float.Parse(rangeString);
            int count = int.Parse(countString);

            for (int i = 0; i < count; i++)
            {
                Orbit orbit = HighLogic.LoadedSceneIsFlight && range > 0 ? NearbyOrbit(range) : GeneratedOrbit();
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

        private Orbit GeneratedOrbit()
        {
            string bodyLower = bodyString.ToLower();
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName.ToLower().Contains(bodyLower));
            if (body == null)
                return null;

            int seed = new System.Random().Next();
            return OrbitUtilities.GenerateOrbit(seed, body, OrbitType.EQUATORIAL, altitude, inclination, eccentricity);
        }

        private IEnumerator StockCraftBrowserSelection()
        {
            bool complete = false;
            var craftBrowser = CraftBrowserDialog.Spawn(
                EditorFacility.SPH, 
                HighLogic.SaveFolder, 
                (path, loadType) => { craftURL = path; complete = true; }, 
                () => complete = true, false);
            
            while (!complete && craftBrowser != null && craftBrowser.gameObject.activeInHierarchy)
                yield return null;
            
            craftBrowser?.Dismiss();
            stockCraftBrowserSelection = null;
        }
    }
}
