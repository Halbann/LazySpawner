using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LazySpawner;

public enum CrewMode
{
    None,
    Pilot,
    FillCommand,
    FillAll,
}

public struct CrewSettings
{
    public CrewMode mode;
    public bool onlyNewKerbals;

    public CrewSettings(CrewMode mode, bool onlyNewKerbals)
    {
        this.mode = mode;
        this.onlyNewKerbals = onlyNewKerbals;
    }
}

// Stamps out copies of a VesselTemplate as new, unloaded vessels.
// The vessels load normally when they come into range of the active vessel.
public static class Spawner
{
    public static Vessel Spawn(VesselTemplate template, SpawnSituation situation, CrewSettings crew)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        if (HighLogic.CurrentGame?.flightState == null || FlightGlobals.fetch == null)
            throw new SpawnException("Vessels can only be spawned in flight or the tracking station.");

        // Work on a copy so that the template can be used again.
        ConfigNode node = template.node.CreateCopy();

        Dictionary<uint, uint> changedPIDs = new Dictionary<uint, uint>();
        bool keptReference = MakeUnique(node, changedPIDs);
        UpdateRoboticsReferences(node, changedPIDs);

        // The stock constructor registers the vessel's and parts' persistent IDs.
        ProtoVessel protoVessel = new ProtoVessel(node, HighLogic.CurrentGame);

        Populate(protoVessel, crew, situation);

        // The control point depends on where the crew are.
        if (!keptReference)
            EstablishReferenceTransform(protoVessel);

        Place(protoVessel, template, situation);

        Logger.Log($"Spawned {protoVessel.GetDisplayName()} {(situation.landed ? $"landed on {situation.body.bodyName} at {situation.latitude:F4}, {situation.longitude:F4}" : $"orbiting {situation.body.bodyName}")}.");

