using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GroundBlastFx.Core
{
    /// <summary>Mesure de la pièce solide, sans les maillages de flamme ou de traînée ajoutés par les mods.</summary>
    internal static class NozzleGeometry
    {
        public static float EstimateDiameter(List<Renderer> renderers, Transform axis)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            bool any = false;
            if (renderers != null && axis != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer r = renderers[i];
                    if (!IsSolidGeometry(r)) continue;
                    Bounds b = r.bounds;
                    Vector3 c = b.center, e = b.extents;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 corner = new Vector3(c.x + ((k & 1) == 0 ? -e.x : e.x), c.y + ((k & 2) == 0 ? -e.y : e.y), c.z + ((k & 4) == 0 ? -e.z : e.z));
                        // Les contrats attendent des mètres monde, même si le modèle moteur est redimensionné.
                        Vector3 relative = corner - axis.position;
                        float x = Vector3.Dot(relative, axis.right), y = Vector3.Dot(relative, axis.up);
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        any = true;
                    }
                }
            }
            if (!any) return 1.25f;
            // Même estimation pour les modèles non redimensionnés ; les effets ajoutés sont exclus.
            return 0.85f * Mathf.Min(maxX - minX, maxY - minY);
        }

        private static bool IsSolidGeometry(Renderer r)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return false;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) return false;
            // Waterfall 0.11 place ses modèles sur TransparentFX (calque 1 dans KSP).
            if (r.gameObject.layer == 1) return false;
            Material[] materials = r.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.renderQueue >= (int)RenderQueue.Transparent) continue;
                if (material.GetTag("RenderType", false, "") == "Transparent") continue;
                return true; // les matériaux opaques et AlphaTest de la pièce restent admissibles
            }
            return false;
        }
    }
}
