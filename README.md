KSP mod for spawning vessels individually and en masse in a variety of ways from flight, the tracking station or the SPH/VAB. It's intended as a development and testing aid, so please don't use it to cheat in your career saves! Don't be lazy.

Press Alt+F, or open the debug console (Alt+F12) and go to Cheats > Spawn Vessels.

- Spawn craft from your game or provide a path to spawn any .craft file. Paths are picked up automatically from the clipboard.
- Clone the active vessel, or the selected one in the Tracking Station.
- Spawn the craft that you're editing in the VAB or SPH anywhere without leaving.
- Place vessels with the mouse: on the ground, next to your vessel, on any planet, or in any orbit from map view.
- Spawn them nearby, in an orbit you type in, or on a launch site.
- Spawn as many as you like at once.
- Select how they should be crewed, hiring kerbals if needed.
- You can always undo the previous spawn.

### For modders

`LazySpawner.dll` can spawn vessels for your mod.

The spawning process itself happens in a fraction of a second and is significantly faster than previous methods. Lazy Spawner translates the craft file straight into a vessel as your save stores it, writes that into the game, and KSP loads it like any other vessel when it comes into range. A craft file is only read once, so subsequent spawns of the same file are even faster.

Craft should spawn exactly as they would if spawned by the stock game, including robotics, staging, action groups, kerbals and the control point.

To use the Lazy Spawner API, reference LazySpawner.dll, add `[assembly: KSPAssemblyDependency("LazySpawner", 1, 0)]`, and list LazySpawner as a dependency.

```csharp
VesselTemplate template = VesselTemplate.FromCraft(path); // Or FromVessel(vessel). You make this once and can use it to spawn the same vessel many times.

// The vessel as the save has it. rover.vesselRef is the vessel itself, except in the editor, where there isn't one yet.
ProtoVessel rover = Spawner.Spawn(template, SpawnSituation.Landed(body, latitude, longitude, heading), new CrewSettings(CrewMode.Pilot));
ProtoVessel probe = Spawner.Spawn(template, SpawnSituation.Orbiting(orbit));

StartCoroutine(Spawner.SpawnAll(template, situations, crew, spawned)); // Several at once, spread over several frames

Spawner.Remove(rover); // Undo.
```

Please list Lazy Spawner as a dependency rather than bundling it. KSP only loads one copy of the DLL, so it may pick your version over another newer version. If you want to reference a specific version for some reason, then please let me know so I can make Lazy Spawner coordinate itself in the necessary way.
