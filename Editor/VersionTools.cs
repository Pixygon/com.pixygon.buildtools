#if UNITY_EDITOR
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Patch-version bumping for the ship flow. The single source of truth is
/// <see cref="PlayerSettings.bundleVersion"/> (per CLAUDE.md) — BuildInfo reads
/// <c>Application.version</c> from it, and the build script stamps it into
/// <c>build-manifest.json</c>, so a bump here flows everywhere automatically.
///
/// <para>Only the Build &amp; Ship menu bumps — plain <c>Pixiel/Build/*</c> builds
/// reuse the current version (so local test builds don't inflate it).</para>
/// </summary>
internal static class VersionTools {
    /// <summary>
    /// Increment the trailing integer of a version string:
    /// "0.5.0" → "0.5.1", "1.2" → "1.3", "0.5.0-beta" → "0.5.1-beta".
    /// If there's no numeric component at all, appends ".1".
    /// </summary>
    public static string NextPatch(string version) {
        if (string.IsNullOrEmpty(version)) return "0.0.1";
        // prefix (non-greedy) + last run of digits + trailing non-digits.
        var m = Regex.Match(version, @"^(.*?)(\d+)(\D*)$");
        if (!m.Success) return version + ".1";
        var num = long.Parse(m.Groups[2].Value) + 1;
        return m.Groups[1].Value + num + m.Groups[3].Value;
    }

    /// <summary>Persist the new version to Player Settings.</summary>
    public static void Apply(string version) {
        PlayerSettings.bundleVersion = version;
        AssetDatabase.SaveAssets(); // flush ProjectSettings.asset now, not on quit
        Debug.Log($"[Version] bundleVersion → {version}");
    }
}
#endif
