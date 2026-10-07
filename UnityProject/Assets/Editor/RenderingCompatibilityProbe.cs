using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GroundBlastFx.Contracts;
using GroundBlastFx.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel;

// Contrôles natifs du code de rendu et du bundle reçu : ni KSP, ni Scatterer/TUFX simulés.
public static class RenderingCompatibilityProbe
{
    private static readonly List<Object> Owned = new List<Object>();
    private static CompatibilityProbe.Report _report;
    private static T Own<T>(T value) where T : Object { Owned.Add(value); return value; }
    private static void Check(string name, bool passed, string detail)
    {
        _report.tests++;
        if (!passed) _report.failures++;
        _report.cases.Add(new CompatibilityProbe.Result { name = name, passed = passed, detail = detail });
    }
    private static void Near(string name, float actual, float expected, float tolerance = 0.00001f)
    {
        Check(name, Mathf.Abs(actual - expected) < tolerance, "expected=" + expected + " actual=" + actual);
    }
    private static Camera CameraAt(float z, int size)
    {
        var camera = Own(new GameObject("compat camera " + z)).AddComponent<Camera>();
        camera.enabled = false;
        camera.transform.position = new Vector3(0f, 0f, z);
        camera.fieldOfView = 50f;
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 100f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.renderingPath = RenderingPath.Forward;
        camera.depthTextureMode = DepthTextureMode.Depth;
        var target = Own(new RenderTexture(size, size, 24, RenderTextureFormat.ARGBFloat));
        target.Create();
        camera.targetTexture = target;
        return camera;
    }
    private static float ReadCenter(RenderTexture texture)
    {
        RenderTexture previous = RenderTexture.active;
        var read = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
        try
        {
            RenderTexture.active = texture;
            read.ReadPixels(new Rect(texture.width / 2, texture.height / 2, 1, 1), 0, 0);
            read.Apply();
            return read.GetPixel(0, 0).r;
        }
        finally { RenderTexture.active = previous; }
    }

