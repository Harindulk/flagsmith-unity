using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

/// <summary>Command-line builds: unity build Sample --target WebGL --execute-method SampleBuild.WebGL -o Builds/WebGL</summary>
public static class SampleBuild
{
    public static void WebGL()
    {
        // Uncompressed so any static file server works without special headers.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        Build(BuildTarget.WebGL, "Builds/WebGL");
    }

    /// <summary>
    /// Phone builds can't reach "localhost": set FLAGSMITH_API_URL (e.g. http://192.168.1.20:8000/api/v1/) to the
    /// Mac's LAN address and it is baked into this build only. Plain HTTP is allowed because the demo server has no TLS.
    /// </summary>
    public static void Android()
    {
        const string config = "Assets/Resources/flagsmith.json";
        var original = System.IO.File.ReadAllText(config);
        var url = Environment.GetEnvironmentVariable("FLAGSMITH_API_URL");
        try
        {
            if (!string.IsNullOrEmpty(url))
            {
                var c = UnityEngine.JsonUtility.FromJson<Connection>(original);
                c.api_url = url;
                System.IO.File.WriteAllText(config, UnityEngine.JsonUtility.ToJson(c, true));
                AssetDatabase.ImportAsset(config);
            }
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // shortcut: demo only; use HTTPS for a real game
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft; // the runner is a landscape game
            Build(BuildTarget.Android, "Builds/FlagRunner.apk");
        }
        finally
        {
            System.IO.File.WriteAllText(config, original);
            AssetDatabase.ImportAsset(config);
        }
    }

    [Serializable] class Connection { public string api_url; public string environment_key; }

    static void Build(BuildTarget target, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "-buildOutput");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback,
            target = target,
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception($"Build failed: {report.summary.result}, {report.summary.totalErrors} errors");
    }
}
