using FinePrint.Utilities;
using UnityEngine;

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

        public static double altitude = 0;
        public static double inclination = 0;
        public static double eccentricity = 0;
        public static string bodyString = "Kerbin";

        private static string rangeString = "0";
        private static string countString = "1";

        private static bool cloneActiveVessel = false;
        private static bool randomRotation = false;

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

        private void FillWindow(int id)
        {
            cloneActiveVessel = GUILayout.Toggle(cloneActiveVessel, "Clone Current Vessel");

            if (!cloneActiveVessel)
                TextFieldSetting("Craft URL", ref craftURL);

            craftURL = craftURL.Trim('"');

            TextFieldSetting("Range", ref rangeString);
            TextFieldSetting("Count", ref countString);
            TextFieldSetting("Body", ref bodyString);

            randomRotation = GUILayout.Toggle(randomRotation, "Random Rotation");

            if (GUILayout.Button("Spawn"))
                CallSpawn();

            // End window and release scroll lock.
            GUI.DragWindow(new Rect(0, 0, 10000, 500));
        }

        private void TextFieldSetting(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": ", GUILayout.Width(windowWidth * 0.13f));
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
                    rotation = !randomRotation ? Quaternion.identity : Quaternion.LookRotation(Random.onUnitSphere, Random.onUnitSphere)
                };

                if (cloneActiveVessel)
                    Spawner.Spawn(FlightGlobals.ActiveVessel, info);
                else
                    Spawner.Spawn(craftURL, info);
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
            //Orbit orbit = OrbitUtilities.GenerateOrbit(base.MissionSeed, orbit.referenceBody, OrbitType.RANDOM, orbitAltitudeFactor, orbitInclinationFactor, orbitEccentricity);
            return OrbitUtilities.GenerateOrbit(seed, body, OrbitType.EQUATORIAL, altitude, inclination, eccentricity);
        }
    }
}
