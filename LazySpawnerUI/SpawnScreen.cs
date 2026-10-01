using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static LazySpawner.Localisation;

namespace LazySpawner;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
internal class SpawnScreenRegistration : MonoBehaviour
{
    protected void Start() => DebugUI.AddScreen<SpawnScreen>("Cheats", Controller.ScreenName, Loc("ScreenName"));
}

// The Spawn Vessels screen, under Cheats in the debug console (Alt+F12). It's built from the stock
// screens' own widgets, and kept in step with the controller's settings every frame.
public class SpawnScreen : MonoBehaviour
{
    private static SpawnScreen instance;
    public static bool Visible => instance != null && instance.isActiveAndEnabled && DebugUI.Shown;

    private readonly List<Action> refresh = new List<Action>();
    private RectTransform main, picker;
    private GameObject notHere, content;
    private TMP_InputField search;
    private string listed;
    private float nextClipboardCheck;


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
        // Otherwise typing "1" fires action group 1, and a space stages.
        GameObject selected = EventSystem.current?.currentSelectedGameObject;
        if (selected != null && selected.transform.IsChildOf(transform) && selected.GetComponent<TMP_InputField>()?.isFocused == true)
            InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, typingLock);
        else
            InputLockManager.RemoveControlLock(typingLock);

        // Where it doesn't work, like the space centre, there's only a note to say so.
        notHere.SetActive(C == null);
        content.SetActive(C != null);
        if (C == null)
        {
            ShowPicker(false);
            return;
        }

        // Copy a craft's path in another program, come back, and it's picked.
        if (Time.unscaledTime > nextClipboardCheck)
        {
            nextClipboardCheck = Time.unscaledTime + 0.5f;
            if (Controller.CheckClipboard() && picker.gameObject.activeSelf)
                ShowPicker(false);
        }

        foreach (Action action in refresh)
            action();
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

    private Button[] Tabs<T>(Transform parent, Setting<T> setting, params string[] names) where T : Enum
    {
        Button[] tabs = DebugUI.Tabs(parent, names, i => setting.Value = (T)Enum.ToObject(typeof(T), i));
        refresh.Add(() =>
        {
            for (int i = 0; i < tabs.Length; i++)
                DebugUI.Select(tabs[i], Convert.ToInt32(setting.Value) == i);
        });
        return tabs;
    }

    // A panel shown only when it applies.
    private RectTransform Panel(Transform parent, Func<bool> when) =>
        Show(DebugUI.Column(parent), when);

    #endregion

    #region Main

    private void BuildMain(Transform page)
    {
        notHere = DebugUI.Paragraph(page, Loc("NotHere")).gameObject;
        Transform content = DebugUI.Column(page);
        this.content = content.gameObject;
        const float half = (DebugUI.ControlWidth - 4) / 2;

        // Craft: its picture where the labels go, and what it is where the controls go.
        DebugUI.Heading(content, Loc("Heading_Craft"));
        Transform card = DebugUI.Row(content);
        card.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
        RawImage thumbnail = Thumbnail(card, 64);
        refresh.Add(() =>
        {
            thumbnail.texture = Controller.source == Controller.Source.Craft && !Controller.InEditor ? Controller.SelectedCraft?.Thumbnail : null;
            thumbnail.enabled = thumbnail.texture != null;
        });
        DebugUI.Label(card, "", DebugUI.LabelWidth - 68);
        Transform about = DebugUI.Column(card);
        LayoutElement aboutLayout = about.gameObject.AddComponent<LayoutElement>();
        aboutLayout.flexibleWidth = 1;
        aboutLayout.minWidth = DebugUI.ControlWidth;
        Text(DebugUI.Paragraph(about), CraftDescription);
        Transform craftButtons = Show(DebugUI.Row(about), () => !Controller.InEditor);
        DebugUI.Button(craftButtons, Loc("Craft_Change"), () => ShowPicker(true), half);
        DebugUI.Tooltip(Show(DebugUI.Button(craftButtons, Loc("Craft_Edit"), () => Controller.OpenInEditor(Controller.SelectedCraft), half),
            () => Controller.source == Controller.Source.Craft && Controller.SelectedCraft != null), Loc("Craft_Edit_Tooltip"));
        Field(content, Controller.count);
        DebugUI.Spacer(content, 10);

        // Where.
        DebugUI.Heading(content, Loc("Heading_Where"));
        Button[] modes = Tabs(content, Controller.situationMode, Loc("Mode_Place"), Loc("Mode_Nearby"), Loc("Mode_Orbit"), Loc("Mode_LaunchSite"));
        Enable(modes[0], () => Controller.CanPlace);
        DebugUI.Tooltip(modes[0], Loc("Mode_Place_Tooltip"));
        Enable(modes[1], () => Controller.InFlight);
        DebugUI.Tooltip(modes[1], Loc("Mode_Nearby_Tooltip"));
        Show(modes[0], () => !Controller.InEditor);
        Show(modes[1], () => !Controller.InEditor);
        DebugUI.Spacer(content, 2);

        Transform place = Panel(content, () => Controller.situationMode == Controller.SituationMode.Place);
        Toggle(place, Loc("RandomRotation"), Controller.randomRotation, Loc("RandomRotation_Place_Tooltip"));

        Transform nearby = Panel(content, () => Controller.situationMode == Controller.SituationMode.Nearby);
        Field(nearby, Controller.range);
        Toggle(nearby, Loc("RandomRotation"), Controller.randomRotation);

        Transform orbit = Panel(content, () => Controller.situationMode == Controller.SituationMode.Orbit);
        BodyPicker(orbit);
        Transform simple = Panel(orbit, () => !Controller.advancedOrbit);
        Field(simple, Controller.altitude);
        Field(simple, Controller.inclination);
        Transform advanced = Panel(orbit, () => Controller.advancedOrbit);
        foreach (ITextField field in new ITextField[] { Controller.sma, Controller.eccentricity, Controller.inclination, Controller.lan, Controller.argPe, Controller.meanAnomaly })
            Field(advanced, field);

        Transform match = Line(orbit, Loc("Orbit_Match"));
        Enable(DebugUI.Button(match, Loc("Orbit_MatchVessel"), () => Controller.SetOrbit(FlightGlobals.ActiveVessel.orbit), half), () => Controller.InFlight);
        Enable(DebugUI.Button(match, Loc("Orbit_MatchTarget"), () => Controller.SetOrbit(FlightGlobals.fetch.VesselTarget.GetOrbit()), half), () => Controller.InFlight && FlightGlobals.fetch.VesselTarget?.GetOrbit() != null);
        Toggle(orbit, Loc("Orbit_Advanced"), Controller.advancedOrbit, Loc("Orbit_Advanced_Tooltip"));
        Toggle(orbit, Loc("Orbit_Spread"), Controller.spreadAlongOrbit, Loc("Orbit_Spread_Tooltip"), () => Controller.count.value > 1);
        Toggle(orbit, Loc("RandomRotation"), Controller.randomRotation);

        SitePicker(Panel(content, () => Controller.situationMode == Controller.SituationMode.LaunchSite));
        DebugUI.Spacer(content, 10);

        // Crew.
        DebugUI.Heading(content, Loc("Heading_Crew"));
        Tabs(content, Controller.crewMode, Loc("Crew_None"), Loc("Crew_Pilot"), Loc("Crew_Command"), Loc("Crew_FillAll"));
        Toggle(content, Loc("Crew_AlwaysHire"), Controller.onlyNewKerbals, Loc("Crew_AlwaysHire_Tooltip"), () => Controller.crewMode != CrewMode.None);
        DebugUI.Spacer(content, 10);

        // What happens, set apart as a list, and the buttons that make it happen. Warnings have orange bullets,
        // and the closing tags keep their colour from running on.
        bool ready = false;
        List<string> summary = new List<string>();
        refresh.Add(() => summary = Summary(out ready));
        Text(DebugUI.Paragraph(Show(DebugUI.Box(content), () => summary.Count > 0)),
            () => string.Join("\n", summary.Select(line => (line.StartsWith(orange) ? orange : "") + "•<indent=14>" + line + "</color></color></indent>"))).paragraphSpacing = 12;

        DebugUI.Spacer(content, 8);
        Transform buttons = DebugUI.Row(content);
        buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        bool placing() => Controller.situationMode == Controller.SituationMode.Place;
        Button spawn = Enable(DebugUI.Button(buttons, "", () => { if (placing()) C.Place(); else C.Spawn(); }, DebugUI.ControlWidth), () => ready && C.spawnRoutine == null);
        Text(spawn.GetComponentInChildren<TextMeshProUGUI>(), () => Loc(C.spawnRoutine != null ? "Button_Spawning" : placing() ? "Button_Place" : "Button_Spawn"));
        // Where to click, or that the editor stays open.
        KSP.UI.TooltipTypes.TooltipController_Text spawnTooltip = DebugUI.Tooltip(spawn, "").GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>();
        refresh.Add(() =>
        {
            spawnTooltip.enabled = placing() || Controller.InEditor;
            spawnTooltip.textString = Loc(placing() ? "Button_Place_Tooltip" : "Button_Spawn_Editor_Tooltip");
        });
        DebugUI.Spacer(content);

        // What happened, with what can be done about it alongside.
        Transform result = DebugUI.Row(Show(DebugUI.Box(content), () => C.status != ""), 6);
        Text(DebugUI.Paragraph(result), () => C.statusIsError ? orange + C.status : C.status);
        Show(DebugUI.Button(result, Loc("Button_SwitchTo"), () => { if (Controller.InEditor) Controller.FlyFromEditor(LastSpawned()); else FlightGlobals.ForceSetActiveVessel(LastSpawned().vesselRef); }, 80),
            () => LastSpawned() != null && (Controller.InEditor || HighLogic.LoadedSceneIsFlight && LastSpawned().vesselRef != FlightGlobals.ActiveVessel));
        Button undo = Show(DebugUI.Button(result, Loc("Button_Undo"), () => C.Undo(), 80), () => Removable() > 0);
        DebugUI.Tooltip(undo, Loc("Button_Undo_Tooltip"));
        Text(undo.GetComponentInChildren<TextMeshProUGUI>(), () => Removable() == 1 ? Loc("Button_Undo") : Loc("Button_UndoCount", Removable()));
    }

    // Vessels from the editor stay in the save until the game leaves it. The rest can be destroyed meanwhile.
    private ProtoVessel LastSpawned()
    {
        C.lastSpawned.RemoveAll(p => !Controller.InEditor && (p.vesselRef == null || p.vesselRef.state == Vessel.State.DEAD));
        return C.lastSpawned.FirstOrDefault();
    }

    private int Removable() =>
        LastSpawned() == null ? 0 : C.lastSpawned.Count(p => p.vesselRef == null || p.vesselRef != FlightGlobals.ActiveVessel);

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
        Stepper(parent, Controller.body.Title, () => Controller.body.Valid ? Controller.body.value.displayName.LocalizeRemoveGender() : Controller.body.Text, step =>
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            int index = Controller.body.Valid ? bodies.IndexOf(Controller.body.value) : 0;
            Controller.body.Text = bodies[(index + step + bodies.Count) % bodies.Count].bodyName;
        });
    }

    // Site names are longer than body names.
    private void SitePicker(Transform parent)
    {
        Stepper(parent, Loc("Site"), () => LaunchSites.Named(Controller.launchSite)?.name ?? Loc("Site_None"), step =>
        {
            List<LaunchSites.Site> sites = LaunchSites.All;
            if (sites.Count > 0)
                Controller.launchSite.Value = sites[(sites.IndexOf(LaunchSites.Named(Controller.launchSite)) + step + sites.Count) % sites.Count].name;
        }, DebugUI.ControlWidth * 1.5f);
    }

    // "< Kerbin >", as wide as a field unless it needs more.
    private void Stepper(Transform parent, string title, Func<string> current, Action<int> step, float width = DebugUI.ControlWidth)
    {
        Transform row = Line(parent, title);
        DebugUI.Button(row, "<", () => step(-1), 24);
        TextMeshProUGUI name = Text(DebugUI.Label(row, "", width - 56), current);
        name.alignment = TextAlignmentOptions.Center;
        DebugUI.Button(row, ">", () => step(1), 24);
    }

    private string CraftDescription()
    {
        if (Controller.InEditor)
        {
            ShipConstruct ship = EditorLogic.fetch.ship;
            return $"<b>{KSP.Localization.Localizer.Format(ship.shipName)}</b>\n{grey}" + Loc("Craft_Editing", ship.parts.Count);
        }

        if (Controller.source == Controller.Source.Clone)
        {
            Vessel original = Controller.CloneSource();
            return original == null
                ? orange + Loc(HighLogic.LoadedSceneIsFlight ? "Craft_NoActiveVessel" : "Craft_SelectVessel")
                : $"<b>{original.GetDisplayName()}</b>\n{grey}" + Loc(HighLogic.LoadedSceneIsFlight ? "Craft_CloneActive" : "Craft_CloneSelected", original.protoVessel?.protoPartSnapshots.Count ?? original.parts.Count);
        }

        Craft craft = Controller.SelectedCraft;
        return craft == null ? orange + Loc("Craft_Choose") : Describe(craft);
    }

    private static string Describe(Craft craft) =>
        $"<b>{craft.DisplayName}</b>\n" + (craft.MissingParts.Count > 0
            ? orange + MissingPartsException.Short(craft.MissingParts)
            : grey + Loc("Craft_Details", Location(craft), craft.PartCount, Ago(craft.modified)));

    // VAB\Drones, and whose it is when it's not this save's.
    private static string Location(Craft craft) =>
        craft.Elsewhere ? Loc("Craft_Elsewhere")
        : craft.save == null ? $"{craft.folder} · {Loc("Craft_Stock")}"
        : craft.save != HighLogic.SaveFolder ? $"{craft.folder} · {craft.save}"
        : craft.folder;

    private static string Ago(DateTime time)
    {
        TimeSpan age = DateTime.Now - time;
        return age.TotalMinutes < 1 ? Loc("Ago_JustNow")
            : age.TotalHours < 1 ? Loc("Ago_Minutes", (int)age.TotalMinutes)
            : age.TotalDays < 1 ? Loc("Ago_Hours", (int)age.TotalHours)
            : age.TotalDays < 60 ? Loc("Ago_Days", (int)age.TotalDays)
            : time.ToString("d MMM yyyy");
    }

    #endregion

    #region Summary

    // What will happen that the settings don't already say, then anything to look out for, a line each.
    // Problems stop it altogether.
    private List<string> Summary(out bool ready)
    {
        ready = false;
        VesselTemplate template = C.Template(out Exception error);
        if (template == null)
            return Warnings(new[] { Controller.ShortMessage(error) });

        List<string> problems = new List<string>(), warnings = new List<string>();
        ITextField[] fields = Controller.situationMode.Value switch
        {
            Controller.SituationMode.Place => new ITextField[] { Controller.count },
            Controller.SituationMode.Nearby => new ITextField[] { Controller.count, Controller.range },
            Controller.SituationMode.LaunchSite => new ITextField[] { Controller.count },
            _ => Controller.advancedOrbit
                ? new ITextField[] { Controller.count, Controller.body, Controller.sma, Controller.eccentricity, Controller.inclination, Controller.lan, Controller.argPe, Controller.meanAnomaly }
                : new ITextField[] { Controller.count, Controller.body, Controller.altitude, Controller.inclination },
        };

        foreach (ITextField field in fields.Where(f => !f.Valid))
            problems.Add(Loc("Summary_FieldWrong", field.Title));

        if (problems.Count > 0)
            return Warnings(problems);

        int number = Controller.count;
        List<string> lines = new List<string> { Where(template, number, problems, warnings), Crew(template, number) };
        ready = problems.Count == 0;

        lines.AddRange(Warnings(problems.Concat(warnings)));
        lines.RemoveAll(line => line == null);
        return lines;
    }

    private static List<string> Warnings(IEnumerable<string> warnings) =>
        warnings.Select(w => orange + w).ToList();

    // Only what the settings don't already say: what happens next, and what follows from them.
    private string Where(VesselTemplate template, int number, List<string> problems, List<string> warnings)
    {
        CelestialBody body = Controller.body.value;
        string bodyName = body?.displayName.LocalizeRemoveGender();

        switch (Controller.situationMode.Value)
        {
            case Controller.SituationMode.Place:
                if (!Controller.CanPlace)
                    problems.Add(Loc("Summary_NothingToPlaceAround"));
                return null;

            case Controller.SituationMode.Nearby:
                Vessel active = FlightGlobals.ActiveVessel;
                bool ground = active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH;
                return Loc(ground ? "Summary_NearbyGround" : "Summary_NearbyOrbit", active.GetDisplayName(), number);

            case Controller.SituationMode.LaunchSite:
                if (LaunchSites.Named(Controller.launchSite) == null)
                {
                    problems.Add(Loc("Summary_NoLaunchSites"));
                    return null;
                }

                List<SpawnSituation> situations = C.PreviewSituations(template);
                if (situations != null && PlacementTool.Clearance(template, situations, out Vessel there) < 0.5f)
                    warnings.Add(Loc("Summary_InTheWay", there.GetDisplayName()));

                return null;

            default:
                Orbit orbit;
                try
                {
                    orbit = Controller.CreateOrbit(0);
                }
                catch
                {
                    problems.Add(Loc("Summary_NotAnOrbit"));
                    return null;
                }

                if (orbit.eccentricity < 1 && orbit.semiMajorAxis < 0)
                    problems.Add(Loc("Summary_EllipticalAxis"));
                else if (orbit.eccentricity >= 1 && orbit.semiMajorAxis > 0)
                    problems.Add(Loc("Summary_HyperbolicAxis"));

                if (orbit.PeR < body.Radius)
                    warnings.Add(Loc("Summary_ThroughSurface"));
                else if (body.atmosphere && orbit.PeA < body.atmosphereDepth)
                    warnings.Add(Loc("Summary_InAtmosphere"));
                if (orbit.eccentricity < 1 && orbit.ApR > body.sphereOfInfluence)
                    warnings.Add(Loc("Summary_LeavesSOI", bodyName));

                // The simple fields say it all. The elements don't make the shape obvious.
                if (!Controller.advancedOrbit)
                    return null;

                return orbit.eccentricity >= 1 ? Loc("Summary_Escaping", bodyName, PlacementTool.Distance(orbit.PeA))
                    : orbit.eccentricity < 0.001 ? Loc("Summary_Circular", PlacementTool.Distance(orbit.PeA))
                    : Loc("Summary_Elliptical", PlacementTool.Distance(orbit.PeA), PlacementTool.Distance(orbit.ApA));
        }
    }

    private static string Crew(VesselTemplate template, int number)
    {
        CrewMode mode = Controller.crewMode;
        if (mode == CrewMode.None)
            return null;

        // The same seats the spawner fills: no external seats, command parts only unless filling all.
        List<Part> seats = template.Parts.Select(p => p.info?.partPrefab).Where(p => p != null && p.CrewCapacity > 0 && !p.HasModuleImplementing<KerbalSeat>()).ToList();
        int each = mode == CrewMode.Pilot ? Math.Min(1, seats.Count)
            : seats.Where(p => mode == CrewMode.FillAll || p.HasModuleImplementing<ModuleCommand>()).Sum(p => p.CrewCapacity);

        if (each == 0)
            return Loc("Crew_NoSeats");

        // Whether kerbals leave the space centre, and how many join.
        int needed = each * number;
        int available = Controller.onlyNewKerbals ? 0 : HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available).Count();
        int hired = Math.Max(0, needed - available);

        return hired == 0 ? Loc("Crew_Taken", needed)
            : hired == needed ? Loc("Crew_Hired", needed)
            : Loc("Crew_TakenAndHired", needed - hired, hired);
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
        search.placeholder.GetComponent<TextMeshProUGUI>().text = Loc("Picker_Search");
        DebugUI.Tooltip(search, Loc("Picker_Search_Tooltip"));

        // Says the order it's in, and switches to the other. Recent craft are always in the order they were used.
        Button sort = Show(DebugUI.Button(top, "", () => CraftList.order.Value = CraftList.order == CraftList.Order.Newest ? CraftList.Order.Name : CraftList.Order.Newest, 60),
            () => CraftList.source != CraftList.Source.Recent);
        Text(sort.GetComponentInChildren<TextMeshProUGUI>(), () => Loc(CraftList.order == CraftList.Order.Newest ? "Picker_Newest" : "Picker_Name"));

        DebugUI.Tooltip(DebugUI.Button(top, Loc("Picker_Browse"), () => { ShowPicker(false); C.StartCoroutine(C.StockCraftBrowser()); }, 70), Loc("Picker_Browse_Tooltip"));
        DebugUI.Button(top, Loc("Picker_Back"), () => ShowPicker(false), 50);

        Tabs(page, CraftList.source, Loc("Picker_Recent"), Loc("Picker_ThisSave"), Loc("Picker_AllSaves"), Loc("Picker_Stock"));

        // Only the rows in view exist. They move and change as the list scrolls, so however many craft
        // there are, the list opens and filters without making hundreds of UI objects.
        scroll = DebugUI.ScrollView(page, 100);

        refresh.Add(() =>
        {
            if (!picker.gameObject.activeSelf)
                return;

            string key = $"{search.text}|{CraftList.source.Value}|{CraftList.order.Value}|{Controller.CloneSource()?.id}|{CraftList.List.Count}";
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
                entries.Add(new Entry { text = $"<b>{original.GetDisplayName()}</b>\n{grey}" + Loc(HighLogic.LoadedSceneIsFlight ? "Picker_CopyActive" : "Picker_CopySelected"), pick = () => Controller.source.Value = Controller.Source.Clone });

            // Every word, in the name or where it is, so "sph" finds the SPH's craft and a save's name finds that save's.
            string[] words = search.text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (Craft craft in CraftList.List)
                if (words.All(w => craft.SearchText.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    entries.Add(new Entry { craft = craft, pick = () => Controller.Select(craft) });
        }

        scroll.content.sizeDelta = new Vector2(0, entries.Count * rowHeight);
        scroll.verticalNormalizedPosition = 1;
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
        tooltip.textString = missing ? Loc("Picker_MissingParts") + "\n" + string.Join("\n", entry.craft.MissingParts) : "";
    }

    #endregion
}
