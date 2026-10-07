using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GroundBlastFx.Core;
using GroundBlastFx.Contracts;
using GroundBlastFx.Surface;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CompatibilityProbe
{
    [Serializable] public sealed class Result { public string name; public bool passed; public string detail; }
    [Serializable] public sealed class Report
    {
        public string environment = "Unity 2019.4 native objects; KSP API doubles; not a KSP flight";
        public string stage;
        public int tests, failures;
        public List<Result> cases = new List<Result>();
    }
    public sealed class Static { public GameObject gameObject; public Site launchSite; }
    public sealed class Site { public string LaunchSiteName; }
    public static class Database
    {
        public static Static[] Data = new Static[0];
        public static Static[] GetAllStatics() { return Data; }
    }
    private sealed class Replacement : IGroundBlastFxRenderer
    {
        public bool IsAvailable { get; set; } = true;
        public string BackendName => "test";
        public void Initialize(RendererSettings settings) { }
        public void ApplySettings(RendererSettings settings) { }
        public void Submit(ImpingementCluster[] clusters, int count, ScorchMark[] marks, int markCount) { }
        public string GetDebugInfo() { return "test"; }
        public void Shutdown() { }
    }

    private static Report _report;
    private static readonly List<Object> Owned = new List<Object>();
    private static Material _opaque, _transparent;
    private static GameObject _root;

    public static void Run()
    {
        _report = new Report { stage = Environment.GetEnvironmentVariable("GE_COMPAT_STAGE") ?? "unknown" };
        try
        {
            _root = Own(new GameObject("CompatibilityProbe"));
            _opaque = Own(new Material(Shader.Find("Standard")));
            _transparent = Own(new Material(Shader.Find("Unlit/Transparent")));
            _transparent.renderQueue = 3000;
            GeometryTests();
            SuppressionTests();
            CacheTests();
        }
        catch (Exception error) { Record("probe setup", false, error.ToString()); }
        finally
        {
            FlightGlobals.VesselsLoaded.Clear();
            Database.Data = new Static[0];
            for (int i = Owned.Count - 1; i >= 0; i--) if (Owned[i] != null) Object.DestroyImmediate(Owned[i]);
            Owned.Clear();
        }
        string output = Environment.GetEnvironmentVariable("GE_COMPAT_OUT");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "compatibility.json"), JsonUtility.ToJson(_report, true));
        Debug.Log("[GroundBlastFx compat] tests=" + _report.tests + " failures=" + _report.failures);
        EditorApplication.Exit(_report.failures > 0 ? 1 : 0);
    }

    private static T Own<T>(T item) where T : Object { Owned.Add(item); return item; }
    private static void Record(string name, bool passed, string detail)
    {
        _report.tests++;
        if (!passed) _report.failures++;
        _report.cases.Add(new Result { name = name, passed = passed, detail = detail });
    }
    private static void Check(string name, bool passed) { Record(name, passed, passed ? "OK" : "Regression reproduced"); }
    private static void Diameter(string name, List<Renderer> renderers, float expected)
    {
        float actual = NozzleGeometry.EstimateDiameter(renderers, _root.transform);
        Record(name, Mathf.Abs(actual - expected) < 0.001f, "expected=" + expected + " actual=" + actual);
    }
    private static MeshRenderer Box(string name, float size, Material material, int layer = 0)
    {
        GameObject box = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
        box.name = name;
        box.transform.SetParent(_root.transform, false);
        box.transform.localScale = new Vector3(size, size, size);
        box.layer = layer;
        MeshRenderer renderer = box.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }
    private static void GeometryTests()
    {
        MeshRenderer solid = Box("solid part", 2f, _opaque);
        Diameter("Opaque part unchanged", new List<Renderer> { solid }, 1.7f);
        MeshRenderer waterfall = Box("Waterfall mesh on TransparentFX", 50f, _opaque, 1);
        Diameter("Waterfall layer excluded from nozzle bounds", new List<Renderer> { solid, waterfall }, 1.7f);
        MeshRenderer flame = Box("Other transparent plume", 50f, _transparent);
        Diameter("Transparent plume excluded from nozzle bounds", new List<Renderer> { solid, flame }, 1.7f);
        Material tagged = Own(new Material(_opaque));
        tagged.SetOverrideTag("RenderType", "Transparent");
        tagged.renderQueue = 2500;
        MeshRenderer unusualFlame = Box("Transparent type at lower queue", 50f, tagged);
        Diameter("Transparent tag excluded even at lower queue", new List<Renderer> { solid, unusualFlame }, 1.7f);
        MeshRenderer disabled = Box("B9 inactive variant", 50f, _opaque);
        disabled.enabled = false;
        Diameter("Disabled variant excluded", new List<Renderer> { solid, disabled }, 1.7f);
        MeshRenderer inactive = Box("Inactive model branch", 50f, _opaque);
        inactive.gameObject.SetActive(false);
        Diameter("Inactive model branch excluded", new List<Renderer> { solid, inactive }, 1.7f);
        Material cutout = Own(new Material(_opaque));
        cutout.renderQueue = 2450;
        cutout.SetOverrideTag("RenderType", "TransparentCutout");
        MeshRenderer cut = Box("ReStock opaque cutout", 3f, cutout);
        Diameter("AlphaTest solid geometry retained", new List<Renderer> { cut }, 2.55f);
        cut.sharedMaterials = new[] { _transparent, cutout };
        Diameter("Mixed glass and solid geometry retained", new List<Renderer> { cut }, 2.55f);
        Diameter("No solid model uses bounded fallback", new List<Renderer> { waterfall, flame }, 1.25f);
        Check("External effect renderers and materials untouched", waterfall.enabled && flame.enabled
            && waterfall.sharedMaterial == _opaque && flame.sharedMaterial == _transparent);
        _root.transform.localScale = Vector3.one * 2f;
        Diameter("Scaled engine measured in world metres", new List<Renderer> { solid }, 3.4f);
        _root.transform.localScale = Vector3.one * 0.5f;
        Diameter("Reduced engine measured in world metres", new List<Renderer> { solid }, 0.85f);
        _root.transform.localScale = new Vector3(-2f, 2f, 2f);
        Diameter("Mirrored scaled engine measured in world metres", new List<Renderer> { solid }, 3.4f);
        _root.transform.localScale = Vector3.one;
    }

    private static void Availability(StockSurfaceFxSuppressor suppressor, bool available)
    {
        MethodInfo method = typeof(StockSurfaceFxSuppressor).GetMethod("SetReplacementRenderer");
        // Sur la version reçue, l'absence du garde reproduit précisément le défaut.
        if (method != null) method.Invoke(suppressor, new object[] { available ? (IGroundBlastFxRenderer)new Replacement() : new NullRenderer() });
    }
    private static void SuppressionTests()
    {
        GroundBlastFx.Config.GeSettings.HideStockSurfaceFx = true;
        GameObject engine = Own(new GameObject("stock surface module"));
        ModuleSurfaceFX stock = engine.AddComponent<ModuleSurfaceFX>();
        stock.fxMax = 3.5f;
        var vessel = new Vessel();
        var part = new Part();
        part.Modules.Add(stock);
        vessel.parts.Add(part);
        FlightGlobals.VesselsLoaded.Add(vessel);
        GameObject pad = Own(new GameObject("pad"));
        LaunchPadFX padFx = pad.AddComponent<LaunchPadFX>();
        GameObject steamObject = Own(new GameObject("stock pad emitter"));
        steamObject.transform.SetParent(pad.transform, false);
        ParticleSystem steam = steamObject.AddComponent<ParticleSystem>();
        ParticleSystemRenderer steamRenderer = steam.GetComponent<ParticleSystemRenderer>();
        padFx.SetSystems(steam);
        GameObject foreignObject = Own(new GameObject("foreign effect, sibling of stock emitter"));
        foreignObject.transform.SetParent(pad.transform, false);
        ParticleSystem foreign = foreignObject.AddComponent<ParticleSystem>();
        ParticleSystemRenderer foreignRenderer = foreign.GetComponent<ParticleSystemRenderer>();
        var suppressor = new StockSurfaceFxSuppressor();
        try
        {
            Availability(suppressor, false);
            suppressor.Apply();
            Check("Unavailable backend preserves stock effects", stock.fxMax == 3.5f && steamRenderer.enabled);
            Check("NullRenderer availability does not hide stock", new NullRenderer().IsAvailable && stock.fxMax == 3.5f && steamRenderer.enabled);
            MethodInfo setRenderer = typeof(StockSurfaceFxSuppressor).GetMethod("SetReplacementRenderer");
            if (setRenderer != null) setRenderer.Invoke(suppressor, new object[] { new Replacement { IsAvailable = false } });
            suppressor.Apply();
            Check("Unsupported replacement preserves stock", stock.fxMax == 3.5f && steamRenderer.enabled);
            var disappearing = new Replacement();
            if (setRenderer != null) setRenderer.Invoke(suppressor, new object[] { disappearing });
            suppressor.Apply();
            disappearing.IsAvailable = false; // le backend disparaît sans lever d'exception dans Submit
            suppressor.Tick(3f);
            Check("Replacement disappearing silently restores stock", stock.fxMax == 3.5f && steamRenderer.enabled && foreignRenderer.enabled);
            Availability(suppressor, true);
            suppressor.Apply();
            Check("Available backend suppresses only stock effects", stock.fxMax == 0f && !steamRenderer.enabled && foreignRenderer.enabled);
            Availability(suppressor, false);
            suppressor.Tick(3f);
            Check("Runtime backend failure restores stock effects", stock.fxMax == 3.5f && steamRenderer.enabled && foreignRenderer.enabled);
            Availability(suppressor, true);
            suppressor.Apply();
            Check("Backend recovery suppresses stock again", stock.fxMax == 0f && !steamRenderer.enabled);
            suppressor.Restore();
            steamRenderer.enabled = false;
            suppressor.Apply();
            suppressor.Restore();
            Check("Initially disabled stock renderer stays disabled", !steamRenderer.enabled && stock.fxMax == 3.5f);
            Object.DestroyImmediate(engine);
            suppressor.Restore();
            Check("Destroyed modules clean up safely", suppressor.SuppressedCount == 0 && foreignRenderer.enabled);
        }
        finally { suppressor.Restore(); FlightGlobals.VesselsLoaded.Clear(); }
    }

    private static void SetPrivate(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
    private static void CacheTests()
    {
        GameObject a = Own(new GameObject("KK static A"));
        GameObject b = Own(new GameObject("KK static B"));
        var bridge = new KerbalKonstructsBridge();
        SetPrivate(bridge, "_probed", true);
        SetPrivate(bridge, "_getAll", typeof(Database).GetMethod("GetAllStatics"));
        SetPrivate(bridge, "_gameObject", typeof(Static).GetField("gameObject"));
        SetPrivate(bridge, "_launchSite", typeof(Static).GetField("launchSite"));
        SetPrivate(bridge, "<Available>k__BackingField", true);
        Database.Data = new[] { new Static { gameObject = a, launchSite = new Site { LaunchSiteName = "Alpha" } } };
        string site;
        Check("KK initial site recognized", bridge.TryClassify(a.transform, out site) && site == "Alpha");
        Database.Data = new[] { new Static { gameObject = b, launchSite = new Site { LaunchSiteName = "Beta" } } };
        SetPrivate(bridge, "_lastRefresh", -100f);
        Check("KK replacement at identical count recognized", bridge.TryClassify(b.transform, out site) && site == "Beta");
        Check("KK removed static no longer recognized", !bridge.TryClassify(a.transform, out site));
        Database.Data[0].launchSite.LaunchSiteName = "Gamma";
        SetPrivate(bridge, "_lastRefresh", -100f);
        Check("KK site metadata change at identical count recognized", bridge.TryClassify(b.transform, out site) && site == "Gamma");
        Database.Data = new Static[0];
        SetPrivate(bridge, "_lastRefresh", -100f);
        Check("KK empty database clears old sites", !bridge.TryClassify(b.transform, out site));
    }
}