    public static void Run()
    {
        _report = new CompatibilityProbe.Report {
            stage = Environment.GetEnvironmentVariable("GE_COMPAT_STAGE"),
            environment = "Unity 2019.4 native DX11, production RenderCore and received shader bundle; not a KSP flight"
        };
        AssetBundle bundle = null;
        RenderCore core = null;
        CommandBuffer commands = null, firstRead = null, secondRead = null;
        Camera first = null, second = null;
        try
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            bundle = AssetBundle.LoadFromFile(Path.Combine(root, "GameData/Volumetric Ground FX/Shaders/GroundBlastFx.unity3d"));
            RenderCore.ForceAnalytic = true;
            core = new RenderCore(bundle);
            if (!core.IsAvailable) throw new Exception(core.Status);
            var settings = new RendererSettings { Quality = QualityLevel.Low };
            core.Configure(settings);
            first = CameraAt(0f, 64);
            second = CameraAt(-2f, 32);
            var env = new RenderEnvironment { SunDir = Vector3.up, SunColor = Color.white, AmbientSky = Color.gray };
            var cluster = new ImpingementCluster {
                Id = 1, Surface = SurfaceKind.Terrain, Medium = MediumKind.Atmosphere,
                ImpactPointWorld = new Vector3(0f, 0f, 20f), CloudAnchorWorld = new Vector3(0f, 0f, 20f),
                SurfaceNormalWorld = Vector3.up, CloudUpWorld = Vector3.up, CloudNorthWorld = Vector3.forward,
                CloudFrontRadiusM = 2f, MaxCloudRadiusM = 3f, ImpingementRadiusM = 1f,
                Intensity01 = 1f, Visibility01 = 1f, DustAlbedoA = Color.white, DustAlbedoB = Color.gray,
                AmbientPressurePa = 101325f, GravityMs2 = 9.81f
            };
            core.Prepare(first, new[] { cluster }, 1, new ScorchMark[0], 0, settings, ref env);
            Material material = (Material)typeof(RenderCore).GetField("_volume", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(core);
            commands = new CommandBuffer { name = "GE compatibility volume" };
            core.Record(commands, 64, 64, 2, 1);
            Near("Initial pixel cone", material.GetFloat("_GEPixelAngle"), 2f * Mathf.Tan(25f * Mathf.Deg2Rad) / 32f);
            first.fieldOfView = 82f; // changement tardif, après Prepare/Record, comme un mod de caméra
            first.transform.position = new Vector3(3f, 2f, -1f);
            first.transform.rotation = Quaternion.Euler(7f, 15f, 2f);
            core.UpdateCamera(first);
            Near("Late zoom updates pixel cone", material.GetFloat("_GEPixelAngle"), 2f * Mathf.Tan(41f * Mathf.Deg2Rad) / 32f);
            Check("Late camera move updates world origin", Vector3.Distance(material.GetVector("_GECamera"), first.transform.position) < 0.0001f, "native camera moved after Prepare");
            Vector3 ray = first.ViewportPointToRay(new Vector3(1f, 1f, 0f)).direction;
            Check("Late camera rotation and zoom update rays", Vector3.Distance(material.GetVector("_GERay11"), ray) < 0.0001f, "native camera rotated after Prepare");
            core.Record(commands, 128, 128, 2, 1);
            Near("Resolution change retains current zoom", material.GetFloat("_GEPixelAngle"), 2f * Mathf.Tan(41f * Mathf.Deg2Rad) / 64f);

            first.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            first.fieldOfView = 50f;
            core.UpdateCamera(first);
            core.Record(commands, 64, 64, 2, 1);
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.transform.position = new Vector3(0f, 0f, 5f);
            cube.GetComponent<Renderer>().sharedMaterial = Own(new Material(Shader.Find("Standard")));
            var reader = Own(new Material(Shader.Find("Hidden/GroundBlastFx/CompatibilityDepth")));
            var firstDepth = Own(new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBFloat)); firstDepth.Create();
            var secondDepth = Own(new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat)); secondDepth.Create();
            firstRead = new CommandBuffer { name = "external depth consumer first" };
            secondRead = new CommandBuffer { name = "external depth consumer second" };
            firstRead.Blit(Texture2D.blackTexture, firstDepth, reader);
            secondRead.Blit(Texture2D.blackTexture, secondDepth, reader);
            first.AddCommandBuffer(RenderCore.Event, commands);
            first.AddCommandBuffer(CameraEvent.AfterForwardAlpha, firstRead);
            second.AddCommandBuffer(CameraEvent.AfterForwardAlpha, secondRead);
            first.Render();
            Near("Late effect reads scene depth after GroundBlastFx", ReadCenter(firstDepth), 4.5f, 0.02f);
            second.Render();
            Near("Secondary camera regenerates its own depth", ReadCenter(secondDepth), 6.5f, 0.02f);
            first.RemoveCommandBuffer(RenderCore.Event, commands);
            first.Render();
            Near("Scene depth returns after own buffer removal", ReadCenter(firstDepth), 4.5f, 0.02f);
            Check("External command buffer retained", first.GetCommandBuffers(CameraEvent.AfterForwardAlpha).Length == 1, "external depth pass remains attached");
        }
        catch (Exception error) { Check("render setup", false, error.ToString()); }
        finally
        {
            if (first != null) first.RemoveAllCommandBuffers();
            if (second != null) second.RemoveAllCommandBuffers();
            commands?.Release(); firstRead?.Release(); secondRead?.Release();
            core?.Dispose();
            for (int i = Owned.Count - 1; i >= 0; i--) if (Owned[i] != null) Object.DestroyImmediate(Owned[i]);
            Owned.Clear();
            if (bundle != null) bundle.Unload(true);
        }
        string output = Environment.GetEnvironmentVariable("GE_COMPAT_OUT");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "rendering.json"), JsonUtility.ToJson(_report, true));
        Debug.Log("[GroundBlastFx render compat] tests=" + _report.tests + " failures=" + _report.failures);
        EditorApplication.Exit(_report.failures > 0 ? 1 : 0);
    }
}
