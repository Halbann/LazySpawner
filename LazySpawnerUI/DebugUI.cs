// Widgets for a screen in the stock debug console (Alt+F12), cloned from the stock screens so they look
// exactly the same. After Rescored's and HotReloadKSP's debug screens, which were after Phantomical's
// BackgroundResourceProcessing. KSP 1.12 is the last version, so the paths into its prefabs won't change.

using KSP.UI.Screens.DebugToolbar;
using KSP.UI.TooltipTypes;
using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LazySpawner;

internal static class DebugUI
{
    private static DebugScreenSpawner Spawner => DebugScreenSpawner.Instance;

    // Labels in front of fields and choices, so they all line up.
    public const float LabelWidth = 140;

    // The stock widgets everything is cloned from, found the first time they're needed.
    private class Prefabs
    {
        public readonly GameObject label, heading, field, button, toggle, scrollView;
        public readonly Tooltip_Text tooltip;

        public Prefabs()
        {
            Transform Screen(string name) => Spawner.debugScreens.screens.First(s => s.name == name).screen;
            Transform position = Screen("Set Position");

            label = position.Find("Latitude/Text").gameObject;
            field = position.Find("Latitude/InputField").gameObject;
            toggle = position.Find("EaseToGround/Toggle").gameObject;
            button = position.Find("SetPositionButton/SetSurfaceButton").gameObject;
            heading = Screen("Create").Find("NameLabel/Text").gameObject;
            scrollView = Spawner.screenPrefab.transform.Find("VerticalLayout/HorizontalLayout/Contents/Contents Scroll View").gameObject;
            tooltip = UISkinManager.GetPrefab("UISliderPrefab").GetComponent<TooltipController_Text>().prefab;
        }
    }

    private static Prefabs prefabs;
    private static Prefabs P => prefabs ??= new Prefabs();

    #region Screens

    // Add a screen to the console's list. Its prefab needs a component that builds the screen in Awake.
    public static void AddScreen<T>(string parent, string name) where T : MonoBehaviour
    {
        // Already there from before a hot reload, which brings its components up to date.
        if (Spawner.debugScreens.screens.Any(s => s.name == name))
            return;

        GameObject prefab = new GameObject(name, typeof(RectTransform));
        prefab.SetActive(false);
        Object.DontDestroyOnLoad(prefab);
        prefab.AddComponent<T>();

        Spawner.debugScreens.screens.Add(new AddDebugScreens.ScreenWrapper { parentName = parent, name = name, text = name, screen = (RectTransform)prefab.transform });
    }

    public static bool Shown => Spawner != null && Spawner.screen.isShown;

    public static void Show(string name) => DebugScreenSpawner.ShowTab(name);

    public static void Hide() => Spawner?.screen.Hide();

    #endregion

    #region Widgets

    private static GameObject Clone(GameObject prefab, Transform parent, float width = -1)
    {
        GameObject clone = Object.Instantiate(prefab, parent, false);
        clone.SetActive(true);

        LayoutElement layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = 20;
        layout.preferredWidth = width;
        layout.flexibleWidth = width < 0 ? 1 : 0;
        return clone;
    }

    // A vertical stack, for a screen or a section of one.
    public static RectTransform Column(Transform parent, float spacing = 3, RectOffset padding = null)
    {
        RectTransform column = new GameObject("Column", typeof(RectTransform)).GetComponent<RectTransform>();
        column.SetParent(parent, false);

        VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.padding = padding ?? new RectOffset();
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return column;
    }

    // Widgets side by side.
    public static RectTransform Row(Transform parent, float spacing = 4)
    {
        RectTransform row = new GameObject("Row", typeof(RectTransform)).GetComponent<RectTransform>();
        row.SetParent(parent, false);

        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        return row;
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float width = -1, bool bold = false)
    {
        TextMeshProUGUI tmp = Clone(bold ? P.heading : P.label, parent, width).GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.enableWordWrapping = width < 0;
        return tmp;
    }

    // Text that grows to fit.
    public static TextMeshProUGUI Paragraph(Transform parent, string text = "")
    {
        TextMeshProUGUI tmp = Label(parent, text);
        tmp.GetComponent<LayoutElement>().preferredHeight = -1;
        tmp.richText = true;
        return tmp;
    }

    public static TMP_InputField Field(Transform parent, string text, Action<string> changed, float width = -1)
    {
        TMP_InputField input = Clone(P.field, parent, width).GetComponent<TMP_InputField>();
        input.text = text;
        input.onValueChanged.AddListener(s => changed(s));
        return input;
    }

    public static Button Button(Transform parent, string text, Action clicked, float width = -1)
    {
        GameObject clone = Clone(P.button, parent, width);
        clone.GetComponentInChildren<TextMeshProUGUI>().text = text;
        Button b = clone.GetComponent<Button>();
        b.onClick.AddListener(() => clicked());
        return b;
    }

    public static Toggle Toggle(Transform parent, string text, bool on, Action<bool> changed, ToggleGroup group = null)
    {
        GameObject clone = Clone(P.toggle, parent);
        TextMeshProUGUI tmp = clone.GetComponentInChildren<TextMeshProUGUI>();
        tmp.text = text;

        LayoutElement layout = clone.GetComponent<LayoutElement>();
        layout.flexibleWidth = 0;
        layout.preferredWidth = tmp.GetPreferredValues(text).x + 28;

        Toggle t = clone.GetComponent<Toggle>();
        t.group = group;
        t.isOn = on;
        t.onValueChanged.AddListener(v => changed(v));
        return t;
    }

    // Radio buttons in a row, one per name, for choosing an enum value.
    public static Toggle[] Choice<T>(Transform parent, string title, Setting<T> setting, string[] names, Action changed = null) where T : Enum
    {
        Transform row = Row(parent, 10);
        Label(row, title, LabelWidth);
        ToggleGroup group = row.gameObject.AddComponent<ToggleGroup>();

        return names.Select((name, i) => Toggle(row, name, Convert.ToInt32(setting.Value) == i, on =>
        {
            if (!on)
                return;

            setting.Value = (T)Enum.ToObject(typeof(T), i);
            changed?.Invoke();
        }, group)).ToArray();
    }

    // A scrolling list, with the stock scrollbar. Returns the list's contents.
    public static RectTransform ScrollList(Transform parent, float height)
    {
        GameObject clone = Clone(P.scrollView, parent);
        LayoutElement layout = clone.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = height;
        layout.flexibleHeight = 1;

        ScrollRect scroll = clone.GetComponent<ScrollRect>();
        foreach (Transform child in scroll.content)
            Object.Destroy(child.gameObject);

        RectTransform content = scroll.content;
        VerticalLayoutGroup list = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
        list.spacing = 2;
        list.childControlWidth = list.childControlHeight = list.childForceExpandWidth = true;
        list.childForceExpandHeight = false;

        ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return content;
    }

    public static void Spacer(Transform parent, float height = 6)
    {
        GameObject spacer = new GameObject("Spacer", typeof(RectTransform));
        spacer.transform.SetParent(parent, false);
        spacer.AddComponent<LayoutElement>().minHeight = height;
    }

    public static T Tooltip<T>(T widget, string text) where T : Component
    {
        TooltipController_Text controller = widget.gameObject.AddComponent<TooltipController_Text>();
        controller.prefab = P.tooltip;
        controller.textString = text;
        return widget;
    }

    #endregion
}
