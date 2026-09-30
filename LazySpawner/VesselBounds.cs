// Vessel bounds from part colliders, after Halban's VesselBounds in Kessler
// (Q:\Visual Studio\Kessler\Source\KerbalCombatSystems\VesselBounds.cs).
//
// Bounds are boxes in the vessel's own frame, the root part's transform. Colliders are
// measured by their eight corners, because a rotated box's size vector isn't its extent.
// Parts that haven't been built yet are measured by their prefabs' colliders, placed where the parts will be.

using System.Collections.Generic;
using UnityEngine;

namespace LazySpawner;

public static class VesselBounds
{
    // Loaded vessels are measured as they are. Unloaded ones are measured like templates.
    public static Bounds FromVessel(Vessel vessel, bool includeLaunchClamps = true)
    {
        if (!vessel.loaded)
            return FromParts(TemplatePart.From(vessel.protoVessel), includeLaunchClamps);

        Matrix4x4 toVessel = vessel.transform.worldToLocalMatrix;
        Bounds? bounds = null;

        // Always include the parts themselves, in case some have no colliders.
        foreach (Part part in vessel.parts)
            if (includeLaunchClamps || !IsLaunchClamp(part))
                Encapsulate(ref bounds, toVessel.MultiplyPoint3x4(part.transform.position));

        // Every part is somewhere under the root part's transform.
        if (vessel.rootPart != null)
        {
            foreach (Collider collider in vessel.rootPart.GetComponentsInChildren<Collider>(false))
            {
                if (!collider.enabled || collider.isTrigger)
                    continue;

                if (!includeLaunchClamps && IsLaunchClamp(collider.GetComponentInParent<Part>()))
                    continue;

                EncapsulateCollider(ref bounds, collider, toVessel * collider.transform.localToWorldMatrix);
            }
        }

        return bounds ?? new Bounds();
    }

    internal static Bounds FromParts(IEnumerable<TemplatePart> parts, bool includeLaunchClamps)
    {
        Bounds? bounds = null;

        foreach (TemplatePart part in parts)
            if (includeLaunchClamps || !IsLaunchClamp(part.info?.partPrefab))
                AddPrefab(ref bounds, part);

        return bounds ?? new Bounds();
    }

    private static bool IsLaunchClamp(Part part) =>
        part != null && part.HasModuleImplementing<LaunchClamp>();

    // A part that hasn't been built yet: its prefab's colliders, moved to where the part will be.
    private static void AddPrefab(ref Bounds? bounds, TemplatePart part)
    {
        Encapsulate(ref bounds, part.position);

        Part prefab = part.info?.partPrefab;
        if (prefab == null)
            return;

        Matrix4x4 partToVessel = Matrix4x4.TRS(part.position, part.rotation, Vector3.one);
        Matrix4x4 worldToPrefab = prefab.transform.worldToLocalMatrix;
        HashSet<string> hidden = HiddenByVariant(prefab, part.variant);

        foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled || collider.isTrigger || !Shown(collider.transform, prefab.transform, hidden))
                continue;

            EncapsulateCollider(ref bounds, collider, partToVessel * worldToPrefab * collider.transform.localToWorldMatrix);
        }
    }

    // Prefabs have every variant's objects. The ones the selected variant turns off don't count.
    private static HashSet<string> HiddenByVariant(Part prefab, string variantName)
    {
        HashSet<string> hidden = new HashSet<string>();
        ModulePartVariants variants = prefab.variants;
        if (variants?.variantList == null || variants.variantList.Count == 0)
            return hidden;

        PartVariant variant = variants.variantList.Find(v => v.Name == variantName) ?? variants.variantList[0];
        foreach (PartGameObjectInfo info in variant.InfoGameObjects)
            if (!info.Status)
                hidden.Add(info.Name);

        return hidden;
    }

    // The prefab itself is inactive, so look at each object's own active flag up to the part.
    private static bool Shown(Transform transform, Transform root, HashSet<string> hidden)
    {
        for (Transform t = transform; t != null && t != root; t = t.parent)
            if (!t.gameObject.activeSelf || hidden.Contains(t.name))
                return false;

        return true;
    }

    private static void EncapsulateCollider(ref Bounds? bounds, Collider collider, Matrix4x4 toVessel)
    {
        Bounds local;

        switch (collider)
        {
            case MeshCollider mesh when mesh.sharedMesh != null:
                local = mesh.sharedMesh.bounds;
                break;
            case BoxCollider box:
                local = new Bounds(box.center, box.size);
                break;
            case SphereCollider sphere:
                local = new Bounds(sphere.center, Vector3.one * sphere.radius * 2);
                break;
            case CapsuleCollider capsule:
                Vector3 size = Vector3.one * capsule.radius * 2;
                size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2);
                local = new Bounds(capsule.center, size);
                break;
            case WheelCollider wheel:
                local = new Bounds(wheel.center, new Vector3(wheel.radius * 2, wheel.radius * 2 + wheel.suspensionDistance, wheel.radius * 2));
                break;
            default:
                return;
        }

        Vector3 min = local.min, max = local.max;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
            Encapsulate(ref bounds, toVessel.MultiplyPoint3x4(corner));
        }
    }

    private static void Encapsulate(ref Bounds? bounds, Vector3 point)
    {
        if (bounds == null)
            bounds = new Bounds(point, Vector3.zero);
        else
        {
            Bounds b = bounds.Value;
            b.Encapsulate(point);
            bounds = b;
        }
    }
}
