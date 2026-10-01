using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using static LazySpawner.Localisation;

namespace LazySpawner;

public enum CrewMode
{
    None,
    Pilot,
    FillCommand,
    FillAll,
}

// Who goes aboard. The mode picks which seats to fill, the first kerbal always a pilot. Seats go to
// the listed kerbals first, who must be available, then to kerbals from the astronaut complex, then to new hires.
public struct CrewSettings
{
    public CrewMode mode;
    public bool onlyNewKerbals;
    public List<ProtoCrewMember> kerbals;

    public CrewSettings(CrewMode mode, bool onlyNewKerbals = false, List<ProtoCrewMember> kerbals = null)
    {
        this.mode = mode;
        this.onlyNewKerbals = onlyNewKerbals;
        this.kerbals = kerbals;
    }
}

// Stamps out copies of a VesselTemplate as new, unloaded vessels, in flight or the tracking station.
// The vessels load normally when they come into range of the active vessel.
public static class Spawner
{
    // Kerbals hired to crew spawned vessels this session, so that removing the vessels can fire them again.
    private static readonly HashSet<string> hiredKerbals = new HashSet<string>();

    // The new vessel as the save has it. Its vesselRef is the vessel itself, except in the editor,
    // where it's only in the save until the game goes to flight or the tracking station.
    public static ProtoVessel Spawn(VesselTemplate template, SpawnSituation situation, CrewSettings crew = default)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        if (HighLogic.CurrentGame?.flightState == null || FlightGlobals.fetch == null)
            throw new SpawnException(Loc("Error_WrongScene"));

        ProtoCrewMember busy = crew.kerbals?.Find(k => k.rosterStatus != ProtoCrewMember.RosterStatus.Available);
        if (busy != null)
            throw new SpawnException(Loc("Error_KerbalBusy", busy.displayName));

        Stopwatch timer = Stopwatch.StartNew();

        // Work on a copy so that the template can be used again.
        ConfigNode node = template.node.CreateCopy();

        Dictionary<uint, uint> changedPIDs = new Dictionary<uint, uint>();
        bool keptReference = MakeUnique(node, changedPIDs);
        UpdateRoboticsReferences(node, changedPIDs);

        // Rovers and planes shouldn't roll away as soon as they touch the ground.
        if (situation.landed)
            SetBrakes(node);
        // Launch clamps count as touching the ground, which makes the whole vessel "landed", even in orbit.
        else
            RemoveLaunchClamps(node);

        // The stock constructor registers the vessel's and parts' persistent IDs.
        ProtoVessel protoVessel = new ProtoVessel(node, HighLogic.CurrentGame);
        double prepared = timer.Elapsed.TotalMilliseconds;

        try
        {
            Populate(protoVessel, template, crew, situation);

            // The control point depends on where the crew are.
            List<ProtoPartSnapshot> snapshots = protoVessel.protoPartSnapshots;
            if (!keptReference)
                protoVessel.refTransform = snapshots[VesselTemplate.ControlPart(snapshots.Select(s => s.partPrefab).ToList(), i => snapshots[i].protoModuleCrew.Count > 0)].flightID;

            Place(protoVessel, template, situation);
        }
        catch
        {
            // Don't leave kerbals assigned to a vessel that never made it.
            Remove(protoVessel);
            throw;
        }

        Logger.Log($"Spawned {protoVessel.GetDisplayName()} {(situation.landed ? $"landed on {situation.body.bodyName} at {situation.latitude:F4}, {situation.longitude:F4}" : $"orbiting {situation.body.bodyName}")} in {timer.Elapsed.TotalMilliseconds:F1} ms ({prepared:F1} ms to prepare).");

