# Building LazySpawner

1. Install the .NET SDK.
2. Set a `KSP_ROOT` environment variable to your KSP folder (the one with `KSP_x64.exe`).
3. Build: open `LazySpawner.slnx`, or run `dotnet build LazySpawner.slnx`.

Debug builds copy `GameData/LazySpawner` into your KSP install's GameData.

There are two projects: `LazySpawner` is the backend other mods can use, and `LazySpawnerUI` is the window and placement tool, which uses the backend like any other mod would. Settings shared by both are in `Directory.Build.props`.

Other ways to set the game path: https://kspbuildtools.readthedocs.io/en/stable/msbuild/ksp-install.html

# Releasing LazySpawner

1. Follow steps 1 and 2 from above.
2. Bump `<Version>` in `Directory.Build.props`.
3. Run `.\release.ps1`.

# Developing LazySpawner

Debug builds of the UI work with [HotReloadKSP](https://github.com/Phantomical/HotReloadKSP): with it installed, you can rebuild and reload LazySpawnerUI from its menu without restarting the game. Unfortunately the backend can't be hot reloaded.
