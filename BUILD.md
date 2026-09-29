# Building LazySpawner

1. Install the .NET SDK.
2. Set a `KSP_ROOT` environment variable to your KSP folder (the one with `KSP_x64.exe`).
3. Make sure Harmony (`GameData/000_Harmony/0Harmony.dll`) is installed in that KSP folder.
4. Build: open `LazySpawner.slnx`, or run `dotnet build LazySpawner.slnx`.

Building copies `GameData/LazySpawner` into your KSP install's GameData.

Other ways to set the game path: https://kspbuildtools.readthedocs.io/en/stable/msbuild/ksp-install.html

# Releasing LazySpawner

1. Follow steps 1 to 3 from above.
2. Bump `<Version>` in `LazySpawner/LazySpawner.csproj`.
3. Run `.\release.ps1`.