        return protoVessel;
    }

    // Spawn many vessels, as many each frame as fit in the time, so big batches don't freeze the game.
    // Run it as a coroutine. The situations can be worked out as it goes, and spawned vessels are added
    // to the list as they appear. Stops at the first failure by throwing it.
    public static IEnumerator SpawnAll(VesselTemplate template, IEnumerable<SpawnSituation> situations, CrewSettings crew, List<ProtoVessel> spawned, double millisecondsPerFrame = 30)
    {
        Stopwatch frame = Stopwatch.StartNew();

        foreach (SpawnSituation situation in situations)
        {
            if (frame.Elapsed.TotalMilliseconds > millisecondsPerFrame)
            {
                yield return null;
                frame.Restart();
            }

            spawned.Add(Spawn(template, situation, crew));
        }
    }

    #region Removal

    // Undo a spawn: crew who were already on the roster go back to the astronaut complex,
    // kerbals hired for the vessel are let go, and the vessel disappears without a trace.
    // Not the active vessel, or one that's already gone.
    public static bool Remove(ProtoVessel spawned)
    {
        Vessel vessel = spawned?.vesselRef;
        if (spawned == null || vessel != null && (vessel.state == Vessel.State.DEAD || vessel == FlightGlobals.ActiveVessel))
            return false;

        // Once the vessel exists, the game saves it afresh, so it holds the record the save has now.
        ProtoVessel protoVessel = vessel?.protoVessel ?? spawned;
        KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;

        foreach (ProtoCrewMember crew in (vessel != null ? vessel.GetVesselCrew() : protoVessel.GetVesselCrew()).ToList())
        {
            if (vessel != null && vessel.loaded)
                foreach (Part part in vessel.parts)
                    if (part.protoModuleCrew.Contains(crew))
                        part.RemoveCrewmember(crew);

            foreach (ProtoPartSnapshot snapshot in protoVessel.protoPartSnapshots)
                snapshot.RemoveCrew(crew);

            protoVessel.RemoveCrew(crew);
            crew.seatIdx = -1;

            if (hiredKerbals.Remove(crew.name))
                roster.Remove(crew);
            else
                crew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
        }

        HighLogic.CurrentGame.flightState?.protoVessels.Remove(protoVessel);
        vessel?.Die();

        return true;
    }

    #endregion

    #region Identity

    // Give the vessel and its parts fresh identities, recording the persistent IDs that changed.
    // Returns whether the vessel's reference transform part was found and kept.
    private static bool MakeUnique(ConfigNode vesselNode, Dictionary<uint, uint> changedPIDs)
    {
        Game game = HighLogic.CurrentGame;
        uint missionID = (uint)Guid.NewGuid().GetHashCode();
        uint launchID = game.launchID++;
        HashSet<uint> usedPIDs = new HashSet<uint>();
        HashSet<uint> usedFlightIDs = new HashSet<uint>(game.flightState.protoVessels.SelectMany(v => v.protoPartSnapshots).Select(p => p.flightID));

        uint.TryParse(vesselNode.GetValue("ref"), out uint oldReference);
        uint newReference = 0;

        vesselNode.SetValue("pid", Guid.NewGuid().ToString("N"), true);
        vesselNode.SetValue("persistentId", 0, true); // 0 means the ProtoVessel constructor assigns a new one.

        foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
        {
            uint.TryParse(partNode.GetValue("uid"), out uint oldFlightID);
            // What ShipConstruction.GetUniqueFlightID does, without searching the whole save every time.
            uint flightID;
            do flightID = (uint)Guid.NewGuid().GetHashCode();
            while (flightID == 0 || !usedFlightIDs.Add(flightID));

            if (oldReference != 0 && oldFlightID == oldReference && newReference == 0)
                newReference = flightID;

            partNode.SetValue("uid", flightID, true);
            partNode.SetValue("mid", missionID, true);
            partNode.SetValue("launchID", launchID, true);

            // Always get a new PID. If the part had a PID before, store the change.
            uint.TryParse(partNode.GetValue("persistentId"), out uint oldPID);
            uint newPID;
            do newPID = FlightGlobals.GetUniquepersistentId();
            while (!usedPIDs.Add(newPID));

            partNode.SetValue("persistentId", newPID, true);
            if (oldPID != 0 && !changedPIDs.ContainsKey(oldPID))
                changedPIDs.Add(oldPID, newPID);

            // Crew are added separately. Cloned crew would be in two places at once.
            partNode.RemoveValues("crew");
        }

        vesselNode.SetValue("ref", newReference, true);

        return newReference != 0;
    }

    #endregion

    #region Robotics

    private static IEnumerable<ConfigNode> Modules(ConfigNode vesselNode, string name) =>
        vesselNode.GetNodes("PART").SelectMany(part => part.GetNodes("MODULE")).Where(module => module.GetValue("name") == name);

    // Robotics controllers reference the parts they control by persistent ID.
    private static void UpdateRoboticsReferences(ConfigNode vesselNode, Dictionary<uint, uint> changedPIDs)
    {
        foreach (ConfigNode module in Modules(vesselNode, "ModuleRoboticController"))
            foreach (ConfigNode actionOrAxis in new[] { "CONTROLLEDAXES", "CONTROLLEDACTIONS" }.SelectMany(list => module.GetNode(list)?.GetNodes() ?? new ConfigNode[0]))
            {
                IEnumerable<ConfigNode.Value> ids = actionOrAxis.values.Cast<ConfigNode.Value>().Where(v => v.name == "persistentId").Take(1)
                    .Concat((actionOrAxis.GetNode("SYMPARTS")?.values ?? new ConfigNode.ValueList()).Cast<ConfigNode.Value>().Where(v => v.name == "symPersistentId"));

                foreach (ConfigNode.Value id in ids)
                    if (uint.TryParse(id.value, out uint originalPID) && changedPIDs.TryGetValue(originalPID, out uint newPID))
                        id.value = newPID.ToString();
            }
    }

    #endregion

    #region Brakes

    // The same as the stock mission spawner's "brakes on" option.
    private static void SetBrakes(ConfigNode vesselNode)
    {
        List<ConfigNode> brakes = Modules(vesselNode, "ModuleWheelBrakes").ToList();
        if (brakes.Count == 0)
            return;

        // Persisted, so it overrides the action group when the wheel starts.
        foreach (ConfigNode module in brakes)
            module.SetValue("brakeInput", 1, true);

        (vesselNode.GetNode("ACTIONGROUPS") ?? vesselNode.AddNode("ACTIONGROUPS")).SetValue(nameof(KSPActionGroup.Brakes), "True, 0", true);
    }

    #endregion

    #region Launch Clamps

    private static bool IsLaunchClamp(string partName)
    {
        Part prefab = PartLoader.getPartInfoByName(partName)?.partPrefab;
        return prefab != null && prefab.HasModuleImplementing<LaunchClamp>();
    }

    // Remove launch clamps and anything attached to them, fixing up the part indices of what's left.
    private static void RemoveLaunchClamps(ConfigNode vesselNode)
    {
        ConfigNode[] parts = vesselNode.GetNodes("PART");
        bool[] remove = new bool[parts.Length];
        bool any = false;

        // Parents always come before their children, so one pass catches whole branches.
        for (int i = 1; i < parts.Length; i++)
        {
            int.TryParse(parts[i].GetValue("parent"), out int parent);
            remove[i] = IsLaunchClamp(parts[i].GetValue("name")) || parent >= 0 && parent < i && remove[parent];
            any |= remove[i];
        }

        if (!any)
            return;

        int[] newIndex = new int[parts.Length];
        for (int i = 0, next = 0; i < parts.Length; i++)
            newIndex[i] = remove[i] ? -1 : next++;

        for (int i = 0; i < parts.Length; i++)
        {
            if (remove[i])
            {
                vesselNode.nodes.Remove(parts[i]);
                continue;
            }

            ConfigNode part = parts[i];

            if (int.TryParse(part.GetValue("parent"), out int parent) && parent >= 0 && parent < parts.Length)
                part.SetValue("parent", Math.Max(newIndex[parent], 0));

            // Symmetry counterparts: drop the removed ones.
            List<string> symmetry = part.GetValues("sym").ToList();
            part.RemoveValues("sym");
            foreach (string sym in symmetry)
                if (int.TryParse(sym, out int index) && index >= 0 && index < parts.Length && newIndex[index] >= 0)
                    part.AddValue("sym", newIndex[index]);

            // Attach nodes: "id, index[,mesh]". Nodes that pointed at removed parts become empty.
            foreach (ConfigNode.Value value in part.values)
            {
                if (value.name != "attN" && value.name != "srfN")
                    continue;

                string[] fields = value.value.Split(',');
                if (fields.Length < 2 || !int.TryParse(fields[1].Trim(), out int index) || index < 0 || index >= parts.Length)
                    continue;

                fields[1] = " " + newIndex[index];
                value.value = string.Join(",", fields);
            }
        }
    }

    #endregion

    #region Placement

    // Take a detached proto vessel and add it to the world, in the correct orientation and position.
    // The result is an unloaded vessel that loads properly when it comes in range.
    private static void Place(ProtoVessel protoVessel, VesselTemplate template, SpawnSituation situation)
    {
        CelestialBody body = situation.body;
        double UT = Planetarium.GetUniversalTime();

        protoVessel.launchTime = UT;
        protoVessel.lastUT = UT;
        protoVessel.missionTime = 0;
        protoVessel.distanceTraveled = 0;
        protoVessel.launchedFrom = "";
        protoVessel.landedAt = "";
        protoVessel.displaylandedAt = "";
        protoVessel.PQSminLevel = 0;
        protoVessel.PQSmaxLevel = 0;
        protoVessel.skipGroundPositioningForDroppedPart = false;

        // The root part has no rotation relative to itself.
        Quaternion referenceRelative = protoVessel.protoPartSnapshots.Find(s => s.flightID == protoVessel.refTransform)?.rotation ?? Quaternion.identity;
        (Vector3d position, Quaternion rotation) = GetPose(template, situation, referenceRelative, out double height, out bool splashed);
        bool landed = situation.landed;

        body.GetLatLonAlt(position, out protoVessel.latitude, out protoVessel.longitude, out protoVessel.altitude);
        protoVessel.height = (float)height;
        protoVessel.normal = landed ? Quaternion.Inverse(rotation) * body.GetSurfaceNVector(situation.latitude, situation.longitude) : Vector3.up;
        protoVessel.situation = !landed ? Placement.OrbitSituation(situation.orbit) : splashed ? Vessel.Situations.SPLASHED : Vessel.Situations.LANDED;
        protoVessel.landed = landed && !splashed;
        protoVessel.splashed = splashed;
        protoVessel.skipGroundPositioning = splashed;
        protoVessel.vesselSpawning = landed;

        // Landed vessels still have an orbit, which is just the ground moving under them.
        protoVessel.orbitSnapShot = new OrbitSnapshot(landed ? Placement.OrbitFromWorldState(body, position, body.getRFrmVel(position), UT) : situation.orbit);

        // Unloaded vessels keep their rotation relative to the body.
        protoVessel.rotation = Quaternion.Inverse(body.bodyTransform.rotation) * rotation;

        // Add to the game. Load creates the (unloaded) vessel. The editor has nowhere to put one,
        // so there it's only in the save until the game goes to flight or the tracking station.
        HighLogic.CurrentGame.flightState.protoVessels.Add(protoVessel);
        if (!HighLogic.LoadedSceneIsEditor)
            protoVessel.Load(HighLogic.CurrentGame.flightState);

        if (protoVessel.vesselRef != null)
            GameEvents.onNewVesselCreated.Fire(protoVessel.vesselRef);
    }

    // Where a vessel would appear, right now, and how it would be turned: its root part's world position
    // and rotation. The part it's controlled from isn't known until the crew are aboard, so this is a
    // good guess rather than a promise. For previews, and for keeping vessels clear of each other.
    public static (Vector3d position, Quaternion rotation) Pose(VesselTemplate template, SpawnSituation situation) =>
        GetPose(template, situation, template.ReferenceRotation, out _, out _);

    private static (Vector3d, Quaternion) GetPose(VesselTemplate template, SpawnSituation situation, Quaternion referenceRelative, out double height, out bool splashed)
    {
        CelestialBody body = situation.body;
        Quaternion toRoot = Quaternion.Inverse(referenceRelative);

        if (!situation.landed)
        {
            double UT = Planetarium.GetUniversalTime();
            height = -1;
            splashed = false;
            return (situation.orbit.getPositionAtUT(UT), (situation.rotation ?? Placement.Prograde(situation.orbit, UT)) * toRoot);
        }

        double terrain = SurfaceAltitude(body, situation.latitude, situation.longitude);
        splashed = body.ocean && terrain < 0;

        Quaternion frame = Placement.SurfaceFrame(body, situation.latitude, situation.longitude, situation.heading);
        Quaternion rotation = situation.rotation * toRoot ?? FaceHeading(frame * template.UprightRotation, referenceRelative, frame);

        // Lift the vessel so its lowest part clears the ground. KSP puts it down properly
        // when it goes off rails, but it should look right before then too.
        height = template.HeightAboveBottom(Quaternion.Inverse(frame) * rotation) + 0.5;
        double altitude = (splashed ? 0 : terrain) + height;

        return (body.GetWorldSurfacePosition(situation.latitude, situation.longitude, altitude), rotation);
    }

    // The height of whatever's there to stand on. The terrain height doesn't include things like
    // the runway and buildings, which are several metres higher at KSC, so when the scenery is
    // loaded, ask it instead.
    private static double SurfaceAltitude(CelestialBody body, double latitude, double longitude)
    {
        if (body.pqsController == null)
            return 0;

        double terrain = body.TerrainAltitude(latitude, longitude, allowNegative: true);

        if (HighLogic.LoadedSceneIsFlight && body == FlightGlobals.currentMainBody)
        {
            const float height = 3000;
            Vector3d up = body.GetSurfaceNVector(latitude, longitude);
            Vector3d origin = body.GetWorldSurfacePosition(latitude, longitude, Math.Max(terrain, 0) + height);

            if (Physics.Raycast(origin, -up, out RaycastHit hit, height * 2, 1 << 15, QueryTriggerInteraction.Ignore))
                terrain = Math.Max(terrain, body.GetAltitude(hit.point));
        }

        return terrain;
    }

    // Turn a vessel about the local vertical so that its nose points along the frame's heading.
    // Vessels that point straight up, like rockets, are left alone.
    private static Quaternion FaceHeading(Quaternion worldRotation, Quaternion referenceRelative, Quaternion frame)
    {
        Vector3 up = frame * Vector3.up;
        Vector3 nose = worldRotation * referenceRelative * Vector3.up;
        Vector3 horizontal = Vector3.ProjectOnPlane(nose, up);

        if (horizontal.magnitude < 0.5f)
            return worldRotation;

        float angle = Vector3.SignedAngle(horizontal, frame * Vector3.forward, up);
        return Quaternion.AngleAxis(angle, up) * worldRotation;
    }

    #endregion

    #region Crew

    // The listed kerbals first, then as many more as the mode wants, the first aboard a pilot.
    private static void Populate(ProtoVessel protoVessel, VesselTemplate template, CrewSettings settings, SpawnSituation situation)
    {
        List<ProtoCrewMember> listed = settings.kerbals ?? new List<ProtoCrewMember>();
        HashSet<string> originalRoster = new HashSet<string>(HighLogic.CurrentGame.CrewRoster.kerbals.Keys);

        // Every empty seat, command parts first so the pilot ends up somewhere they can fly from. The parts are in
        // top-down order, so the first command part is also where the stock game would put the pilot.
        List<(ProtoPartSnapshot part, int seat)> seats = protoVessel.protoPartSnapshots
            .Where(p => VesselTemplate.Crewable(p.partPrefab))
            .OrderBy(p => p.partPrefab.HasModuleImplementing<ModuleCommand>() ? 0 : 1)
            .SelectMany(p => Enumerable.Range(p.protoModuleCrew.Count, p.partPrefab.CrewCapacity - p.protoModuleCrew.Count).Select(seat => (p, seat)))
            .ToList();

        for (int i = 0; i < Math.Min(seats.Count, Math.Max(listed.Count, template.Seats(settings.mode))); i++)
        {
            (ProtoPartSnapshot part, int seat) = seats[i];
            ProtoCrewMember crewMember = i < listed.Count ? listed[i] : NextKerbal(settings.onlyNewKerbals, i == 0 ? KerbalRoster.pilotTrait : null);
            if (crewMember == null)
                return;

            // Set any newly hired kerbals to max level, but don't mess with already existing kerbals.
            if (!originalRoster.Contains(crewMember.name))
            {
                KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel());
                hiredKerbals.Add(crewMember.name);
            }

            crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
            crewMember.seatIdx = seat;
            CreateLogEntry(crewMember, situation);

            part.protoModuleCrew.Add(crewMember);
            part.protoCrewNames.Add(crewMember.name);
            protoVessel.AddCrew(crewMember);
        }
    }

    // The next kerbal at the astronaut complex with the trait, if any trait will do, or else a new hire with it.
    private static ProtoCrewMember NextKerbal(bool onlyNew, string trait)
    {
        KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
        ProtoCrewMember kerbal = onlyNew ? null : roster.Kerbals(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available).FirstOrDefault(k => trait == null || k.trait == trait);
        if (kerbal == null)
        {
            kerbal = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
            if (trait != null)
                KerbalRoster.SetExperienceTrait(kerbal, trait);
        }

        return kerbal;
    }

    // Create the initial log entry that would otherwise be missing.
    private static void CreateLogEntry(ProtoCrewMember crewMember, SpawnSituation situation)
    {
        CelestialBody body = situation.body;
        FlightLog.EntryType entryType = situation.landed ? FlightLog.EntryType.Land : Placement.OrbitSituation(situation.orbit) switch
        {
            Vessel.Situations.ESCAPING => FlightLog.EntryType.Escape,
            Vessel.Situations.SUB_ORBITAL when body.atmosphere && situation.orbit.ApA < body.atmosphereDepth => FlightLog.EntryType.Flight,
            Vessel.Situations.SUB_ORBITAL => FlightLog.EntryType.Suborbit,
            _ => FlightLog.EntryType.Orbit,
        };

        crewMember.flightLog?.AddEntryUnique(entryType, body.name);
    }

    #endregion
}
