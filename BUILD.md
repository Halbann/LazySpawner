# Building LazySpawner

1. Install the .NET SDK.
2. Set a `KSP_ROOT` environment variable to your KSP folder (the one with `KSP_x64.exe`).
3. Build: open `LazySpawner.slnx`, or run `dotnet build LazySpawner.slnx`.

Building copies `GameData/LazySpawner` into your KSP install's GameData.

Other ways to set the game path: https://kspbuildtools.readthedocs.io/en/stable/msbuild/ksp-install.html

# Releasing LazySpawner

1. Follow steps 1 and 2 from above.
2. Bump `<Version>` in `LazySpawner/LazySpawner.csproj`.
3. Run `.\release.ps1`.
