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

    public static void Android() => Build(BuildTarget.Android, "Builds/FlagRunner.apk");

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
