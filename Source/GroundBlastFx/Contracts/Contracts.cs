using System;
using UnityEngine;

namespace GroundBlastFx.Contracts
{
    public enum SurfaceKind { Unknown = 0, Terrain = 1, LaunchPad = 2, Structure = 3, Water = 4, VesselDeck = 5 }
    public enum MediumKind  { Atmosphere = 0, Vacuum = 1 }
    public enum QualityLevel { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    /// <summary>
    /// Foyer d'impact : un ou plusieurs jets fusionnés qui touchent la même zone.
    /// Produit par le Core à chaque frame, consommé par le Rendering.
    /// Toutes les positions sont en espace monde Unity de la frame courante (origine flottante déjà gérée par le Core).
    /// </summary>
    public struct ImpingementCluster
    {
        // Identité et cycle de vie
        public int Id;                        // stable pendant toute la vie du foyer (actif puis dissipation)
        public bool EnginesActive;            // false = moteurs coupés, le nuage se dissipe
        public float TimeSinceIgnitionS;
        public float TimeSinceCutoffS;        // 0 tant que EnginesActive
        public bool IsDemo;                   // foyer virtuel du mode Démo

        // Géométrie
        public Vector3 ImpactPointWorld;
        public Vector3 SurfaceNormalWorld;
        public Vector3 PlumeAxisWorld;        // direction du jet (normalisée), vers la surface
        public Vector3 NozzleCenterWorld;     // centre de poussée moyen des tuyères
        public float StandoffM;               // h
        public float ImpingementRadiusM;      // r_i
        public float CloudFrontRadiusM;       // R_front courant
        public float MaxCloudRadiusM;         // R_max
        public Transform AnchorTransform;     // non null si la surface bouge (pont de barge), sinon null

        // Physique
        public SurfaceKind Surface;
        public MediumKind Medium;
        public int EngineCount;
        public float TotalThrustN;
        public float MassFlowKgS;
        public float ExhaustVelocityMs;
        public float ImpingementPressurePa;
        public float WallJetVelocityMs;       // u_i au bord de la tache
        public float AmbientPressurePa;
        public float AmbientDensityKgM3;
        public float GravityMs2;
        public Vector3 WindWorldMs;

        // Paramètres visuels calculés par le Core
        public float Intensity01;             // intensité globale, fondus d'entrée et de sortie inclus
        public float Erodibility01;
        public float SteamFraction01;
        public Color DustAlbedoA;
        public Color DustAlbedoB;
        public Vector3 TrenchDirectionWorld;  // pas de tir : direction de déflexion ; Vector3.zero = radial
        public Color FlameLightColor;
        public float FlameLightIntensity;     // 0 si moteurs coupés
        public float VacuumEjectaSpeedMs;     // vitesse caractéristique des éjectas (vide)
        public float VacuumEjectaAngleDeg;    // élévation de la nappe (vide)

        // Aide au rendu
        public float CameraDistanceM;
        public int LodLevel;                  // 0 = détail max ; proposé par le Core, affinable par le Rendering

        // Ajouts v1.1 : le nuage déposé ne suit pas le jet.
        public Vector3 CloudAnchorWorld;      // point du sol, fixe pendant toute la vie du foyer (repère de la grille simulée)
        public Vector3 CloudNorthWorld;       // direction horizontale fixe au sol (nord local) : orientation de la grille
        public float Visibility01;            // présence du nuage déjà formé (priorité, distance, fin de dissipation) ; ≠ Intensity01 (= apport du jet)
        public float DissipationTimeS;        // durée de dissipation prévue après coupure
        public float Source01;                // apport instantané du jet au nuage (0 après coupure ou quand le jet ne touche plus)

        // Ajout v1.3 : sorties réelles du déflecteur de flammes (pas de tir stock), 0 = inconnues.
        public int DeflectorOutletCount;
        public Vector3 DeflectorOutlet0World;
        public Vector3 DeflectorOutlet1World;

