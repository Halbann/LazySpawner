// Widgets for a screen in the stock debug console (Alt+F12), cloned from the stock screens so they look
// exactly the same. After Rescored's and HotReloadKSP's debug screens, which were after Phantomical's
// BackgroundResourceProcessing. KSP 1.12 is the last version, so the paths into its prefabs won't change.

using KSP.UI;
using KSP.UI.Screens.DebugToolbar;
using KSP.UI.TooltipTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LazySpawner;

internal static class DebugUI
{
    private static DebugScreenSpawner Spawner => DebugScreenSpawner.Instance;

    // Labels in front of fields and choices, so they all line up, and the width of most controls, as in stock.
    public const float LabelWidth = 140;
    public const float ControlWidth = 160;

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
    public static void AddScreen<T>(string parent, string name, string text) where T : MonoBehaviour
    {
        // Already there from before a hot reload, which brings its components up to date.
        if (Spawner.debugScreens.screens.Any(s => s.name == name))
            return;

        GameObject prefab = new GameObject(name, typeof(RectTransform));
        prefab.SetActive(false);
        Object.DontDestroyOnLoad(prefab);
        prefab.AddComponent<T>();

        Spawner.debugScreens.screens.Add(new AddDebugScreens.ScreenWrapper { parentName = parent, name = name, text = text, screen = (RectTransform)prefab.transform });
    }

    public static bool Shown => Spawner != null && Spawner.screen.isShown;

    public static void Show(string name) => DebugScreenSpawner.ShowTab(name);

    public static void Hide() => Spawner?.screen.Hide();

    public static UIWindow Window => Spawner.screen.GetComponent<UIWindow>();

    // Stock's own minimum size, which other screens are given back.
    public static void ResetWindowMinimum()
    {
        if (Spawner != null)
            Window.minSize = Spawner.screenPrefab.GetComponent<UIWindow>().minSize;
    }

    #endregion

    #region Widgets

    private static GameObject Clone(GameObject prefab, Transform parent, float width = -1)
    {
        GameObject clone = Object.Instantiate(prefab, parent, false);
        clone.SetActive(true);
        Size(clone, width, 20).flexibleWidth = width < 0 ? 1 : 0;
        return clone;
    }

    // A fixed size is a minimum too, so a narrow window cuts widgets off, as stock does, instead of squashing them.
    public static LayoutElement Size(GameObject widget, float? width = null, float? height = null)
    {
        LayoutElement layout = widget.GetComponent<LayoutElement>() ?? widget.AddComponent<LayoutElement>();
        if (width is float w)
            layout.minWidth = layout.preferredWidth = w;
        if (height is float h)
            layout.minHeight = layout.preferredHeight = h;
        return layout;
    }

    // Filling its parent, or stretched across the top of it.
    public static RectTransform Fill(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    public static RectTransform Top(RectTransform rect)
    {
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 1);
        return rect;
    }

    // An empty UI object, for laying out others.
    public static RectTransform Empty(Transform parent, string name)
    {
        RectTransform empty = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        empty.SetParent(parent, false);
        return empty;
    }

    // A vertical stack, for a screen or a section of one.
    public static RectTransform Column(Transform parent, float spacing = 3, RectOffset padding = null) =>
        Stack(Empty(parent, "Column"), spacing, padding ?? new RectOffset());

    private static RectTransform Stack(RectTransform column, float spacing, RectOffset padding)
    {
        VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.padding = padding;
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return column;
    }

