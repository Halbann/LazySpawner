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
    private RectTransform main, picker;
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
        // A hot reload copies these across, full of the old screen's widgets.
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        refresh.Clear();
        rows.Clear();
        shownFrom = -1;

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

    // Every row of the form is a label in the label column, then its controls, so they all line up.
    private static Transform Line(Transform parent, string label = "", string tooltip = null)
    {
        Transform row = DebugUI.Row(parent);
        TextMeshProUGUI text = DebugUI.Label(row, label, DebugUI.LabelWidth);
        if (tooltip != null)
            DebugUI.Tooltip(text, tooltip);
        return row;
    }

    private TMP_InputField Field(Transform parent, ITextField setting)
    {
        TMP_InputField input = DebugUI.Field(Line(parent, setting.Title, setting.Tooltip), setting.Text, s => setting.Text = s, DebugUI.ControlWidth);
        refresh.Add(() =>
        {
            if (!input.isFocused && input.text != setting.Text)
                input.text = setting.Text;
            input.image.color = setting.Valid ? Color.white : invalidColor;
        });
        return input;
    }

    // Toggles go one to a line, under the controls above them, and the line goes when they don't apply.
    private Toggle Toggle(Transform parent, string text, Setting<bool> setting, string tooltip = null, Func<bool> when = null)
    {
        Transform line = Line(parent);
        Toggle toggle = DebugUI.Toggle(line, text, setting, on => setting.Value = on);
        if (tooltip != null)
            DebugUI.Tooltip(toggle, tooltip);
        if (when != null)
            Show(line, when);

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
        const float half = (DebugUI.ControlWidth - 4) / 2;

        // Craft: its picture where the labels go, and what it is where the controls go.
        DebugUI.Heading(content, "Craft");
        Transform card = DebugUI.Row(content);
        card.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
        RawImage thumbnail = Thumbnail(card, 64);
        refresh.Add(() =>
        {
            thumbnail.texture = Controller.source == Controller.Source.Craft ? Controller.SelectedCraft?.Thumbnail : null;
            thumbnail.enabled = thumbnail.texture != null;
        });
        DebugUI.Label(card, "", DebugUI.LabelWidth - 68);
        Transform about = DebugUI.Column(card);
        LayoutElement aboutLayout = about.gameObject.AddComponent<LayoutElement>();
        aboutLayout.flexibleWidth = 1;
        aboutLayout.minWidth = DebugUI.ControlWidth;
        Text(DebugUI.Paragraph(about), CraftDescription);
        DebugUI.Button(DebugUI.Row(about), "Change…", () => ShowPicker(true), DebugUI.ControlWidth);
        Field(content, Controller.count);
        DebugUI.Spacer(content, 10);

        // Where.
        DebugUI.Heading(content, "Where");
        Toggle[] modes = Choice(content, "", Controller.situationMode, "Nearby", "Orbit", "Landed");
        Enable(modes[0], () => Controller.InFlight);
        DebugUI.Tooltip(modes[0], "Scattered at random around the active vessel, keeping clear of it.");

        Transform nearby = Panel(content, () => Controller.situationMode == Controller.SituationMode.Nearby);
        Field(nearby, Controller.range);
        Toggle(nearby, "Random Rotation", Controller.randomRotation);

        Transform orbit = Panel(content, () => Controller.situationMode == Controller.SituationMode.Orbit);
        BodyPicker(orbit);
        Transform simple = Panel(orbit, () => !Controller.advancedOrbit);
        Field(simple, Controller.altitude);
        Field(simple, Controller.inclination);
        Transform advanced = Panel(orbit, () => Controller.advancedOrbit);
        foreach (ITextField field in new ITextField[] { Controller.sma, Controller.eccentricity, Controller.inclination, Controller.lan, Controller.argPe, Controller.meanAnomaly })
            Field(advanced, field);

        Transform match = Line(orbit, "Match Orbit Of");
        Enable(DebugUI.Button(match, "Vessel", () => Controller.SetOrbit(FlightGlobals.ActiveVessel.orbit), half), () => Controller.InFlight);
        Enable(DebugUI.Button(match, "Target", () => Controller.SetOrbit(FlightGlobals.fetch.VesselTarget.GetOrbit()), half), () => Controller.InFlight && FlightGlobals.fetch.VesselTarget?.GetOrbit() != null);
        Toggle(orbit, "Advanced", Controller.advancedOrbit, "Describe the orbit with all its elements.");
        Toggle(orbit, "Spread Evenly", Controller.spreadAlongOrbit, "Space the vessels out evenly around the orbit, like a constellation. Otherwise they fly in formation.", () => Controller.count.value > 1);
        Toggle(orbit, "Random Rotation", Controller.randomRotation);

        Transform landed = Panel(content, () => Controller.situationMode == Controller.SituationMode.Landed);
        BodyPicker(landed);
        SitePicker(landed);
        Field(landed, Controller.latitude);
        Field(landed, Controller.longitude);
        Field(landed, Controller.heading);
        Enable(DebugUI.Button(Line(landed), "Use Active Vessel", Controller.UseActiveVesselPosition, DebugUI.ControlWidth), () => Controller.InFlight);
        Toggle(landed, "Random Heading", Controller.randomRotation);
        DebugUI.Spacer(content, 10);

        // Crew.
        DebugUI.Heading(content, "Crew");
        Choice(content, "", Controller.crewMode, "None", "Pilot", "Command", "Fill All");
        Toggle(content, "Hire New Kerbals", Controller.onlyNewKerbals, "Always hire new kerbals, instead of using ones already at the space centre.", () => Controller.crewMode != CrewMode.None);
        DebugUI.Spacer(content, 10);

        // What happens, set apart as a list, and the buttons that make it happen.
        bool ready = false;
        List<string> summary = new List<string>();
        refresh.Add(() => summary = Summary(out ready));

        Transform box = DebugUI.Box(content);
        for (int i = 0; i < 6; i++)
        {
            int line = i;
            Transform row = Show(DebugUI.Row(box), () => line < summary.Count);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
            Text(DebugUI.Label(row, "", 14), () => line < summary.Count && summary[line].StartsWith(orange) ? orange + "•" : "•");
            Text(DebugUI.Paragraph(row), () => line < summary.Count ? summary[line] : "");
        }

        DebugUI.Spacer(content, 8);
        Transform buttons = DebugUI.Row(content);
        buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        Button spawn = Enable(DebugUI.Button(buttons, "Spawn", () => C.Spawn(), DebugUI.ControlWidth), () => ready && C.spawnRoutine == null);
        Text(spawn.GetComponentInChildren<TextMeshProUGUI>(), () => C.spawnRoutine == null ? "Spawn" : "Spawning…");
        Button place = Enable(DebugUI.Button(buttons, "Place…", () => C.Place(), DebugUI.ControlWidth), () => ready && C.PlaceKind() != PlacementTool.Kind.None);
        DebugUI.Tooltip(place, "Point at where the vessels go. Click to spawn, Q/E to turn, shift-click to keep going, right-click to stop.");
        DebugUI.Spacer(content);

        Text(DebugUI.Paragraph(content), () => C.statusIsError ? orange + C.status : C.status);
        Transform after = DebugUI.Row(content);
        Show(DebugUI.Button(after, "Switch To", () => FlightGlobals.SetActiveVessel(LastSpawned()), DebugUI.ControlWidth),
            () => HighLogic.LoadedSceneIsFlight && LastSpawned() != null && LastSpawned() != FlightGlobals.ActiveVessel);
        Button undo = Show(DebugUI.Button(after, "Undo", () => C.Undo(), DebugUI.ControlWidth), () => Removable() > 0);
        DebugUI.Tooltip(undo, "Remove the vessels you just spawned. Their crew go home.");
        Text(undo.GetComponentInChildren<TextMeshProUGUI>(), () => Removable() == 1 ? "Undo" : $"Undo ({Removable()})");
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

    // "< Kerbin >", as wide as a field.
    private void Stepper(Transform parent, string title, Func<string> current, Action<int> step)
    {
        Transform row = Line(parent, title);
        DebugUI.Button(row, "<", () => step(-1), 24);
        TextMeshProUGUI name = Text(DebugUI.Label(row, "", DebugUI.ControlWidth - 56), current);
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
        $"<b>{craft.DisplayName}</b>\n" + (craft.MissingParts.Count > 0
            ? $"{orange}{craft.MissingParts.Count} missing part{(craft.MissingParts.Count == 1 ? "" : "s")}: {string.Join(", ", craft.MissingParts.Take(3))}{(craft.MissingParts.Count > 3 ? "…" : "")}"
            : $"{grey}{(craft.elsewhere ? "From elsewhere" : craft.facility)} · {craft.PartCount} parts · {Ago(craft.modified)}");

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

    // What Spawn will do, then anything to look out for, a line each. Problems stop it altogether.
    private List<string> Summary(out bool ready)
    {
        ready = false;
        if (C == null)
            return new List<string>();

        VesselTemplate template = C.PreviewTemplate(out string error);
        if (template == null)
            return Warnings(new[] { error ?? "" });

        List<string> problems = new List<string>(), warnings = new List<string>();
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
            return Warnings(problems);

        int number = Controller.count;
        string what = number == 1 ? template.DisplayName : $"{number} × {template.DisplayName}";
        List<string> lines = new List<string> { $"{what} {Where(number, problems, warnings)}.", Crew(template, number) };
        ready = problems.Count == 0;

        lines.AddRange(Warnings(problems.Concat(warnings)));
        return lines;
    }

    private static List<string> Warnings(IEnumerable<string> warnings) =>
        warnings.Select(w => orange + w).ToList();

    private string Where(int number, List<string> problems, List<string> warnings)
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
                    warnings.Add("That's on top of another vessel.");

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

                if (orbit.PeR < body.Radius)
                    warnings.Add("The orbit goes through the surface.");
                else if (body.atmosphere && orbit.PeA < body.atmosphereDepth)
                    warnings.Add("The orbit dips into the atmosphere.");
                if (orbit.eccentricity < 1 && orbit.ApR > body.sphereOfInfluence)
                    warnings.Add($"The orbit leaves {bodyName}'s sphere of influence.");

                string inclined = Math.Abs(orbit.inclination) > 0.05 ? $", inclined {orbit.inclination:0.#}°" : "";
                if (orbit.eccentricity >= 1)
                    return $"escaping {bodyName}{inclined}";

                string shape = orbit.PeA < 0 ? $"an orbit of {bodyName}"
                    : orbit.eccentricity < 0.001 ? $"a {Distance(orbit.PeA)} orbit of {bodyName}"
                    : $"a {Distance(orbit.PeA)} by {Distance(orbit.ApA)} orbit of {bodyName}";
                string spread = number < 2 ? "in " : Controller.spreadAlongOrbit ? "spread evenly around " : "in formation in ";
                return spread + shape + inclined;
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

    // A line in the list: a craft, or the vessel to clone.
    private class Entry
    {
        public Craft craft;
        public string text;
        public Action pick;
    }

    private const float rowHeight = 42;
    private ScrollRect scroll;
    private readonly List<Entry> entries = new List<Entry>();
    private readonly List<Button> rows = new List<Button>();
    private int shownFrom = -1;

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
            scroll.verticalNormalizedPosition = 1;
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

        // Only the rows in view exist. They move and change as the list scrolls, so however many craft
        // there are, the list opens and filters without making hundreds of UI objects.
        scroll = DebugUI.ScrollView(page, 100);

        refresh.Add(() =>
        {
            if (!picker.gameObject.activeSelf)
                return;

            string key = $"{search.text}|{filter.Value}|{Controller.CloneSource()?.id}|{CraftList.All.Count}";
            if (key != listed)
            {
                listed = key;
                Refilter();
            }

            ShowRows();
        });
    }

    private void Refilter()
    {
        entries.Clear();
        shownFrom = -1;

        // A path, pasted or typed.
        Craft pasted = CraftList.FromText(search.text);
        if (pasted != null)
            entries.Add(new Entry { craft = pasted, text = Describe(pasted) + $"\n{grey}{pasted.path}", pick = () => Controller.Select(pasted) });
        else
        {
            Vessel original = Controller.CloneSource();
            if (original != null && !original.isEVA && search.text == "")
                entries.Add(new Entry { text = $"<b>{original.GetDisplayName()}</b>\n{grey}Copy the {(HighLogic.LoadedSceneIsFlight ? "active" : "selected")} vessel", pick = () => Controller.source.Value = Controller.Source.Clone });

            string[] words = search.text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (Craft craft in CraftList.All)
                if ((filter == Filter.All || craft.facility == filter.Value.ToString()) && words.All(w => craft.DisplayName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    entries.Add(new Entry { craft = craft, pick = () => Controller.Select(craft) });
        }

        scroll.content.sizeDelta = new Vector2(0, entries.Count * rowHeight);
    }

    private void ShowRows()
    {
        int visible = Mathf.CeilToInt(scroll.viewport.rect.height / rowHeight) + 1;
        while (rows.Count < visible)
        {
            rows.Add(NewRow(rows.Count));
            shownFrom = -1;
        }

        int first = Mathf.Max(0, (int)(scroll.content.anchoredPosition.y / rowHeight));
        if (first == shownFrom)
            return;

        shownFrom = first;
        for (int i = 0; i < rows.Count; i++)
        {
            int index = first + i;
            rows[i].gameObject.SetActive(index < entries.Count);
            if (index < entries.Count)
                Fill(rows[i], entries[index], index);
        }
    }

    private Button NewRow(int slot)
    {
        Button row = DebugUI.Button(scroll.content, "", () =>
        {
            Entry entry = entries[shownFrom + slot];
            if (entry.craft?.MissingParts.Count > 0)
                return;

            entry.pick();
            ShowPicker(false);
        });
        RectTransform rect = (RectTransform)row.transform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 1);
        rect.sizeDelta = new Vector2(0, rowHeight - 2);

        // The row's own colour says what it holds, and is set with the rest of it when it's filled.
        // The button only tints that for the mouse, which doesn't change as rows are reused while scrolling.
        ColorBlock colors = row.colors;
        colors.normalColor = colors.selectedColor = colors.disabledColor = Color.white;
        colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f);
        colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
        colors.colorMultiplier = 1;
        row.colors = colors;

        TextMeshProUGUI label = row.GetComponentInChildren<TextMeshProUGUI>();
        label.color = Color.white;
        label.richText = true;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.margin = new Vector4(44, 0, 4, 0);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        RectTransform image = Thumbnail(row.transform, 36).rectTransform;
        image.anchorMin = image.anchorMax = image.pivot = new Vector2(0, 0.5f);
        image.anchoredPosition = new Vector2(2, 0);
        image.sizeDelta = new Vector2(36, 36);

        DebugUI.Tooltip(row, "");
        return row;
    }

    private void Fill(Button row, Entry entry, int index)
    {
        ((RectTransform)row.transform).anchoredPosition = new Vector2(0, -index * rowHeight);
        row.GetComponentInChildren<TextMeshProUGUI>().text = entry.text ?? Describe(entry.craft);

        // Thumbnails load as their rows come into view.
        RawImage image = row.GetComponentInChildren<RawImage>();
        image.texture = entry.craft?.Thumbnail;
        image.enabled = image.texture != null;

        bool missing = entry.craft != null && entry.craft.MissingParts.Count > 0;
        row.image.color = missing ? new Color(0.24f, 0.24f, 0.24f) : new Color(0.3f, 0.3f, 0.3f);
        KSP.UI.TooltipTypes.TooltipController_Text tooltip = row.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>();
        tooltip.enabled = missing;
        tooltip.textString = missing ? "Missing parts:\n" + string.Join("\n", entry.craft.MissingParts) : "";
    }

    #endregion
}
