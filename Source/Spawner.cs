using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LazySpawner
{
    public static class Spawner
    {
        //private static readonly Vessel.Situations[] landedSituation

        public struct SituationInfo
        {
            public Vessel.Situations situation;
            public Quaternion rotation;
            public Orbit orbit;
            public Coordinates coordinates;
        }

        public struct Coordinates
        {
            public double longitude;
            public double latitude;
            public double altitudeAGL;
        }

        public enum CrewMode
        {
            None,
            Pilot,
            FillCommand,
            FillAll,
        }

        // Perhaps this should actually be an explicit argument if this is going to be reusable.
        public static bool onlyHireNewKerbals = true;

        // Multi-spawn that takes care of memory better than repeated calls to Spawn.
        public static Vessel[] Spawn()
        {
            return null;
        }

        public static Vessel Spawn(string craftURL, SituationInfo situationInfo, CrewMode crewMode)
        {
            if (!File.Exists(craftURL))
                return null;

            // Parse craft file.
            ConfigNode craftNode = ConfigNode.Load(craftURL);
            if (craftNode == null)
                return null;

            // Create a proto vessel.
            CraftParser parser = new CraftParser();
            ProtoVessel protoVessel = parser.Parse(craftNode);
            Vessel vessel = Spawn(protoVessel, situationInfo, crewMode);

            parser.EstablishReferenceTransform(protoVessel); // Needs to be done after Spawn because it depends on crew.

            return vessel;
        }

        public static Vessel Spawn(Vessel original, SituationInfo situationInfo, CrewMode crewMode)
        {
            if (original == null)
                return null;

            return Spawn(VesselToProtoVessel(original), situationInfo, crewMode);
        }

        public static Vessel Spawn(ProtoVessel protoVessel, SituationInfo situationInfo, CrewMode crewMode)
        {
            if (protoVessel == null)
                return null;

            // need to scrub Ids from existing protovessels.

            Dictionary<uint, uint> changedPIDs = new Dictionary<uint, uint>();

            MakeUnique(protoVessel, changedPIDs);
            UpdateRoboticsReferences(protoVessel, changedPIDs);
            Populate(protoVessel, crewMode, situationInfo);

            Place(protoVessel, situationInfo);

            return protoVessel.vesselRef;
        }

        #region ProtoVessel

        private static ProtoVessel VesselToProtoVessel(Vessel vessel)
        {
            vessel.isBackingUp = true; // isBackingUp must be true for modules to be serialised.
            ProtoVessel oldProto = vessel.protoVessel;
            ProtoVessel proto = new ProtoVessel(vessel);
            vessel.protoVessel = oldProto; // Undo automatic re-assignment in ProtoVessel constructor.
            vessel.isBackingUp = false;

            // Make doubly sure that our new protovessel is no longer associated with the original vessel.
            proto.vesselRef = null;
            foreach (ProtoPartSnapshot part in proto.protoPartSnapshots)
                part.partRef = null;

            return proto;
        }

        private static void MakeUnique(ProtoVessel protoVessel, Dictionary<uint, uint> changedPIDs)
        {
            protoVessel.vesselID = Guid.NewGuid(); // pid
            //protoVessel.persistentId = FlightGlobals.GetUniquepersistentId();
            protoVessel.persistentId = FlightGlobals.CheckVesselpersistentId(protoVessel.persistentId, null, false, true);

            Game game = HighLogic.CurrentGame;
            uint mid = (uint)Guid.NewGuid().GetHashCode(); // mid
            uint launchId = game.launchID++;
            bool refFound = false;

            foreach (ProtoPartSnapshot snapshot in protoVessel.protoPartSnapshots)
            {
                snapshot.missionID = mid; // mid
                snapshot.launchID = launchId;

                if (!refFound && snapshot.flightID != 0 && snapshot.flightID == protoVessel.refTransform)
                {
                    refFound = true;
                    protoVessel.refTransform = snapshot.flightID = ShipConstruction.GetUniqueFlightID(game.flightState); // uid
                }
                else
                    snapshot.flightID = ShipConstruction.GetUniqueFlightID(game.flightState); // uid    

                // Always get a new PID. If the part had a PID before, store the change.

                uint originalPID = snapshot.persistentId;
                snapshot.persistentId = FlightGlobals.GetUniquepersistentId();
                if (originalPID != default)
                    changedPIDs.Add(originalPID, snapshot.persistentId);
            }
        }

        private static void Place(ProtoVessel protoVessel, SituationInfo situationInfo)
        {
            // Take a detached proto vessel and add it to the world, in the correct orientation and position.
            // The result is an unloaded vessel that should load properly when it comes in range.

            protoVessel.situation = situationInfo.situation;

            protoVessel.launchTime = Planetarium.GetUniversalTime();
            protoVessel.lastUT = Planetarium.GetUniversalTime();
            protoVessel.missionTime = 0;
            protoVessel.distanceTraveled = 0;
            protoVessel.launchedFrom = "LaunchPad";

            //Vector3d positionAtUT = vessel.orbit.getPositionAtUT(Planetarium.GetUniversalTime());
            //vessel.orbit.referenceBody.GetLatLonAlt(positionAtUT, out var lat, out var lon, out var alt);

            switch (situationInfo.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.FLYING:
                    protoVessel.longitude = situationInfo.coordinates.longitude;
                    protoVessel.latitude = situationInfo.coordinates.latitude;
                    protoVessel.altitude = 0; // alt
                    protoVessel.height = (float)situationInfo.coordinates.altitudeAGL; // hgt, heightFromTerrain
                    protoVessel.normal = Vector3.up; // nrm, terrainNormal

                    // Dummy orbit?
                    protoVessel.orbitSnapShot = new OrbitSnapshot(situationInfo.orbit.referenceBody);

                    if (situationInfo.situation == Vessel.Situations.SPLASHED)
                    {
                        protoVessel.splashed = true;
                        protoVessel.landed = true;
                    }
                    else if (situationInfo.situation == Vessel.Situations.FLYING)
                        protoVessel.landed = false;
                    else
                        protoVessel.landed = true;

                    protoVessel.landedAt = "";
                    protoVessel.displaylandedAt = "";

                    break;
                default:
                    protoVessel.orbitSnapShot = new OrbitSnapshot(situationInfo.orbit);
                    break;
            }

            protoVessel.PQSminLevel = situationInfo.orbit.referenceBody.pqsController.minLevel; // body dependent, post-sit
            protoVessel.PQSmaxLevel = situationInfo.orbit.referenceBody.pqsController.maxLevel; // body dependent, post-sit

            // Load.
            HighLogic.CurrentGame.flightState.protoVessels.Add(protoVessel);
            protoVessel.Load(HighLogic.CurrentGame.flightState);
            GameEvents.onNewVesselCreated.Fire(protoVessel.vesselRef);

            // Set pos/rot.
            Vessel vessel = protoVessel.vesselRef;

            switch (situationInfo.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.FLYING:
                    //protoVessel.vesselRef.SetPosition(situationInfo.coordinates.longitude, situationInfo.coordinates.latitude, situationInfo.coordinates.altitudeAGL);

                    CelestialBody body = situationInfo.orbit.referenceBody;
                    Coordinates coords = situationInfo.coordinates;

                    //Vector3d surfaceNVector = LatLon.GetSurfaceNVector(cf, vesselSituation.location.vesselGroundLocation.latitude, vesselSituation.location.vesselGroundLocation.longitude);

                    double terrainHeight = body.TerrainAltitude(coords.latitude, coords.longitude);
                    Vector3d position = body.GetRelSurfacePosition(coords.latitude, coords.longitude, terrainHeight + coords.altitudeAGL);
                    vessel.SetPosition(position);

                    Vector3d normalVector = body.GetSurfaceNVector(coords.latitude, coords.longitude);
                    Vector3 forwards = Vector3.ProjectOnPlane(vessel.transform.forward, normalVector);
                    var rot = Quaternion.LookRotation(forwards, normalVector);
                    vessel.SetRotation(rot);

                    protoVessel.rotation = Quaternion.Inverse(body.bodyTransform.rotation) * rot;
                    vessel.srfRelRotation = protoVessel.rotation;

                    // Add draw transform to dummy.
                    vessel.gameObject.AddComponent<DrawTransform>();

                    GameObject go = new GameObject();
                    go.AddComponent<DrawTransform>();
                    go.transform.position = body.GetWorldSurfacePosition(coords.latitude, coords.longitude, terrainHeight + coords.altitudeAGL);
                    go.transform.rotation = rot;
                    go.name = $"DrawTransform for {vessel.vesselName} at {coords.latitude} {coords.longitude} {coords.altitudeAGL}";

                    //vessel.vesselSpawning = true; // ?
                    vessel.PQSminLevel = 0;
                    vessel.PQSmaxLevel = 0;

                    break;
                default:
                    vessel.SetPosition(situationInfo.orbit.getPositionAtUT(Planetarium.GetUniversalTime()));
                    vessel.SetRotation(situationInfo.rotation, false);
                    break;
            }
        }

        #endregion

        #region Robotics

        private static void UpdateRoboticsReferences(ProtoVessel protoVessel, Dictionary<uint, uint> changedPIDs)
        {
            if (protoVessel == null || changedPIDs.Count < 1)
                return;

            ConfigNode symmetryNode = default;

            foreach (ProtoPartSnapshot part in protoVessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot module in part.modules)
                {
                    if (module.moduleName != "ModuleRoboticController")
                        continue;

                    bool foundAxes = false;
                    bool foundActions = false;

                    foreach (ConfigNode node in module.moduleValues.nodes)
                    {
                        bool check = (!foundAxes && (foundAxes = node.name == "CONTROLLEDAXES"))
                            || (!foundActions && (foundActions = node.name == "CONTROLLEDACTIONS"));

                        if (!check)
                            continue;

                        foreach (ConfigNode actionOrAxis in node.nodes)
                        {
                            UpdatePidField(actionOrAxis, "persistentId", changedPIDs);

                            if (!actionOrAxis.TryGetNode("SYMPARTS", ref symmetryNode))
                                continue;

                            foreach (ConfigNode.Value entry in symmetryNode.values)
                                UpdatePidField(symmetryNode, "symPersistentId", entry.value, changedPIDs);
                        }

                        if (foundAxes && foundActions)
                            break;
                    }

                    break;
                }
            }
        }

        private static void UpdatePidField(ConfigNode node, string name, Dictionary<uint, uint> changedPIDs)
        {
            string originalString = default;

            if (node.TryGetValue(name, ref originalString))
                UpdatePidField(node, name, originalString, changedPIDs);
        }

        private static void UpdatePidField(ConfigNode node, string name, string originalString, Dictionary<uint, uint> changedPIDs)
        {
            if (uint.TryParse(originalString, out uint originalPID) && changedPIDs.TryGetValue(originalPID, out uint newPID))
                node.SetValue(name, newPID.ToString());
        }

        #endregion

        #region Crew

        private static void Populate(ProtoVessel protoVessel, CrewMode crewMode, SituationInfo situationInfo)
        {
            if (crewMode == CrewMode.None)
                return;

            double UT = Planetarium.GetUniversalTime();
            protoVessel.crewedParts = 0;
            protoVessel.crewableParts = 0;
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            HashSet<string> originalRoster = roster.kerbals.Keys.ToHashSet();
            ProtoCrewMember.KerbalType crewType = ProtoCrewMember.KerbalType.Crew;

            // Because the parts are sorted in top down order, the first
            // part with crew capacity we come across should be the reference transform.

            foreach (ProtoPartSnapshot part in protoVessel.protoPartSnapshots)
            {
                // Skip non-crew parts.
                int capacity = part.partInfo.partPrefab.CrewCapacity;
                if (capacity < 1)
                    continue;

                protoVessel.crewableParts++;

                // Skip after filling one command seat if Pilot mode, but continue counting crewable parts.
                if (protoVessel.crewedParts > 0 && crewMode == CrewMode.Pilot)
                    continue;

                // Skip passenger parts if we're not filling all seats.
                bool isPassenger = !part.partInfo.partPrefab.HasModuleImplementing<ModuleCommand>();
                if (isPassenger && crewMode != CrewMode.FillAll)
                    continue;

                UnityEngine.Debug.Log($"[LazySpawner]: {part.partInfo.title} has {part.partInfo.partPrefab.CrewCapacity} seats.");
                protoVessel.crewedParts++;

                // Put a crew member in each seat.
                for (int i = 0; i < capacity; i++)
                {
                    ProtoCrewMember crewMember;
                    bool pilot = protoVessel.crewedParts == 1 && i == 0;

                    // The very first crew member should always be a pilot.
                    if (pilot)
                        crewMember = GetAvailableCrewWithTrait(onlyHireNewKerbals, KerbalRoster.pilotTrait);
                    else
                        crewMember = onlyHireNewKerbals ? roster.GetNewKerbal(crewType) : roster.GetNextOrNewKerbal(crewType);

                    // Set any newly hired kerbals to max level, but don't mess with already existing kerbals.
                    if (!originalRoster.Contains(crewMember.name))
                    {
                        KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel());
                        crewMember.UTaR = UT + (double)(UnityEngine.Random.Range(1f, 3f) * 86400f);
                    }

                    crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    crewMember.seatIdx = i;
                    CreateLogEntry(crewMember, situationInfo);

                    protoVessel.crew.Add(crewMember);
                    part.protoModuleCrew.Add(crewMember);
                    part.protoCrewNames.Add(crewMember.name);

                    UnityEngine.Debug.Log($"[LazySpawner]: {crewMember.name} has been assigned to {part.partInfo.title}.");

                    if (pilot && crewMode == CrewMode.Pilot)
                        break;
                }
            }
        }

        private static ProtoCrewMember GetAvailableCrewWithTrait(bool onlyNew, string trait)
        {
            // The same as GetNextOrNewKerbal, but with a certain trait like pilot, engineer, scientist.

            ProtoCrewMember crewMember = null;
            KerbalRoster crewRoster = HighLogic.CurrentGame.CrewRoster;

            if (!onlyNew)
            {
                IEnumerable<ProtoCrewMember> availableCrew = crewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew, new ProtoCrewMember.RosterStatus[] { ProtoCrewMember.RosterStatus.Available });
                foreach (ProtoCrewMember kerbal in availableCrew)
                    if (kerbal.trait == trait)
                        crewMember = kerbal;
            }

            if (crewMember == null)
            {
                crewMember = crewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                KerbalRoster.SetExperienceTrait(crewMember, trait);
            }

            return crewMember;
        }

        private static void CreateLogEntry(ProtoCrewMember crewMember, SituationInfo situationInfo)
        {
            // Create the initial log entry that would otherwise be missing.
            // IDK what purpose they serve exactly but might as well.

            FlightLog.EntryType entryType;

            switch (situationInfo.situation)
            {
                case Vessel.Situations.FLYING:
                    entryType = FlightLog.EntryType.Flight;
                    break;
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                    entryType = FlightLog.EntryType.Land;
                    break;
                case Vessel.Situations.ORBITING:
                    entryType = FlightLog.EntryType.Orbit;
                    break;
                case Vessel.Situations.SUB_ORBITAL:
                    entryType = FlightLog.EntryType.Suborbit;
                    break;
                case Vessel.Situations.ESCAPING:
                    entryType = FlightLog.EntryType.Escape;
                    break;
                default:
                    return;
            }

            crewMember?.flightLog?.AddEntryUnique(entryType, situationInfo.orbit.referenceBody.name);
        }

        #endregion
    }
}
