using UnityEngine;

namespace LazySpawner;

class ClickBlocker : MonoBehaviour
{
    public RectTransform rect;

    public bool Blocking
    {
        get => gameObject.activeSelf;
        set => gameObject.SetActive(value);
    }

    public void UpdateRect(Rect windowRect)
    {
        if (rect != null)
        {
            rect.sizeDelta = new Vector2(windowRect.width, windowRect.height);
            rect.anchoredPosition = new Vector2(windowRect.x - 0.5f * Screen.width, 0.5f * Screen.height - windowRect.y);
        }
    }

    protected void OnDestroy()
    {
        Destroy(gameObject);
    }

    public static ClickBlocker Create(Canvas canvas, string owner)
    {
        GameObject blocker = new (owner + "ClickBlocker");
        ClickBlocker clickBlocker = blocker.AddComponent<ClickBlocker>();

        blocker.transform.SetParent(canvas.transform);

        RectTransform rect = blocker.AddComponent<RectTransform>();
        rect.pivot = new Vector2(0f, 1f);
        clickBlocker.rect = rect;

        blocker.AddComponent<CanvasRenderer>();
        blocker.AddComponent<UnityEngine.UI.Text>();
        blocker.SetActive(false);

        return clickBlocker;
    }
}
