#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Roll the live WebGL build back to any previously-shipped version. Each ship
/// archives its exact output at <c>Builds/WebGL/&lt;version&gt;/{GameName}_WebGL/</c>;
/// "Promote to live" re-uploads that build's bytes to the live path and purges —
/// no rebuild, so the rollback is the precise artifact that was tested.
///
/// <para>On open it fetches the live <c>build-manifest.json</c> and reads
/// <c>productVersion</c> so the row matching what's currently live is marked and
/// disabled.</para>
/// </summary>
public sealed class WebGLRollbackWindow : EditorWindow {
    private string[] _versions = Array.Empty<string>();
    private string _liveVersion = "(checking…)";
    private bool _busy;

    [MenuItem("Pixygon/Build & Ship/Rollback WebGL…", priority = 20)]
    public static void Open() {
        var w = GetWindow<WebGLRollbackWindow>(true, "Rollback WebGL", true);
        w.minSize = new Vector2(440, 320);
        w.Refresh();
    }

    private void Refresh() {
        var root = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", "WebGL");
        _versions = Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Where(d => Path.GetFileName(d) != "latest")
                .Where(d => File.Exists(ManifestPath(Path.GetFileName(d))))
                .OrderByDescending(SemverKey)
                .Select(Path.GetFileName)
                .ToArray()
            : Array.Empty<string>();
        _ = FetchLiveVersion();
        Repaint();
    }

    private static string ManifestPath(string version) =>
        Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", "WebGL", version,
                     Pixygon.BuildTools.BuildTools.GameName + "_WebGL", "build-manifest.json");

    /// <summary>Sortable key so "0.10.0" ranks above "0.9.0" (numeric, not lexical).</summary>
    private static string SemverKey(string dir) {
        var name = Path.GetFileName(dir);
        return Regex.Replace(name, @"\d+", m => m.Value.PadLeft(10, '0'));
    }

    private async Task FetchLiveVersion() {
        try {
            var cfg = BunnyConfigLoader.Load();
            var url = $"https://{cfg.PullZoneHost}/WebGL/{Pixygon.BuildTools.BuildTools.GameName}_WebGL/" +
                      $"build-manifest.json?ts={DateTime.UtcNow.Ticks}"; // bust edge cache for the read
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(url);
            var m = Regex.Match(json, "\"productVersion\"\\s*:\\s*\"([^\"]+)\"");
            _liveVersion = m.Success ? m.Groups[1].Value : "(unknown)";
        } catch {
            _liveVersion = "(unreachable)";
        }
        Repaint();
    }

    private void OnGUI() {
        EditorGUILayout.HelpBox(
            "Promote a previously-built local version back to the live WebGL path. " +
            "Re-uploads that build's exact bytes and purges the cache.", MessageType.Info);

        using (new EditorGUILayout.HorizontalScope()) {
            EditorGUILayout.LabelField("Currently live:", _liveVersion, EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
        }
        EditorGUILayout.Space();

        if (_versions.Length == 0) {
            EditorGUILayout.LabelField("No local versioned builds under Builds/WebGL/.");
            return;
        }

        using (new EditorGUI.DisabledScope(_busy)) {
            foreach (var v in _versions) {
                using (new EditorGUILayout.HorizontalScope()) {
                    var isLive = v == _liveVersion;
                    EditorGUILayout.LabelField(isLive ? $"{v}   (LIVE)" : v);
                    using (new EditorGUI.DisabledScope(isLive)) {
                        if (GUILayout.Button("Promote to live", GUILayout.Width(140)))
                            _ = Promote(v);
                    }
                }
            }
        }
    }

    private async Task Promote(string version) {
        var cfg = BunnyConfigLoader.Load();
        if (!cfg.HasStorageKey) {
            EditorUtility.DisplayDialog("Bunny credentials missing",
                "No storage-zone API key. Set one in Pixygon → Build & Ship → Bunny Credentials…", "OK");
            return;
        }
        var purgeNote = cfg.HasAccountKey ? "" : "\n\n⚠ No account key — edge cache will NOT be purged.";
        if (!EditorUtility.DisplayDialog($"Roll back to v{version}?",
                $"Re-uploads the v{version} build to the live path on {cfg.PullZoneHost}, " +
                $"overwriting the current live build.{purgeNote}",
                $"Promote v{version}", "Cancel"))
            return;

        var buildRoot = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", "WebGL", version,
                                     Pixygon.BuildTools.BuildTools.GameName + "_WebGL");
        _busy = true;
        try {
            var ok = await BuildAndShip.UploadAndPurgeWebGL(buildRoot, cfg);
            if (ok) Debug.Log($"[Rollback] Live WebGL is now v{version}.");
        } finally {
            _busy = false;
            Refresh();
        }
    }
}
#endif
