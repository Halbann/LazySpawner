// Vessel bounds from part colliders, after Halban's VesselBounds in Kessler
// (Q:\Visual Studio\Kessler\Source\KerbalCombatSystems\VesselBounds.cs).
//
// Bounds are boxes in the vessel's own frame, the root part's transform. Colliders are
// measured by their eight corners, because a rotated box's size vector isn't its extent.
// Vessel templates haven't been built yet, so their bounds come from the part prefabs'
// colliders placed where the parts will be.

using System.Collections.Generic;
using UnityEngine;

namespace LazySpawner;

public static class VesselBounds
{
    #region Measuring

    // Loaded vessels are measured as they are. Unloaded ones are measured like templates.
    public static Bounds FromVessel(Vessel vessel, bool includeLaunchClamps = true)
    {
        if (!vessel.loaded)
            return FromSnapshots(vessel.protoVessel.protoPartSnapshots, includeLaunchClamps);

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

    public static Bounds FromTemplate(VesselTemplate template, bool includeLaunchClamps = true)
    {
        ConfigNode[] parts = template.node.GetNodes("PART");
        Bounds? bounds = null;

        for (int i = 0; i < parts.Length; i++)
        {
            string name = parts[i].GetValue("name");
            if (!includeLaunchClamps && Spawner.IsLaunchClamp(name))
                continue;

            Vector3 position = i < template.partPositions.Count ? template.partPositions[i] : Vector3.zero;
            Quaternion rotation = KSPUtil.ParseQuaternion(parts[i].GetValue("rotation"));
            AddPrefab(ref bounds, name, parts[i].GetValue("moduleVariantName"), position, rotation);
        }

        return bounds ?? new Bounds();
    }

    private static bool IsLaunchClamp(Part part) =>
        part != null && part.HasModuleImplementing<LaunchClamp>();

    private static Bounds FromSnapshots(List<ProtoPartSnapshot> snapshots, bool includeLaunchClamps)
    {
        Bounds? bounds = null;

        foreach (ProtoPartSnapshot snapshot in snapshots)
            if (includeLaunchClamps || !Spawner.IsLaunchClamp(snapshot.partName))
                AddPrefab(ref bounds, snapshot.partName, snapshot.moduleVariantName, snapshot.position, snapshot.rotation);

        return bounds ?? new Bounds();
    }

    // A part that hasn't been built yet: its prefab's colliders, moved to where the part will be.
    private static void AddPrefab(ref Bounds? bounds, string partName, string variantName, Vector3 position, Quaternion rotation)
    {
        Encapsulate(ref bounds, position);

        Part prefab = PartLoader.getPartInfoByName(partName)?.partPrefab;
        if (prefab == null)
            return;

        Matrix4x4 partToVessel = Matrix4x4.TRS(position, rotation, Vector3.one);
        Matrix4x4 worldToPrefab = prefab.transform.worldToLocalMatrix;
        HashSet<string> hidden = HiddenByVariant(prefab, variantName);

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

    #endregion

    #region Cache

    private struct CacheEntry
    {
        public int partCount;
        public float time;
        public Bounds bounds;
    }

    private static readonly Dictionary<uint, CacheEntry> cache = new Dictionary<uint, CacheEntry>();

    // Vessels don't change shape often, so measuring once a few seconds is plenty.
    public static Bounds Get(Vessel vessel)
    {
        int partCount = vessel.loaded ? vessel.parts.Count : vessel.protoVessel.protoPartSnapshots.Count;

        if (cache.TryGetValue(vessel.persistentId, out CacheEntry entry) && entry.partCount == partCount && Time.time - entry.time < 5f)
            return entry.bounds;

        entry = new CacheEntry { partCount = partCount, time = Time.time, bounds = FromVessel(vessel) };
        cache[vessel.persistentId] = entry;
        return entry.bounds;
    }

    #endregion

    #region Overlap

    // A box in the world: vessel bounds placed at a vessel's position and rotation.
    public struct Box
    {
        public Vector3 centre;
        public Vector3 extents;
        public Vector3 x, y, z;

        public Box(Bounds bounds, Vector3 position, Quaternion rotation)
        {
            centre = position + rotation * bounds.center;
            extents = bounds.extents;
            x = rotation * Vector3.right;
            y = rotation * Vector3.up;
            z = rotation * Vector3.forward;
        }

        public static Box Of(Vessel vessel) =>
            new Box(Get(vessel), vessel.transform.position, vessel.transform.rotation);

        private Vector3 Axis(int i) => i == 0 ? x : i == 1 ? y : z;

        // Half the box's length along a direction.
        public float Extent(Vector3 axis) => Radius(axis.normalized);

        private float Radius(Vector3 axis) =>
            extents.x * Mathf.Abs(Vector3.Dot(x, axis)) + extents.y * Mathf.Abs(Vector3.Dot(y, axis)) + extents.z * Mathf.Abs(Vector3.Dot(z, axis));

        // The widest gap between the boxes along any separating axis, negative if they overlap.
        // Zero or more means they don't touch. It's never more than the true distance between them.
        public float Gap(Box other)
        {
            Vector3 offset = other.centre - centre;
            float gap = float.MinValue;

            // Each box's three faces, and the cross products of their edges: the separating axis theorem.
            for (int n = 0; n < 15; n++)
            {
                Vector3 axis = n < 3 ? Axis(n) : n < 6 ? other.Axis(n - 3) : Vector3.Cross(Axis((n - 6) / 3), other.Axis((n - 6) % 3));

                float length = axis.magnitude;
                if (length < 1e-4f)
                    continue;

                axis /= length;
                float separation = Mathf.Abs(Vector3.Dot(offset, axis)) - Radius(axis) - other.Radius(axis);
                gap = Mathf.Max(gap, separation);
            }

            return gap;
        }

        // Radius of a sphere around the centre enclosing the box.
        public float Radius() => extents.magnitude;
    }

    #endregion
}
