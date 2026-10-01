// Vessel bounds from part colliders, after Halban's VesselBounds in Kessler
// (Q:\Visual Studio\Kessler\Source\KerbalCombatSystems\VesselBounds.cs).
//
// Bounds are boxes in the vessel's own frame, the root part's transform. Colliders are
// measured by their eight corners, because a rotated box's size vector isn't its extent.
// Parts that haven't been built yet are measured by their prefabs' colliders, placed where the parts will be.

using System.Collections.Generic;
using System.Linq;
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

        // Always include the parts themselves, in case some have no colliders.
        IEnumerable<Vector3> parts = vessel.parts
            .Where(part => includeLaunchClamps || !IsLaunchClamp(part))
            .Select(part => toVessel.MultiplyPoint3x4(part.transform.position));

        // Every part is somewhere under the root part's transform.
        IEnumerable<Vector3> colliders = (vessel.rootPart != null ? vessel.rootPart.GetComponentsInChildren<Collider>(false) : new Collider[0])
            .Where(collider => collider.enabled && !collider.isTrigger && (includeLaunchClamps || !IsLaunchClamp(collider.GetComponentInParent<Part>())))
            .SelectMany(collider => Corners(collider, toVessel * collider.transform.localToWorldMatrix));

        return Enclose(parts.Concat(colliders));
    }

    internal static Bounds FromParts(IEnumerable<TemplatePart> parts, bool includeLaunchClamps) =>
        Enclose(parts.Where(part => includeLaunchClamps || !IsLaunchClamp(part.info?.partPrefab)).SelectMany(Prefab));

    private static bool IsLaunchClamp(Part part) =>
        part != null && part.HasModuleImplementing<LaunchClamp>();

    // A part that hasn't been built yet: its prefab's colliders, moved to where the part will be.
    private static IEnumerable<Vector3> Prefab(TemplatePart part)
    {
        yield return part.position;

        Part prefab = part.info?.partPrefab;
        if (prefab == null)
            yield break;

        Matrix4x4 partToVessel = Matrix4x4.TRS(part.position, part.rotation, Vector3.one);
        Matrix4x4 worldToPrefab = prefab.transform.worldToLocalMatrix;
        HashSet<string> hidden = HiddenByVariant(prefab, part.variant);

        foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
            if (collider.enabled && !collider.isTrigger && Shown(collider.transform, prefab.transform, hidden))
                foreach (Vector3 corner in Corners(collider, partToVessel * worldToPrefab * collider.transform.localToWorldMatrix))
                    yield return corner;
    }

    // Prefabs have every variant's objects. The ones the selected variant turns off don't count.
    private static HashSet<string> HiddenByVariant(Part prefab, string variantName)
    {
        List<PartVariant> variants = prefab.variants?.variantList;
        if (variants == null || variants.Count == 0)
            return new HashSet<string>();

        PartVariant variant = variants.Find(v => v.Name == variantName) ?? variants[0];
        return new HashSet<string>(variant.InfoGameObjects.Where(info => !info.Status).Select(info => info.Name));
    }

    // The prefab itself is inactive, so look at each object's own active flag up to the part.
    private static bool Shown(Transform transform, Transform root, HashSet<string> hidden)
    {
        for (Transform t = transform; t != null && t != root; t = t.parent)
            if (!t.gameObject.activeSelf || hidden.Contains(t.name))
                return false;

        return true;
    }

    // A collider's box, moved into the vessel's frame.
    private static IEnumerable<Vector3> Corners(Collider collider, Matrix4x4 toVessel)
    {
        Bounds? local = collider switch
        {
            MeshCollider mesh when mesh.sharedMesh != null => mesh.sharedMesh.bounds,
            BoxCollider box => new Bounds(box.center, box.size),
            SphereCollider sphere => new Bounds(sphere.center, Vector3.one * sphere.radius * 2),
            CapsuleCollider capsule => new Bounds(capsule.center, Vector3.one * capsule.radius * 2 + new Vector3 { [capsule.direction] = Mathf.Max(0, capsule.height - capsule.radius * 2) }),
            WheelCollider wheel => new Bounds(wheel.center, new Vector3(wheel.radius * 2, wheel.radius * 2 + wheel.suspensionDistance, wheel.radius * 2)),
            _ => null,
        };

        return local == null ? Enumerable.Empty<Vector3>() : Corners(local.Value).Select(toVessel.MultiplyPoint3x4);
    }

    internal static IEnumerable<Vector3> Corners(Bounds bounds)
    {
        Vector3 min = bounds.min, max = bounds.max;
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
    }

    // The smallest box around the points.
    internal static Bounds Enclose(IEnumerable<Vector3> points)
    {
        List<Vector3> list = points.ToList();
        Bounds bounds = new Bounds();
        if (list.Count > 0)
            bounds.SetMinMax(list.Aggregate(Vector3.Min), list.Aggregate(Vector3.Max));

        return bounds;
    }
}