    // Widgets side by side.
    public static RectTransform Row(Transform parent, float spacing = 4, TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        RectTransform row = Empty(parent, "Row");
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childAlignment = alignment;
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

    // A section's heading, underlined, as in Rescored.
    public static void Heading(Transform parent, string text)
    {
        TextMeshProUGUI heading = Label(parent, text, bold: true);
        heading.fontSize *= 1.2f;
        heading.GetComponent<LayoutElement>().preferredHeight = 24;

        GameObject line = Empty(parent, "Line").gameObject;
        line.AddComponent<Image>().color = new Color(0.4f, 0.4f, 0.4f);
        line.AddComponent<LayoutElement>().minHeight = 1;
        Spacer(parent, 2);
    }

    // A shaded panel, to set something apart.
    public static RectTransform Box(Transform parent)
    {
        RectTransform box = Column(parent, 3, new RectOffset(6, 6, 5, 5));
        box.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
        return box;
    }

    // Text that grows to fit.
    public static TextMeshProUGUI Paragraph(Transform parent, string text = "")
    {
        TextMeshProUGUI tmp = Label(parent, text);
        tmp.GetComponent<LayoutElement>().preferredHeight = -1;
        tmp.richText = true;
        return tmp;
    }

    // Typing in a field mustn't fire the ship's keys: "1" is action group 1, and a space stages.
    public const string TypingLock = "LazySpawnerTyping";

    public static TMP_InputField Field(Transform parent, string text, Action<string> changed, float width = -1)
    {
        TMP_InputField input = Clone(P.field, parent, width).GetComponent<TMP_InputField>();
        input.text = text;

        // Stock centres the letters typed, so the text jumps as ones with a tail or a dot come and go. Lined up by its
        // baseline it stays put, moved down to look centred.
        foreach (TMP_Text label in new[] { input.textComponent, (TMP_Text)input.placeholder })
        {
            label.alignment = TextAlignmentOptions.Baseline;
            label.rectTransform.offsetMin += new Vector2(0, -4);
            label.rectTransform.offsetMax += new Vector2(0, -4);
        }

        input.onValueChanged.AddListener(s => changed(s));
        input.onSelect.AddListener(_ => InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, TypingLock));
        input.onDeselect.AddListener(_ => InputLockManager.RemoveControlLock(TypingLock));
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

    public static Toggle Toggle(Transform parent, string text, bool on, Action<bool> changed)
    {
        GameObject clone = Clone(P.toggle, parent);
        TextMeshProUGUI tmp = clone.GetComponentInChildren<TextMeshProUGUI>();
        tmp.text = text;
        Size(clone, tmp.GetPreferredValues(text).x + 36).flexibleWidth = 0;

        Toggle t = clone.GetComponent<Toggle>();
        t.isOn = on;
        t.onValueChanged.AddListener(v => changed(v));
        return t;
    }

    // Buttons side by side, all the same width, for choosing one of them. Show which with Select.
    public static Button[] Tabs(Transform parent, string[] names, Action<int> chosen)
    {
        Transform row = Row(parent, 2);
        float width = names.Max(name => P.button.GetComponentInChildren<TextMeshProUGUI>().GetPreferredValues(name).x) + 16;

        // At least as wide as the widest, sharing what's left of the row.
        return names.Select((name, i) =>
        {
            Button tab = Button(row, name, () => chosen(i));
            Size(tab.gameObject, width);
            return tab;
        }).ToArray();
    }

    // The chosen tab looks like a stock button, and the rest are dark with light text, like the craft list.
    public static void Select(Button tab, bool selected)
    {
        tab.image.color = selected ? Color.white : new Color(0.3f, 0.3f, 0.3f);
        tab.GetComponentInChildren<TextMeshProUGUI>().color = selected ? P.button.GetComponentInChildren<TextMeshProUGUI>().color : new Color(0.85f, 0.85f, 0.85f);
    }

    // An empty scroll view, with the stock scrollbar, that takes up whatever height is left.
    public static ScrollRect ScrollView(Transform parent, float minHeight)
    {
        GameObject clone = Clone(P.scrollView, parent);
        clone.name = "ScrollView";
        Size(clone, height: minHeight).flexibleHeight = 1;

        // Fresh contents, without the stock tree view and its layout.
        ScrollRect scroll = clone.GetComponent<ScrollRect>();
        if (scroll.viewport == null)
            scroll.viewport = (RectTransform)scroll.content.parent;

        RectTransform content = Top(Empty(scroll.viewport, "Content"));
        content.sizeDelta = Vector2.zero;
        Object.DestroyImmediate(scroll.content.gameObject);
        scroll.content = content;

        return scroll;
    }

    // A scrolling list. Returns the list's contents.
    public static RectTransform ScrollList(Transform parent, float minHeight)
    {
        RectTransform content = Stack(ScrollView(parent, minHeight).content, 2, new RectOffset(6, 6, 4, 4));
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return content;
    }

    public static void Spacer(Transform parent, float height = 6) =>
        Empty(parent, "Spacer").gameObject.AddComponent<LayoutElement>().minHeight = height;

    public static T Tooltip<T>(T widget, string text) where T : Component
    {
        TooltipController_Text controller = widget.gameObject.AddComponent<TooltipController_Text>();
        controller.prefab = P.tooltip;
        controller.textString = text;
        return widget;
    }

    #endregion
}

// Says when what it holds changes size, which is when its sections come and go.
internal class SizeWatcher : UIBehaviour
{
    public Action changed;

    protected override void OnRectTransformDimensionsChange() => changed?.Invoke();
}

// A screen in the console, built from DebugUI's widgets, some of them bound to the game. UpdateBindings brings those up to date.
internal abstract class ConsoleScreen : MonoBehaviour
{
    internal const string grey = "<color=#a0a0a0>", orange = "<color=#ff9a6a>";
    private readonly List<Action> bindings = new List<Action>();

    protected void OnDisable()
    {
        InputLockManager.RemoveControlLock(DebugUI.TypingLock);
        DebugUI.ResetWindowMinimum();
        fitDirty = true;
    }

    #region Window

    private RectTransform list;
    private bool fitDirty;

