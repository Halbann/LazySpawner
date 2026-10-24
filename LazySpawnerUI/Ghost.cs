using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LazySpawner;

// A see-through, non-physical model of a vessel template, built from the part prefabs' models.
// Used to preview where a vessel will appear before spawning it.
public class Ghost
{
    public readonly GameObject gameObject;
    private readonly List<Material> materials;
    private readonly List<GameObject> launchClamps = new List<GameObject>();
    private Color color;

    // In the air: a line up to it from the ground.
    private readonly LineRenderer drop;
    private float dropHeight;

    private static Shader shader;
    private static Material lineMaterial;

    public static readonly Color validColor = new Color(0.45f, 0.85f, 1f, 0.4f);
    public static readonly Color invalidColor = new Color(1f, 0.35f, 0.25f, 0.4f);

    public Ghost(VesselTemplate template)
    {
        gameObject = new GameObject($"LazySpawner Ghost ({template.Name})");
        shader ??= Shader.Find("KSP/Alpha/Translucent") ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");

        // Plain colour. Part textures keep their specular map in the alpha channel,
        // which would make a textured ghost see-through in all the wrong places.
        Dictionary<Material, Material> ghostly = new Dictionary<Material, Material>();
        foreach (TemplatePart templatePart in template.Parts)
        {
            Part prefab = templatePart.info?.partPrefab;
            Transform model = prefab != null ? prefab.transform.Find("model") : null;
            if (model == null)
                continue;

            GameObject part = new GameObject(templatePart.info.name);
            if (prefab.HasModuleImplementing<LaunchClamp>())
                launchClamps.Add(part);
            part.transform.SetParent(gameObject.transform, false);
            part.transform.localPosition = templatePart.position;
            part.transform.localRotation = templatePart.rotation;

            GameObject copy = Object.Instantiate(model.gameObject, part.transform, false);
            copy.SetActive(true);
            templatePart.Pose(copy.transform);

            // Parts with variants have every variant's meshes in the prefab. Show the selected one's.
            Dictionary<string, bool> variant = VesselBounds.Variant(prefab, templatePart.variant);
            foreach (Transform child in copy.GetComponentsInChildren<Transform>(true))
                if (variant.TryGetValue(child.name, out bool shown))
                    child.gameObject.SetActive(shown);

            // Keep only what's needed to draw the model.
            foreach (Component component in copy.GetComponentsInChildren<Component>(true))
                if (component != null && component is not (Transform or MeshFilter or Renderer))
                    Object.DestroyImmediate(component);

            foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = renderer is not (ParticleSystemRenderer or TrailRenderer or LineRenderer);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.gameObject.layer = 0;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m == null ? null : ghostly.TryGetValue(m, out Material g) ? g : ghostly[m] = new Material(shader)).ToArray();
            }
        }

        materials = ghostly.Values.ToList();

        GameObject line = new GameObject("Drop");
        line.transform.SetParent(gameObject.transform, false);
        drop = line.AddComponent<LineRenderer>();
        drop.useWorldSpace = false;
        drop.sharedMaterial = lineMaterial ??= new Material(Shader.Find("Sprites/Default"));
        drop.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        drop.receiveShadows = false;
        drop.positionCount = 2;
        line.SetActive(false);

        SetColor(validColor);
    }

    public void SetColor(Color color)
    {
        if (color == this.color)
            return;

        this.color = color;
        foreach (Material material in materials)
            material.color = color;

        drop.startColor = drop.endColor = new Color(color.r, color.g, color.b, 0.4f);
    }

    // Under the middle of the vessel, as tall as the gap beneath it, and as wide as it needs to be seen from the camera.
    public void ShowDrop(bool show, Vector3 ground = default, Vector3 up = default, float height = 0, float width = 0)
    {
        if (drop.gameObject.activeSelf != show)
            drop.gameObject.SetActive(show);
        if (!show)
            return;

        drop.transform.SetPositionAndRotation(ground, Quaternion.FromToRotation(Vector3.up, up));
        drop.widthMultiplier = width;
        if (height != dropHeight)
            drop.SetPosition(1, Vector3.up * (dropHeight = height));
    }

    // Launch clamps are only spawned on the ground.
    public void ShowLaunchClamps(bool show)
    {
        foreach (GameObject clamp in launchClamps)
            clamp.SetActive(show);
    }

    public void Destroy()
    {
        foreach (Material material in materials)
            Object.Destroy(material);

        Object.Destroy(gameObject);
    }
}
