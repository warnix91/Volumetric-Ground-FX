// Doubles des seules API KSP nécessaires au banc. Les objets Unity et leurs renderers sont natifs.
// Ce fichier reste dans Assets/Editor : il ne fait jamais partie du mod livré.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class AssemblyLoader
{
    public sealed class LoadedAssembly { public string name; public Assembly assembly; }
    public static readonly List<LoadedAssembly> loadedAssemblies = new List<LoadedAssembly>();
}
public sealed class ModuleSurfaceFX : MonoBehaviour { public float fxMax = 1f; }
public sealed class LaunchPadFX : MonoBehaviour
{
    private ParticleSystem[] ps;
    public void SetSystems(params ParticleSystem[] value) { ps = value; }
}
public sealed class Vessel { public bool loaded = true; public readonly List<Part> parts = new List<Part>(); }
public sealed class Part { public readonly List<object> Modules = new List<object>(); }
public static class FlightGlobals { public static readonly List<Vessel> VesselsLoaded = new List<Vessel>(); }
namespace GroundBlastFx.Config { public static class GeSettings { public static bool HideStockSurfaceFx = true; } }
namespace GroundBlastFx.Core
{
    public static class GeLog
    {
        public static void Info(string text) { Debug.Log(text); }
        public static void Warn(string text) { Debug.LogWarning(text); }
        public static void ExceptionOnce(string key, Exception error) { Debug.LogException(error); }
    }
}