        // Ajout v1.4 : profil du sol devant chaque bouche (pas surélevé au-dessus du terrain) :
        // x = dénivelé (m, ≥ 0) de la bouche 0 jusqu'au terrain environnant, y = distance (m) depuis la bouche où le sol
        // est descendu de moitié ; z, w = idem pour la bouche 1.
        public Vector4 DeflectorGround;

        // Ajout v1.6 : direction horizontale de sortie de chaque bouche (sens d'émission de la fumée
        // stock) ; Vector3.zero = inconnue (le rendu prend alors la direction depuis l'ancrage du nuage).
        public Vector3 DeflectorDir0World;
        public Vector3 DeflectorDir1World;

        // Ajout v1.7 : verticale FIXE du nuage (verticale du lieu, ou du pont de barge). La normale du
        // sol mesurée à chaque sondage bascule sur les pentes et les structures : le nuage ne doit pas basculer avec.
        public Vector3 CloudUpWorld;
        // Air raréfié 0..1 (Duna ≈ 0,85 ; Kerbin 0 ; vide 1) : nappe d'éjectas en plus du nuage.
        public float ThinAir01;
    }

    public struct ScorchMark
    {
        public int Id;
        public Vector3 CenterWorld;
        public Vector3 NormalWorld;
        public float RadiusM;
        public float Strength01;
        public SurfaceKind Surface;
        public MediumKind Medium;             // Vacuum = halo clair de décapage ; Atmosphere = brûlure sombre
        public float AgeS;
        public Transform AnchorTransform;     // non null sur un pont de barge

        // Ajout v1.2
        public float Soot01;                  // noircissement selon les ergols : kérolox ≈ 1 (suie), méthalox ≈ 0,2, hydrolox ≈ 0
    }

    public sealed class RendererSettings
    {
        public QualityLevel Quality = QualityLevel.High;
        public bool EnableDust = true;
        public bool EnablePadSteam = true;
        public bool EnableWater = true;
        public bool EnableVacuumEjecta = true;
        public bool EnableScorch = true;
        public bool EnableFlameGroundLight = true;
        public float GlobalIntensity = 1f;
        public float MaxRenderDistanceM = 5000f;
        public int MaxRenderedClusters = 4;
        public bool DebugView = false;

        // gains visuels uniquement ; indépendants de la densité et de la physique du vaisseau.
        public float EffectBrightness = VisualTuning.DefaultBrightness;
        public float IgnitionStrength = VisualTuning.DefaultIgnitionStrength;
    }

    public interface IGroundBlastFxRenderer
    {
        bool IsAvailable { get; }
        string BackendName { get; }           // "Volumetric-Simulated" | "Volumetric-Analytic" | "Null"
        void Initialize(RendererSettings settings);
        void ApplySettings(RendererSettings settings);
        /// Appelé une fois par frame depuis LateUpdate par le Core.
        /// Les tableaux appartiennent au Core et sont réutilisés : ne pas les conserver (copier si besoin). Aucune allocation.
        void Submit(ImpingementCluster[] clusters, int clusterCount, ScorchMark[] marks, int markCount);
        string GetDebugInfo();                // texte court pour la fenêtre debug
        void Shutdown();
    }

    public sealed class NullRenderer : IGroundBlastFxRenderer
    {
        public bool IsAvailable => true;
        public string BackendName => "Null";
        public void Initialize(RendererSettings settings) { }
        public void ApplySettings(RendererSettings settings) { }
        public void Submit(ImpingementCluster[] clusters, int clusterCount, ScorchMark[] marks, int markCount) { }
        public string GetDebugInfo() => "Null renderer";
        public void Shutdown() { }
    }

    public static class RendererLocator
    {
        /// Renseigné par le module Rendering au démarrage, via un KSPAddon(Startup.Instantly, true).
        public static Func<IGroundBlastFxRenderer> Factory;

        public static IGroundBlastFxRenderer Create()
        {
            if (Factory == null) return new NullRenderer();
            try { return Factory() ?? new NullRenderer(); }
            catch (Exception e) { Debug.LogError("[GroundBlastFx] Renderer factory failed: " + e); return new NullRenderer(); }
        }
    }
}
