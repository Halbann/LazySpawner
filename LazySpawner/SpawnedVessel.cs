using System.Collections.Generic;
using System.Linq;

namespace LazySpawner;

// What a spawned vessel does once physics starts, if it was asked to anything. Whatever the situation, it fires the
// action groups it was given, once the parts have unpacked. One spawned in the air is also landed where it was put
// until then, so that on rails it neither falls nor is destroyed by the air pressure, the way unloaded vessels in
// flight are. Meanwhile it loads and unpacks at a flying vessel's distances. Then it flies: along its nose, gear up,
// brakes off, SAS on, full throttle, and the engines of the first stage that has any running, though nothing else in
// that stage. Without the mod, it falls.
public class SpawnedVessel : VesselModule
{
    [KSPField(isPersistant = true)]
    public bool airborne;

    [KSPField(isPersistant = true)]
    public float airspeed;

    [KSPField(isPersistant = true)]
    public KSPActionGroup actionGroups;

    // Taking the whole air load at once, the joints overshoot it and the weakest snap, wing roots first. So they
    // can't break for a while, starting in the first physics step after the parts unpack, which is when they would.
    public static int settlingSteps = 400;
    private int settling;

    // Nothing for a vessel with nothing to do, which a clone of one that hasn't done it yet would otherwise be.
    internal static void Set(ProtoVessel protoVessel, float? airspeed, KSPActionGroup actionGroups)
    {
        ConfigNode modules = protoVessel.vesselModules ??= new ConfigNode("VESSELMODULES");
        modules.RemoveNodes(nameof(SpawnedVessel));
        if (airspeed == null && actionGroups == KSPActionGroup.None)
            return;

        ConfigNode node = modules.AddNode(nameof(SpawnedVessel));
        node.AddValue(nameof(airborne), airspeed != null);
        node.AddValue(nameof(SpawnedVessel.airspeed), airspeed ?? 0);
        node.AddValue(nameof(SpawnedVessel.actionGroups), actionGroups);
    }

    public override void OnLoad(ConfigNode node)
    {
        if (airborne)
            vessel.vesselRanges.landed = new VesselRanges.Situation(vessel.vesselRanges.flying);
    }

    // Before the parts unpack, which is when they take the orbit's velocity.
    public override void OnGoOffRails()
    {
        if (!airborne)
            return;

        airborne = false;
        vessel.vesselRanges.landed = new VesselRanges.Situation(PhysicsGlobals.Instance.VesselRangesDefault.landed);
        vessel.Landed = false;

        CelestialBody body = vessel.mainBody;
        Vector3d velocity = body.getRFrmVel(vessel.CoMD) + (Vector3d)vessel.ReferenceTransform.up * airspeed;
        vessel.orbit.UpdateFromStateVectors((vessel.CoMD - body.position).xzy, velocity.xzy, body, Planetarium.GetUniversalTime());

        // Groups only fire their actions when they change, and the gear's isn't kept in step with the wheels except on the launch pad.
        foreach ((KSPActionGroup group, bool on) in new[] { (KSPActionGroup.Gear, false), (KSPActionGroup.Brakes, false), (KSPActionGroup.SAS, true) })
        {
            vessel.ActionGroups.SetGroup(group, !on);
            vessel.ActionGroups.SetGroup(group, on);
        }
        vessel.ctrlState.mainThrottle = 1;

        // Stages fire from the highest number down. A multi-mode engine's other mode is disabled.
        List<ModuleEngines> engines = vessel.FindPartModulesImplementing<ModuleEngines>().Where(e => e.isEnabled && e.stagingEnabled && e.part.inverseStage >= 0).ToList();
        int first = engines.Count > 0 ? engines.Max(e => e.part.inverseStage) : -1;
        foreach (ModuleEngines engine in engines.Where(e => e.part.inverseStage == first))
            engine.Activate();

        Logger.Log($"{vessel.GetDisplayName()} took off at {airspeed:F0} m/s with {engines.Count(e => e.part.inverseStage == first)} engines.");
        settling = settlingSteps;
        enabled = true;
    }

    // Every vessel has one, so it only updates while it has something to do. Stock still calls the rest.
    public override bool ShouldBeActive() => settling > 0 || actionGroups != KSPActionGroup.None;

    // Then back to however they unpacked, as stock does it.
    protected void FixedUpdate()
    {
        if (vessel.packed)
            return;

        for (KSPActionGroup group = KSPActionGroup.Custom01; group <= KSPActionGroup.Custom10; group = (KSPActionGroup)((int)group << 1))
            if ((actionGroups & group) != 0)
                vessel.ActionGroups.ToggleGroup(group);
        actionGroups = KSPActionGroup.None;

        if (settling > 0)
        {
            bool unbreakable = --settling > 0 || CheatOptions.UnbreakableJoints;
            foreach (Part part in vessel.parts)
                if (part.attachJoint != null)
                    part.attachJoint.SetUnbreakable(unbreakable, part.rigidAttachment);
        }
        enabled = ShouldBeActive();
    }
}
