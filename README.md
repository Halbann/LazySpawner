A KSP mod for spawning vessels, lots of them if you like, from Flight or the Tracking Station. Pick a craft or clone a vessel, point at where you want it, and click.

It's a cheat, so it lives with the stock cheats: **Spawn Vessels**, under Cheats in the debug console (Alt+F12). **Alt+F** opens it straight there.

### Craft

- **Change…** lists every craft in the save and the game, newest first, with their thumbnails. Type to search. Crafts with missing parts are greyed out, with the missing parts in a tooltip.
- **Craft from anywhere:** copy a .craft file's path, from Explorer or Everything, quotes and all, and come back to the game. It's picked up from the clipboard. Pasting a path into the search box works too. Crafts from elsewhere stay in the list once used.
- **Cloning:** the top of the list copies the active vessel, or the selected vessel in the Tracking Station.
- **Stock…** picks with the stock craft browser instead.
- **Count** spawns as many copies as you like. Big batches are spread over several frames, so the game doesn't freeze.

### Situation

- **Place** lets you point at where you want them. See below.
- **Nearby** scatters vessels around the active vessel. In orbit they match its velocity. On the ground they're spread out around it.
- **Orbit** puts vessels in the orbit you describe, simply by altitude and inclination, or with every orbital element. Several vessels can be spread evenly around the orbit like a constellation, or flown in a tidy formation. **Match Active Vessel** and **Match Target** copy their orbits.
- **Launch Site** puts vessels on the runway, the launch pad, or one of Making History's sites, facing the right way, side by side if there are several.

A sentence under the settings says exactly what **Spawn** will do, and what's wrong if it can't, like another vessel being in the way. **Orbit** draws the orbit in map view, with a marker for each vessel.

### Placing

In **Place** mode, **Place…** gets the console out of the way and a ghost follows the mouse. What's under the mouse decides where it goes:

- The ground, in the flight view: on the terrain, runways, and rooftops. It turns red if the ground is too steep or another vessel is in the way.
- The sky, in the flight view: beside the active vessel, matching its velocity, unless that's near the ground.
- A planet or moon, in map view or the Tracking Station: on its surface.
- Space, in map view: a circular orbit through the mouse, facing you. Look down on the pole for an equatorial orbit, or from the side for a polar one. **Q/E** reverse it.

The editor's rotation keys turn it the same way they turn parts: 90° a press, or 5° with **Shift**. On the ground **Q/E** turn it. In space it starts nose prograde and roof up on screen, **WASDQE** rotate it, and **Space** resets it.

**Left-click** to spawn. **Ctrl-click** keeps placing, **right-click** or **Escape** stops. The console comes back when you're done.

### Crew

**Pilot** puts a pilot in the first command seat, **Command** fills the command seats, and **Fill All** fills passenger seats too. Kerbals already at the space centre are used first, unless **Always Hire New Kerbals** is ticked. New hires are fully trained.

### Afterwards

**Switch To** flies the vessel you just spawned. **Undo** removes the last batch: kerbals who were already on the roster go home, and kerbals hired for it are let go.

### Good to know

- Spawned vessels start out unloaded and load in like any other vessel when they come into range. Landed vessels are set down on the ground when they load, with their brakes on.
- Launch clamps only come along when spawning on the ground.
- Kerbals don't get put in external command seats.
- It's a cheat. Nothing costs funds, and nothing checks what you've unlocked.

Requires KSP 1.12. No other dependencies.

### For modders

`LazySpawner.dll` spawns vessels for any mod. It does the fiddly parts: turning a craft file into a vessel without building its parts, fresh IDs, robotics, launch clamps, brakes, crew and hiring, the control point, standing the vessel on the ground, and undo. Where vessels go is up to you. The Spawn Vessels screen is `LazySpawnerUI.dll`, built on the same API. Reference `LazySpawner.dll`, add `[assembly: KSPAssemblyDependency("LazySpawner", 0, 3)]`, and have players install LazySpawner.

```csharp
VesselTemplate template = VesselTemplate.FromCraft(path); // Or FromVessel(vessel). Make it once, spawn it many times.

// Landed: the spawner finds the ground. In orbit: prograde unless given a rotation for the control part.
Vessel rover = Spawner.Spawn(template, SpawnSituation.Landed(body, latitude, longitude, heading), new CrewSettings(CrewMode.Pilot));
Vessel probe = Spawner.Spawn(template, SpawnSituation.Orbiting(orbit, rotation), new CrewSettings(CrewMode.None, kerbals: someKerbals));

// Many at once, spread over frames. The situations can be worked out as it goes.
StartCoroutine(Spawner.SpawnAll(template, situations, crew, spawnedSoFar));

Spawner.Remove(rover); // Undo. Its crew go home, and kerbals hired for it are let go.
```

`Spawner.Pose` says where a vessel would appear and how it would be turned, for previews, and `VesselBounds.FromVessel` measures any vessel. Failures throw `SpawnException`, with a title and a message fit to show players.
