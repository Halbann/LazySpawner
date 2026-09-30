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
        gameObject = new GameObject($"LazySpawner Ghost ({template.name})");

        shader ??= Shader.Find("KSP/Alpha/Translucent") ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");

        Dictionary<Material, Material> materialCache = new Dictionary<Material, Material>();
        ConfigNode[] partNodes = template.node.GetNodes("PART");

        for (int i = 0; i < partNodes.Length; i++)
        {
            ConfigNode partNode = partNodes[i];
            AvailablePart availablePart = PartLoader.getPartInfoByName(partNode.GetValue("name"));
            Part prefab = availablePart?.partPrefab;
            Transform model = prefab != null ? prefab.transform.Find("model") : null;
            if (model == null)
                continue;

            GameObject part = new GameObject(availablePart.name);
            if (prefab.HasModuleImplementing<LaunchClamp>())
                launchClamps.Add(part);
            part.transform.SetParent(gameObject.transform, false);
            part.transform.localPosition = i < template.partPositions.Count ? template.partPositions[i] : Vector3.zero;
            part.transform.localRotation = KSPUtil.ParseQuaternion(partNode.GetValue("rotation"));

            GameObject modelCopy = Object.Instantiate(model.gameObject, part.transform, false);
            modelCopy.transform.localPosition = model.localPosition;
            modelCopy.transform.localRotation = model.localRotation;
            modelCopy.transform.localScale = model.localScale;
            modelCopy.SetActive(true);

            ApplyVariant(prefab, modelCopy.transform, partNode.GetValue("moduleVariantName"));
            Strip(modelCopy, materialCache);
        }

        materials.AddRange(materialCache.Values);
        SetColor(validColor);
    }

    // Parts with variants have every variant's meshes in the prefab. Show the selected one.
    private static void ApplyVariant(Part prefab, Transform model, string variantName)
    {
        ModulePartVariants variants = prefab.variants;
        if (variants == null || variants.variantList == null || variants.variantList.Count == 0)
            return;

        PartVariant variant = variants.variantList.Find(v => v.Name == variantName) ?? variants.variantList[0];

        foreach (PartGameObjectInfo info in variant.InfoGameObjects)
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true))
                if (child.name == info.Name)
                    child.gameObject.SetActive(info.Status);
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
