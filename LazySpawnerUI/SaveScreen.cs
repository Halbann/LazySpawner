using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static LazySpawner.Localisation;

namespace LazySpawner;

// The Save Vessel screen, under Cheats in the debug console: the active vessel, saved as a craft. It's only
// brought up to date when something changes: it's shown, the vessel or scene changes, or a choice on it does.
internal class SaveScreen : ConsoleScreen
{
    public const string ScreenName = "Save Vessel";

    private GameObject content, resultBox, edit;
    private string savedPath;
    private TextMeshProUGUI notHere, about, summary, result;
    private TMP_InputField nameField;
    private Button[] buildings;

    // The vessel the building was picked for. Another vessel starts afresh.
    private Vessel vessel;
    private EditorFacility facility;
    private string freeName;

    // The name typed, or else the vessel's.
    private string CraftName => nameField.text.Trim() != "" ? nameField.text.Trim() : freeName;

    #region Lifecycle

    protected void Start()
    {
        Clear();
        Build();
        Refresh();
        Fit();
        GameEvents.onVesselChange.Add(OnVesselChange);
        GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
    }

    protected void OnDestroy()
    {
        GameEvents.onVesselChange.Remove(OnVesselChange);
        GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
    }

    protected void OnEnable()
    {
        if (content != null)
            Refresh();
    }

    private void OnVesselChange(Vessel _) => Refresh();
    private void OnLevelWasLoaded(GameScenes _) => Refresh();

    #endregion

    private void Build()
    {
        RectTransform page = DebugUI.ScrollList(Page(), 100);
        FitWindow(page);
        notHere = DebugUI.Paragraph(page);
        content = DebugUI.Column(page).gameObject;
        Transform column = content.transform;

        about = DebugUI.Paragraph(column);
        DebugUI.Spacer(column, 6);

        // Empty, it's named as the hint says.
        nameField = DebugUI.Field(Line(column, Loc("Save_Name")), "", _ => Refresh());
        buildings = DebugUI.Tabs(Line(column, Loc("Save_Building")), new[] { Loc("Building_VAB"), Loc("Building_SPH") }, i =>
        {
            facility = i == 0 ? EditorFacility.VAB : EditorFacility.SPH;
            Refresh();
        });
        DebugUI.Spacer(column, 10);

        // What will happen, as on the spawn screen, then the button, then what happened.
        summary = DebugUI.Paragraph(DebugUI.Box(column));
        summary.paragraphSpacing = 12;
        DebugUI.Spacer(column, 8);
        DebugUI.Button(DebugUI.Row(column, alignment: TextAnchor.MiddleCenter), Loc("Button_Save"), Save, DebugUI.ControlWidth);
        DebugUI.Spacer(column);
        resultBox = DebugUI.Box(column).gameObject;
        Transform resultRow = DebugUI.Row(resultBox.transform, 6);
        result = DebugUI.Paragraph(resultRow);
        edit = DebugUI.Tooltip(DebugUI.Button(resultRow, Loc("Craft_Edit"), () => Controller.OpenInEditor(CraftList.Get(savedPath)), 80), Loc("Save_Edit_Tooltip")).gameObject;
    }

    private void Refresh()
    {
        Vessel active = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
        bool here = active != null && !active.isEVA;
        notHere.text = active != null && active.isEVA ? Loc("Error_SaveEVA") : Loc("Save_NotHere");
        notHere.gameObject.SetActive(!here);
        content.SetActive(here);
        if (!here)
            return;

        if (active != vessel)
        {
            vessel = active;
            facility = CraftSaver.Facility(active);
            nameField.text = "";
            resultBox.SetActive(false);
        }

        for (int i = 0; i < buildings.Length; i++)
            DebugUI.Select(buildings[i], i == (facility == EditorFacility.SPH ? 1 : 0));

        about.text = $"<b>{vessel.GetDisplayName()}</b>\n{grey}" + Loc("Save_Vessel", vessel.parts.Count);
        freeName = CraftSaver.FreeName(vessel, facility);
        ((TextMeshProUGUI)nameField.placeholder).text = freeName;

        List<string> lines = new List<string> { Loc("Save_Summary_Where", Loc(facility == EditorFacility.SPH ? "Building_SPH" : "Building_VAB")) };
        if (vessel.FindPartModulesImplementing<ModuleDockingNode>().Any(d => d.fsm?.currentStateName.StartsWith("Docked") == true))
            lines.Add(Loc("Save_Summary_Docked"));
        if (vessel.GetCrewCount() > 0)
            lines.Add(Loc("Save_Summary_Crew"));
        lines.Add(Loc("Save_Summary_Rotated"));
        if (CraftName != freeName && File.Exists(CraftSaver.PathFor(CraftName, facility)))
            lines.Add(orange + Loc("Save_Summary_Replace", CraftName));
        summary.text = Bullets(lines);
    }

    private void Save()
    {
        try
        {
            savedPath = CraftSaver.Save(vessel, facility, CraftName);
            result.text = Loc("Status_SavedCraft", $"{facility}\\{Path.GetFileName(savedPath)}");
            ScreenMessages.PostScreenMessage(result.text, 3f, ScreenMessageStyle.UPPER_CENTER);
            nameField.text = "";
        }
        catch (Exception e)
        {
            savedPath = null;
            result.text = orange + Controller.ShortMessage(e);
            Controller.Popup(e, Loc("Error_SaveCraft_Title"));
        }

        resultBox.SetActive(true);
        edit.SetActive(savedPath != null);
        Refresh();
    }
}
