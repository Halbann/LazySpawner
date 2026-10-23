using System.Collections.Generic;
using System.Linq;

namespace LazySpawner;

// A vessel spawned in the air is landed where it was put until physics starts, so that on rails it neither falls
// nor is destroyed by the air pressure, the way unloaded vessels in flight are. Meanwhile it loads and unpacks at
// a flying vessel's distances. Then it flies: along its nose, gear up, brakes off, SAS on, full throttle, and the
// engines of the first stage that has any running, though nothing else in that stage. Without the mod, it falls.
public class AirborneStart : VesselModule
{
    [KSPField(isPersistant = true)]
    public bool waiting;

    [KSPField(isPersistant = true)]
    public float airspeed;

    // Null for a vessel that isn't spawned in the air, which a clone of one waiting would otherwise be.
    internal static void Set(ProtoVessel protoVessel, float? airspeed)
    {
        ConfigNode modules = protoVessel.vesselModules ??= new ConfigNode("VESSELMODULES");
        modules.RemoveNodes(nameof(AirborneStart));
        if (airspeed == null)
            return;

        ConfigNode node = modules.AddNode(nameof(AirborneStart));
        node.AddValue(nameof(waiting), true);
        node.AddValue(nameof(AirborneStart.airspeed), airspeed.Value);
    }

    public override void OnLoad(ConfigNode node)
    {
        if (waiting)
            vessel.vesselRanges.landed = new VesselRanges.Situation(vessel.vesselRanges.flying);
    }

    // Before the parts unpack, which is when they take the orbit's velocity.
    public override void OnGoOffRails()
    {
        if (!waiting)
            return;

        waiting = false;
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
    }
}
