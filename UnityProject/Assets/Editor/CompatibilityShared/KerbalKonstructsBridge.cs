using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GroundBlastFx.Core;
using UnityEngine;

namespace GroundBlastFx.Surface
{
    /// <summary>
    /// Lecture optionnelle des statics de Kerbal Konstructs par réflexion (aucune dépendance dure).
    /// API utilisée (KK 1.x, relevée dans KerbalKonstructs.dll) : KerbalKonstructs.Core.StaticDatabase.GetAllStatics()
    /// → StaticInstance[] avec les champs gameObject (GameObject) et launchSite (KKLaunchSite, non null pour un site de lancement).
    /// Le tableau est lu seulement quand un collider inconnu est touché (résultat mis en cache par le classifieur).
    /// </summary>
    public sealed class KerbalKonstructsBridge
    {
        private bool _probed;
        private MethodInfo _getAll;
        private FieldInfo _gameObject;
        private FieldInfo _launchSite;
        private readonly Dictionary<int, string> _launchSiteByObject = new Dictionary<int, string>();
        private readonly HashSet<int> _staticObjects = new HashSet<int>();
        private float _lastRefresh = -100f;

        public bool Available { get; private set; }

        private void Probe()
        {
            _probed = true;
            try
            {
                for (int i = 0; i < AssemblyLoader.loadedAssemblies.Count; i++)
                {
                    AssemblyLoader.LoadedAssembly la = AssemblyLoader.loadedAssemblies[i];
                    if (la.name != "KerbalKonstructs") continue;
                    Type db = la.assembly.GetType("KerbalKonstructs.Core.StaticDatabase");
                    Type inst = la.assembly.GetType("KerbalKonstructs.Core.StaticInstance");
                    if (db == null || inst == null) break;
                    _getAll = db.GetMethod("GetAllStatics", BindingFlags.Public | BindingFlags.Static);
                    _gameObject = inst.GetField("gameObject");
                    _launchSite = inst.GetField("launchSite");
                    Available = _getAll != null && _gameObject != null;
                    break;
                }
                GeLog.Info(Available ? "Kerbal Konstructs détecté : détection des pas de tir KK activée." : "Kerbal Konstructs absent : détection KK désactivée.");
            }
            catch (Exception e)
            {
                Available = false;
                GeLog.Warn("Kerbal Konstructs : réflexion impossible, détection KK désactivée (" + e.Message + ")");
            }
        }

        /// <summary>
        /// Cherche un static KK parmi les parents du collider.
        /// </summary>
        /// <returns>true si le collider appartient à un static KK ; siteName non null si c'est un site de lancement.</returns>
        public bool TryClassify(Transform t, out string siteName)
        {
            siteName = null;
            if (!_probed) Probe();
            if (!Available || t == null) return false;
            try
            {
                Refresh();
                for (int depth = 0; t != null && depth < 16; depth++, t = t.parent)
                {
                    int id = t.gameObject.GetInstanceID();
                    if (_launchSiteByObject.TryGetValue(id, out siteName)) return true;
                    if (_staticObjects.Contains(id)) return true;
                }
            }
            catch (Exception e)
            {
                Available = false;
                GeLog.Warn("Kerbal Konstructs : erreur de lecture, détection KK désactivée (" + e.Message + ")");
            }
            return false;
        }

        private void Refresh()
        {
            if (Time.realtimeSinceStartup - _lastRefresh < 5f) return;
            _lastRefresh = Time.realtimeSinceStartup;
            var all = _getAll.Invoke(null, null) as IList;
            if (all == null) return;
            // KK peut remplacer des statics ou changer leur site sans changer leur nombre.
            // Relire leurs identités au rythme déjà limité à 5 s ; aucun changement de KK.
            _launchSiteByObject.Clear();
            _staticObjects.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                object s = all[i];
                if (s == null) continue;
                var go = _gameObject.GetValue(s) as GameObject;
                if (go == null) continue;
                int id = go.GetInstanceID();
                _staticObjects.Add(id);
                object site = _launchSite != null ? _launchSite.GetValue(s) : null;
                if (site != null) _launchSiteByObject[id] = ReadSiteName(site) ?? go.name;
            }
        }

        private static string ReadSiteName(object site)
        {
            Type t = site.GetType();
            FieldInfo f = t.GetField("LaunchSiteName");
            if (f != null) return f.GetValue(site) as string;
            PropertyInfo p = t.GetProperty("LaunchSiteName");
            return p != null ? p.GetValue(site, null) as string : null;
        }
    }
}
