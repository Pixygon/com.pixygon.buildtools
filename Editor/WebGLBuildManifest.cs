#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Emits <c>build-manifest.json</c> next to the WebGL build's <c>index.html</c> so
/// the website (PixygonClient / <c>GameContainer.tsx</c>) can DISCOVER the exact
/// loader / data / framework / wasm filenames + compression instead of guessing
/// them by string concatenation.
///
/// <para><b>Why this exists.</b> The site used to build URLs as
/// <c>{folderName}.{ext}</c>. Any change to "Name Files As Hashes", the
/// compression format, or the output folder name silently renamed the files and
/// 404'd the loader. The manifest makes a build self-describing: the site fetches
/// ONE stable URL (<c>{baseUrl}/build-manifest.json</c>) and reads the real paths
/// out of it. New builds then drop in with zero site code changes.</para>
///
/// <para><b>Runs for EVERY WebGL build</b> — the <c>Pixiel/Build/WebGL</c> menu in
/// <see cref="Pixygon.BuildTools.BuildTools"/> AND a manual <c>File → Build Settings</c> build —
/// because it hooks the post-process callback, not a specific menu item. (The
/// build we actually shipped last time came from a manual build, so a menu-only
/// hook would have missed it.)</para>
///
/// <para>Manifest shape — paths are relative to the build root (the folder that
/// holds <c>index.html</c>), so the site just prepends its <c>baseUrl</c>:</para>
/// <code>
/// {
///   "loaderUrl":          "Build/PixielDreadwager_WebGL.loader.js",
///   "dataUrl":            "Build/PixielDreadwager_WebGL.data.unityweb",
///   "frameworkUrl":       "Build/PixielDreadwager_WebGL.framework.js.unityweb",
///   "codeUrl":            "Build/PixielDreadwager_WebGL.wasm.unityweb",
///   "streamingAssetsUrl": "StreamingAssets",
///   "compression":        "unityweb",        // unityweb | br | gz | none
///   "productVersion":     "0.5.0",
///   "builtAtUtc":         "2026-06-01T12:34:56Z",
///   "unityVersion":       "6000.4.3f1"
/// }
/// </code>
///
/// <para><b>Site-side caching note:</b> serve THIS one file with no-cache (or a
/// cache-busting query) — its URL is stable while its contents change every
/// build. The files it points to can be cached hard.</para>
/// </summary>
public sealed class WebGLBuildManifest : IPostprocessBuildWithReport {
    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report) {
        if (report.summary.platform != BuildTarget.WebGL)
            return;

        var buildRoot = report.summary.outputPath; // folder that contains index.html
        var buildDir = Path.Combine(buildRoot, "Build");
        if (!Directory.Exists(buildDir)) {
            Debug.LogWarning($"[WebGLBuildManifest] No Build/ dir at {buildDir}; manifest skipped.");
            return;
        }

        var names = Directory.GetFiles(buildDir).Select(Path.GetFileName).ToArray();

        // Match by suffix/segment so this is agnostic to product name, folder
        // name, AND hashed filenames (df0a..loader.js still ends in .loader.js).
        string loader    = names.FirstOrDefault(n => n.EndsWith(".loader.js", StringComparison.Ordinal));
        string framework = names.FirstOrDefault(n => n.Contains(".framework.js"));
        string wasm      = names.FirstOrDefault(n => n.Contains(".wasm"));
        string data      = names.FirstOrDefault(n => n.Contains(".data"));

        if (loader == null || framework == null || wasm == null || data == null) {
            Debug.LogWarning("[WebGLBuildManifest] Could not resolve all four core files " +
                             $"(loader={loader}, framework={framework}, wasm={wasm}, data={data}). " +
                             "Manifest NOT written.");
            return;
        }

        var manifest = new Manifest {
            loaderUrl = "Build/" + loader,
            dataUrl = "Build/" + data,
            frameworkUrl = "Build/" + framework,
            codeUrl = "Build/" + wasm,
            streamingAssetsUrl = "StreamingAssets",
            compression = DetectCompression(data),
            productVersion = Application.version,
            builtAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            unityVersion = Application.unityVersion,
        };

        var json = JsonUtility.ToJson(manifest, prettyPrint: true);
        var manifestPath = Path.Combine(buildRoot, "build-manifest.json");
        File.WriteAllText(manifestPath, json);
        Debug.Log($"[WebGLBuildManifest] Wrote {manifestPath}\n{json}");
    }

    /// <summary>
    /// unityweb (decompression fallback) | br | gz | none — read off the data
    /// filename's tail after ".data".
    /// </summary>
    private static string DetectCompression(string dataFile) {
        var idx = dataFile.IndexOf(".data", StringComparison.Ordinal);
        if (idx < 0) return "none";
        var tail = dataFile.Substring(idx + ".data".Length).TrimStart('.');
        return string.IsNullOrEmpty(tail) ? "none" : tail;
    }

    [Serializable]
    private class Manifest {
        public string loaderUrl;
        public string dataUrl;
        public string frameworkUrl;
        public string codeUrl;
        public string streamingAssetsUrl;
        public string compression;
        public string productVersion;
        public string builtAtUtc;
        public string unityVersion;
    }
}
#endif
