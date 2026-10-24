namespace LazySpawner;

internal static class Extensions
{
    // As the player sees it, in their language.
    public static string Name(this CelestialBody body) => body.displayName.LocalizeRemoveGender();

    // A loaded vessel's own parts, which its saved record only catches up with when the game saves.
    public static int PartCount(this Vessel vessel) => vessel.loaded ? vessel.parts.Count : vessel.protoVessel.protoPartSnapshots.Count;
}