    // The window can't be smaller than this scroll list's contents need, screen allowing, and grows to that if it is. It
    // follows them as they grow, but is left alone as they shrink. Other screens get stock's minimum back.
    protected void FitWindow(RectTransform list)
    {
        this.list = list;
        list.gameObject.AddComponent<SizeWatcher>().changed = () => fitDirty = true;
        fitDirty = true;
    }

    // For whatever changes size other than through a binding, which the watcher only notices after layout.
    protected void LateUpdate()
    {
        if (fitDirty)
            Fit();
    }

    // Once the screen is bound, so it's laid out for what it shows. A screen that's just been shown is drawn once before
    // it updates, so it fits itself as it's built.
    protected void Fit()
    {
        if (list == null)
            return;

        fitDirty = false;
        Canvas.ForceUpdateCanvases();
        UIWindow window = DebugUI.Window;
        RectTransform rect = (RectTransform)window.transform;

        // What the window has around the list is the difference between their sizes. Fixed widths are minimums, so the
        // list's own minimum is as narrow as it can be without cutting anything off, where its preferred is as tall as it wants.
        Vector2 size = rect.rect.size;
        Vector2 around = size - ((RectTransform)list.parent).rect.size;
        Vector2 needed = Vector2.Min(around + new Vector2(LayoutUtility.GetMinWidth(list), LayoutUtility.GetPreferredHeight(list)), ((RectTransform)rect.parent).rect.size);
        window.minSize = needed;

        // Never smaller than that, but never made smaller either. Its top left corner stays where it is.
        Vector2 grown = Vector2.Max(size, needed);
        if (grown != size)
        {
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, grown.x);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, grown.y);
            rect.anchoredPosition += new Vector2((grown.x - size.x) * rect.pivot.x, (size.y - grown.y) * (1 - rect.pivot.y));
            window.ClampToScreen();

            // Laid out again at its new size, so the scroll bar is gone and nothing is cut off when it's drawn. The window's
            // layouts aren't dirty yet, so the canvas has nothing to rebuild, and a rebuild skips what's under a rect with
            // no layout of its own. So it starts from the topmost layout between the window and the list.
            Transform top = list;
            for (Transform t = list; t != rect; t = t.parent)
                if (t.GetComponent<ILayoutController>() != null)
                    top = t;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)top);
            Canvas.ForceUpdateCanvases();
        }
    }

    #endregion

    // Built in Start rather than Awake, so that after a hot reload it's built by the new code. A hot reload copies the old
    // screen's widgets across, so they go first.
    protected void Clear()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        bindings.Clear();
        DebugUI.Fill((RectTransform)transform);
    }

    protected RectTransform Page() => DebugUI.Fill(DebugUI.Column(transform, 3, new RectOffset(4, 4, 4, 4)));

    // When a binding shows, hides or rewrites something, the window is fitted at once, ahead of every late update in the
    // frame. The scroll view's sets its bar from the window's size then, so any later and it's drawn wrong for a frame.
    protected void UpdateBindings()
    {
        foreach (Action binding in bindings)
            binding();

        if (fitDirty)
            Fit();
    }

    protected void Bind(Action binding) => bindings.Add(binding);

    protected T Show<T>(T widget, Func<bool> when) where T : Component
    {
        Bind(() =>
        {
            bool show = when();
            if (widget.gameObject.activeSelf == show)
                return;

            widget.gameObject.SetActive(show);
            fitDirty = true;
        });
        return widget;
    }

    protected T Enable<T>(T widget, Func<bool> when) where T : Selectable
    {
        Bind(() => widget.interactable = when());
        return widget;
    }

    protected TextMeshProUGUI Text(TextMeshProUGUI text, Func<string> content)
    {
        Bind(() =>
        {
            string s = content();
            if (text.text == s)
                return;

            text.text = s;
            fitDirty = true;
        });
        return text;
    }

    // Shows which one is chosen, coloured again only when that changes.
    protected Button[] Tabs(Transform parent, Func<int> chosen, Action<int> choose, params string[] names)
    {
        Button[] tabs = DebugUI.Tabs(parent, names, choose);
        int shown = -1;
        Bind(() =>
        {
            if (chosen() == shown)
                return;
            shown = chosen();
            for (int i = 0; i < tabs.Length; i++)
                DebugUI.Select(tabs[i], i == shown);
        });
        return tabs;
    }

    // Every row of the form is a label in the label column, then its controls, so they all line up.
    protected static Transform Line(Transform parent, string label = "", string tooltip = null)
    {
        Transform row = DebugUI.Row(parent);
        TextMeshProUGUI text = DebugUI.Label(row, label, DebugUI.LabelWidth);
        if (tooltip != null)
            DebugUI.Tooltip(text, tooltip);
        return row;
    }

    // What will happen, a line each, set apart in a box. Warnings start orange, and their bullets are too.
    // The closing tags keep the colour from running on.
    protected static string Bullets(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(line => (line.StartsWith(orange) ? orange : "") + "•<indent=14>" + line + "</color></color></indent>"));
}
