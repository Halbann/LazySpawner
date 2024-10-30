using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LazySpawner
{
    static class Spawner
    {
        public struct SituationInfo
        {
            public Orbit orbit;
            public Quaternion rotation;
        }

        // Multi-spawn that takes care of memory better than repeated calls to Spawn.
        public static Vessel[] Spawn()
        {
            return null;
        }

        public static Vessel Spawn(string craftURL, SituationInfo situationInfo)
        {
            if (!File.Exists(craftURL))
                return null;

            // Parse craft file.
            ConfigNode craftNode = ConfigNode.Load(craftURL);
            if (craftNode == null)
                return null;

            // Create a proto vessel.
            ProtoVessel protoVessel = new CraftParser().CraftNodeToProtoVessel(craftNode);
            //ProtoVessel protoVessel = CloneVessel(FlightGlobals.ActiveVessel);

            return Spawn(protoVessel, situationInfo);
        }

        public static Vessel Spawn(Vessel original, SituationInfo situationInfo)
        {
            if (original == null)
                return null;

            return Spawn(VesselToProtoVessel(original), situationInfo);
        }

        public static Vessel Spawn(ProtoVessel protoVessel, SituationInfo situationInfo)
        {
            if (protoVessel == null)
                return null;

            // need to scrub Ids from existing protovessels.

            Dictionary<uint, uint> changedpIDs = new Dictionary<uint, uint>();

            MakeUnique(protoVessel, changedpIDs);
            UpdateRoboticsReferences(protoVessel, changedpIDs);
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

        private static void MakeUnique(ProtoVessel protoVessel, Dictionary<uint, uint> changedpIDs)
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

                // does part persistentId need to be unique as well? or is it supposed to be the same as in the craft file?
                uint originalpID = snapshot.persistentId;
                snapshot.persistentId = FlightGlobals.CheckProtoPartSnapShotpersistentId(snapshot.persistentId, snapshot, false, true);
                if (originalpID != 0u && originalpID != snapshot.persistentId)
                    changedpIDs.Add(originalpID, snapshot.persistentId);
            }
        }

        private static void Place(ProtoVessel protoVessel, SituationInfo situationInfo)
        {
            protoVessel.situation = Vessel.Situations.ORBITING;
            protoVessel.orbitSnapShot = new OrbitSnapshot(situationInfo.orbit);

            protoVessel.launchTime = Planetarium.GetUniversalTime();
            protoVessel.lastUT = Planetarium.GetUniversalTime();
            protoVessel.splashed = false;
            protoVessel.missionTime = 0;
            protoVessel.distanceTraveled = 0;
            protoVessel.launchedFrom = "LaunchPad";
            protoVessel.landedAt = "";
            protoVessel.displaylandedAt = "";
            protoVessel.landed = false;

            //Vector3d positionAtUT = vessel.orbit.getPositionAtUT(Planetarium.GetUniversalTime());
            //vessel.orbit.referenceBody.GetLatLonAlt(positionAtUT, out var lat, out var lon, out var alt);

            protoVessel.latitude = 0; // lat
            protoVessel.longitude = 0; // lon
            protoVessel.altitude = 0; // alt
            protoVessel.height = 0; // hgt, heightFromTerrain
            protoVessel.normal = Vector3.up; // nrm, terrainNormal
            //protoVessel.rotation = Quaternion.identity; // rot, srfRelRotation, might be confusing this with editor rotation above?

            protoVessel.PQSminLevel = situationInfo.orbit.referenceBody.pqsController.minLevel; // body dependent, post-sit
            protoVessel.PQSmaxLevel = situationInfo.orbit.referenceBody.pqsController.maxLevel; // body dependent, post-sit

            // Load.
            HighLogic.CurrentGame.flightState.protoVessels.Add(protoVessel);
            protoVessel.Load(HighLogic.CurrentGame.flightState);
            GameEvents.onNewVesselCreated.Fire(protoVessel.vesselRef);

            // Set pos/rot.
            protoVessel.vesselRef.SetRotation(situationInfo.rotation, false);
            protoVessel.vesselRef.SetPosition(situationInfo.orbit.getPositionAtUT(Planetarium.GetUniversalTime()));
        }

        #endregion
        #region Robotics

        private static void UpdateRoboticsReferences(ProtoVessel protoVessel, Dictionary<uint, uint> changedpIDs)
        {
            if (protoVessel == null || changedpIDs.Count < 1)
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
                        if ((!foundAxes && (foundAxes = node.name == "CONTROLLEDAXES"))
                            || (!foundActions && (foundActions = node.name == "CONTROLLEDACTIONS")))
                        {
                            foreach (ConfigNode actionOrAxis in node.nodes)
                            {
                                UpdatePidField(actionOrAxis, "persistentId", changedpIDs);

                                if (actionOrAxis.TryGetNode("SYMPARTS", ref symmetryNode))
                                    UpdatePidField(symmetryNode, "symPersistentId", changedpIDs);
                            }
                        }

                        if (foundAxes && foundActions)
                            break;
                    }

                    break;
                }
            }
        }

        private static void UpdatePidField(ConfigNode node, string name, Dictionary<uint, uint> changedpIDs)
        {
            uint originalPID = default;

            if (node.TryGetValue(name, ref originalPID) && changedpIDs.TryGetValue(originalPID, out uint newPID))
                node.SetValue(name, newPID.ToString());
        }

        #endregion
    }
}
