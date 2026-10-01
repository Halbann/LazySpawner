using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LazySpawner;

internal static class Logger
{
    // "[LazySpawner]: Spawner: message". The compiler fills in the file it's called from.
    public static void Log(string message, LogType type = LogType.Log, [CallerFilePath] string sourceFilePath = "") =>
        Debug.unityLogger.Log(type, $"[{Meta.name}]: {Path.GetFileNameWithoutExtension(sourceFilePath)}: {message}");
}
