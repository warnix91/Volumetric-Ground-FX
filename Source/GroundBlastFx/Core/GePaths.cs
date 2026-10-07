using System;
using System.IO;

namespace GroundBlastFx.Core
{
    /// <summary>Chemins du mod, déduits de l'emplacement de la DLL (GameData/Volumetric Ground FX/Plugins/GroundBlastFx.dll).</summary>
    public static class GePaths
    {
        private static string _modRoot;

        /// <summary>GameData/Volumetric Ground FX (chemin absolu).</summary>
        public static string ModRoot
        {
            get
            {
                if (_modRoot != null) return _modRoot;
                try
                {
                    string dll = typeof(GePaths).Assembly.Location;
                    string plugins = Path.GetDirectoryName(dll);
                    _modRoot = Path.GetDirectoryName(plugins);
                }
                catch (Exception)
                {
                    _modRoot = null;
                }
                if (string.IsNullOrEmpty(_modRoot) || !Directory.Exists(_modRoot))
                    _modRoot = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "Volumetric Ground FX");
                return _modRoot;
            }
        }

        public static string PluginData => Path.Combine(ModRoot, "PluginData");

        public static string LogFile => Path.Combine(PluginData, "GroundBlastFx.log");

        public static string SettingsFile => Path.Combine(PluginData, "Settings.cfg");

        public static string DefaultSettingsFile => Path.Combine(PluginData, "DefaultSettings.cfg");

        public static string UserLaunchSitesFile => Path.Combine(PluginData, "LaunchSites_user.cfg");

        public static string DevAutomationFile => Path.Combine(PluginData, "DevAutomation.cfg");

        public static string CapturesDir => Path.Combine(PluginData, "Captures");
    }
}
