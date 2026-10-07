using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Construction autonome du bundle GroundBlastFx. Aucune dépendance au projet PlumeDynamics.
public static class GroundBlastFxBundleBuilder
{
    public static void Build()
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outDir = Path.Combine(project, "Build");
        Directory.CreateDirectory(outDir);
        const string noisePath = "Assets/GroundBlastFx/Noise128.asset";
        if (AssetDatabase.LoadAssetAtPath<Texture3D>(noisePath) == null)
            NoiseBaker.Create(noisePath, 128);
        var map = new AssetBundleBuild
        {
            assetBundleName = "groundblastfx",
            assetNames = new[]
            {
                "Assets/GroundBlastFx/GroundVolume.shader",
                "Assets/GroundBlastFx/GroundDebris.shader",
                "Assets/GroundBlastFx/VolumeField.compute",
                noisePath
            }
        };
        var manifests = BuildPipeline.BuildAssetBundles(outDir, new[] { map },
            BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.DeterministicAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifests == null) throw new Exception("BuildPipeline a échoué");
        string source = Path.Combine(outDir, "groundblastfx");
        if (!File.Exists(source)) throw new Exception("Bundle absent après compilation");
        string target = Path.GetFullPath(Path.Combine(project, "..", "GameData", "Volumetric Ground FX", "Shaders", "GroundBlastFx.unity3d"));
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.Copy(source, target, true);
        File.WriteAllText(Path.Combine(outDir, "build-info.txt"),
            "GroundBlastFx bundle Windows64 / Unity " + Application.unityVersion + "\n" +
            "Shader: GroundBlastFx/GroundVolume\nCompute: VolumeField.compute\n");
        Debug.Log("[GroundBlastFx] Bundle compilé : " + target);
    }

    // Bruit 3D périodique ; trois canaux décorrélés pour le domain warping.
    private static void CreateNoise(string path)
    {
        const int size = 64;
        var pixels = new Color[size * size * size];
        for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size, w = z / (float)size;
                    pixels[x + size * (y + size * z)] = new Color(
                        Fbm(u, v, w, 3), Fbm(u, v, w, 19), Fbm(u, v, w, 41),
                        Hash(x, y, z, 73) / 16777215f);
                }
        var texture = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
        {
            name = "GroundBlastFx periodic 3D noise",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear
        };
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        AssetDatabase.CreateAsset(texture, path);
        AssetDatabase.SaveAssets();
    }

    private static float Fbm(float u, float v, float w, int seed)
    {
        return 0.54f * Value(u, v, w, 8, seed) +
               0.29f * Value(u, v, w, 16, seed + 5) +
               0.17f * Value(u, v, w, 32, seed + 11);
    }

    private static float Value(float u, float v, float w, int cells, int seed)
    {
        float px = u * cells, py = v * cells, pz = w * cells;
        int ix = Mathf.FloorToInt(px), iy = Mathf.FloorToInt(py), iz = Mathf.FloorToInt(pz);
        float fx = px - ix, fy = py - iy, fz = pz - iz;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float sum = 0;
        for (int dz = 0; dz <= 1; dz++)
            for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    float weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
                    sum += weight * (Hash((ix + dx) % cells, (iy + dy) % cells, (iz + dz) % cells, seed) / 16777215f);
                }
        return sum;
    }

    private static uint Hash(int x, int y, int z, int seed)
    {
        uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed * 104729);
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h & 0xffffff;
    }
}
