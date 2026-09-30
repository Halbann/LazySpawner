using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LazySpawner;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
internal class SpawnScreenRegistration : MonoBehaviour
{
    protected void Start() => DebugUI.AddScreen<SpawnScreen>("Cheats", Controller.ScreenName);
}

// The Spawn Vessels screen, under Cheats in the debug console (Alt+F12). It's built from the stock
// screens' own widgets, and kept in step with the controller's settings every frame.
public class SpawnScreen : MonoBehaviour
{
    private static SpawnScreen instance;
    public static bool Visible => instance != null && instance.isActiveAndEnabled && DebugUI.Shown;

    private readonly List<Action> refresh = new List<Action>();
    private RectTransform main, picker, list;
    private TMP_InputField search;
    private string listed;
    private float nextClipboardCheck;

    private enum Filter { All, VAB, SPH }
    private static readonly Setting<Filter> filter = Filter.All;

    private const string typingLock = "LazySpawnerTyping";
    private const string grey = "<color=#a0a0a0>", orange = "<color=#ff9a6a>";
    private static readonly Color invalidColor = new Color(1f, 0.6f, 0.5f);

    private static Controller C => Controller.Instance;

    #region Lifecycle

    // Built in Start rather than Awake, so that after a hot reload it's rebuilt by the new code.
    protected void Start()
    {
        instance = this;
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        refresh.Clear();

        Stretch((RectTransform)transform);
        main = Page();
        picker = Page();
        BuildMain(DebugUI.ScrollList(main, 100));
        BuildPicker(picker);
        picker.gameObject.SetActive(false);
    }

    protected void OnEnable() => nextClipboardCheck = 0;

    protected void OnDisable() => InputLockManager.RemoveControlLock(typingLock);

