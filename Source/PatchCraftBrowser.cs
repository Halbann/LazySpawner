using HarmonyLib;
using KSP.UI.Screens;
using System.IO;
using System.Linq;

namespace LazySpawner
{
    [HarmonyPatch(typeof(CraftBrowserDialog), nameof(CraftBrowserDialog.setbottomButtons))]
    class FixMergeButtonVisibility
    {
        static void Postfix(CraftBrowserDialog __instance) =>
            __instance.btnMerge.gameObject.SetActive(__instance.btnMerge.gameObject.activeSelf && __instance.showMergeOption);
    }

    [HarmonyPatch(typeof(ShipConstruction), nameof(ShipConstruction.CheckCraftFileType))]
    class SkipCraftThumbnails
    {
        public static bool enabled = true;

        static bool Prefix(string filePath, ref EditorFacility __result)
        {
            if (!enabled)
                return true;

            if (string.IsNullOrEmpty(filePath))
            {
                __result = EditorFacility.None;
                return false;
            }

            // Search the file path for either "SPH" or "VAB".
            bool sph = false;
            string facilityString = filePath.Split(Path.DirectorySeparatorChar).FirstOrDefault(f => (sph = f == "SPH") || f == "VAB");

            if (string.IsNullOrEmpty(facilityString))
            {
                // Fallback in case the split fails for any reason.

                __result = facilityString.Contains("SPH") ? EditorFacility.SPH :
                    (facilityString.Contains("VAB") ? EditorFacility.VAB : EditorFacility.None);
            }
            else
            {
                __result = sph ? EditorFacility.SPH : EditorFacility.VAB;
            }

            return false;
        }
    }

    /*[HarmonyPatch(typeof(CraftBrowserDialog), nameof(CraftBrowserDialog.BuildPlayerCraftList))]
    class LimitPlayerCraftListRebuilds
    {
        public static int lastFrame;
        public static HashSet<int> builtThisFrame = new HashSet<int>();
        public static bool enabled = true;

        static bool Prefix(CraftBrowserDialog __instance)
        {
            if (!enabled)
                return true;

            if (Time.frameCount != lastFrame)
            {
                builtThisFrame.Clear();
                lastFrame = Time.frameCount;
            }

            int hash = __instance.GetHashCode();
            if (builtThisFrame.Contains(hash))
                return false;

            builtThisFrame.Add(hash);

            return true;
        }

        public static void PreventDelayedRebuild(CraftBrowserDialog dialog)
        {
            if (!enabled)
                return;

            dialog.directoryController.isEnabledThisFrame = false;
        }
    }

    [HarmonyPatch(typeof(CraftBrowserDialog), nameof(CraftBrowserDialog.Start))]
    class Patch1
    {
        static void Postfix(CraftBrowserDialog __instance) =>
            LimitPlayerCraftListRebuilds.PreventDelayedRebuild(__instance);
    }

    [HarmonyPatch(typeof(CraftBrowserDialog), nameof(CraftBrowserDialog.ReDisplay))]
    class Patch2
    {
        static void Postfix(CraftBrowserDialog __instance) =>
            LimitPlayerCraftListRebuilds.PreventDelayedRebuild(__instance);
    }*/
}
