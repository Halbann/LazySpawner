# Changelog

## 1.2

### Placing

- Added spawning in the air. Alt-scroll sets the height and S-scroll sets the airspeed. Vessels start with engines in their first stage activated.
- Added the ability to trigger custom action groups on spawned vessels.
- Added V-click to spawn and switch to the new spawned vessel at the same time.
- Added placing on the sea.
- Changed placing to put the middle of the vessel under the mouse as opposed to its root part.
- Changed the placing hint to show only show the most relevant hints for the currently held keys.
- Fixed vessels spawning with no physics for several seconds.
- Fixed vessels spawning on top of a structure rather when trying to spawn inside.
- Fixed planes and other vessels on landing gear popping up and bouncing when spawned on the ground.
- Fixed craft popping up or falling when spawned on the ground.
- Fixed placement appearing at floor level when the mouse is on a building's roof.
- Fixed vessels spawning too high, and ghosts showing animated parts in the wrong pose.

### Orbit

- Added "Spread Ascending Nodes", which puts each vessel in its own orbital plane.
- The selected body now follows the focus in the map view and tracking station.
- Fixed orbiting vessels moving slightly on spawn.

### Craft list

- Added expansion craft to the Stock tab.
- Made the craft search text persistent.
- Fixed cloning a loaded vessel showing an out-of-date part count.

### Save Vessel

- Fixed saved craft keeping their engines running at the throttle they had, which took effect when they were launched or spawned.

### Other

- Changed the default shortcut to Alt+F11 from Alt+F.
- Fixed the camera mode changing when typing V in a text field.
- Fixed switched-off lights flashing on as spawned vessels appear.
- Improved performance and memory use while the Spawn Vessels screen is open.

### Backend

- Added airborne spawns, with `radarAltitude` and `airspeed` on `SpawnSituation.Landed`.
- Added `SpawnSituation.actionGroups`.
- Added `groundSearchAltitude` to `SpawnSituation.Landed`.
- Added `SpawnSituation.easeGravity`, which eases gravity in over 100 physics steps as a landed spawn unpacks, instead of stock's 16.