        return protoVessel.vesselRef;
    }

    #region Identity

    // Give the vessel and its parts fresh identities, recording the persistent IDs that changed.
    // Returns whether the vessel's reference transform part was found and kept.
    private static bool MakeUnique(ConfigNode vesselNode, Dictionary<uint, uint> changedPIDs)
    {
        Game game = HighLogic.CurrentGame;
        uint missionID = (uint)Guid.NewGuid().GetHashCode();
        uint launchID = game.launchID++;
        HashSet<uint> usedPIDs = new HashSet<uint>();

        uint.TryParse(vesselNode.GetValue("ref"), out uint oldReference);
        uint newReference = 0;

        vesselNode.SetValue("pid", Guid.NewGuid().ToString("N"), true);
        vesselNode.SetValue("persistentId", 0, true); // 0 means the ProtoVessel constructor assigns a new one.

        foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
        {
            uint.TryParse(partNode.GetValue("uid"), out uint oldFlightID);
            uint flightID = ShipConstruction.GetUniqueFlightID(game.flightState);

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

    // Robotics controllers reference the parts they control by persistent ID.
    private static void UpdateRoboticsReferences(ConfigNode vesselNode, Dictionary<uint, uint> changedPIDs)
    {
        if (changedPIDs.Count < 1)
            return;

        foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
        {
            foreach (ConfigNode moduleNode in partNode.GetNodes("MODULE"))
            {
                if (moduleNode.GetValue("name") != "ModuleRoboticController")
                    continue;

                foreach (string listName in new[] { "CONTROLLEDAXES", "CONTROLLEDACTIONS" })
                {
                    ConfigNode list = moduleNode.GetNode(listName);
                    if (list == null)
                        continue;

                    foreach (ConfigNode actionOrAxis in list.nodes)
                    {
                        UpdatePidValue(actionOrAxis.values.Cast<ConfigNode.Value>().FirstOrDefault(v => v.name == "persistentId"), changedPIDs);

                        ConfigNode symmetryNode = actionOrAxis.GetNode("SYMPARTS");
                        if (symmetryNode == null)
                            continue;

                        foreach (ConfigNode.Value entry in symmetryNode.values)
                            if (entry.name == "symPersistentId")
                                UpdatePidValue(entry, changedPIDs);
                    }
                }
            }
        }
    }

    private static void UpdatePidValue(ConfigNode.Value value, Dictionary<uint, uint> changedPIDs)
    {
        if (value != null && uint.TryParse(value.value, out uint originalPID) && changedPIDs.TryGetValue(originalPID, out uint newPID))
            value.value = newPID.ToString();
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

        Quaternion referenceRelative = ReferencePart(protoVessel)?.rotation ?? Quaternion.identity;
        Quaternion worldRotation;

        if (situation.landed)
        {
            double terrain = body.TerrainAltitude(situation.latitude, situation.longitude, allowNegative: true);
            bool splashed = body.ocean && terrain < 0;

            Quaternion frame = Placement.SurfaceFrame(body, situation.latitude, situation.longitude, situation.heading);
            worldRotation = FaceHeading(frame * template.uprightRotation, referenceRelative, frame);

            // Lift the vessel so its lowest part clears the ground. KSP puts it down properly
            // when it goes off rails, but it should look right before then too.
            Quaternion upright = Quaternion.Inverse(frame) * worldRotation;
            float heightAboveBottom = template.HeightAboveBottom(upright);
            double altitude = (splashed ? 0 : terrain) + heightAboveBottom + (splashed ? 0.5 : 1.5);

            protoVessel.latitude = situation.latitude;
            protoVessel.longitude = situation.longitude;
            protoVessel.altitude = altitude;
            protoVessel.height = (float)(altitude - Math.Max(terrain, 0));
            protoVessel.normal = Quaternion.Inverse(worldRotation) * body.GetSurfaceNVector(situation.latitude, situation.longitude);

            protoVessel.situation = splashed ? Vessel.Situations.SPLASHED : Vessel.Situations.LANDED;
            protoVessel.landed = !splashed;
            protoVessel.splashed = splashed;
            protoVessel.skipGroundPositioning = splashed;
            protoVessel.vesselSpawning = true;

            // Landed vessels still have an orbit, which is just the ground moving under them.
            Vector3d position = body.GetWorldSurfacePosition(situation.latitude, situation.longitude, altitude);
            Orbit orbit = Placement.OrbitFromWorldState(body, position, body.getRFrmVel(position), UT);
            protoVessel.orbitSnapShot = new OrbitSnapshot(orbit);
        }
        else
        {
            Orbit orbit = situation.orbit;
            Vector3d position = orbit.getPositionAtUT(UT);
            body.GetLatLonAlt(position, out protoVessel.latitude, out protoVessel.longitude, out protoVessel.altitude);

            worldRotation = situation.orbitRotation switch
            {
                OrbitRotation.Random => Random.rotation,
                OrbitRotation.Fixed => situation.worldRotation,
                _ => Placement.Prograde(orbit, UT, referenceRelative),
            };

            protoVessel.height = -1;
            protoVessel.normal = Vector3.up;
            protoVessel.situation = Placement.OrbitSituation(orbit);
            protoVessel.landed = false;
            protoVessel.splashed = false;
            protoVessel.skipGroundPositioning = false;
            protoVessel.vesselSpawning = false;
            protoVessel.orbitSnapShot = new OrbitSnapshot(orbit);
        }

        // Unloaded vessels keep their rotation relative to the body.
        protoVessel.rotation = Quaternion.Inverse(body.bodyTransform.rotation) * worldRotation;

        // Add to the game. Load creates the (unloaded) vessel.
        HighLogic.CurrentGame.flightState.protoVessels.Add(protoVessel);
        protoVessel.Load(HighLogic.CurrentGame.flightState);

        if (protoVessel.vesselRef != null)
            GameEvents.onNewVesselCreated.Fire(protoVessel.vesselRef);
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

    private static ProtoPartSnapshot ReferencePart(ProtoVessel protoVessel)
    {
        foreach (ProtoPartSnapshot snapshot in protoVessel.protoPartSnapshots)
            if (snapshot.flightID == protoVessel.refTransform)
                return snapshot;

        return protoVessel.protoPartSnapshots.Count > 0 ? protoVessel.protoPartSnapshots[protoVessel.rootIndex] : null;
    }

    #endregion

    #region Control

    // The same rules as launching: the root part if it can control the vessel,
    // otherwise the first crewed control part, then the first control part, then the root.
    private static void EstablishReferenceTransform(ProtoVessel protoVessel)
    {
        List<ProtoPartSnapshot> snapshots = protoVessel.protoPartSnapshots;
        ProtoPartSnapshot root = snapshots[protoVessel.rootIndex];
        ProtoPartSnapshot controlPart;

        if (IsControlSource(root))
            controlPart = root;
        else
        {
            // The snapshots are in top-down tree order, which is the same order the stock game searches in.
            controlPart = snapshots.FirstOrDefault(s => IsControlSource(s) && s.partPrefab.CrewCapacity > 0 && s.protoModuleCrew.Count > 0)
                ?? snapshots.FirstOrDefault(IsControlSource)
                ?? root;
        }

        protoVessel.refTransform = controlPart.flightID;
    }

    private static bool IsControlSource(ProtoPartSnapshot snapshot) =>
        snapshot.partPrefab != null && snapshot.partPrefab.isControlSource > Vessel.ControlLevel.NONE;

    #endregion

    #region Crew

    private static void Populate(ProtoVessel protoVessel, CrewSettings settings, SpawnSituation situation)
    {
        if (settings.mode == CrewMode.None)
            return;

        double UT = Planetarium.GetUniversalTime();
        KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
        HashSet<string> originalRoster = new HashSet<string>(roster.kerbals.Keys);
        bool pilotAssigned = false;

        // Skip non-crew parts, and external seats, which need a kerbal on EVA to sit in them.
        // Command parts come first so the pilot ends up somewhere they can fly from.
        // The parts are sorted in top down order, so the first command part is also where
        // the stock game would put the pilot.
        IEnumerable<ProtoPartSnapshot> crewable = protoVessel.protoPartSnapshots
            .Where(p => p.partPrefab.CrewCapacity > 0 && !p.partPrefab.HasModuleImplementing<KerbalSeat>())
            .OrderBy(p => p.partPrefab.HasModuleImplementing<ModuleCommand>() ? 0 : 1);

        foreach (ProtoPartSnapshot part in crewable)
        {
            int capacity = part.partPrefab.CrewCapacity;

            // Skip passenger parts if we're not filling all seats.
            bool isPassenger = !part.partPrefab.HasModuleImplementing<ModuleCommand>();
            if (isPassenger && settings.mode != CrewMode.FillAll)
                continue;

            // Put a crew member in each seat.
            for (int seat = part.protoModuleCrew.Count; seat < capacity; seat++)
            {
                // The very first crew member should always be a pilot.
                ProtoCrewMember crewMember = !pilotAssigned
                    ? GetAvailableCrewWithTrait(settings.onlyNewKerbals, KerbalRoster.pilotTrait)
                    : GetAvailableCrew(settings.onlyNewKerbals);

                if (crewMember == null)
                    return;

                pilotAssigned = true;

                // Set any newly hired kerbals to max level, but don't mess with already existing kerbals.
                if (!originalRoster.Contains(crewMember.name))
                    KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel());

                crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                crewMember.seatIdx = seat;
                CreateLogEntry(crewMember, situation);

                part.protoModuleCrew.Add(crewMember);
                part.protoCrewNames.Add(crewMember.name);
                protoVessel.AddCrew(crewMember);

                if (settings.mode == CrewMode.Pilot)
                    return;
            }
        }
    }

    private static ProtoCrewMember GetAvailableCrew(bool onlyNew)
    {
        KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
        return onlyNew ? roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew) : roster.GetNextOrNewKerbal(ProtoCrewMember.KerbalType.Crew);
    }

    // The same as GetNextOrNewKerbal, but with a certain trait like pilot, engineer, scientist.
    private static ProtoCrewMember GetAvailableCrewWithTrait(bool onlyNew, string trait)
    {
        KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;

        if (!onlyNew)
        {
            foreach (ProtoCrewMember kerbal in roster.Kerbals(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available))
                if (kerbal.trait == trait)
                    return kerbal;
        }

        ProtoCrewMember crewMember = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
        KerbalRoster.SetExperienceTrait(crewMember, trait);
        return crewMember;
    }

    // Create the initial log entry that would otherwise be missing.
    private static void CreateLogEntry(ProtoCrewMember crewMember, SpawnSituation situation)
    {
        FlightLog.EntryType entryType;

        if (situation.landed)
            entryType = FlightLog.EntryType.Land;
        else
        {
            switch (Placement.OrbitSituation(situation.orbit))
            {
                case Vessel.Situations.SUB_ORBITAL:
                    entryType = situation.body.atmosphere && situation.orbit.PeA < situation.body.atmosphereDepth && situation.orbit.ApA < situation.body.atmosphereDepth
                        ? FlightLog.EntryType.Flight : FlightLog.EntryType.Suborbit;
                    break;
                case Vessel.Situations.ESCAPING:
                    entryType = FlightLog.EntryType.Escape;
                    break;
                default:
                    entryType = FlightLog.EntryType.Orbit;
                    break;
            }
        }

        crewMember.flightLog?.AddEntryUnique(entryType, situation.body.name);
    }

    #endregion
}
