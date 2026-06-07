using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Pixygon.BuildTools {
    /// <summary>
    /// One-button, multi-platform build entry points for any Pixygon Unity game.
    /// Generalized from Pixiel Dreadwager's BuildScripts so every project gets the
    /// same pipeline with near-zero config — <see cref="GameName"/> derives from
    /// <c>PlayerSettings.productName</c>, scenes come from the Build Profiles list,
    /// and output follows a fixed convention.
    ///
    /// <para><b>Menu</b> (under <c>Pixygon/Build/</c>): WebGL, Windows, macOS, Linux,
    /// and "All Standalone + WebGL".</para>
    ///
    /// <para><b>Conventions</b></para>
    /// <list type="bullet">
    /// <item>Output: <c>&lt;projectRoot&gt;/Builds/&lt;target&gt;/&lt;version&gt;/</c>;
    /// version is <see cref="PlayerSettings.bundleVersion"/>. WebGL also mirrors to
    /// <c>Builds/WebGL/latest/</c> as the stable deploy head.</item>
    /// <item>Scenes: <see cref="EditorBuildSettings.scenes"/> filtered to enabled —
    /// edit the Build Profiles list, never this file.</item>
    /// <item>WebGL is forced to Brotli + hashed filenames + decompression-fallback
    /// for own-site/CDN hosting. Standalone is forced to Mono (IL2CPP can be opted
    /// into per project).</item>
    /// <item>Never quits the editor on failure; never adds define symbols at build
    /// time (that would be invisible drift).</item>
    /// </list>
    ///
    /// <para>The ship layer (BunnyCDN upload, version bump, WebGL manifest, macOS
    /// notarization, the cross-reload "Build &amp; Ship ALL" queue, and the batchmode
    /// CLI) migrates here next — see the package README for that design.</para>
    /// </summary>
    public static class BuildTools {
        private const string MenuRoot = "Pixygon/Build/";

        /// <summary>Canonical, space-free game name derived from the product name
        /// (non-alphanumerics stripped): "Pixiel: Dreadwager" → "PixielDreadwager",
        /// "Veilwalkers" → "Veilwalkers". Drives output filenames + the (future)
        /// CDN upload paths. Override by renaming productName in Player Settings.</summary>
        public static string GameName =>
            new string(PlayerSettings.productName.Where(char.IsLetterOrDigit).ToArray());

        /// <summary>WebGL output leaf folder — Unity names the build files after this
        /// (<c>{GameName}_WebGL.loader.js</c>, …), the stable name the website expects.</summary>
        private static string WebGLFolderName => $"{GameName}_WebGL";

        // ── WebGL ────────────────────────────────────────────────────────────
        [MenuItem(MenuRoot + "WebGL (Release)")]
        public static void BuildWebGL() => BuildWebGLInternal();

        /// <summary>Build WebGL + mirror to <c>Builds/WebGL/latest/</c>. Returns the
        /// build root (index.html + Build/) on success, or null on failure.</summary>
        public static string BuildWebGLInternal(bool allowSwitch = false) {
            var version = PlayerSettings.bundleVersion;
            var outDir = Path.Combine(ProjectRoot, "Builds", "WebGL", version, WebGLFolderName);
            EnsureCleanDir(outDir);
            if (!EnsureActiveTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL, allowSwitch)) return null;

            // Own-site/CDN hosting profile: Brotli + content-hashed names (cache
            // forever) + JS decompression fallback (so a CDN's 206 range responses
            // on .br files don't break the load). Explicitly-thrown exceptions only
            // (~3x faster scripts). These also persist to ProjectSettings so an
            // editor build and a CLI build match.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

            var options = new BuildPlayerOptions {
                scenes = EnabledScenePaths(),
                locationPathName = outDir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            };
            Debug.Log($"[Build] WebGL → {outDir}  (v{version})");
            var report = BuildPipeline.BuildPlayer(options);
            LogReport(report, "WebGL");
            if (report.summary.result != BuildResult.Succeeded) return null;

            var latestDir = Path.Combine(ProjectRoot, "Builds", "WebGL", "latest");
            MirrorDir(outDir, latestDir);
            Debug.Log($"[Build] WebGL latest → {latestDir}");
            return outDir;
        }

        // ── Windows ──────────────────────────────────────────────────────────
        [MenuItem(MenuRoot + "Windows (Mono)")]
        public static void BuildWindowsMono() => BuildWindowsMonoInternal();

        /// <summary>Standalone Windows x64 (Mono). Returns the build folder (.exe + Data) or null.</summary>
        public static string BuildWindowsMonoInternal(bool allowSwitch = false) {
            var version = PlayerSettings.bundleVersion;
            var outDir = Path.Combine(ProjectRoot, "Builds", "Windows", version);
            EnsureCleanDir(outDir);
            if (!EnsureActiveTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64, allowSwitch)) return null;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);

            var exePath = Path.Combine(outDir, SafeFilename($"{GameName}-{version}.exe"));
            var options = new BuildPlayerOptions {
                scenes = EnabledScenePaths(),
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            Debug.Log($"[Build] Windows (Mono) → {exePath}  (v{version})");
            var report = BuildPipeline.BuildPlayer(options);
            LogReport(report, "Windows");
            return report.summary.result == BuildResult.Succeeded ? outDir : null;
        }

        // ── macOS ────────────────────────────────────────────────────────────
        [MenuItem(MenuRoot + "macOS (Mono)")]
        public static void BuildMacMono() => BuildMacInternal();

        /// <summary>Standalone macOS (Mono) .app bundle. Returns the .app path or null.</summary>
        public static string BuildMacInternal(bool allowSwitch = false) {
            var version = PlayerSettings.bundleVersion;
            var outDir = Path.Combine(ProjectRoot, "Builds", "macOS", version);
            EnsureCleanDir(outDir);
            if (!EnsureActiveTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX, allowSwitch)) return null;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);

            var appPath = Path.Combine(outDir, SafeFilename($"{GameName}.app"));
            var options = new BuildPlayerOptions {
                scenes = EnabledScenePaths(),
                locationPathName = appPath,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            Debug.Log($"[Build] macOS (Mono) → {appPath}  (v{version})");
            var report = BuildPipeline.BuildPlayer(options);
            LogReport(report, "macOS");
            return report.summary.result == BuildResult.Succeeded ? appPath : null;
        }

        // ── Linux ────────────────────────────────────────────────────────────
        [MenuItem(MenuRoot + "Linux (Mono)")]
        public static void BuildLinuxMono() => BuildLinuxInternal();

        /// <summary>Standalone Linux x64 (Mono). Returns the build folder ({GameName}.x86_64 + Data) or null.</summary>
        public static string BuildLinuxInternal(bool allowSwitch = false) {
            var version = PlayerSettings.bundleVersion;
            var outDir = Path.Combine(ProjectRoot, "Builds", "Linux", version);
            EnsureCleanDir(outDir);
            if (!EnsureActiveTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64, allowSwitch)) return null;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);

            var exePath = Path.Combine(outDir, SafeFilename($"{GameName}.x86_64"));
            var options = new BuildPlayerOptions {
                scenes = EnabledScenePaths(),
                locationPathName = exePath,
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            Debug.Log($"[Build] Linux (Mono) → {exePath}  (v{version})");
            var report = BuildPipeline.BuildPlayer(options);
            LogReport(report, "Linux");
            return report.summary.result == BuildResult.Succeeded ? outDir : null;
        }

        // ── All ──────────────────────────────────────────────────────────────
        [MenuItem(MenuRoot + "All Standalone + WebGL")]
        public static void BuildAll() {
            // Windows first — failures there are faster to diagnose, and the long
            // WebGL build is wasted effort if a standalone target is broken.
            // allowSwitch: this blocks the main thread for the whole run, so the
            // recompile a target switch queues can only reload after we return.
            BuildWindowsMonoInternal(allowSwitch: true);
            BuildMacInternal(allowSwitch: true);
            BuildLinuxInternal(allowSwitch: true);
            BuildWebGLInternal(allowSwitch: true);
        }

        // ── internals ──────────────────────────────────────────────────────────
        /// <summary>
        /// Ensure <paramref name="target"/> is the ACTIVE build target before building
        /// (required so Addressables-on-player-build builds content for the right
        /// platform). On a mismatch it switches and, for an async single-platform run,
        /// aborts (returns false) so the queued domain reload settles while idle rather
        /// than killing an in-flight build. Batchmode / a blocking "build all" continue.
        /// </summary>
        private static bool EnsureActiveTarget(BuildTargetGroup group, BuildTarget target, bool allowSwitch) {
            if (EditorUserBuildSettings.activeBuildTarget == target) return true;
            var from = EditorUserBuildSettings.activeBuildTarget;
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target)) {
                Debug.LogError($"[Build] Could not switch active build target to {target}. Is that platform module installed?");
                return false;
            }
            if (Application.isBatchMode || allowSwitch) {
                Debug.Log($"[Build] Switched active target {from} → {target} (continuing in-process).");
                return true;
            }
            Debug.LogWarning($"[Build] Active build target was {from}; switched to {target}. " +
                             "Unity is recompiling for the new platform — wait for it to finish, then build again.");
            return false;
        }

        /// <summary>The Unity project root — one level up from /Assets.</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;

        private static string[] EnabledScenePaths() =>
            EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

        // Wipe + recreate so a build can never inherit stale files (per-version
        // history is preserved by the <version> subfolder, so nothing's lost).
        private static void EnsureCleanDir(string dir) {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }

        private static void MirrorDir(string src, string dest) {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dest));
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(src, dest), overwrite: true);
        }

        private static void LogReport(BuildReport report, string label) {
            var s = report.summary;
            if (s.result == BuildResult.Succeeded) {
                Debug.Log($"[Build] {label} OK · {FormatSize(s.totalSize)} · {s.totalTime.TotalSeconds:F1}s · {s.outputPath}");
                EditorUtility.RevealInFinder(s.outputPath);
            } else {
                Debug.LogError($"[Build] {label} FAILED · errors={s.totalErrors} · warnings={s.totalWarnings} · {s.outputPath}");
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"  · {step.name}: {msg.content}");
            }
        }

        private static string FormatSize(ulong bytes) {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            var i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:F1} {units[i]}";
        }

        private static string SafeFilename(string name) {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name;
        }
    }
}
