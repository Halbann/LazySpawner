using System.Collections.Generic;
using UnityEngine;

namespace LazySpawner;

public static class GUIEnabled
{
    private static readonly Stack<bool> guiState = new();

    public static void Push(bool state)
    {
        guiState.Push(GUI.enabled);
        GUI.enabled = GUI.enabled && state;
    }

    public static void Pop() =>
        GUI.enabled = guiState.Pop();
}