using System;
using System.IO;
using System.Linq;
using UnityEditor;

public static class BuildPlayer
{
    public static void BuildWindows64()
    {
        string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "Builds", "capture-hotfix");
        Directory.CreateDirectory(outputDir);

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException("No enabled scenes found in EditorBuildSettings.");
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outputDir, "MDSK_360_VideoPlayer.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new InvalidOperationException("Build failed: " + report.summary.result);
        }

        UnityEngine.Debug.Log("Build succeeded: " + options.locationPathName);
    }
}