    protected void Update()
    {
        // Copy a craft's path in another program, come back, and it's picked.
        if (Time.unscaledTime > nextClipboardCheck && C != null)
        {
            nextClipboardCheck = Time.unscaledTime + 0.5f;
            if (Controller.CheckClipboard() && picker.gameObject.activeSelf)
                ShowPicker(false);
        }

        foreach (Action action in refresh)
            action();

        // Otherwise typing "1" fires action group 1, and a space stages.
        GameObject selected = EventSystem.current?.currentSelectedGameObject;
        if (selected != null && selected.transform.IsChildOf(transform) && selected.GetComponent<TMP_InputField>()?.isFocused == true)
            InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, typingLock);
        else
            InputLockManager.RemoveControlLock(typingLock);
    }

    private RectTransform Page() =>
        Stretch(DebugUI.Column(transform, 3, new RectOffset(4, 4, 4, 4)));

    private static RectTransform Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    #endregion

    #region Binding

    private T Show<T>(T widget, Func<bool> when) where T : Component
    {
        refresh.Add(() =>
        {
            bool show = when();
            if (widget.gameObject.activeSelf != show)
                widget.gameObject.SetActive(show);
        });
        return widget;
    }

    private T Enable<T>(T widget, Func<bool> when) where T : Selectable
    {
        refresh.Add(() => widget.interactable = when());
        return widget;
    }

    private TextMeshProUGUI Text(TextMeshProUGUI text, Func<string> content)
    {
        refresh.Add(() =>
        {
            string s = content();
            if (text.text != s)
                text.text = s;
        });
        return text;
    }

    private TMP_InputField Field(Transform parent, ITextField setting, float width = 110)
    {
        Transform row = DebugUI.Row(parent);
        TextMeshProUGUI label = DebugUI.Label(row, setting.Title, DebugUI.LabelWidth);
        if (setting.Tooltip != null)
            DebugUI.Tooltip(label, setting.Tooltip);

        TMP_InputField input = DebugUI.Field(row, setting.Text, s => setting.Text = s, width);
        refresh.Add(() =>
        {
            if (!input.isFocused && input.text != setting.Text)
                input.text = setting.Text;
            input.image.color = setting.Valid ? Color.white : invalidColor;
        });
        return input;
    }

    private Toggle Toggle(Transform parent, string text, Setting<bool> setting, string tooltip = null)
    {
        Toggle toggle = DebugUI.Toggle(parent, text, setting, on => setting.Value = on);
        if (tooltip != null)
            DebugUI.Tooltip(toggle, tooltip);

        refresh.Add(() => toggle.SetIsOnWithoutNotify(setting));
        return toggle;
    }

    private Toggle[] Choice<T>(Transform parent, string title, Setting<T> setting, params string[] names) where T : Enum
    {
        Toggle[] toggles = DebugUI.Choice(parent, title, setting, names);
        refresh.Add(() =>
        {
            for (int i = 0; i < toggles.Length; i++)
                toggles[i].SetIsOnWithoutNotify(Convert.ToInt32(setting.Value) == i);
        });
        return toggles;
    }

    // A panel shown only when it applies.
    private RectTransform Panel(Transform parent, Func<bool> when) =>
        Show(DebugUI.Column(parent), when);

    #endregion

    #region Main

    private void BuildMain(Transform page)
    {
        Show(DebugUI.Paragraph(page, "Vessels can be spawned in flight and in the tracking station."), () => C == null);
        Transform content = Panel(page, () => C != null);

        // Craft.
        Transform craftRow = DebugUI.Row(content, 6);
        RawImage thumbnail = Thumbnail(craftRow);
        Text(DebugUI.Paragraph(craftRow), CraftDescription);
        DebugUI.Button(craftRow, "Change…", () => ShowPicker(true), 80);
        refresh.Add(() =>
        {
            thumbnail.texture = Controller.source == Controller.Source.Craft ? Controller.SelectedCraft?.Thumbnail : null;
            thumbnail.enabled = thumbnail.texture != null;
        });

        Field(content, Controller.count, 60);
        DebugUI.Spacer(content);

        // Where.
        Toggle[] modes = Choice(content, "Where", Controller.situationMode, "Nearby", "Orbit", "Landed");
        Enable(modes[0], () => Controller.InFlight);
        DebugUI.Tooltip(modes[0], "Scattered at random around the active vessel, keeping clear of it.");

        Transform nearby = Panel(content, () => Controller.situationMode == Controller.SituationMode.Nearby);
        Field(nearby, Controller.range, 60);
        Toggle(nearby, "Random Rotation", Controller.randomRotation);

        Transform orbit = Panel(content, () => Controller.situationMode == Controller.SituationMode.Orbit);
        BodyPicker(orbit);
        Transform simple = Panel(orbit, () => !Controller.advancedOrbit);
        Field(simple, Controller.altitude);
        Field(simple, Controller.inclination);
        Transform advanced = Panel(orbit, () => Controller.advancedOrbit);
        foreach (ITextField field in new ITextField[] { Controller.sma, Controller.eccentricity, Controller.inclination, Controller.lan, Controller.argPe, Controller.meanAnomaly })
            Field(advanced, field);

        Transform orbitOptions = DebugUI.Row(orbit, 12);
        Toggle(orbitOptions, "Advanced", Controller.advancedOrbit, "Describe the orbit with all its elements.");
        Show(Toggle(orbitOptions, "Spread Evenly", Controller.spreadAlongOrbit, "Space the vessels out evenly around the orbit, like a constellation. Otherwise they fly in formation."), () => Controller.count.value > 1);
        Toggle(orbitOptions, "Random Rotation", Controller.randomRotation);

        Transform orbitButtons = DebugUI.Row(orbit);
        Show(DebugUI.Button(orbitButtons, "Match Active Vessel", () => Controller.SetOrbit(FlightGlobals.ActiveVessel.orbit)), () => Controller.InFlight);
        Show(DebugUI.Button(orbitButtons, "Match Target", () => Controller.SetOrbit(FlightGlobals.fetch.VesselTarget.GetOrbit())), () => Controller.InFlight && FlightGlobals.fetch.VesselTarget?.GetOrbit() != null);

        Transform landed = Panel(content, () => Controller.situationMode == Controller.SituationMode.Landed);
        BodyPicker(landed);
        SitePicker(landed);
        Field(landed, Controller.latitude);
        Field(landed, Controller.longitude);
        Field(landed, Controller.heading);
        Transform landedOptions = DebugUI.Row(landed, 12);
        Show(DebugUI.Button(landedOptions, "Use Active Vessel's Position", Controller.UseActiveVesselPosition, 190), () => Controller.InFlight);
        Toggle(landedOptions, "Random Heading", Controller.randomRotation);
        DebugUI.Spacer(content);

        // Crew.
        Choice(content, "Crew", Controller.crewMode, "None", "Pilot", "Command", "Fill All");
        Transform hire = DebugUI.Row(content);
        DebugUI.Label(hire, "", DebugUI.LabelWidth);
        Show(Toggle(hire, "Hire New Kerbals", Controller.onlyNewKerbals, "Always hire new kerbals, instead of using ones already at the space centre."), () => Controller.crewMode != CrewMode.None);
        DebugUI.Spacer(content);

        // What happens, and the buttons that make it happen.
        bool ready = false;
        Text(DebugUI.Paragraph(content), () => Summary(out ready));

        Transform buttons = DebugUI.Row(content);
        Button place = Show(DebugUI.Button(buttons, "Place…", () => C.Place()), () => C.PlaceKind() != PlacementTool.Kind.None);
        DebugUI.Tooltip(place, "Point at where the vessels go. Click to spawn, Q/E to turn, shift-click to keep going, right-click to stop.");
        Enable(place, () => ready);
        Button spawn = Enable(DebugUI.Button(buttons, "Spawn", () => C.Spawn()), () => ready && C.spawnRoutine == null);
        Text(spawn.GetComponentInChildren<TextMeshProUGUI>(), () => C.spawnRoutine == null ? "Spawn" : "Spawning…");

        Text(DebugUI.Paragraph(content), () => C.statusIsError ? orange + C.status : C.status);

        Transform after = DebugUI.Row(content);
        Button switchTo = DebugUI.Button(after, "Switch To", () => FlightGlobals.SetActiveVessel(LastSpawned()));
        Show(switchTo, () => HighLogic.LoadedSceneIsFlight && LastSpawned() != null && LastSpawned() != FlightGlobals.ActiveVessel);
        Text(switchTo.GetComponentInChildren<TextMeshProUGUI>(), () => $"Switch To {LastSpawned()?.GetDisplayName()}");
        Button undo = Show(DebugUI.Button(after, "Undo", () => C.Undo()), () => Removable() > 0);
        DebugUI.Tooltip(undo, "Remove the vessels you just spawned. Their crew go home.");
        Text(undo.GetComponentInChildren<TextMeshProUGUI>(), () => Removable() == 1 ? "Undo" : $"Undo ({Removable()})");
        DebugUI.Spacer(content, 12);

        Transform settings = DebugUI.Row(content, 12);
        Toggle(settings, "Toolbar Button", Controller.showButton).onValueChanged.AddListener(on => { if (on) C.AddToolbarButton(); else C.RemoveToolbarButton(); });
        Toggle(settings, "Alt+F Opens This", Controller.useKeybind);
    }

    private Vessel LastSpawned()
    {
        C.lastSpawned.RemoveAll(v => v == null || v.state == Vessel.State.DEAD);
        return C.lastSpawned.FirstOrDefault();
    }

    private int Removable() =>
        LastSpawned() == null ? 0 : C.lastSpawned.Count(v => v != FlightGlobals.ActiveVessel);

    private static RawImage Thumbnail(Transform parent, float size = 36)
    {
        GameObject image = new GameObject("Thumbnail", typeof(RectTransform));
        image.transform.SetParent(parent, false);
        LayoutElement layout = image.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = layout.minHeight = layout.preferredHeight = size;
        return image.AddComponent<RawImage>();
    }

    // "< Kerbin >", like the stock Set Position screen.
    private void BodyPicker(Transform parent)
    {
        Stepper(parent, "Body", () => Controller.body.Valid ? Controller.body.value.displayName.LocalizeRemoveGender() : Controller.body.Text, step =>
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            int index = Controller.body.Valid ? bodies.IndexOf(Controller.body.value) : 0;
            Controller.body.Text = bodies[(index + step + bodies.Count) % bodies.Count].bodyName;
        });
    }

    private void SitePicker(Transform parent)
    {
        Stepper(parent, "Site", () => LaunchSites.At(Controller.body.value, Controller.latitude, Controller.longitude)?.name ?? "Anywhere", step =>
        {
            List<LaunchSites.Site> sites = LaunchSites.On(Controller.body.value);
            if (sites.Count == 0)
                return;

            int index = sites.IndexOf(LaunchSites.At(Controller.body.value, Controller.latitude, Controller.longitude));
            LaunchSites.Site site = sites[index < 0 ? (step > 0 ? 0 : sites.Count - 1) : (index + step + sites.Count) % sites.Count];
            Controller.SetLanded(site.body, site.latitude, site.longitude, site.heading, false);
        });
    }

    private void Stepper(Transform parent, string title, Func<string> current, Action<int> step)
    {
        Transform row = DebugUI.Row(parent);
        DebugUI.Label(row, title, DebugUI.LabelWidth);
        DebugUI.Button(row, "<", () => step(-1), 24);
        TextMeshProUGUI name = Text(DebugUI.Label(row, "", 140), current);
        name.alignment = TextAlignmentOptions.Center;
        DebugUI.Button(row, ">", () => step(1), 24);
    }

    private string CraftDescription()
    {
        if (C == null)
            return "";

        if (Controller.source == Controller.Source.Clone)
        {
            Vessel original = Controller.CloneSource();
            return original == null
                ? orange + (HighLogic.LoadedSceneIsFlight ? "There's no active vessel to clone." : "Select a vessel to clone.")
                : $"<b>{original.GetDisplayName()}</b>\n{grey}A copy of the {(HighLogic.LoadedSceneIsFlight ? "active" : "selected")} vessel · {original.protoVessel?.protoPartSnapshots.Count ?? original.parts.Count} parts";
        }

        Craft craft = Controller.SelectedCraft;
        return craft == null ? orange + "Choose a craft." : Describe(craft);
    }

    private static string Describe(Craft craft) =>
        $"<b>{craft.DisplayName}</b>\n" + (craft.missingParts.Count > 0
            ? $"{orange}{craft.missingParts.Count} missing part{(craft.missingParts.Count == 1 ? "" : "s")}: {string.Join(", ", craft.missingParts.Take(3))}{(craft.missingParts.Count > 3 ? "…" : "")}"
            : $"{grey}{(craft.elsewhere ? "From elsewhere" : craft.facility)} · {craft.partCount} parts · {Ago(craft.modified)}");

    private static string Ago(DateTime time)
    {
        TimeSpan age = DateTime.Now - time;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 60 ? $"{(int)age.TotalDays} days ago"
            : time.ToString("d MMM yyyy");
    }

    #endregion

    #region Summary

    // One or two sentences saying what Spawn will do, and anything that's wrong with it.
    private string Summary(out bool ready)
    {
        ready = false;
        if (C == null)
            return "";

        VesselTemplate template = C.PreviewTemplate(out string error);
        if (template == null)
            return orange + (error ?? "");

        List<string> problems = new List<string>();
        ITextField[] fields = Controller.situationMode.Value switch
        {
            Controller.SituationMode.Nearby => new ITextField[] { Controller.count, Controller.range },
            Controller.SituationMode.Landed => new ITextField[] { Controller.count, Controller.body, Controller.latitude, Controller.longitude, Controller.heading },
            _ => Controller.advancedOrbit
                ? new ITextField[] { Controller.count, Controller.body, Controller.sma, Controller.eccentricity, Controller.inclination, Controller.lan, Controller.argPe, Controller.meanAnomaly }
                : new ITextField[] { Controller.count, Controller.body, Controller.altitude, Controller.inclination },
        };

        foreach (ITextField field in fields.Where(f => !f.Valid))
            problems.Add($"{field.Title.Split('(')[0].Trim()} isn't right.");

        if (problems.Count > 0)
            return orange + string.Join(" ", problems);

        int number = Controller.count;
        string what = number == 1 ? template.DisplayName : $"{number} × {template.DisplayName}";
        string where = Where(number, problems);
        ready = problems.Count == 0;

        string text = $"{what} {where}. {Crew(template, number)}";
        return problems.Count == 0 ? text : $"{text}\n{orange}{string.Join(" ", problems)}";
    }

    private string Where(int number, List<string> problems)
    {
        CelestialBody body = Controller.body.value;
        string bodyName = body?.displayName.LocalizeRemoveGender();

        switch (Controller.situationMode.Value)
        {
            case Controller.SituationMode.Nearby:
                Vessel active = FlightGlobals.ActiveVessel;
                bool ground = active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH;
                return $"{(number > 1 ? "scattered" : "placed at random")} within {Controller.range.value:0} m of {active.GetDisplayName()}, {(ground ? "on the ground" : "in orbit")}";

            case Controller.SituationMode.Landed:
                if (body.pqsController == null)
                    problems.Add($"{bodyName} has no surface.");
                if (C.placementTool.PreviewBlocked)
                    problems.Add("That's on top of another vessel.");

                string site = LaunchSites.At(body, Controller.latitude, Controller.longitude)?.name;
                string biome = ScienceUtil.GetExperimentBiomeLocalized(body, Controller.latitude, Controller.longitude);
                string place = site != null ? $"at the {site} on {bodyName}"
                    : string.IsNullOrEmpty(biome) ? $"on {bodyName} at {Controller.latitude.value:0.###}°, {Controller.longitude.value:0.###}°"
                    : $"on {bodyName}'s {biome}";
                return $"{(number > 1 ? "side by side " : "")}{place}";

            default:
                Orbit orbit;
                try
                {
                    orbit = Controller.CreateOrbit(0);
                }
                catch
                {
                    problems.Add("That's not an orbit.");
                    return "";
                }

                if (orbit.eccentricity < 1 && orbit.semiMajorAxis < 0)
                    problems.Add("Elliptical orbits need a positive semi-major axis.");
                else if (orbit.eccentricity >= 1 && orbit.semiMajorAxis > 0)
                    problems.Add("Hyperbolic orbits need a negative semi-major axis.");

                string warning = orbit.PeR < body.Radius ? $" {orange}It hits the surface.</color>"
                    : body.atmosphere && orbit.PeA < body.atmosphereDepth ? $" {orange}It dips into the atmosphere.</color>"
                    : orbit.eccentricity < 1 && orbit.ApR > body.sphereOfInfluence ? $" {orange}It leaves {bodyName}'s sphere of influence.</color>" : "";

                string shape = orbit.eccentricity < 0.001
                    ? $"a {Distance(orbit.PeA)} orbit of {bodyName}"
                    : $"orbit of {bodyName}, {Distance(orbit.PeA)} by {(orbit.eccentricity < 1 ? Distance(orbit.ApA) : "escaping")}";
                string inclined = Math.Abs(orbit.inclination) > 0.05 ? $", inclined {orbit.inclination:0.#}°" : "";
                string spread = number < 2 ? "in " : Controller.spreadAlongOrbit && orbit.eccentricity < 1 ? "spread around " : "in formation, in ";
                return $"{spread}{shape}{inclined}.{warning}".TrimEnd('.');
        }
    }

    private static string Distance(double metres) =>
        Math.Abs(metres) < 10000 ? $"{metres:0} m" : $"{metres / 1000:0.#} km";

    private static string Crew(VesselTemplate template, int number)
    {
        CrewMode mode = Controller.crewMode;
        if (mode == CrewMode.None)
            return "No crew.";

        // The same seats the spawner fills: no external seats, command parts only unless filling all.
        List<Part> seats = template.Parts.Select(p => p.info?.partPrefab).Where(p => p != null && p.CrewCapacity > 0 && !p.HasModuleImplementing<KerbalSeat>()).ToList();
        int each = mode == CrewMode.Pilot ? Math.Min(1, seats.Count)
            : seats.Where(p => mode == CrewMode.FillAll || p.HasModuleImplementing<ModuleCommand>()).Sum(p => p.CrewCapacity);

        if (each == 0)
            return "There are no seats for crew.";

        int needed = each * number;
        int available = Controller.onlyNewKerbals ? 0 : HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available).Count();
        int hired = Math.Max(0, needed - available);
        string crew = mode == CrewMode.Pilot ? (number == 1 ? "A pilot" : "A pilot each") : $"{needed} crew";

        return hired == 0 ? $"{crew}." : hired == needed ? $"{crew}, newly hired." : $"{crew}, {hired} of them newly hired.";
    }

    #endregion

    #region Craft Picker

    private void ShowPicker(bool show)
    {
        main.gameObject.SetActive(!show);
        picker.gameObject.SetActive(show);

        if (show)
        {
            CraftList.Refresh();
            listed = null;
            search.text = "";
            search.ActivateInputField();
        }
    }

    private void BuildPicker(Transform page)
    {
        Transform top = DebugUI.Row(page);
        search = DebugUI.Field(top, "", _ => { }, -1);
        search.placeholder.GetComponent<TextMeshProUGUI>().text = "Search, or paste a path to a craft file";
        DebugUI.Tooltip(DebugUI.Button(top, "Stock…", () => { ShowPicker(false); C.StartCoroutine(C.StockCraftBrowser()); }, 60), "Choose with the stock craft browser instead.");
        DebugUI.Button(top, "Back", () => ShowPicker(false), 50);

        Choice(page, "Show", filter, "All", "VAB", "SPH");
        list = DebugUI.ScrollList(page, 100);

        refresh.Add(() =>
        {
            string key = $"{search.text}|{filter.Value}|{Controller.CloneSource()?.id}|{CraftList.All.Count}";
            if (picker.gameObject.activeSelf && key != listed)
            {
                listed = key;
                FillList();
            }
        });
    }

    private void FillList()
    {
        foreach (Transform child in list)
            Destroy(child.gameObject);

        // A path, pasted or typed.
        Craft pasted = CraftList.FromText(search.text);
        if (pasted != null)
        {
            CraftRow(pasted.Thumbnail, Describe(pasted) + $"\n{grey}{pasted.path}", pasted, () => Controller.Select(pasted));
            return;
        }

        Vessel original = Controller.CloneSource();
        if (original != null && !original.isEVA && search.text == "")
            CraftRow(null, $"<b>{original.GetDisplayName()}</b>\n{grey}Copy the {(HighLogic.LoadedSceneIsFlight ? "active" : "selected")} vessel", null, () => Controller.source.Value = Controller.Source.Clone);

        string[] words = search.text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (Craft craft in CraftList.All)
        {
            if (filter != Filter.All && craft.facility != filter.Value.ToString())
                continue;
            if (!words.All(w => craft.DisplayName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;

            CraftRow(craft.Thumbnail, Describe(craft), craft, () => Controller.Select(craft));
        }
    }

    private void CraftRow(Texture thumbnail, string text, Craft craft, Action pick)
    {
        Button row = DebugUI.Button(list, "", () => { pick(); ShowPicker(false); });
        LayoutElement layout = row.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = 40;

        TextMeshProUGUI label = row.GetComponentInChildren<TextMeshProUGUI>();
        label.text = text;
        label.richText = true;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.margin = new Vector4(44, 0, 4, 0);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        RectTransform image = Thumbnail(row.transform, 36).rectTransform;
        image.GetComponent<RawImage>().texture = thumbnail;
        image.GetComponent<RawImage>().enabled = thumbnail != null;
        image.anchorMin = image.anchorMax = image.pivot = new Vector2(0, 0.5f);
        image.anchoredPosition = new Vector2(2, 0);
        image.sizeDelta = new Vector2(36, 36);

        if (craft != null && craft.missingParts.Count > 0)
        {
            row.interactable = false;
            DebugUI.Tooltip(row, "Missing parts:\n" + string.Join("\n", craft.missingParts));
        }
    }

    #endregion
}
