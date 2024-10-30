using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LazySpawner
{
    class CraftParser
    {
        private readonly StringBuilder sb = new StringBuilder();

        // Refs used in stock functions. Here to save GC.
        private string nodeID = string.Empty;
        private string attnPartID = string.Empty;
        private Vector3 attnPos = Vector3.zero;

        private readonly Dictionary<uint, int> craftIdLookupCraftFile = new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> craftIdLookupProto = new Dictionary<uint, int>();
        private readonly List<PartConstruct> partsCraftFile = new List<PartConstruct>();

        public class PartConstruct
        {
            public ProtoPartSnapshot snapshot;
            public ConfigNode partNode;
            public List<int> links;
            public List<uint> symmetryCounterparts;
            public List<string> attachNodes;
            public string srfAttachNode;
        }

        private void SortParts(PartConstruct info, Dictionary<uint, int> visitedParts, List<ProtoPartSnapshot> sortedParts)
        {
            if (visitedParts.ContainsKey(info.snapshot.craftID))
                return;

            int index = sortedParts.Count;
            visitedParts.Add(info.snapshot.craftID, index);
            sortedParts.Add(info.snapshot);

            foreach (int link in info.links)
            {
                PartConstruct child = partsCraftFile[link];
                child.snapshot.parentIdx = index;
                SortParts(child, visitedParts, sortedParts);
            }
        }

        private bool TryCreateAttachNode(string value, out AttachNodeSnapshot attachNodeSnapshot)
        {
            string attnMeshName = string.Empty;
            attachNodeSnapshot = null;

            if (!KSPUtil.GetAttachNodeInfo(value, ref nodeID, ref attnPartID, ref attnPos, ref attnMeshName))
                return false;

            if (nodeID == "Null" || !TryIndexFromCID(attnPartID, out int attNIndex))
                return false;

            sb.Clear();
            sb.Append(nodeID).Append(",").Append(attNIndex);

            if (attnMeshName != string.Empty)
                sb.Append(",").Append(attnMeshName);

            attachNodeSnapshot = new AttachNodeSnapshot(sb.ToString());
            return true;
        }

        private bool TryIndexFromCID(string nameAndCID, out int index)
        {
            index = -1;
            if (!uint.TryParse(nameAndCID.Split('_').Last(), out uint linkCID))
                return false;

            if (linkCID == 0)
                return false;

            return craftIdLookupProto.TryGetValue(linkCID, out index) && index < craftIdLookupProto.Values.Count;
        }

        private bool TryGetCraftID(string nameAndCID, out uint craftID)
        {
            int num = nameAndCID.IndexOf('_');
            return uint.TryParse(nameAndCID.Substring(num + 1, nameAndCID.Length - num - 1), out craftID);
        }

        private bool TryGetCraftFileIndex(string nameAndCID, out int index)
        {
            index = -1;
            if (!TryGetCraftID(nameAndCID, out uint craftID))
                return false;

            return craftIdLookupCraftFile.TryGetValue(craftID, out index) && index < craftIdLookupCraftFile.Values.Count;
        }

        private void EstablishReferenceTransform(ProtoVessel protoVessel)
        {
            // The stock game finds the control source by recursively following the part tree down from the root.
            // But since the part snapshots are sorted in top-down order, the result should be the same.

            ProtoPartSnapshot firstControlPart = null;
            ProtoPartSnapshot firstCrewedPart = null;

            foreach (ProtoPartSnapshot snapshot in protoVessel.protoPartSnapshots)
            {
                if (firstControlPart == null && snapshot.partPrefab.isControlSource > Vessel.ControlLevel.NONE)
                {
                    firstControlPart = snapshot;
                    if (firstControlPart == protoVessel.protoPartSnapshots[0])
                        break;
                }

                if (snapshot.partPrefab.CrewCapacity > 0 && snapshot.protoModuleCrew.Count > 0 && snapshot.partPrefab.isControlSource > Vessel.ControlLevel.NONE)
                {
                    firstCrewedPart = snapshot;
                    break;
                }
            }

            ProtoPartSnapshot controlPart = firstCrewedPart ?? firstControlPart ?? protoVessel.protoPartSnapshots[0];
            if (controlPart.flightID == default)
                controlPart.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);

            protoVessel.refTransform = controlPart.flightID;
        }

        private PartConstruct FindCrewOrControlPart(PartConstruct part, ref PartConstruct firstControlSource)
        {
            // Returns first crew part or null if not found, while storing the first control source found in firstControlSource.

            if (firstControlSource == null && part.snapshot.partPrefab.isControlSource > Vessel.ControlLevel.NONE)
                firstControlSource = part;

            if (part.snapshot.partPrefab.CrewCapacity > 0 && part.snapshot.protoModuleCrew.Count > 0 && part.snapshot.partPrefab.isControlSource > Vessel.ControlLevel.NONE)
                return part;

            foreach (int child in part.links)
                if (FindCrewOrControlPart(partsCraftFile[child], ref firstControlSource) != null)
                    return part;

            return null;
        }

        public ProtoVessel CraftNodeToProtoVessel(ConfigNode craftNode)
        {
            // Create a proto vessel.

            // can't include directly because there is false overlap in value names that causes exceptions
            //ProtoVessel protoVessel = new ProtoVessel(craftNode, HighLogic.CurrentGame);

            ConfigNode dummyProtoVesselNode = new ConfigNode();
            ProtoVessel protoVessel = new ProtoVessel(dummyProtoVesselNode, HighLogic.CurrentGame);

            string missionFlag = string.Empty;

            // Read craft file and translate.

            foreach (ConfigNode.Value value in craftNode.values)
            {
                switch (value.name)
                {
                    case "ship": // name
                        protoVessel.vesselName = value.value;
                        break;
                    case "persistentId": // name
                        uint.TryParse(value.value, out protoVessel.persistentId);
                        break;
                    //case "vesselType": // type
                    //    protoVessel.vesselType = (VesselType)Enum.Parse(typeof(VesselType), value.value);
                    //    break;
                    case "rot":
                        //protoVessel.rotation = KSPUtil.ParseQuaternion(value.value);
                        protoVessel.rotation = Quaternion.identity;
                        break;
                    case "OverrideDefault":
                        ParseExtensions.TryParseBoolArray(value.value, out protoVessel.OverrideDefault);
                        protoVessel.OverrideDefault = ParseExtensions.ArrayMinSize(Vessel.NumOverrideGroups, protoVessel.OverrideDefault);
                        break;
                    case "OverrideActionControl":
                        ParseExtensions.TryParseEnumIntArray(value.value, out protoVessel.OverrideActionControl);
                        protoVessel.OverrideActionControl = ParseExtensions.ArrayMinSize(Vessel.NumOverrideGroups, protoVessel.OverrideActionControl);
                        break;
                    case "OverrideAxisControl":
                        ParseExtensions.TryParseEnumIntArray(value.value, out protoVessel.OverrideAxisControl);
                        protoVessel.OverrideAxisControl = ParseExtensions.ArrayMinSize(Vessel.NumOverrideGroups, protoVessel.OverrideAxisControl);
                        break;
                    case "OverrideGroupNames":
                        protoVessel.OverrideGroupNames = ParseExtensions.ArrayMinSize(Vessel.NumOverrideGroups, ParseExtensions.ParseArray(value.value, StringSplitOptions.None));
                        break;
                    case "missionFlag":
                        missionFlag = value.value;
                        break;
                    default:
                        break;
                }
            }

            protoVessel.skipGroundPositioning = false;
            protoVessel.skipGroundPositioningForDroppedPart = false;
            protoVessel.vesselSpawning = false;

            protoVessel.CoM = Vector3.zero;

            // stage must be set after part snapshots are loaded. could do the same with CoM if it's needed, but I suspect not.
            // this reminds me I can get missionFlag from craft file. do parts have their own flags?

            // I could have three steps to creating the protovessel:
            // - init
            // - load parts
            // - post-load parts
            // - apply sit
            // - post-sit
            // sit and load parts could be interchangeable?

            protoVessel.stage = 0; // stg

            //for (int k = 0; k < protoVessel.protoPartSnapshots.Count; k++)
            //{
            //    ProtoPartSnapshot protoPartSnapshot = protoVessel.protoPartSnapshots[k];
            //    protoPartSnapshot.flagURL = ((shipOut.missionFlag != string.Empty) ? shipOut.missionFlag : newMission.flagURL);
            //    if (protoPartSnapshot.inverseStageIndex > num6)
            //    {
            //        num6 = protoPartSnapshot.inverseStageIndex;
            //    }
            //}
            //protoVessel.stage = num6 + 1;

            protoVessel.persistent = false;
            protoVessel.refTransform = 0u; // "ref", refTransform, uid of control source. should use root until below code is usable.


            //if (localRoot.isControlSource == Vessel.ControlLevel.NONE)
            //{
            //    Part part2 = findFirstCrewablePart(ship.parts[0]);
            //    vessel.SetReferenceTransform(part2 ?? findFirstControlSource(vessel) ?? localRoot);
            //}
            //else
            //{
            //    vessel.SetReferenceTransform(localRoot);
            //}

            protoVessel.wasControllable = true; // ctrl


            protoVessel.GroupOverride = 0;

            protoVessel.altimeterDisplayState = AltimeterDisplayState.ASL; // altDisplayState

            // could set camera state values here if required.
            // a default orbit could be set here if required.

            // Check for invalid parts.

            //HashSet<string> hashSet = new HashSet<string>();

            //foreach (ConfigNode configNode in craftNode.nodes)
            //{
            //    string partName = KSPUtil.GetPartName(configNode.GetValue("part"));
            //    AvailablePart availablePart = PartLoader.getPartInfoByName(partName);

            //    if ((availablePart == null || !availablePart.partPrefab) && !hashSet.Contains(partName))
            //        hashSet.Add(partName);
            //}

            // Load parts.

            Game game = HighLogic.CurrentGame;
            string partNameAndID = string.Empty;
            string partName = string.Empty;
            string craftID = string.Empty;
            ConfigNode dummyPartNode = new ConfigNode();

            int count = -1;
            craftIdLookupCraftFile.Clear();
            partsCraftFile.Clear();

            foreach (ConfigNode partNode in craftNode.nodes)
            {
                if (partNode.name != "PART")
                    continue;

                if (!partNode.TryGetValue("part", ref partNameAndID) || string.IsNullOrEmpty(partNameAndID))
                    continue;

                KSPUtil.GetPartInfo(partNameAndID, ref partName, ref craftID); // name

                // Perhaps I should create ConfigNodes here?
                // I believe it's slightly faster to create a snapshot directly.
                // But it relies on making sure to account for any bad/missed creations/events/adds/assignments 
                // after I pass the dummy node to the snapshot constructor.

                ProtoPartSnapshot snapshot = new ProtoPartSnapshot(dummyPartNode, protoVessel, game);
                count++;
                partsCraftFile.Add(new PartConstruct()
                {
                    snapshot = snapshot,
                    partNode = partNode,
                    links = new List<int>(),
                    symmetryCounterparts = new List<uint>(),
                    attachNodes = new List<string>(),
                    srfAttachNode = string.Empty,
                });

                // The ConfigNode constructor for ProtoPartSnapshot always results in either the original or a new PID, we're not using it.
                FlightGlobals.PersistentUnloadedPartIds.Remove(snapshot.persistentId);
                snapshot.persistentId = 0u;

                snapshot.partName = partName;
                snapshot.partInfo = PartLoader.getPartInfoByName(snapshot.partName);
                snapshot.craftID = uint.Parse(craftID); // cid (a unique ID *within* the craft, not an ID *for* the craft)

                craftIdLookupCraftFile.Add(snapshot.craftID, count);
            }

            // Node variables.
            string nodeID = string.Empty;
            string attnPartID = string.Empty;
            Vector3 attnPos = Vector3.zero;
            //Vector3 attnRot = Vector3.zero;
            //Vector3 attnActualPos = Vector3.zero;
            //Vector3 attnActualRot = Vector3.zero;

            foreach (PartConstruct info in partsCraftFile)
            {
                foreach (ConfigNode.Value value in info.partNode.values)
                {
                    // transformation notes
                    // attPos0 is the original localPosition
                    // attPos is the difference between the current localPosition and the original localPosition (referenceTransform.localPosition - selectedPart.attPos0)
                    // pos is the current transform.position

                    // rot is the current transform.rotation
                    // attRot0 is the original localRotation
                    // attRot is the difference between the current localRotation and the original localRotation (referenceTransform.localRotation * Quaternion.Inverse(selectedPart.attRot0))

                    // protopart.position and part.orgpos are the position of the part in the local space of the root part

                    // I need to know the root part BEFORE I start assigning positions. Which means after links and parents

                    switch (value.name)
                    {
                        case "persistentId":
                            if (!uint.TryParse(value.value, out uint pid) && !uint.TryParse(value.value.Split(',')[0].Trim(), out pid))
                                pid = 0u;

                            // We need to know the original pID because robotics controllers work by referencing pID.
                            info.snapshot.persistentId = pid;

                            break;
                        //case "attPos0":
                        //    info.snapshot.position = KSPUtil.ParseVector3(value.value) + KSPUtil.ParseVector3(info.partNode.GetValue("attPos"));
                        //    break;
                        case "pos":
                            info.snapshot.position = KSPUtil.ParseVector3(value.value);
                            break;
                        case "rot":
                            info.snapshot.rotation = KSPUtil.ParseQuaternion(value.value);
                            break;
                        //case "attRot0":
                        //    info.snapshot.rotation = KSPUtil.ParseQuaternion(value.value);
                        //    break;
                        //case "link":
                        //    if (TryIndexFromCID(value.value, out int linkIndex))
                        //        info.links.Add(linkIndex);
                        //    break;
                        //case "sym":
                        //    if (TryIndexFromCID(value.value, out int symIndex))
                        //        info.snapshot.symLinkIdxs.Add(symIndex);
                        //    break;
                        //case "attN":
                        //    if (TryCreateAttachNode(value.value, out AttachNodeSnapshot attachNodeSnapshot))
                        //        info.snapshot.attachNodes.Add(attachNodeSnapshot);
                        //    break;
                        //case "srfN":
                        //    if (TryCreateAttachNode(value.value, out AttachNodeSnapshot surfaceNodeSnapshot))
                        //        info.snapshot.srfAttachNode = surfaceNodeSnapshot;
                        case "link":
                            if (TryGetCraftFileIndex(value.value, out int linkID))
                                info.links.Add(linkID);
                            break;
                        case "sym":
                            if (TryGetCraftID(value.value, out uint symID))
                                info.symmetryCounterparts.Add(symID);
                            break;
                        case "attN":
                            info.attachNodes.Add(value.value);
                            break;
                        case "srfN":
                            info.srfAttachNode = value.value;
                            break;
                        case "mir":
                            //part.SetMirror(KSPUtil.ParseVector3(value.value));
                            info.snapshot.mirror = KSPUtil.ParseVector3(value.value);
                            break;
                        case "symMethod":
                            info.snapshot.symMethod = (SymmetryMethod)Enum.Parse(typeof(SymmetryMethod), value.value);
                            break;
                        case "istg":
                            info.snapshot.inverseStageIndex = int.Parse(value.value);
                            break;
                        case "resPri":
                            info.snapshot.resourcePriorityOffset = int.Parse(value.value);
                            break;
                        case "dstg":
                            info.snapshot.defaultInverseStage = int.Parse(value.value);
                            break;
                        case "sqor":
                            info.snapshot.seqOverride = int.Parse(value.value);
                            break;
                        case "sepI":
                            info.snapshot.separationIndex = int.Parse(value.value);
                            break;
                        case "sidx":
                            info.snapshot.inStageIndex = int.Parse(value.value);
                            break;
                        case "attm":
                            info.snapshot.attachMode = int.Parse(value.value);
                            break;
                        case "sameVesselCollision":
                            info.snapshot.sameVesselCollision = bool.Parse(value.value);
                            break;
                        case "modCost":
                            info.snapshot.moduleCosts = float.Parse(value.value);
                            break;
                        case "modMass":
                            info.snapshot.moduleMass = float.Parse(value.value);
                            break;
                        case "autostrutMode":
                            info.snapshot.autostrutMode = (Part.AutoStrutMode)Enum.Parse(typeof(Part.AutoStrutMode), value.value);
                            break;
                        case "rigidAttachment":
                            info.snapshot.rigidAttachment = bool.Parse(value.value);
                            break;
                        default:
                            break;
                    }
                }

                info.snapshot.shielded = false;
                info.snapshot.temperature = 300;
                info.snapshot.skinTemperature = 300;
                info.snapshot.skinUnexposedTemperature = 4;
                info.snapshot.staticPressureAtm = 0;
                info.snapshot.state = 0;
                info.snapshot.PreFailState = 0;
                info.snapshot.attached = true;
                info.snapshot.flagURL = missionFlag;


                // Ignored
                // moduleVariantName
                // moduleCargoStackableQuantity
                // mass
                // expt
                // rTrf (refTransformName, could be to do with control point direction?) (activeControlPointName? or maybe not)
                // crew

                // Set part modules.

                for (int k = 0; k < info.partNode.nodes.Count; k++)
                {
                    ConfigNode configNode = info.partNode.nodes[k];
                    switch (configNode.name)
                    {
                        case "MODULE":
                            info.snapshot.modules.Add(new ProtoPartModuleSnapshot(configNode));
                            break;
                        case "ACTIONS":
                            info.snapshot.partActions = new ConfigNode();
                            configNode.CopyTo(info.snapshot.partActions);
                            break;
                        case "PARTDATA":
                            info.snapshot.partData = new ConfigNode();
                            configNode.CopyTo(info.snapshot.partData);
                            break;
                        case "RESOURCE":
                            info.snapshot.resources.Add(new ProtoPartResourceSnapshot(configNode));
                            break;
                        case "VESSELNAMING":
                            info.snapshot.vesselNaming = new VesselNaming(configNode);
                            break;
                        case "EVENTS":
                            info.snapshot.partEvents = new ConfigNode();
                            configNode.CopyTo(info.snapshot.partEvents);
                            break;
                        case "EFFECTS":
                            info.snapshot.partEffects = new ConfigNode();
                            configNode.CopyTo(info.snapshot.partEffects);
                            break;
                    }
                }
            }

            int highestPriority = int.MinValue;
            int highestStage = int.MinValue;

            VesselNaming vesselNaming = null;

            foreach (PartConstruct info in partsCraftFile)
            {
                // Inverse assign parents.
                foreach (int link in info.links)
                    partsCraftFile[link].snapshot.parent = info.snapshot;

                // Find the highest priority name/type.
                if (info.snapshot.vesselNaming != null && info.snapshot.vesselNaming.namingPriority > highestPriority)
                {
                    highestPriority = info.snapshot.vesselNaming.namingPriority;
                    vesselNaming = info.snapshot.vesselNaming;
                }

                // Find the starting stage.
                if (info.snapshot.inverseStageIndex > highestStage)
                    highestStage = info.snapshot.inverseStageIndex;
            }

            // Set stage.
            protoVessel.stage = highestStage + 1;

            // Set vessel naming.
            if (vesselNaming != null)
            {
                protoVessel.vesselType = vesselNaming.vesselType;
                protoVessel.vesselName = vesselNaming.vesselName;
            }
            else
            {
                protoVessel.vesselType = VesselType.Ship;
            }

            // Now that we know parents, find the root part by starting anywhere and going up until I find a part with no parent.
            // It's not always at index 0.

            ProtoPartSnapshot current = partsCraftFile.First().snapshot;
            while (current.parent != null)
                current = current.parent;

            PartConstruct rootPart = partsCraftFile[craftIdLookupCraftFile[current.craftID]];
            Transform root = new GameObject("LazySpawnerTempRoot").transform;
            root.position = rootPart.snapshot.position;
            root.rotation = rootPart.snapshot.rotation;


            // Now that we know the root, assign positions.

            Quaternion inverse = Quaternion.Inverse(root.rotation);
            foreach (PartConstruct info in partsCraftFile)
            {
                info.snapshot.position = root.InverseTransformPoint(info.snapshot.position);
                info.snapshot.rotation = inverse * info.snapshot.rotation;
            }

            UnityEngine.Object.Destroy(root.gameObject);

            // protoPartSnapshots must be given in top-down order according to the part tree.
            // Do a recursive sort to establish the order.

            craftIdLookupProto.Clear();
            List<ProtoPartSnapshot> sortedParts = new List<ProtoPartSnapshot>();
            SortParts(rootPart, craftIdLookupProto, sortedParts);
            protoVessel.rootIndex = 0;

            Debug.Assert(craftIdLookupCraftFile.Count == sortedParts.Count && sortedParts.Count == partsCraftFile.Count,
                "[LazySpawner]: Critical error: the number of craft parts does not match the number of spawned parts.");

            // We now know the indices of the parts in the final sorted part list.
            // Therefore we can finally establish all links that rely on that order.

            foreach (PartConstruct info in partsCraftFile)
            {
                foreach (uint sym in info.symmetryCounterparts)
                    info.snapshot.symLinkIdxs.Add(craftIdLookupProto[sym]);

                foreach (string attachNodeValue in info.attachNodes)
                    if (TryCreateAttachNode(attachNodeValue, out AttachNodeSnapshot attachNodeSnapshot))
                        info.snapshot.attachNodes.Add(attachNodeSnapshot);

                if (info.srfAttachNode != string.Empty && TryCreateAttachNode(info.srfAttachNode, out AttachNodeSnapshot surfaceNodeSnapshot))
                    info.snapshot.srfAttachNode = surfaceNodeSnapshot;
                else
                    info.snapshot.srfAttachNode = new AttachNodeSnapshot(",-1");
            }

            protoVessel.protoPartSnapshots = sortedParts;

            // These can be empty for new vessels, but must not be null.
            protoVessel.actionGroups = new ConfigNode("ACTIONGROUPS");
            protoVessel.discoveryInfo = new ConfigNode("DISCOVERY");
            protoVessel.flightPlan = new ConfigNode("FLIGHTPLAN");
            protoVessel.ctrlState = new ConfigNode("CTRLSTATE");
            protoVessel.vesselModules = new ConfigNode("VESSELMODULES");

            // result nodes (orig vs spawned)

            // wrong state 1 vs 0
            // wrong group override 0 vs 1
            // wrong istg 1 vs -1

            // mass correct!
            // Action groups wrong? Stage and SAS are true and have time values
            // lat through com state values seem fine.
            // all other vessel modules are fine.

            // fixed

            // no refTransform
            // wrong parent
            // no syms (6 and 7 vs nothing)
            // wrong position (0,-1.2810792922973633,0.62297248840332031 vs 0,-0.11854171752929688,0.62297248840332031)
            // no robotics.
            // - when persistent ID is changed, the reference to the part in the AXIS in CONTROLLEDAXES must change
            // - ship construct line 679 (dictionary of changes)
            // - moduleroboticscontroller line 930 (event)
            // - AXIS and ACTIONS
            // - I only need to worry about part persistentId, module doesn't change.

            craftIdLookupCraftFile.Clear();
            craftIdLookupProto.Clear();
            partsCraftFile.Clear();

            // this doesn't actually free the memory of the dictionaries/lists (8 bytes per reference).
            // I would need to set null. But that would use more memory overall if I'm going to use them again in short order.

            EstablishReferenceTransform(protoVessel);

            return protoVessel;
        }
    }
}
