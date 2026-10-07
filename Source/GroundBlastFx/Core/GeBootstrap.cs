using System;
using System.Reflection;
using System.Text;
using GroundBlastFx.Config;
using UnityEngine;

namespace GroundBlastFx.Core
{
    /// <summary>Ouverture du log dès le chargement de la DLL (avant les autres addons).</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class GeBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(this);
            GeLog.Init();
            GeLog.Info("Volumetric Ground FX (VGFX) " + GeSession.ModVersion + " chargé.");
        }
    }

    /// <summary>Au menu principal (GameDatabase prête) : configuration, réglages, textes, en-tête du log.</summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class GeMainMenuInit : MonoBehaviour
    {
        private void Start()
        {
            GeSession.EnsureLoaded();
            GeSession.WriteHeader();
        }
    }

    /// <summary>État de session partagé par les addons (chargement unique, en-tête).</summary>
    public static class GeSession
    {
        private static bool _loaded;
        private static bool _headerWritten;
        private static bool _backendLogged;

        public static string ModVersion
        {
            get
            {
                var attr = typeof(GeSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr != null ? attr.InformationalVersion : typeof(GeSession).Assembly.GetName().Version.ToString();
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            GeLog.Init();
            GeConfig.Load();
            GeSettings.Load();
            GeStrings.Load(GeSettings.Language);
        }

        public static void WriteHeader()
        {
            if (_headerWritten) return;
            _headerWritten = true;
            var sb = new StringBuilder();
            sb.AppendLine("===== En-tête de session =====");
            sb.AppendLine("Mod : Volumetric Ground FX (VGFX) " + ModVersion);
            sb.AppendLine("KSP : " + Versioning.GetVersionString() + " — Unity " + Application.unityVersion);
            sb.AppendLine("GPU : " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVendor + "), " + SystemInfo.graphicsMemorySize + " Mo, API " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
            sb.AppendLine("Compute shaders : " + (SystemInfo.supportsComputeShaders ? "oui" : "non") + ", textures 3D : " + (SystemInfo.supports3DTextures ? "oui" : "non") + ", shader level " + SystemInfo.graphicsShaderLevel);
            sb.AppendLine("Écran : " + Screen.width + "×" + Screen.height);
            sb.AppendLine("Renderer (fabrique) : " + (Contracts.RendererLocator.Factory != null ? "enregistrée par le module Rendering" : "absente → NullRenderer"));
            sb.AppendLine("Mods détectés : " + DetectedMods());
            sb.AppendLine("Réglages : " + GeSettings.Dump());
            sb.AppendLine("Physique : " + GeConfig.DumpPhysics());
            sb.Append("==============================");
            GeLog.FileOnly(sb.ToString());
            GeLog.Info("Volumetric Ground FX (VGFX) " + ModVersion + " prêt — " + SystemInfo.graphicsDeviceName + ", compute " + (SystemInfo.supportsComputeShaders ? "oui" : "non") + ", qualité " + GeSettings.Renderer.Quality);
        }

        /// <summary>Complète l'en-tête avec le backend réellement choisi (premier vol de la session).</summary>
        public static void LogBackend(string backend, string info)
        {
            if (_backendLogged) return;
            _backendLogged = true;
            GeLog.Info("Backend de rendu : " + backend + " — " + info);
        }

        private static readonly string[] Interesting =
        {
            "ModuleManager", "Waterfall", "scatterer", "Scatterer", "EVEManager", "Atmosphere", "CloudsManager", "Parallax", "TUFX", "Deferred",
            "KerbalKonstructs", "RealFuels", "SolverEngines", "Principia", "ksp_plugin_adapter", "kOS", "Kopernicus", "RealSolarSystem", "RP-0", "RP0",
            "SmokeScreen", "RealPlume", "Losket",
        };

        public static string DetectedMods()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < AssemblyLoader.loadedAssemblies.Count; i++)
            {
                AssemblyLoader.LoadedAssembly la = AssemblyLoader.loadedAssemblies[i];
                string n = la.name;
                for (int k = 0; k < Interesting.Length; k++)
                {
                    if (n.IndexOf(Interesting[k], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(n);
                    Version v = la.assembly.GetName().Version;
                    if (v != null) sb.Append(' ').Append(v);
                    break;
                }
            }
            return sb.Length > 0 ? sb.ToString() : "aucun des mods suivis";
        }

        public static bool IsAssemblyLoaded(string name)
        {
            for (int i = 0; i < AssemblyLoader.loadedAssemblies.Count; i++)
                if (string.Equals(AssemblyLoader.loadedAssemblies[i].name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
