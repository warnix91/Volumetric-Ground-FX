using System;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using UnityEngine;
using UnityEngine.Rendering;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel;

namespace GroundBlastFx.Rendering
{
    /// <summary>Éclairage de la scène, fourni par le jeu (Sun.Instance) ou par le harnais Unity.</summary>
    public struct RenderEnvironment
    {
        public Vector3 SunDir;          // vers le soleil, normalisé
        public Color SunColor;          // couleur × intensité, déjà assombrie la nuit
        public Color AmbientSky;
        public Color AmbientGround;
        public Color FogColor;
        public float FogDensity;        // extinction de la perspective aérienne (1/m)
        public float Time;
        public float DeltaTime;

        /// <summary>
        /// Construit l'éclairage à partir de la direction du soleil et de l'atmosphère locale.
        /// atmosphere01 = pression / 101 325 (0 dans le vide). up = verticale locale au foyer principal.
        /// </summary>
        public static RenderEnvironment FromSun(Vector3 sunDir, Color sunRaw, Vector3 up, float atmosphere01, Color groundAlbedo, float time, float dt)
        {
            var e = new RenderEnvironment { Time = time, DeltaTime = dt };
            sunDir = sunDir.sqrMagnitude > 1e-6f ? sunDir.normalized : Vector3.up;
            e.SunDir = sunDir;
            float elev = Vector3.Dot(sunDir, up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up);
            float atm = Mathf.Clamp01(atmosphere01);
            // Soleil sous l'horizon : nuit (fondu court au crépuscule, plus long avec une atmosphère).
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f - 0.08f * atm, 0.05f, elev));
            // Rougissement rasant en atmosphère.
            Color low = new Color(1.0f, 0.62f, 0.38f);
            float redden = atm * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.35f, elev)));
            Color sun = sunRaw * Color.Lerp(Color.white, low, redden) * day;
            e.SunColor = sun;
            float lum = Mathf.Max(sun.r, Mathf.Max(sun.g, sun.b));
            Color skyDay = new Color(0.30f, 0.44f, 0.68f);
            // Nuit : KSP garde le décor lisible (lumière ambiante) ; le nuage doit l'être autant, sinon il forme un disque
            // noir sur le sol. Plancher non multiplié par le soleil.
            Color skyNight = new Color(0.030f, 0.036f, 0.050f);
            e.AmbientSky = atm > 0.001f
                ? Color.Lerp(skyNight, skyDay * Mathf.Lerp(0.5f, 1.0f, atm) * Mathf.Max(lum, 0.05f), day)
                : new Color(0.012f, 0.012f, 0.014f) + sun * 0.02f;
            // Rebond du sol : le régolithe éclaire le dessous des éjectas et du nuage.
            e.AmbientGround = groundAlbedo * sun * Mathf.Clamp01(elev) * (atm > 0.001f ? 0.35f : 0.45f);
            e.FogColor = Color.Lerp(skyNight * 0.6f, new Color(0.62f, 0.70f, 0.82f) * Mathf.Max(lum, 0.05f), day);
            e.FogDensity = 2.5e-5f * atm;
            return e;
        }
    }

    /// <summary>Paramètres d'une lumière de flamme au sol, appliqués à une Light Unity par l'appelant.</summary>
    public struct FlameLightParams
    {
        public bool Enabled;
        public Vector3 Position;
        public Color Color;
        public float Intensity;
        public float Range;
    }

    /// <summary>
    /// Cœur du rendu, sans aucune dépendance à KSP : utilisé par VolumetricRenderer en jeu ET par le harnais Unity
    /// hors jeu (mêmes shaders, mêmes paramètres, même empaquetage des foyers). Aucune allocation par frame.
    /// Principe : RIEN n'est attaché au jet. Chaque foyer a une grille de densité ANCRÉE AU SOL (slot stable
    /// tant que le foyer vit) ; le jet n'est qu'une source qui l'alimente et la pousse. Les gouttes et les grains sont
    /// des particules GPU lancées depuis l'impact puis laissées dans le repère du sol.
    /// </summary>
    public sealed class RenderCore : IDisposable
    {
        public const int MaxClusters = 4;
        public const int MaxMarks = 16;
        /// <summary>Résolution de la carte des hauteurs du sol autour de chaque nuage (1.7).</summary>
        public const int GroundRes = 48;

        /// <summary>
        /// Fournisseur de rayons vers le sol (jeu : décor de KSP et niveau de la mer ; harnais : colliders de la scène).
        /// Pour chaque origine, écrit la distance jusqu'au premier sol touché le long de <paramref name="down"/>, ou -1.
        /// </summary>
        public interface IGroundSampler { void Cast(Vector3[] origins, int count, Vector3 down, float maxDist, float[] outDist); }
        /// <summary>null = sol plat (carte désactivée).</summary>
        public static IGroundSampler Ground;
        /// <summary>
        /// Moment du rendu : après les opaques, AVANT les transparents. Les flammes (Waterfall, transparentes et
        /// additives) sont donc dessinées par-dessus la poussière et restent visibles.
        /// </summary>
        public const CameraEvent Event = CameraEvent.BeforeForwardAlpha;
        public static Action<string> Log = s => Debug.Log("[GroundBlastFx] " + s);
        /// <summary>Diagnostic du harnais : désactive la grille simulée (volume vide).</summary>
        public static bool ForceAnalytic;
        /// <summary>1.0.2 : écrit la profondeur du nuage opaque (passe 3). Faux = comportement 1.0.1 (diagnostic du banc).</summary>
        public static bool WriteCloudDepth = true;

        private static readonly int LowId = Shader.PropertyToID("_GELowTarget");
        private static readonly int CopyId = Shader.PropertyToID("_GECopyTarget");
        private static readonly int CloudDepthId = Shader.PropertyToID("_GECloudDepth");
        private static readonly int CameraDepthTexId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int LowTexId = Shader.PropertyToID("_GELowTex");
        private static readonly int CountId = Shader.PropertyToID("_GECount");
        private static readonly int SurfaceGroupId = Shader.PropertyToID("_GESurfaceGroup");

        // Tableaux par slot (index = slot, pas l'ordre des foyers du Core).
        private readonly Vector4[] _points = new Vector4[MaxClusters];
        private readonly Vector4[] _normals = new Vector4[MaxClusters];
        private readonly Vector4[] _axes = new Vector4[MaxClusters];
        private readonly Vector4[] _params = new Vector4[MaxClusters];
        private readonly Vector4[] _colorA = new Vector4[MaxClusters];
        private readonly Vector4[] _colorB = new Vector4[MaxClusters];
        private readonly Vector4[] _flames = new Vector4[MaxClusters];
        private readonly Vector4[] _flamePoints = new Vector4[MaxClusters];
        private readonly Vector4[] _shape = new Vector4[MaxClusters];
        private readonly Vector4[] _extra = new Vector4[MaxClusters];   // air raréfié, nappe en atmosphère, mode des particules
        private readonly Vector4[] _gridO = new Vector4[MaxClusters];
        private readonly Vector4[] _gridU = new Vector4[MaxClusters];
        private readonly Vector4[] _gridN = new Vector4[MaxClusters];
        private readonly Vector4[] _src = new Vector4[MaxClusters];
        private readonly Vector4[] _srcP = new Vector4[MaxClusters];
        private readonly Vector4[] _prevExt = new Vector4[MaxClusters];
        private readonly Vector4[] _jet = new Vector4[MaxClusters];
        private readonly Vector4[] _flowP = new Vector4[MaxClusters];
        private readonly Vector4[] _jetDir = new Vector4[MaxClusters];
        private readonly Vector4[] _misc = new Vector4[MaxClusters];
        private readonly Vector4[] _outlets = new Vector4[MaxClusters * 2];
        private readonly Vector4[] _outletGround = new Vector4[MaxClusters];
        private readonly Vector4[] _flowX = new Vector4[MaxClusters];
        private readonly Vector4[] _jetX = new Vector4[MaxClusters];
        // Vitesse mémorisée de la vapeur déjà sortie ; y = raccord aux bouffées de pad.
        // Distincte de _flowX : couper les moteurs doit arrêter l'émission, pas figer la vapeur en transit.
        private readonly Vector4[] _jetMotion = new Vector4[MaxClusters];
        private readonly float[] _slotJetL = new float[MaxClusters];
        private readonly float[] _slotJetS = new float[MaxClusters];
        private readonly float[] _slotJetD = new float[MaxClusters];
        private readonly Vector4[] _outletDir = new Vector4[MaxClusters];
        private float _dt, _sampleDt;
        private float _effectBrightness = VisualTuning.DefaultBrightness;
        private readonly ThrustPulse[] _thrustPulse = new ThrustPulse[MaxClusters];
        private readonly PadFlowLog[] _padFlowLog = new PadFlowLog[MaxClusters];
        private int _padFlowLogBudget = 24; // transitions, jamais une ligne par frame
        private readonly float[] _slotFloor = new float[MaxClusters];
        private readonly float[] _slotOutD = new float[MaxClusters];
        // Dérive de la grille au vent (1.8, m, repère local est/nord) : quand l'apport est coupé, la grille suit le nuage
        // qui s'en va avec le vent (sinon il sortait par un bord et y était coupé en ligne droite). _simDrift : dérive au
        // dernier pas de simulation (le pas suivant relit l'ancienne grille décalée) ; _groundDrift : dérive du relevé du sol.
        private readonly Vector2[] _slotDrift = new Vector2[MaxClusters];
        private readonly Vector2[] _simDrift = new Vector2[MaxClusters];
        private readonly Vector2[] _groundDrift = new Vector2[MaxClusters];
        // Couleur réelle du sol (1.8, passe 2 du volume) : deux textures 4 × 1 en alternance (moyenne glissante sur le GPU,
        // aucune relecture) ; x = poids de cette couleur face à la palette, y = remise à zéro, z = pas de temps.
        private readonly Vector4[] _groundColW = new Vector4[MaxClusters];
        // Remise à zéro de la couleur du sol en attente jusqu'à la prochaine image dessinée (le foyer peut naître sur une
        // image qui n'est pas rendue : il garderait sinon la couleur du foyer précédent du même emplacement).
        private readonly bool[] _gcResetPending = new bool[MaxClusters];
        private RenderTexture _gcRead, _gcWrite;
        private static readonly int GroundColId = Shader.PropertyToID("_GEGroundCol");
        private static readonly int GroundColPrevId = Shader.PropertyToID("_GEGroundColPrev");
        // Carte des hauteurs du sol (1.7) : le nuage épouse le relief, contourne les bâtiments, les grains rebondissent sur
        // le vrai terrain. Hauteurs relatives à l'origine de la grille, une bande de GroundRes lignes par emplacement.
        private readonly Vector4[] _groundInfo = new Vector4[MaxClusters];   // x = carte valide, y = niveau de l'ancrage (m)
        private readonly float[] _groundE = new float[MaxClusters];
        private readonly Vector3[] _groundOrigins = new Vector3[GroundRes * GroundRes];
        private readonly float[] _groundDist = new float[GroundRes * GroundRes];
        private readonly Color[] _groundPixels = new Color[GroundRes * GroundRes];
        private Texture2D _groundTex;
        private readonly Vector4[] _debrisData = new Vector4[MaxClusters];
        private readonly Vector4[] _debrisGravity = new Vector4[MaxClusters];
        private readonly float[] _debrisReset = new float[MaxClusters];
        private readonly float[] _simReset = new float[MaxClusters];
        private readonly float[] _simStep = new float[MaxClusters];
        // Drapeaux envoyés au GPU en float4 (x = remise à zéro, y = simulation) : un tableau « float x[4] » d'un tampon
        // de constantes range chaque élément sur 16 octets, SetFloats ne remplissait que l'emplacement 0 (bug corrigé en 1.4).
        private readonly Vector4[] _simFlags = new Vector4[MaxClusters];
        /// <summary>Banc de test : premier emplacement GPU essayé pour un nouveau foyer (vérifie les emplacements 1 à 3).</summary>
        public static int FirstSlotForTests;
        private readonly int[] _slotIds = new int[MaxClusters];
        private readonly bool[] _slotFresh = new bool[MaxClusters];
        private readonly bool[] _slotUsed = new bool[MaxClusters];
        private readonly int[] _slotOf = new int[MaxClusters];
        private readonly float[] _slotE = new float[MaxClusters];
        private readonly float[] _slotHg = new float[MaxClusters];
        private readonly Vector4[] _markPoints = new Vector4[MaxMarks];
        private readonly Vector4[] _markNormals = new Vector4[MaxMarks];
        private readonly Vector4[] _markAges = new Vector4[MaxMarks];
        private readonly int[] _markIndices = new int[MaxMarks];
        private readonly float[] _markScores = new float[MaxMarks];

        /// <summary>Lumières de flamme, indexées par slot (plus fortes la nuit).</summary>
        public readonly FlameLightParams[] Lights = new FlameLightParams[MaxClusters];

        private Material _volume, _debris;
        private Texture3D _noise;
        private ComputeShader _compute;
        private ComputeBuffer _debrisBuffer, _debrisArgs;
        // 1.0.2 : vapeur du pas en bouffées (méthode lagrangienne) : avancées ici, fondues chaque image dans la grille de
        // l'emplacement par le compute shader (union de sphères douces) au lieu du transport de la grille.
        private readonly PadPuffs[] _puffs = new PadPuffs[MaxClusters];
        private readonly bool[] _puffMode = new bool[MaxClusters];
        private readonly Vector4[] _puffData = new Vector4[MaxClusters * PadPuffs.Capacity * 2];
        private ComputeBuffer _puffBuffer;
        private int _puffSlot;
        private Func<float, float, float> _puffGround;
        private int _debrisKernel, _debrisPerSlot;
        private RenderTexture _simRead, _simWrite;
        private int _simKernel, _simX, _simY, _simZ;
        private bool _simActive, _simFailed;
        private int _clusterCount, _markCount;
        private int _lowHeight; // hauteur réellement enregistrée pour le cône de chaque pixel
        private QualityLevel _quality;
        private float _time, _night, _fovTan = 0.6f, _simDt;
        private bool _lightLogged;
        private uint _frame;

        public bool IsAvailable => _volume != null;
        public bool SimActive => _simActive;
        public int ClusterCount => _clusterCount;
        public int MarkCount => _markCount;
        public string Status { get; private set; } = "non initialisé";
        public string BackendName => !IsAvailable ? "Null" : _simActive ? "Volumetric-Simulated" : "Volumetric-Analytic";

        public RenderCore(AssetBundle bundle)
        {
            for (int i = 0; i < MaxClusters; i++) _slotIds[i] = int.MinValue;
            if (bundle == null) { Status = "bundle absent"; return; }
            Shader shader = bundle.LoadAsset<Shader>("Assets/GroundBlastFx/GroundVolume.shader");
            if (shader == null || !shader.isSupported) { Status = shader == null ? "shader absent du bundle" : "shader non supporté par ce GPU"; return; }
            _noise = bundle.LoadAsset<Texture3D>("Assets/GroundBlastFx/Noise128.asset");
            if (_noise == null) { Status = "bruit 3D absent du bundle"; return; }
            _volume = new Material(shader) { name = "GroundBlastFx volume", hideFlags = HideFlags.DontSave };
            _volume.SetTexture("_GENoise", _noise);
            _compute = bundle.LoadAsset<ComputeShader>("Assets/GroundBlastFx/VolumeField.compute");
            _debris = NewMaterial(bundle, "Assets/GroundBlastFx/GroundDebris.shader");
            _groundTex = new Texture2D(GroundRes, GroundRes * MaxClusters, TextureFormat.RFloat, false, true)
            {
                name = "GroundBlastFx ground heights", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
            };
            _gcRead = NewGroundColorTarget("GroundBlastFx ground color A");
            _gcWrite = NewGroundColorTarget("GroundBlastFx ground color B");
            Status = "shaders DX11 chargés";
        }

        private static RenderTexture NewGroundColorTarget(string name)
        {
            var rt = new RenderTexture(MaxClusters, 1, 0, RenderTextureFormat.ARGBHalf)
            {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
            };
            rt.Create();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
            return rt;
        }

        private static Material NewMaterial(AssetBundle bundle, string path)
        {
            Shader s = bundle.LoadAsset<Shader>(path);
            return s != null && s.isSupported ? new Material(s) { hideFlags = HideFlags.DontSave } : null;
        }

        /// <summary>Applique la qualité : résolution de la grille simulée et nombre de particules GPU.</summary>
        public void Configure(RendererSettings settings)
        {
            if (!IsAvailable) return;
            _quality = settings.Quality;
            ConfigureSimulation();
            ConfigureDebris();
            Status = _simActive ? "shaders DX11 + grille 3D " + _simX + "×" + _simY + "×" + _simZ + " par foyer" : "shaders DX11, grille 3D indisponible";
        }

        /// <summary>
        /// Copie les foyers et traces dans les tableaux des shaders, fait avancer la simulation GPU et les particules.
        /// Les tableaux du Core ne sont jamais conservés. Renvoie le nombre de foyers rendus.
        /// </summary>
        public int Prepare(Camera cam, ImpingementCluster[] clusters, int clusterCount, ScorchMark[] marks, int markCount,
                           RendererSettings settings, ref RenderEnvironment env)
        {
            if (!IsAvailable || cam == null) return 0;
            _time = env.Time;
            _dt = Mathf.Clamp(env.DeltaTime, 0f, 0.1f);
            _sampleDt = env.DeltaTime;
            _effectBrightness = VisualTuning.Brightness(settings.EffectBrightness);
            _frame++;
            // Obscurité 0 (plein jour) … 1 (nuit) : sans exposition automatique dans KSP, la lueur des flammes n'est
            // visible que la nuit ; en plein jour le soleil l'efface complètement.
            float lum = Mathf.Max(env.SunColor.r, Mathf.Max(env.SunColor.g, env.SunColor.b));
            _night = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.04f, 0.45f, lum));
            int n = Math.Min(Math.Min(clusterCount, MaxClusters), Math.Max(0, settings.MaxRenderedClusters));
            AssignSlots(clusters, n);
            float global = Mathf.Clamp(settings.GlobalIntensity, 0.25f, 2f);
            _clusterCount = 0;
            for (int s = 0; s < MaxClusters; s++)
            {
                if (_slotOf[s] >= 0) { PackCluster(s, ref clusters[_slotOf[s]], settings, global); _clusterCount++; }
                else ClearSlot(s);
            }
            if (!_lightLogged && _clusterCount > 0)
            {
                // Une fois par scène : valeurs réelles de l'éclairage du jeu (Scatterer, TUFX…), pour l'étalonnage du rendu.
                _lightLogged = true;
                Log("Éclairage au premier foyer : soleil " + env.SunColor.r.ToString("F2") + "/" + env.SunColor.g.ToString("F2") + "/" + env.SunColor.b.ToString("F2")
                    + ", ciel " + env.AmbientSky.r.ToString("F2") + "/" + env.AmbientSky.g.ToString("F2") + "/" + env.AmbientSky.b.ToString("F2")
                    + ", obscurité " + _night.ToString("F2") + ", élévation du soleil " + (Mathf.Asin(Mathf.Clamp(Vector3.Dot(env.SunDir, _gridU[0].sqrMagnitude > 0.5f ? (Vector3)_gridU[0] : Vector3.up), -1f, 1f)) * Mathf.Rad2Deg).ToString("F0") + "°");
            }
            if (_simActive)
            {
                // Qualité Bas : un pas de simulation pour trois images (pas de temps cumulé), invisible sur une grille aussi grossière.
                _simDt += env.DeltaTime;
                if (_quality != QualityLevel.Low || (_frame % 3u) == 0u || AnyFreshSlot()) { AdvanceSimulation(_simDt); _simDt = 0f; }
            }
            if (_debrisBuffer != null && _clusterCount > 0) AdvanceDebris(env.DeltaTime);
            PackMarks(cam, marks, markCount, settings);
            Feed(cam, ref env);
            return _clusterCount;
        }

        /// <summary>Chaque foyer garde son slot tant qu'il est listé ; un nouveau foyer prend un slot libre (grille remise à zéro).</summary>
        private void AssignSlots(ImpingementCluster[] clusters, int n)
        {
            for (int s = 0; s < MaxClusters; s++) { _slotOf[s] = -1; _slotUsed[s] = false; _slotFresh[s] = false; }
            for (int i = 0; i < n; i++)
                for (int s = 0; s < MaxClusters; s++)
                    if (!_slotUsed[s] && _slotIds[s] == clusters[i].Id) { _slotUsed[s] = true; _slotOf[s] = i; break; }
            for (int i = 0; i < n; i++)
            {
                bool placed = false;
                for (int s = 0; s < MaxClusters && !placed; s++) placed = _slotOf[s] == i;
                if (placed) continue;
                for (int j = 0; j < MaxClusters; j++)
                {
                    int s = (j + FirstSlotForTests) % MaxClusters;
                    if (_slotUsed[s]) continue;
                    _slotUsed[s] = true; _slotOf[s] = i; _slotIds[s] = clusters[i].Id; _slotFresh[s] = true;
                    float rmax = Mathf.Max(clusters[i].MaxCloudRadiusM, Mathf.Max(clusters[i].CloudFrontRadiusM, 3f * clusters[i].ImpingementRadiusM));
                    // Fixés pour toute la vie du foyer (la grille ne doit pas bouger) : profondeur du plancher sous le
                    // pas (bouches plus basses que la table) et distance des bouches déduites du cap de tranchée.
                    _slotFloor[s] = GridFloor(ref clusters[i]);
                    _slotOutD[s] = Mathf.Clamp(2.2f * clusters[i].ImpingementRadiusM + 12f, 18f, 45f);
                    _slotE[s] = WantedExtent(s, ref clusters[i], rmax);
                    _slotHg[s] = GridHeight(_slotE[s]) + _slotFloor[s];
                    _prevExt[s] = new Vector4(_slotE[s], _slotHg[s], 0f, 0f);
                    break;
                }
            }
            for (int s = 0; s < MaxClusters; s++) if (!_slotUsed[s]) _slotIds[s] = int.MinValue;
        }

        private bool AnyFreshSlot()
        {
            for (int s = 0; s < MaxClusters; s++) if (_simReset[s] > 0.5f) return true;
            return false;
        }

        private static float GridExtent(float rmax) { return Mathf.Clamp(1.45f * rmax, 20f, 1400f); }
        /// <summary>
        /// Longueur visible des jets du déflecteur, limitée pour qu'ils se fondent dans le nuage de surface.
        /// </summary>
        private static float JetLength(float rmax) { return Mathf.Clamp(1.45f * rmax, 50f, 400f); }
        private static float GridHeight(float extent) { return Mathf.Clamp(0.7f * extent, 16f, 950f); }

        private readonly float[] _groundRel = new float[MaxClusters * GroundRes * GroundRes];

        /// <summary>
        /// Relève le sol sous la grille de l'emplacement s : rayons verticaux sur GroundRes × GroundRes points couvrant
        /// [-E, E]². Hauteurs relatives à l'ancrage du nuage. Renvoie la plus basse (m). Coût : un lot de rayons à la
        /// naissance d'un nuage et quand sa grille s'agrandit, jamais à chaque image.
        /// </summary>
        private float SampleGround(int s, Vector3 anchor, Vector3 n, Vector3 east, Vector3 north, float E, bool enabled)
        {
            _groundE[s] = E;
            int count = GroundRes * GroundRes, b = s * count;
            if (Ground == null || !enabled)
            {
                _groundInfo[s].x = 0f;
                for (int i = 0; i < count; i++) _groundRel[b + i] = 0f;
                return 0f;
            }
            float top = GridHeight(E) + 40f, maxDist = top + 120f;
            for (int iz = 0, i = 0; iz < GroundRes; iz++)
                for (int ix = 0; ix < GroundRes; ix++, i++)
                {
                    float x = ((ix + 0.5f) / GroundRes - 0.5f) * 2f * E, z = ((iz + 0.5f) / GroundRes - 0.5f) * 2f * E;
                    _groundOrigins[i] = anchor + east * x + north * z + n * top;
                }
            float min = 0f;
            try
            {
                Ground.Cast(_groundOrigins, count, -n, maxDist, _groundDist);
                int hits = 0;
                for (int i = 0; i < count; i++)
                {
                    float d = _groundDist[i];
                    float h = d >= 0f ? Mathf.Clamp(top - d, -120f, top) : 0f;
                    if (d >= 0f) hits++;
                    _groundRel[b + i] = h;
                    if (h < min) min = h;
                }
                _groundInfo[s].x = hits > count / 4 ? 1f : 0f;
            }
            catch (Exception e)
            {
                Log("Relevé du sol impossible : " + e.Message);
                Ground = null;
                _groundInfo[s].x = 0f;
            }
            return min;
        }

        /// <summary>Envoie la carte de l'emplacement s au GPU, en hauteurs locales (origine de la grille).</summary>
        private void UploadGround(int s)
        {
            if (_groundTex == null) return;
            int count = GroundRes * GroundRes, b = s * count;
            float floor = _slotFloor[s];
            for (int i = 0; i < count; i++) _groundPixels[i] = new Color(_groundRel[b + i] + floor, 0f, 0f, 0f);
            _groundTex.SetPixels(0, s * GroundRes, GroundRes, GroundRes, _groundPixels);
            _groundTex.Apply(false);
        }

        private static Vector3 AnchorOf(ref ImpingementCluster c)
        {
            return c.CloudAnchorWorld.sqrMagnitude > 0f || c.Visibility01 > 0f ? c.CloudAnchorWorld : c.ImpactPointWorld;
        }

        private static Vector3 NormalOf(ref ImpingementCluster c)
        {
            // Verticale fixe du nuage ; anciens contrats : normale du sol.
            if (c.CloudUpWorld.sqrMagnitude > 1e-4f) return c.CloudUpWorld.normalized;
            return c.SurfaceNormalWorld.sqrMagnitude > 1e-4f ? c.SurfaceNormalWorld.normalized : Vector3.up;
        }

        private static bool HasDeflector(ref ImpingementCluster c)
        {
            return c.Surface == SurfaceKind.LaunchPad && c.Medium != MediumKind.Vacuum
                && (c.DeflectorOutletCount > 0 || c.TrenchDirectionWorld.sqrMagnitude > 0.25f);
        }

        /// <summary>
        /// 1.0.2 : bouffées de vapeur d'un foyer « pas de tir » à déflecteur. Repère des bouffées = repère de la grille sans
        /// sa dérive au vent (elles sont fixées au sol et dérivent d'elles-mêmes) ; converties en repère de la grille pour
        /// le GPU. Les autres foyers gardent la simulation sur grille.
        /// </summary>
        private void UpdatePuffs(int s, ref ImpingementCluster c, bool vacuum, float jetFeed, float channel, float exitSpeed,
                                 int outletCount, float rmax, float ri, float u, float steam, Vector2 windL, float source, float global, float fadeTau, float impulse)
        {
            int b = s * PadPuffs.Capacity * 2;
            bool pad = c.Surface == SurfaceKind.LaunchPad && !vacuum && HasDeflector(ref c);
            if (_slotFresh[s] && _puffs[s] != null) _puffs[s].Clear();
            if (!pad)
            {
                if (_puffMode[s]) { for (int i = 0; i < PadPuffs.Capacity * 2; i++) _puffData[b + i] = Vector4.zero; }
                _puffMode[s] = false;
                return;
            }
            if (_puffs[s] == null) _puffs[s] = new PadPuffs(c.Id * 7919 + 13);
            if (_puffGround == null) _puffGround = PuffGround;
            _puffMode[s] = true;
            Vector4 o0 = _outlets[2 * s], o1 = _outlets[2 * s + 1], od = _outletDir[s];
            Vector2 d0 = new Vector2(od.x, od.y), d1 = new Vector2(od.z, od.w);
            if (d0.sqrMagnitude > 1e-4f) d0.Normalize(); else d0 = new Vector2(o0.x, o0.y).normalized;
            if (d1.sqrMagnitude > 1e-4f) d1.Normalize(); else d1 = new Vector2(o1.x, o1.y).normalized;
            Vector2 drift = _slotDrift[s];
            var inp = new PadPuffs.Inputs
            {
                OutletCount = outletCount,
                O0X = o0.x + drift.x, O0Y = o0.z, O0Z = o0.y + drift.y, D0X = d0.x, D0Z = d0.y,
                O1X = o1.x + drift.x, O1Y = o1.z, O1Z = o1.y + drift.y, D1X = d1.x, D1Z = d1.y,
                OutletFeed = jetFeed, ExitSpeed = exitSpeed, OutletHalfWidth = Mathf.Max(_flowX[s].z, 1f),
                JetLength = JetLength(rmax),
                CenterFeed = c.EnginesActive ? (1f - channel) * Mathf.Clamp01(source * 1.3f) * Mathf.Clamp01((u - 8f) / 60f) : 0f,
                CenterX = _src[s].x + drift.x, CenterY = _src[s].y, CenterZ = _src[s].z + drift.y,
                CenterSpeed = Mathf.Min(u, 60f), CenterRadius = ri,
                WindX = windL.x, WindZ = windL.y, CloudRadius = rmax, Steam = steam,
                Life = Mathf.Clamp(c.DissipationTimeS > 1f ? c.DissipationTimeS : 20f + 70f * Mathf.Clamp01(rmax / 400f), 25f, 240f),
                Density = Mathf.Clamp01(0.9f * Mathf.Min(global, 1.5f)),
                FadeTau = fadeTau,   // même rythme que les jets des bouches (1.0.1)
            };
            _puffSlot = s;
            PadIgnitionImpulse.Apply(_puffs[s].Items, ref inp, impulse, _sampleDt);
            _puffs[s].Step(_dt, ref inp, _puffGround);
            PadPuffs.Puff[] items = _puffs[s].Items;
            for (int i = 0; i < PadPuffs.Capacity; i++)
            {
                float a = _puffs[s].Opacity(items[i]);
                if (a <= 0.002f) { _puffData[b + 2 * i] = Vector4.zero; _puffData[b + 2 * i + 1] = Vector4.zero; continue; }
                _puffData[b + 2 * i] = new Vector4(items[i].X - drift.x, items[i].Y, items[i].Z - drift.y, items[i].Radius);
                _puffData[b + 2 * i + 1] = new Vector4(a, items[i].Tall, 0f, 0f);
            }
        }

        /// <summary>Hauteur du sol (repère de la grille, plancher compris) sous un point des bouffées de l'emplacement courant.</summary>
        private float PuffGround(float x, float z)
        {
            int s = _puffSlot;
            float floor = _slotFloor[s];
            if (_groundInfo[s].x < 0.5f) return floor;
            float E = Mathf.Max(_slotE[s], 1f);
            float gx = ((x - _groundDrift[s].x) / (2f * E) + 0.5f) * GroundRes - 0.5f;
            float gz = ((z - _groundDrift[s].y) / (2f * E) + 0.5f) * GroundRes - 0.5f;
            gx = Mathf.Clamp(gx, 0f, GroundRes - 1.001f); gz = Mathf.Clamp(gz, 0f, GroundRes - 1.001f);
            int ix = (int)gx, iz = (int)gz, b = s * GroundRes * GroundRes;
            float fx = gx - ix, fz = gz - iz;
            float h00 = _groundRel[b + iz * GroundRes + ix], h10 = _groundRel[b + iz * GroundRes + ix + 1];
            float h01 = _groundRel[b + (iz + 1) * GroundRes + ix], h11 = _groundRel[b + (iz + 1) * GroundRes + ix + 1];
            return floor + Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }

        /// <summary>Profondeur (m) dont on abaisse la grille quand une bouche du déflecteur est plus basse que la table du pas.</summary>
        private static float GridFloor(ref ImpingementCluster c)
        {
            if (!HasDeflector(ref c) || c.DeflectorOutletCount <= 0) return 0f;
            Vector3 anchor = AnchorOf(ref c), n = NormalOf(ref c);
            // Terrain environnant (profil mesuré devant chaque bouche) : la grille descend jusqu'à lui.
            float low = Vector3.Dot(c.DeflectorOutlet0World - anchor, n) - c.DeflectorGround.x;
            if (c.DeflectorOutletCount > 1) low = Mathf.Min(low, Vector3.Dot(c.DeflectorOutlet1World - anchor, n) - c.DeflectorGround.z);
            return low < -0.5f ? Mathf.Min(2f - low, 30f) : 0f;
        }

        /// <summary>Demi-étendue voulue : le nuage prévu, et sur un pas de tir les bouches plus la portée de leurs jets.</summary>
        private float WantedExtent(int s, ref ImpingementCluster c, float rmax)
        {
            float e = GridExtent(rmax);
            if (!HasDeflector(ref c)) return e;
            float dOut = _slotOutD[s];
            if (c.DeflectorOutletCount > 0)
            {
                Vector3 anchor = AnchorOf(ref c), n = NormalOf(ref c);
                dOut = Vector3.ProjectOnPlane(c.DeflectorOutlet0World - anchor, n).magnitude;
                if (c.DeflectorOutletCount > 1) dOut = Mathf.Max(dOut, Vector3.ProjectOnPlane(c.DeflectorOutlet1World - anchor, n).magnitude);
            }
            // Bouches + longueur des jets + nuage qui bourgeonne au bout.
            return Mathf.Clamp(Mathf.Max(e, dOut + JetLength(rmax) + 0.45f * rmax + 30f), 20f, 1400f);
        }

        /// <summary>
        /// Bouches du déflecteur de flammes dans le repère local de la grille (x est, z nord, y hauteur) : bouches réelles
        /// du pas ou, à défaut, deux bouches sur l'axe de tranchée configuré
        /// (LaunchSites.cfg). Renvoie la distance horizontale moyenne des bouches à l'ancrage (0 = pas de déflecteur).
        /// </summary>
        private float PackOutlets(int s, ref ImpingementCluster c, Vector3 gridOrigin, Vector3 n, Vector3 east, Vector3 north)
        {
            _outlets[2 * s] = Vector4.zero;
            _outlets[2 * s + 1] = Vector4.zero;
            _outletGround[s] = Vector4.zero;
            _outletDir[s] = Vector4.zero;
            if (!HasDeflector(ref c)) return 0f;
            float sum = 0f;
            int count = 0;
            if (c.DeflectorOutletCount > 0)
            {
                for (int i = 0; i < Math.Min(c.DeflectorOutletCount, 2); i++)
                {
                    Vector3 rel = (i == 0 ? c.DeflectorOutlet0World : c.DeflectorOutlet1World) - gridOrigin;
                    var o = new Vector4(Vector3.Dot(rel, east), Vector3.Dot(rel, north), Vector3.Dot(rel, n), 1f);
                    _outlets[2 * s + count++] = o;
                    sum += new Vector2(o.x, o.y).magnitude;
                }
                _outletGround[s] = c.DeflectorGround;
                // Sens réel de sortie (émission de la fumée stock) : juste même si la fusée n'est pas au centre du pas.
                Vector3 d0 = Vector3.ProjectOnPlane(c.DeflectorDir0World, n), d1 = Vector3.ProjectOnPlane(c.DeflectorDir1World, n);
                _outletDir[s] = new Vector4(Vector3.Dot(d0, east), Vector3.Dot(d0, north), Vector3.Dot(d1, east), Vector3.Dot(d1, north));
                return sum / count;
            }
            Vector3 trench = Vector3.ProjectOnPlane(c.TrenchDirectionWorld, n).normalized;
            float d = _slotOutD[s];
            float tx = Vector3.Dot(trench, east) * d, tz = Vector3.Dot(trench, north) * d;
            _outlets[2 * s] = new Vector4(tx, tz, _slotFloor[s], 1f);
            _outlets[2 * s + 1] = new Vector4(-tx, -tz, _slotFloor[s], 1f);
            _outletDir[s] = new Vector4(tx / d, tz / d, -tx / d, -tz / d);
            return d;
        }

        private void ReportPadFlow(int s, ref ImpingementCluster c, bool feeding, float pulse,
                                   float jetFeed, float channel, float exitSpeed, int outlets)
        {
            PadFlowEvent events = _padFlowLog[s].Step(feeding, pulse > 0f, _slotFresh[s]);
            if (events == PadFlowEvent.None || _padFlowLogBudget <= 0) return;
            _padFlowLogBudget--;
            Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Vapeur du pad : foyer {0}, événements={1}, t={2:F3}s, poussée={3:F3}MN, " +
                "hausse={4:F3}MN, amplitude={5:F3}, souffle={6:F3}, alimentation={7:F3}, " +
                "canal={8:F3}, sorties={9}, vitesse={10:F1}m/s, moteurs={11}, dt={12:F4}s, âge={13:F3}s{14}",
                c.Id, events, _time, c.TotalThrustN / 1000000f, _thrustPulse[s].RiseThrustN / 1000000f,
                _thrustPulse[s].Strength01, pulse, jetFeed, channel, outlets, exitSpeed,
                c.EnginesActive, _sampleDt, c.TimeSinceIgnitionS,
                _padFlowLogBudget == 0 ? " (limite de 24 transitions atteinte pour cette scène)" : ""));
        }

        private void ClearSlot(int s)
        {
            Lights[s].Enabled = false;
            _params[s] = Vector4.zero;
            _extra[s] = Vector4.zero;
            _groundColW[s] = Vector4.zero;
            _gridN[s].w = 0f;
            _jet[s] = Vector4.zero;
            _flowX[s] = Vector4.zero;
            _thrustPulse[s] = default;
            _padFlowLog[s] = default;
            _jetX[s] = Vector4.zero;
            _jetMotion[s] = Vector4.zero;
            _slotJetL[s] = 0f; _slotJetS[s] = 0f; _slotJetD[s] = 0f;
            _outletDir[s] = Vector4.zero;
            _outlets[2 * s] = Vector4.zero;
            _outlets[2 * s + 1] = Vector4.zero;
            _outletGround[s] = Vector4.zero;
            _simStep[s] = 0f;
            _simReset[s] = 0f;
            _debrisData[s].w = 0f;
        }

        private void PackCluster(int s, ref ImpingementCluster c, RendererSettings settings, float global)
        {
            bool vacuum = c.Medium == MediumKind.Vacuum;
            bool water = c.Surface == SurfaceKind.Water;
            float ri = Mathf.Max(c.ImpingementRadiusM, 0.3f);
            float R = Mathf.Max(Mathf.Max(c.CloudFrontRadiusM, ri * 1.3f), 2f);
            float rmax = Mathf.Max(c.MaxCloudRadiusM, R);
            float steam = Mathf.Clamp01(c.SteamFraction01);
            bool enabled = true;
            if (!vacuum)
            {
                if ((c.Surface == SurfaceKind.Terrain || c.Surface == SurfaceKind.Unknown || c.Surface == SurfaceKind.Structure) && !settings.EnableDust) enabled = false;
                if (c.Surface == SurfaceKind.LaunchPad && !settings.EnablePadSteam) enabled = false;
                if ((water || c.Surface == SurfaceKind.VesselDeck) && !settings.EnableWater) enabled = false;
            }
            else if (!settings.EnableVacuumEjecta) enabled = false;
            // Contrats anciens (champs v1.1 absents) : visibilité = intensité, apport = intensité.
            float visibility = c.Visibility01 > 0f || c.Source01 > 0f ? c.Visibility01 : c.Intensity01;
            float source = c.Visibility01 > 0f || c.Source01 > 0f ? c.Source01 : (c.EnginesActive ? c.Intensity01 : 0f);
            float intensity = enabled ? Mathf.Clamp01(c.Intensity01 * global) : 0f;
            visibility = enabled ? Mathf.Clamp01(visibility) : 0f;
            source = enabled ? Mathf.Clamp01(source * global) : 0f;

            Vector3 n = NormalOf(ref c);
            Vector3 tang = Vector3.ProjectOnPlane(c.PlumeAxisWorld, n); // |tang| = sin(α)
            float cut = c.EnginesActive ? 0f : Mathf.Max(c.TimeSinceCutoffS, 0f);
            float seed = Mathf.Repeat(c.Id * 0.6180339f, 1f) * 7.13f;
            // Partie dense et visible de la nappe : quelques dizaines à ~250 m (les grains fins vont plus loin mais
            // deviennent invisibles) ; au-delà, seuls les grains GPU continuent.
            // Au moins 2,5 r_i : en altitude (20 à 30 m) la tache est large et la nappe doit la dépasser, sinon rien
            // n'est visible alors que la poussière d'Apollo apparaît dès ~30 m.
            // Air raréfié (Duna) : les grains fusent aussi en nappe, sous le nuage (1.7).
            float thin = vacuum ? 1f : Mathf.Clamp01(c.ThinAir01);
            bool sheet = vacuum || thin > 0.05f;
            float sheetReach = sheet ? Mathf.Max(Mathf.Min(Mathf.Max(c.VacuumEjectaSpeedMs, 50f) * Mathf.Max(c.TimeSinceIgnitionS, 0.1f) * 0.5f,
                                                            Mathf.Min(0.35f * rmax, 160f)), Mathf.Min(3f * ri, 200f)) : 0f;

            // --- Paramètres communs (nappe du vide, particules, éclairage) ---
            _points[s] = new Vector4(c.ImpactPointWorld.x, c.ImpactPointWorld.y, c.ImpactPointWorld.z, vacuum ? Mathf.Max(sheetReach, R) : R);
            _normals[s] = new Vector4(n.x, n.y, n.z, rmax);
            _axes[s] = new Vector4(tang.x, tang.y, tang.z, ri);
            _params[s] = new Vector4(vacuum ? intensity : visibility * Mathf.Min(global, 1.5f), vacuum ? 0f : steam, (float)c.Surface, vacuum ? 1f : 0f);
            _colorA[s] = new Vector4(c.DustAlbedoA.r, c.DustAlbedoA.g, c.DustAlbedoA.b, c.TimeSinceIgnitionS);
            _colorB[s] = new Vector4(c.DustAlbedoB.r, c.DustAlbedoB.g, c.DustAlbedoB.b, cut);
            float flame = settings.EnableFlameGroundLight ? c.FlameLightIntensity : 0f;
            _flames[s] = new Vector4(c.FlameLightColor.r, c.FlameLightColor.g, c.FlameLightColor.b, flame);
            _flamePoints[s] = new Vector4(c.NozzleCenterWorld.x, c.NozzleCenterWorld.y, c.NozzleCenterWorld.z,
                                          c.EnginesActive ? Mathf.Clamp01(c.WallJetVelocityMs / 80f) : 0f);
            _shape[s] = new Vector4(rmax, Mathf.Clamp01(cut / 60f), seed, sheetReach);

            // --- Grille ancrée au sol ---
            Vector3 anchor = AnchorOf(ref c);
            Vector3 north = Vector3.ProjectOnPlane(c.CloudNorthWorld, n);
            if (north.sqrMagnitude < 1e-4f) north = Vector3.ProjectOnPlane(Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward, n);
            north.Normalize();
            Vector3 east = Vector3.Cross(n, north);
            Vector3 nozzleRel = c.NozzleCenterWorld - c.ImpactPointWorld;
            Vector3 wind = Vector3.ProjectOnPlane(c.WindWorldMs, n);
            Vector2 windL = new Vector2(Vector3.Dot(wind, east), Vector3.Dot(wind, north));
            if (_slotFresh[s]) { _slotDrift[s] = Vector2.zero; _simDrift[s] = Vector2.zero; _groundDrift[s] = Vector2.zero; }
            // Quand l'apport est coupé depuis 3 s, la grille suit le nuage qui dérive au vent (≈ 0,8 × le vent : le bas du
            // nuage est freiné par le sol). Bornée à une demi-étendue : l'ancrage reste dans la grille, un jet qui revient
            // retrouve son nuage.
            if (!vacuum && !c.EnginesActive && cut > 3f && windL.sqrMagnitude > 0.01f)
            {
                _slotDrift[s] += windL * (0.8f * _dt);
                float lim = 0.5f * _slotE[s];
                if (_slotDrift[s].magnitude > lim) _slotDrift[s] = _slotDrift[s].normalized * lim;
            }
            Vector3 driftW = east * _slotDrift[s].x + north * _slotDrift[s].y;
            // La grille s'agrandit si le nuage prévu grossit (poussée qui monte après l'allumage) : le pas de simulation
            // suivant relit l'étendue du dernier pas calculé (_GEPrevExt, mise à jour après chaque pas), rien n'est perdu.
            float want = WantedExtent(s, ref c, rmax);
            if (want > _slotE[s] * 1.12f) { _slotE[s] = want; _slotHg[s] = GridHeight(want) + _slotFloor[s]; }
            // Relief réel autour du nuage : relevé à la naissance (le plancher de la grille descend jusqu'au terrain le plus
            // bas, fixé ensuite) et quand la grille s'agrandit.
            if (_slotFresh[s] || Mathf.Abs(_groundE[s] - _slotE[s]) > 0.5f
                || (_slotDrift[s] - _groundDrift[s]).magnitude > 2f * _slotE[s] / GroundRes)
            {
                bool fresh = _slotFresh[s];
                _groundDrift[s] = _slotDrift[s];
                float minRel = SampleGround(s, anchor + driftW, n, east, north, _slotE[s], !vacuum);
                if (fresh && minRel < -0.5f)
                {
                    _slotFloor[s] = Mathf.Max(_slotFloor[s], Mathf.Min(2f - minRel, 40f));
                    _slotHg[s] = GridHeight(_slotE[s]) + _slotFloor[s];
                    _prevExt[s] = new Vector4(_slotE[s], _slotHg[s], 0f, 0f);
                }
                UploadGround(s);
            }
            float E = _slotE[s], Hg = _slotHg[s];
            // Grille abaissée sous l'ancrage si une bouche du déflecteur ou le terrain alentour est plus bas (fixé à la
            // création du nuage).
            Vector3 gridOrigin = anchor - n * _slotFloor[s] + driftW;
            Vector3 rel = c.ImpactPointWorld - gridOrigin;
            _groundInfo[s].y = _slotFloor[s];
            _gridO[s] = new Vector4(gridOrigin.x, gridOrigin.y, gridOrigin.z, E);
            _gridU[s] = new Vector4(n.x, n.y, n.z, Hg);
            _gridN[s] = new Vector4(north.x, north.y, north.z, vacuum ? 0f : visibility * Mathf.Min(global, 1.5f));
            _src[s] = new Vector4(Vector3.Dot(rel, east), Vector3.Dot(rel, n), Vector3.Dot(rel, north), ri);
            // Vitesse du jet pariétal plafonnée pour le transport (au-delà, seul le front compte visuellement).
            float u = c.EnginesActive && !vacuum ? Mathf.Min(c.WallJetVelocityMs, 140f) : 0f;
            _jet[s] = new Vector4(u, vacuum ? 0f : source, steam, c.EnginesActive ? 1f : 0f);
            // Déflecteur de flammes : tant que la tache du jet tient dans la tranchée (fusée basse, centrée sur le pas),
            // le jet ressort par les bouches ; quand elle couvre tout le pas, il s'étale de nouveau en rond.
            float dOut = PackOutlets(s, ref c, gridOrigin, n, east, north);
            int outletCount = (_outlets[2 * s].w > 0f ? 1 : 0) + (_outlets[2 * s + 1].w > 0f ? 1 : 0);
            float channel = 0f;
            if (dOut > 1f)
            {
                float off = new Vector2(_src[s].x, _src[s].z).magnitude;
                // Le déflecteur canalise le jet jusqu'à ce que sa tache couvre largement le pas (≈ 2 à 3,5 fois la distance
                // des bouches, soit une centaine de mètres d'altitude au KSC), comme le déluge réel.
                channel = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2.0f, 3.5f, Mathf.Max(ri, off) / dOut));
            }
            float bo = Mathf.Clamp(0.55f * ri + 0.05f * rmax + 2.5f, 3f, 16f);
            // La vitesse en sortie augmente avec la vitesse pariétale et la poussée totale : un gros lanceur
            // expulse le nuage plus vite qu'un moteur isolé. Plafond pour garder l'advection GPU stable.
            float thrustMN = Mathf.Max(c.TotalThrustN, 0f) / 1000000f;
            // Le modèle pariétal seul donne déjà plusieurs centaines de m/s, même pour un petit moteur.
            // Son ancien plafond identique pour tous effaçait la différence de poussée dans les jets du pad.
            float exitSpeed = Mathf.Min(12f + 34f * Mathf.Sqrt(thrustMN), Mathf.Min(0.25f * u, 140f));
            // Allumage SRB / hausse brusque : accélérer brièvement la vapeur des bouches existantes.
            // Aucun changement de rayon, hauteur, ancrage, densité, durée de vie ou apport nominal des bouffées.
            bool pulseEligible = enabled && c.EnginesActive && c.Surface == SurfaceKind.LaunchPad && !vacuum && HasDeflector(ref c) && channel > 0.2f && u > 8f;
            bool coldIgnition = c.TimeSinceIgnitionS < 0.15f && source < 0.15f;
            float pulse = _thrustPulse[s].Step(_time, _sampleDt, c.TotalThrustN, pulseEligible, _slotFresh[s], coldIgnition);
            float strength = VisualTuning.IgnitionStrength(settings.IgnitionStrength);
            pulse *= strength;
            float impulse = 1.8f * exitSpeed * _thrustPulse[s].Rise * strength;
            exitSpeed = Mathf.Min(exitSpeed * (1f + 1.8f * pulse), 140f);
            _flowX[s] = new Vector4(channel, exitSpeed, bo, c.EnginesActive ? R : 0f);
            // Jets visibles des bouches (pas de tir de KSP 2, mods de panaches volumétriques, vidéos de lancements) :
            // cônes denses qui jaillissent des bouches en ~1,5 s, s'ouvrent à ~17° et se dissolvent en nuage au bout.
            // La longueur ne recule jamais (pas d'effet « image qui se rétracte ») : c'est l'intensité qui s'éteint.
            // Longueur et largeur selon la poussée (R_max = 3 √F kN) : un petit lanceur fait des jets courts et fins,
            // une Saturn V des jets de plus de 200 m. Après le départ, le jet se décroche de la bouche et part avec le
            // nuage (plus de disparition en une seconde) ; s'il reprend, il se raccroche.
            if (_slotFresh[s]) { _slotJetL[s] = 0f; _slotJetS[s] = 0f; _slotJetD[s] = 0f; _jetMotion[s] = Vector4.zero; }
            float jetPush = Mathf.Pow(Mathf.Clamp01((u - 8f) / 60f), 0.6f);
            float jetTarget = outletCount > 0 && c.EnginesActive ? channel * Mathf.Clamp01(source * 1.3f) * jetPush : 0f;
            bool feeding = jetTarget > 0.05f;
            // Disparition : lente pendant l'activité (le nuage est entretenu), puis sur la durée de dissipation prévue.
            float diss = c.DissipationTimeS > 1f ? c.DissipationTimeS : 20f + 70f * Mathf.Clamp01(rmax / 400f);
            // La vapeur de pas de tir persiste longtemps ; la brume d'embruns s'évapore en quelques secondes.
            float offDecay = (2.2f - 1.3f * steam) / diss;
            float decay = c.EnginesActive ? (c.Surface == SurfaceKind.LaunchPad ? 0.004f : 0.01f) : offDecay;
            // 1.0.1 : moteurs encore allumés mais le jet n'alimente presque plus ce foyer (fusée qui monte au-dessus du pas) :
            // le nuage n'est plus entretenu et doit se dissiper (≈ 1 min), sinon il reste figé tant que la fusée est proche.
            // L'alimentation réelle dépend aussi du souffle au sol (jetPush) : une fusée haute garde une forte poussée mais ne souffle plus.
            if (c.EnginesActive) decay = Mathf.Lerp(Mathf.Max(offDecay, 0.025f), decay, Mathf.Clamp01(source * 4f) * jetPush);
            if (water) decay += c.EnginesActive ? 0.03f : 0.10f;
            // Après que la fusée quitte le déflecteur, la vapeur garde son impulsion et se fond
            // progressivement dans le nuage. Un fondu de 3 s faisait disparaître le jet en vol.
            // (1.8.1 : 5 s ; le jet qui s'éteint se déchire en bouffées dans le rendu, plus de tube fantôme pendant 8 s.)
            // (1.9.2 : 3,5 s ; le nuage simulé garde la vapeur au bout des jets, le jet lui-même n'a pas à traîner.)
            // 1.0.1 : le jet s'efface au même rythme que le nuage simulé (taux général + évaporation moyenne du corps dense,
            // ≈ 0,012/s dans VolumeField) : les jets des bouches et le nuage à leur bout disparaissent ensemble.
            float cloudTau = Mathf.Clamp(1f / Mathf.Max(decay + 0.012f, 1e-3f), 6f, 60f);
            float jetTau = jetTarget > _slotJetS[s] ? 0.25f : cloudTau;
            _slotJetS[s] += (jetTarget - _slotJetS[s]) * (1f - Mathf.Exp(-_dt / jetTau));
            // 1.0.2 : avec les bouffées, le jet visible n'est plus qu'une langue de feu à la sortie des bouches (3 largeurs
            // de bouche) : les bouffées sont la vapeur. Plus long, ce cône analytique (absent des bouffées) se voyait dans le
            // nuage comme un « deuxième panache dans le premier » (retour de l'auteur en jeu).
            bool puffJets = c.Surface == SurfaceKind.LaunchPad && !vacuum && HasDeflector(ref c);
            float jetMax = puffJets ? Mathf.Min(JetLength(rmax) * 0.5f, 3f * bo) : JetLength(rmax);
            if (feeding) _jetMotion[s].x = exitSpeed;
            _jetMotion[s].y = puffJets ? 1f : 0f;
            if (feeding)
            {
                _slotJetL[s] = Mathf.Min(jetMax, _slotJetL[s] + Mathf.Clamp(25f + 1.25f * exitSpeed, 55f, 220f) * _dt);
                _slotJetD[s] = Mathf.Max(0f, _slotJetD[s] - Mathf.Max(45f, exitSpeed) * _dt);
            }
            // La coupure voyage dans le jet déjà présent à la vitesse de sortie, ralentie en aval.
            // Aucun apport résiduel : UpdatePuffs reçoit toujours jetTarget, qui est nul après la coupure.
            else if (_slotJetL[s] > 0f)
                _slotJetD[s] += (puffJets
                    ? _jetMotion[s].x / (1f + _slotJetD[s] / (5f * bo))
                    : 1.1f * _slotJetL[s] / (3f * cloudTau)) * _dt;
            if (_slotJetL[s] > 0f && _slotJetD[s] > (puffJets ? 1.2f : 1.1f) * _slotJetL[s])
            { _slotJetL[s] = 0f; _slotJetS[s] = 0f; _slotJetD[s] = 0f; _jetMotion[s].x = 0f; }
            float jetR0 = Mathf.Clamp(0.05f * rmax, 3f, 10f);
            _jetX[s] = outletCount > 0 && _slotJetL[s] > 0.5f && _slotJetS[s] > 0.003f
                ? new Vector4(Mathf.Max(_slotJetL[s], 2f), _slotJetS[s], jetR0, _slotJetD[s]) : Vector4.zero;
            _srcP[s] = SourceParams(ri, rmax, steam, c.Surface, E, Hg, channel, outletCount, _jetX[s]);
            UpdatePuffs(s, ref c, vacuum, jetTarget, channel, exitSpeed, outletCount, rmax, ri, u, steam, windL, source, global, cloudTau, impulse);
            ReportPadFlow(s, ref c, HasDeflector(ref c) && feeding, pulse, jetTarget, channel, exitSpeed, outletCount);
            _flowP[s] = new Vector4(windL.x, windL.y, rmax, decay);
            Vector3 trench = c.TrenchDirectionWorld;
            _jetDir[s] = new Vector4(Vector3.Dot(tang, east), Vector3.Dot(tang, north), Vector3.Dot(trench, east), Vector3.Dot(trench, north));
            _misc[s] = new Vector4(seed, (float)c.Surface, c.TimeSinceIgnitionS, Mathf.Max(Vector3.Dot(nozzleRel, n), 2f * ri));
            _simReset[s] = _slotFresh[s] ? 1f : 0f;
            _simStep[s] = vacuum ? 0f : 1f;

            // --- Particules GPU (repère du sol) : grains du vide ou gouttes d'embruns ---
            float mode = 0f, pSpeed = 0f, pRate = 0f, pGravity = Mathf.Max(c.GravityMs2, 0f);
            if (vacuum && c.EnginesActive && intensity > 0.01f)
            {
                mode = 1f; pSpeed = Mathf.Max(c.VacuumEjectaSpeedMs, 0f); pRate = 0.5f;
            }
            else if (water && c.EnginesActive && source > 0.02f)
            {
                // Gerbe d'embruns : gouttes lentes et nombreuses, qui retombent à quelques dizaines de mètres.
                mode = 2f; pSpeed = Mathf.Clamp(u * 0.12f, 6f, 22f); pRate = 0.25f;
                if (pGravity < 0.5f) pGravity = 9.81f;
            }
            else if (thin > 0.5f && c.EnginesActive && source > 0.02f)
            {
                // Air raréfié : grains balistiques comme dans le vide, un peu freinés.
                mode = 1f; pSpeed = Mathf.Max(c.VacuumEjectaSpeedMs, 40f) * 0.6f; pRate = 0.35f * thin;
            }
            else if ((c.Surface == SurfaceKind.Terrain || c.Surface == SurfaceKind.Unknown) && c.EnginesActive
                     && c.Erodibility01 > 0.35f && u > 50f && source > 0.05f)
            {
                // Gravillons et mottes arrachés au sol meuble, en gerbe basse (vidéos d'atterrissages et d'essais moteur).
                mode = 3f; pSpeed = Mathf.Clamp(u * 0.14f, 8f, 45f); pRate = 0.18f * Mathf.Clamp01((u - 50f) / 80f);
                if (pGravity < 0.5f) pGravity = 9.81f;
            }
            _debrisData[s] = new Vector4(ri, pSpeed, Mathf.Clamp(c.VacuumEjectaAngleDeg, 0.5f, 20f) * Mathf.Deg2Rad, mode);
            _debrisGravity[s] = new Vector4(pGravity, mode > 1.5f || thin < 0.99f ? source : intensity, pRate, 0f);
            _extra[s] = new Vector4(vacuum ? 1f : thin, !vacuum && sheet ? Mathf.Clamp01(source * global) * thin * 0.85f : 0f, mode, 0f);
            _debrisReset[s] = _slotFresh[s] ? 1f : 0f;
            // Couleur réelle du sol : forte sur le terrain (la poussière vient de ce sol), moyenne sur un pas de tir (béton et
            // abords), nulle sur l'eau et les ponts (embruns, vapeur).
            float groundW = water || c.Surface == SurfaceKind.VesselDeck ? 0f
                          : c.Surface == SurfaceKind.LaunchPad ? 0.4f
                          : vacuum ? 0.6f : c.Surface == SurfaceKind.Structure ? 0.5f : 0.65f;
            if (_slotFresh[s]) _gcResetPending[s] = true;
            _groundColW[s] = new Vector4(groundW, _gcResetPending[s] ? 1f : 0f, _dt, 0f);

            // Lumière de flamme sur le décor : visible aussi de jour (les photos de décollages le montrent), plus forte la nuit.
            ref FlameLightParams l = ref Lights[s];
            l.Enabled = flame > 0.01f && c.EnginesActive;
            l.Position = c.NozzleCenterWorld;
            l.Color = c.FlameLightColor;
            // De jour, le soleil écrase la lueur (photos) : lumière faible, sinon le décor vire au jaune.
            l.Intensity = Mathf.Min(flame * 0.8f, 4f) * Mathf.Lerp(0.12f, 1f, _night) * _effectBrightness;
            l.Range = Mathf.Clamp(Mathf.Max(ri * 6f, R * 0.4f), 12f, 120f);
        }

        /// <summary>
        /// Anneau source et débit normalisé : un petit moteur (tache de 1,5 m) comme un gros (tache de 40 m) remplit en
        /// quelques secondes le volume du nuage prévu à la densité visée. Largeur et hauteur d'au moins une maille.
        /// </summary>
        private Vector4 SourceParams(float ri, float rmax, float steam, SurfaceKind surface, float E, float Hg,
                                     float channel, int outletCount, Vector4 jet)
        {
            float cellH = 2f * E / Mathf.Max(_simX, 1);
            float cellV = (Hg + 1f) / Mathf.Max(_simY, 1);
            float rc = Mathf.Max(1.35f * ri, cellH);
            float w = Mathf.Max(0.75f * ri, 1.3f * cellH);
            float hs = Mathf.Max(0.3f * ri + 0.5f, 1.2f * cellV);
            float ringVolume = 2f * Mathf.PI * Mathf.Max(rc, w) * w * 1.772f * hs;
            float sourceVolume = ringVolume;
            // Sur l'eau, le cœur d'embruns est local et plus haut qu'un simple voile à plat.
            bool water = surface == SurfaceKind.Water, deck = surface == SurfaceKind.VesselDeck;
            bool pad = surface == SurfaceKind.LaunchPad;
            float cloudRadius = (water ? 0.32f : deck ? 0.55f : pad ? 0.34f : 0.7f) * rmax;
            float cloudHeight = (water ? 0.24f : deck ? 0.10f : pad ? 0.16f : 0.20f + 0.25f * steam) * rmax + 2f;
            float target = water ? 0.85f : deck ? 0.7f : pad ? 0.88f : Mathf.Lerp(1.4f, 1.2f, steam);
            if (pad && outletCount > 0 && channel > 0.001f)
            {
                // Déflecteur : le déluge vaporisé sort par les bouches en un nuage énorme (Saturn V, SLS, Falcon 9 au
                // LC-39A) qui roule au sol puis monte. Mêmes blocs sources que VolumeField.compute (gaussiennes le long
                // et en travers de la bouche, exponentielle en hauteur) : volume π·lo·wo·ho par bouche.
                // Source = bande du bout de chaque jet (GEJetShape × rampe 0,35–0,75 L) : section gaussienne π·r²/2,2 dont
                // ≈ 75 % au-dessus du sol, sur ≈ 0,45 L de long.
                // Source = la seconde moitié du jet (GEJetShape × rampe 0,2–0,5 L, jusqu'à 1,1 L) : section π·r²/1,4, ≈ 75 %
                // au-dessus du sol, sur ≈ 0,7 L. Le nuage simulé reprend ainsi la forme des jets quand ils se décrochent.
                float L = Mathf.Max(jet.x, 5f), rMid = jet.z + 0.2f * 0.6f * L;
                float band = Mathf.PI * rMid * rMid / 1.4f * 0.75f * 0.7f * L;   // dépôt sur ≈ 0,7 L (1.8.1)
                sourceVolume = channel * outletCount * Mathf.Max(band, 1f) + (0.04f + 0.96f * (1f - channel)) * ringVolume;
                cloudRadius = Mathf.Lerp(cloudRadius, 0.5f * rmax, channel);
                cloudHeight = Mathf.Lerp(cloudHeight, 0.2f * rmax + 2f, channel);
                // Réserve dense : après le décollage, le nuage s'étale et se dilue pendant des minutes sans disparaître.
                target = Mathf.Lerp(target, 2.0f, channel);
            }
            float cloudVolume = Mathf.PI * cloudRadius * cloudRadius * cloudHeight;
            float fill = Mathf.Clamp(0.08f * rmax + 3f, 4f, 20f);
            float rate = target * cloudVolume / (fill * Mathf.Max(sourceVolume, 1f));
            return new Vector4(rc, w, hs, Mathf.Min(rate, 400f));
        }

        private void PackMarks(Camera cam, ScorchMark[] marks, int markCount, RendererSettings settings)
        {
            _markCount = 0;
            if (!settings.EnableScorch || marks == null) return;
            Vector3 camPos = cam.transform.position;
            for (int i = 0; i < markCount; i++)
            {
                float distance = Vector3.Distance(marks[i].CenterWorld, camPos);
                float score = 2f / (1f + distance / 60f) + (marks[i].Medium == MediumKind.Atmosphere ? 1.8f * Mathf.Exp(-marks[i].AgeS / 9f) : 0f) + marks[i].Strength01 * 0.3f;
                int pos = 0;
                while (pos < _markCount && score <= _markScores[pos]) pos++;
                if (pos >= MaxMarks) continue;
                if (_markCount < MaxMarks) _markCount++;
                for (int j = _markCount - 1; j > pos; j--) { _markScores[j] = _markScores[j - 1]; _markIndices[j] = _markIndices[j - 1]; }
                _markScores[pos] = score;
                _markIndices[pos] = i;
            }
            for (int i = 0; i < _markCount; i++)
            {
                ScorchMark m = marks[_markIndices[i]];
                _markPoints[i] = new Vector4(m.CenterWorld.x, m.CenterWorld.y, m.CenterWorld.z, Mathf.Max(m.RadiusM, 0.1f));
                _markNormals[i] = new Vector4(m.NormalWorld.x, m.NormalWorld.y, m.NormalWorld.z, Mathf.Clamp01(m.Strength01) * (m.Medium == MediumKind.Vacuum ? -1f : 1f));
                _markAges[i] = new Vector4(Mathf.Max(0f, m.AgeS), (float)m.Surface, m.Id, m.Soot01 > 0f ? Mathf.Clamp01(m.Soot01) : 0.6f);
            }
        }

        private void SetGrid(Material m)
        {
            m.SetVectorArray("_GEGridO", _gridO);
            m.SetVectorArray("_GEGridU", _gridU);
            m.SetVectorArray("_GEGridN", _gridN);
            m.SetVectorArray("_GESrc", _src);
            m.SetVectorArray("_GEJet", _jet);
            m.SetVectorArray("_GEFlowP", _flowP);
            m.SetVectorArray("_GEJetDir", _jetDir);
            m.SetVectorArray("_GEMisc", _misc);
            m.SetVectorArray("_GESrcP", _srcP);
            m.SetVectorArray("_GEOutlets", _outlets);
            m.SetVectorArray("_GEOutletGround", _outletGround);
            m.SetVectorArray("_GEFlowX", _flowX);
            m.SetVectorArray("_GEJetX", _jetX);
            m.SetVectorArray("_GEJetMotion", _jetMotion);
            m.SetVectorArray("_GEGroundInfo", _groundInfo);
            if (_groundTex != null) m.SetTexture("_GEGround", _groundTex);
            m.SetVectorArray("_GEOutletDir", _outletDir);
        }

        private void Feed(Camera cam, ref RenderEnvironment env)
        {
            Vector3 origin = cam.transform.position, fwd = cam.transform.forward;
            Vector4 sun = new Vector4(env.SunDir.x, env.SunDir.y, env.SunDir.z, 0f);
            Material m = _volume;
            m.SetInt(CountId, MaxClusters);
            m.SetInt("_GEMarkCount", _markCount);
            m.SetFloat("_GESimBlend", _simActive ? 1f : 0f);
            m.SetFloat("_GENight", _night);
            m.SetVectorArray("_GEGroundColW", _groundColW);
            if (_simActive && _simRead != null) m.SetTexture("_GEField", _simRead);
            m.SetVector("_GEFieldDims", new Vector4(_simX, _simY, _simZ, MaxClusters));
            SetGrid(m);
            m.SetVectorArray("_GEPoints", _points);
            m.SetVectorArray("_GENormals", _normals);
            m.SetVectorArray("_GEAxes", _axes);
            m.SetVectorArray("_GEParams", _params);
            m.SetVectorArray("_GEColorA", _colorA);
            m.SetVectorArray("_GEColorB", _colorB);
            m.SetVectorArray("_GEFlames", _flames);
            m.SetVectorArray("_GEFlamePoints", _flamePoints);
            m.SetVectorArray("_GEShape", _shape);
            m.SetVectorArray("_GEExtra", _extra);
            m.SetVectorArray("_GEMarkPoints", _markPoints);
            m.SetVectorArray("_GEMarkNormals", _markNormals);
            m.SetVectorArray("_GEMarkAges", _markAges);
            m.SetVector("_GECamera", origin);
            // Profondeur au-delà de laquelle aucun nuage n'existe : le suréchantillonnage n'a pas à distinguer le ciel d'un
            // sol lointain derrière le nuage (sinon une ligne apparaît à l'horizon à travers le nuage).
            float volFar = 1f;
            for (int s = 0; s < MaxClusters; s++)
            {
                if (_params[s].x < 0.002f) continue;
                Vector3 up = _gridU[s], center = (Vector3)_gridO[s] + up * (0.5f * _gridU[s].w);
                float radius = Mathf.Sqrt(2f * _gridO[s].w * _gridO[s].w + _gridU[s].w * _gridU[s].w);
                if (_params[s].w > 0.5f) { center = _points[s]; radius = Mathf.Max(_points[s].w, 50f) * 1.2f; }
                volFar = Mathf.Max(volFar, Vector3.Distance(origin, center) + radius);
            }
            m.SetFloat("_GEVolFar", volFar);
            UpdateCamera(cam);
            m.SetVector("_GESunDir", sun);
            m.SetFloat("_GEEffectBrightness", _effectBrightness);
            m.SetColor("_GESunColor", env.SunColor);
            m.SetColor("_GEAmbientSky", env.AmbientSky);
            m.SetColor("_GEAmbientGround", env.AmbientGround);
            m.SetVector("_GEFog", new Vector4(env.FogColor.r, env.FogColor.g, env.FogColor.b, env.FogDensity));
            m.SetFloat("_GETime", env.Time);
            m.SetInt("_GESteps", _quality == QualityLevel.Low ? 18 : _quality == QualityLevel.Medium ? 31 : _quality == QualityLevel.High ? 64 : 96);
            m.SetInt("_GELightSteps", _quality == QualityLevel.Low ? 2 : _quality == QualityLevel.Medium ? 3 : _quality == QualityLevel.High ? 4 : 5);
            m.SetFloat("_GEDetailLevel", _quality == QualityLevel.Low ? 0f : _quality == QualityLevel.Medium ? 1f : 2f);
            // Ombre des nuages au sol (1.8) : pas de marche vers le soleil selon la qualité (Bas : désactivée).
            m.SetInt("_GEShadowSteps", _quality == QualityLevel.Low ? 0 : _quality == QualityLevel.Medium ? 5 : _quality == QualityLevel.High ? 8 : 12);

            if (_debris != null && _debrisBuffer != null)
            {
                _debris.SetBuffer("_GEDebris", _debrisBuffer);
                _debris.SetInt("_GEDebrisPerSlot", _debrisPerSlot);
                SetGrid(_debris);
                // Nuage simulé : les grains qui sont derrière ou dans un nuage dense en sont cachés (1.8).
                if (_simActive && _simRead != null) _debris.SetTexture("_GEField", _simRead);
                _debris.SetVector("_GEFieldDims", new Vector4(_simX, _simY, _simZ, MaxClusters));
                _debris.SetFloat("_GESimBlend", _simActive ? 1f : 0f);
                _debris.SetVectorArray("_GEGroundColW", _groundColW);
                _debris.SetVectorArray("_GEColorA", _colorA);
                _debris.SetVectorArray("_GEParams", _params);
                _debris.SetVectorArray("_GEExtra", _extra);
                _debris.SetVectorArray("_GEColorB", _colorB);
                _debris.SetColor("_GESunColor", env.SunColor * _effectBrightness);
                _debris.SetColor("_GEAmbientSky", env.AmbientSky * _effectBrightness);
                _debris.SetVector("_GESunDir", sun);
            }
        }

        /// <summary>
        /// Position et rayons de la caméra (1.0.1). Appelé aussi juste avant le rendu (Camera.onPreRender en jeu) : la
        /// caméra de KSP peut encore bouger après notre LateUpdate (suivi d'une fusée qui roule, tremblement au décollage,
        /// mods de caméra). Avec des rayons d'une orientation déjà dépassée, les nuages semblaient tourner avec la caméra.
        /// </summary>
        public void UpdateCamera(Camera cam)
        {
            if (cam == null || _volume == null) return;
            // Le zoom peut lui aussi changer après LateUpdate (mods de caméra) : les rayons et leur
            // empreinte angulaire doivent provenir de la même caméra au moment du rendu.
            _fovTan = Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);
            if (_lowHeight > 0) _volume.SetFloat("_GEPixelAngle", 2f * _fovTan / _lowHeight);
            _volume.SetVector("_GECamera", cam.transform.position);
            _volume.SetVector("_GECamForward", cam.transform.forward);
            _volume.SetVector("_GERay00", cam.ViewportPointToRay(new Vector3(0f, 0f, 0f)).direction);
            _volume.SetVector("_GERay10", cam.ViewportPointToRay(new Vector3(1f, 0f, 0f)).direction);
            _volume.SetVector("_GERay01", cam.ViewportPointToRay(new Vector3(0f, 1f, 0f)).direction);
            _volume.SetVector("_GERay11", cam.ViewportPointToRay(new Vector3(1f, 1f, 0f)).direction);
            if (_debris != null) _debris.SetVector("_GECameraRight", cam.transform.right);
        }

        /// <summary>Enregistre les commandes de la frame (volume réduit → composition → particules).</summary>
        // surfaceGroup : 0 = toutes les surfaces (harnais), 1 = sol/vide, 2 = eau après l'océan.
        public void Record(CommandBuffer cmd, int width, int height, int divisor, int surfaceGroup = 0)
        {
            cmd.Clear();
            bool hasClusters = false;
            for (int i = 0; i < MaxClusters; i++)
                if (_params[i].x >= 0.002f && MatchesSurface(i, surfaceGroup)) { hasClusters = true; break; }
            if (!IsAvailable || (!hasClusters && (surfaceGroup == 2 || _markCount == 0))) return;
            cmd.SetGlobalInt(SurfaceGroupId, surfaceGroup);
            int lw = Math.Max(1, width / divisor), lh = Math.Max(1, height / divisor);
            _lowHeight = lh;
            _volume.SetVector("_GELowTexel", new Vector4(1f / lw, 1f / lh, 0f, 0f));
            _volume.SetFloat("_GEPixelAngle", 2f * _fovTan / lh);
            // Couleur réelle du sol autour des foyers (passe 2), avant le volume qui s'en sert. La passe de l'eau (groupe 2)
            // réutilise le dernier résultat.
            if (_gcRead != null && _gcWrite != null)
            {
                if (surfaceGroup != 2)
                {
                    cmd.SetGlobalTexture(GroundColPrevId, _gcRead);
                    cmd.Blit(BuiltinRenderTextureType.CameraTarget, _gcWrite, _volume, 2);
                    RenderTexture swapGc = _gcRead; _gcRead = _gcWrite; _gcWrite = swapGc;
                    for (int i = 0; i < MaxClusters; i++) _gcResetPending[i] = false;
                }
                cmd.SetGlobalTexture(GroundColId, _gcRead);
            }
            cmd.GetTemporaryRT(LowId, lw, lh, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, LowId, _volume, 0);
            cmd.SetGlobalTexture(LowTexId, LowId);
            cmd.GetTemporaryRT(CopyId, width, height, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, CopyId);
            cmd.Blit(CopyId, BuiltinRenderTextureType.CameraTarget, _volume, 1);
            // 1.0.2 : profondeur du nuage opaque. Pas pour les embruns (passe après les transparents : le ciel est déjà dessiné).
            // Sans nuage visible (seulement des traces au sol), rien à faire : la carte de profondeur du jeu reste intacte.
            if (WriteCloudDepth && surfaceGroup != 2 && hasClusters)
            {
                // Carte de profondeur scène + nuage (passe 4), puis écriture dans le tampon de profondeur (passe 3) : Scatterer
                // (versions publiques) dessine son ciel après nous, sur tout pixel resté à la profondeur du ciel (ticket GitHub
                // n° 2).
                cmd.GetTemporaryRT(CloudDepthId, width, height, 0, FilterMode.Point, RenderTextureFormat.RGFloat);
                cmd.Blit(CopyId, CloudDepthId, _volume, 4);
                cmd.Blit(CopyId, BuiltinRenderTextureType.CameraTarget, _volume, 3);
                // Les effets dessinés après nous lisent cette carte à la place de celle de la caméra : la fumée d'autres mods
                // qui s'y cale (traînées volumétriques des boosters) ne passe plus devant notre nuage. Pas de libération ici :
                // la texture temporaire reste valable jusqu'à la fin du rendu de la caméra.
                cmd.SetGlobalTexture(CameraDepthTexId, CloudDepthId);
            }
            if (_debrisBuffer != null && HasParticles(surfaceGroup))
                cmd.DrawProceduralIndirect(Matrix4x4.identity, _debris, 0, MeshTopology.Triangles, _debrisArgs);
            cmd.ReleaseTemporaryRT(CopyId);
            cmd.ReleaseTemporaryRT(LowId);
            cmd.SetGlobalInt(SurfaceGroupId, 0);
        }

        private bool MatchesSurface(int slot, int surfaceGroup)
        {
            bool water = _params[slot].z == (float)SurfaceKind.Water && _params[slot].w < 0.5f;
            return surfaceGroup == 0 || (surfaceGroup == 2 ? water : !water);
        }

        private bool HasParticles(int surfaceGroup)
        {
            for (int i = 0; i < MaxClusters; i++)
            {
                if (_params[i].x < 0.002f || !MatchesSurface(i, surfaceGroup)) continue;
                if (_params[i].w > 0.5f) return true;                                   // vide
                if (_params[i].z == (float)SurfaceKind.Water) return true;               // embruns (gouttes en vol)
                if (_extra[i].z > 0.5f) return true;                                     // grains (air raréfié), gravillons
            }
            return false;
        }

        // ---------------- Simulation GPU (grille ancrée au sol) ----------------

        private void ConfigureSimulation()
        {
            if (ForceAnalytic || _simFailed || !SystemInfo.supportsComputeShaders || _compute == null) { ReleaseSimulation(); return; }
            int x = _quality == QualityLevel.Low ? 48 : _quality == QualityLevel.Medium ? 64 : _quality == QualityLevel.High ? 96 : 128;
            int y = _quality == QualityLevel.Low ? 22 : _quality == QualityLevel.Medium ? 30 : _quality == QualityLevel.High ? 44 : 60;
            if (_simActive && x == _simX && y == _simY) return;
            ReleaseSimulation();
            try
            {
                _simKernel = _compute.FindKernel("Simulate");
                _simRead = NewVolume(x, y, x * MaxClusters);
                _simWrite = NewVolume(x, y, x * MaxClusters);
                _simX = x; _simY = y; _simZ = x;
                for (int i = 0; i < MaxClusters; i++) _slotIds[i] = int.MinValue; // grilles neuves : on repart de zéro
                _simActive = true;
            }
            catch (Exception e)
            {
                Log("Grille GPU indisponible : " + e.Message);
                _simFailed = true;
                ReleaseSimulation();
            }
        }

        private static RenderTexture NewVolume(int x, int y, int z)
        {
            var t = new RenderTexture(x, y, 0, RenderTextureFormat.RHalf)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = z, enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, useMipMap = false,
                autoGenerateMips = false, name = "GroundBlastFx simulated density", hideFlags = HideFlags.DontSave
            };
            if (!t.Create()) { UnityEngine.Object.DestroyImmediate(t); throw new InvalidOperationException("Texture3D UAV refusée"); }
            return t;
        }

        private void AdvanceSimulation(float dt)
        {
            bool any = false;
            for (int i = 0; i < MaxClusters; i++) any |= _simStep[i] > 0.5f || _simReset[i] > 0.5f;
            if (!any) return;
            try
            {
                _compute.SetVector("_GEDims", new Vector4(_simX, _simY, _simZ, MaxClusters));
                _compute.SetVectorArray("_GEGridO", _gridO);
                _compute.SetVectorArray("_GEGridU", _gridU);
                _compute.SetVectorArray("_GEGridN", _gridN);
                _compute.SetVectorArray("_GESrc", _src);
                _compute.SetVectorArray("_GESrcP", _srcP);
                // zw : déplacement de la grille (dérive) depuis le pas précédent, pour relire l'ancienne grille au bon endroit.
                for (int i = 0; i < MaxClusters; i++) { _prevExt[i].z = _slotDrift[i].x - _simDrift[i].x; _prevExt[i].w = _slotDrift[i].y - _simDrift[i].y; }
                _compute.SetVectorArray("_GEPrevExt", _prevExt);
                _compute.SetVectorArray("_GEJet", _jet);
                _compute.SetVectorArray("_GEFlowP", _flowP);
                _compute.SetVectorArray("_GEJetDir", _jetDir);
                _compute.SetVectorArray("_GEMisc", _misc);
                _compute.SetVectorArray("_GEOutlets", _outlets);
                _compute.SetVectorArray("_GEOutletGround", _outletGround);
                _compute.SetVectorArray("_GEFlowX", _flowX);
                _compute.SetVectorArray("_GEJetX", _jetX);
                _compute.SetVectorArray("_GEGroundInfo", _groundInfo);
                if (_groundTex != null) _compute.SetTexture(_simKernel, "_GEGround", _groundTex);
                _compute.SetVectorArray("_GEOutletDir", _outletDir);
                for (int i = 0; i < MaxClusters; i++) _simFlags[i] = new Vector4(_simReset[i], _simStep[i], _puffMode[i] ? 1f : 0f, 0f);
                if (_puffBuffer == null) _puffBuffer = new ComputeBuffer(_puffData.Length, 16);
                _puffBuffer.SetData(_puffData);
                _compute.SetBuffer(_simKernel, "_GEPuffs", _puffBuffer);
                _compute.SetVectorArray("_GESimFlags", _simFlags);
                // Bas : un pas pour 3 images (pas de temps plus long, transport semi-lagrangien stable).
                _compute.SetFloat("_DeltaTime", Mathf.Clamp(dt, 0.001f, _quality == QualityLevel.Low ? 0.08f : 0.05f));
                _compute.SetFloat("_GETime", _time);
                _compute.SetTexture(_simKernel, "_GENoise", _noise);
                _compute.SetTexture(_simKernel, "_InDensity", _simRead);
                _compute.SetTexture(_simKernel, "_OutDensity", _simWrite);
                _compute.Dispatch(_simKernel, (_simX + 3) / 4, (_simY + 3) / 4, (_simZ * MaxClusters + 3) / 4);
                RenderTexture swap = _simRead; _simRead = _simWrite; _simWrite = swap;
                for (int i = 0; i < MaxClusters; i++) { _prevExt[i] = new Vector4(_slotE[i], _slotHg[i], 0f, 0f); _simDrift[i] = _slotDrift[i]; }
            }
            catch (Exception e)
            {
                Log("Dispatch GPU impossible : " + e.Message);
                _simFailed = true;
                ReleaseSimulation();
            }
        }

        /// <summary>Banc de mesure uniquement : un pas de simulation GPU supplémentaire (coût du compute shader).</summary>
        internal void StepSimulationForTiming(float dt)
        {
            if (_simActive) AdvanceSimulation(dt);
        }

        private void ReleaseSimulation()
        {
            _simActive = false;
            if (_simRead != null) { _simRead.Release(); UnityEngine.Object.DestroyImmediate(_simRead); _simRead = null; }
            if (_simWrite != null) { _simWrite.Release(); UnityEngine.Object.DestroyImmediate(_simWrite); _simWrite = null; }
            _simX = _simY = _simZ = 0;
        }

        // ---------------- Particules GPU : grains du vide, gouttes d'embruns ----------------

        private void ConfigureDebris()
        {
            int perSlot = _quality == QualityLevel.Low ? 0 : _quality == QualityLevel.Medium ? 256 : _quality == QualityLevel.High ? 1024 : 2048;
            if (_debrisPerSlot == perSlot && _debrisBuffer != null) return;
            ReleaseDebris();
            if (perSlot == 0 || !SystemInfo.supportsComputeShaders || _compute == null || _debris == null) return;
            try
            {
                _debrisKernel = _compute.FindKernel("Debris");
                _debrisPerSlot = perSlot;
                _debrisBuffer = new ComputeBuffer(perSlot * MaxClusters, 32);
                _debrisArgs = new ComputeBuffer(1, 16, ComputeBufferType.IndirectArguments);
                _debrisArgs.SetData(new uint[] { 6, (uint)(perSlot * MaxClusters), 0, 0 });
                for (int i = 0; i < MaxClusters; i++) _debrisReset[i] = 1f;
            }
            catch (Exception e)
            {
                Log("Particules GPU indisponibles : " + e.Message);
                ReleaseDebris();
            }
        }

        private void AdvanceDebris(float dt)
        {
            try
            {
                _compute.SetVectorArray("_GEDebrisData", _debrisData);
                for (int i = 0; i < MaxClusters; i++) _debrisGravity[i].w = _debrisReset[i]; // remise à zéro (float4, voir _simFlags)
                _compute.SetVectorArray("_GEDebrisGravity", _debrisGravity);
                _compute.SetVectorArray("_GESrc", _src);
                _compute.SetInt("_GEDebrisPerSlot", _debrisPerSlot);
                _compute.SetFloat("_GEDebrisDeltaTime", Mathf.Clamp(dt, 0.001f, 0.05f));
                _compute.SetFloat("_GEDebrisFrame", _frame % 1000000u);
                _compute.SetBuffer(_debrisKernel, "_GEDebris", _debrisBuffer);
                // Grains et gouttes rebondissent sur le vrai sol (carte des hauteurs).
                _compute.SetVectorArray("_GEGridO", _gridO);
                _compute.SetVectorArray("_GEGroundInfo", _groundInfo);
                if (_groundTex != null) _compute.SetTexture(_debrisKernel, "_GEGround", _groundTex);
                _compute.Dispatch(_debrisKernel, (_debrisPerSlot * MaxClusters + 63) / 64, 1, 1);
                for (int i = 0; i < MaxClusters; i++) _debrisReset[i] = 0f;
            }
            catch (Exception e)
            {
                Log("Dispatch des particules impossible : " + e.Message);
                ReleaseDebris();
            }
        }

        private void ReleaseDebris()
        {
            if (_debrisBuffer != null) { _debrisBuffer.Release(); _debrisBuffer = null; }
            if (_debrisArgs != null) { _debrisArgs.Release(); _debrisArgs = null; }
            _debrisPerSlot = 0;
        }

        public string DebugInfo(int width, int height, int divisor)
        {
            return Status + " ; foyers " + _clusterCount + " ; traces " + _markCount + " ; passe 1/" + divisor + " ; " + width + "×" + height;
        }

        public void Dispose()
        {
            ReleaseSimulation();
            if (_puffBuffer != null) { _puffBuffer.Release(); _puffBuffer = null; }
            ReleaseDebris();
            if (_volume != null) { UnityEngine.Object.DestroyImmediate(_volume); _volume = null; }
            if (_debris != null) { UnityEngine.Object.DestroyImmediate(_debris); _debris = null; }
            _noise = null;
            if (_groundTex != null) { UnityEngine.Object.DestroyImmediate(_groundTex); _groundTex = null; }
            if (_gcRead != null) { _gcRead.Release(); UnityEngine.Object.DestroyImmediate(_gcRead); _gcRead = null; }
            if (_gcWrite != null) { _gcWrite.Release(); UnityEngine.Object.DestroyImmediate(_gcWrite); _gcWrite = null; }
        }
    }
}
