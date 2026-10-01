using System.Collections.Generic;
using UnityEngine;

namespace LazySpawner;

// A see-through, non-physical model of a vessel template, built from the part prefabs' models.
// Used to preview where a vessel will appear before spawning it.
public class Ghost
{
    public readonly GameObject gameObject;
    private readonly List<Material> materials = new List<Material>();
    private readonly List<GameObject> launchClamps = new List<GameObject>();
    private Color color;

    private static Shader shader;

    public static readonly Color validColor = new Color(0.45f, 0.85f, 1f, 0.4f);
    public static readonly Color invalidColor = new Color(1f, 0.35f, 0.25f, 0.4f);

    public bool Visible
    {
        get => gameObject.activeSelf;
        set
        {
            if (gameObject.activeSelf != value)
                gameObject.SetActive(value);
        }
    }

    public Ghost(VesselTemplate template)
    {
        gameObject = new GameObject($"LazySpawner Ghost ({template.Name})");

        shader ??= Shader.Find("KSP/Alpha/Translucent") ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");

        Dictionary<Material, Material> materialCache = new Dictionary<Material, Material>();
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

            GameObject modelCopy = Object.Instantiate(model.gameObject, part.transform, false);
            modelCopy.transform.localPosition = model.localPosition;
            modelCopy.transform.localRotation = model.localRotation;
            modelCopy.transform.localScale = model.localScale;
            modelCopy.SetActive(true);

            // Parts with variants have every variant's meshes in the prefab. Show the selected one's.
            Dictionary<string, bool> variant = VesselBounds.Variant(prefab, templatePart.variant);
            foreach (Transform child in modelCopy.GetComponentsInChildren<Transform>(true))
                if (variant.TryGetValue(child.name, out bool shown))
                    child.gameObject.SetActive(shown);

            Strip(modelCopy, materialCache);
        }

        materials.AddRange(materialCache.Values);
        SetColor(validColor);
    }

    // Keep only what's needed to draw the model.
    private static void Strip(GameObject model, Dictionary<Material, Material> materialCache)
    {
        foreach (Component component in model.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component is Transform || component is MeshFilter || component is Renderer)
                continue;

            Object.DestroyImmediate(component);
        }

        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
            {
                renderer.enabled = false;
                continue;
            }

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.gameObject.layer = 0;

            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; i++)
            {
                Material original = shared[i];
                if (original == null)
                    continue;

                // Plain colour. Part textures keep their specular map in the alpha channel,
                // which would make a textured ghost see-through in all the wrong places.
                if (!materialCache.TryGetValue(original, out Material ghost))
                {
                    ghost = new Material(shader);
                    materialCache.Add(original, ghost);
                }

                shared[i] = ghost;
            }

            renderer.sharedMaterials = shared;
        }
    }

    public void SetColor(Color newColor)
    {
        if (newColor == color)
            return;

        color = newColor;
        foreach (Material material in materials)
            material.color = color;
    }

    // Launch clamps are only spawned on the ground.
    public void ShowLaunchClamps(bool show)
    {
        foreach (GameObject clamp in launchClamps)
            if (clamp.activeSelf != show)
                clamp.SetActive(show);
    }

    public void SetPose(Vector3 position, Quaternion rotation)
    {
        gameObject.transform.SetPositionAndRotation(position, rotation);
    }

    public void Destroy()
    {
        foreach (Material material in materials)
            Object.Destroy(material);

        Object.Destroy(gameObject);
    }
}
