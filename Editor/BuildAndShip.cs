#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-keystroke "build then deploy to live" entry points. Builds via
/// <see cref="Pixygon.BuildTools.BuildTools"/> (so all the Brotli / decompression-fallback / hash
/// settings stay in one place), bumps the patch version, then uploads to
/// BunnyCDN via <see cref="BunnyUploader"/> and purges the edge cache.
///
/// <para><b>This overwrites the LIVE build.</b> A storage-zone key has write
/// access to every Pixygon game in the bucket, and these menu items have
/// shortcuts, so each ship is gated behind a confirmation dialog that names the
/// exact remote path + the version being shipped.</para>
///
/// <para><b>Rollback:</b> every ship keeps a local archive at
/// <c>Builds/WebGL/&lt;version&gt;/</c>. <see cref="WebGLRollbackWindow"/> re-promotes
/// any of those exact builds to the live path without rebuilding.</para>
///
/// <para>Bunny layout this follows (contract — the site matches these paths):
/// WebGL → <c>WebGL/{GameName}_WebGL/</c>, Windows → <c>Builds/{GameName}_win.zip</c>.</para>
/// </summary>
public static class BuildAndShip {
    private const string Menu = "Pixygon/Build & Ship/";

    // WebGL ships directly in-editor (Ctrl+Alt+W) — it's the active target, no switch.
    [MenuItem(Menu + "WebGL %&w", priority = 0)]
    public static async void ShipWebGL() => await RunWebGL(standalone: true);

    // Desktop targets need a build-target switch, which interactive Unity can't do for
    // Addressables. These hand off to a headless batchmode build that CLOSES and REOPENS
    // the editor automatically (no keyboard shortcuts — closing the editor by accident
    // would be rude). See BuildHandoff.
    [MenuItem(Menu + "Windows", priority = 1)]
    public static void ShipWindows() => BuildHandoff.Launch(new[] { "windows" });

    [MenuItem(Menu + "macOS", priority = 2)]
    public static void ShipMac() => BuildHandoff.Launch(new[] { "mac" });

    [MenuItem(Menu + "Linux", priority = 3)]
    public static void ShipLinux() => BuildHandoff.Launch(new[] { "linux" });

    [MenuItem(Menu + "All Desktop (Windows + macOS)", priority = 4)]
    public static void ShipAllDesktop() => BuildHandoff.Launch(new[] { "windows", "mac" });

    // The one-button release: WebGL first, then the desktop targets — each in its own
    // batchmode process (the handoff closes + reopens the editor), so no mid-session
    // build-target switch ever poisons Addressables/SBP. Linux is omitted (its
    // Addressables build is broken — ship it explicitly if/when fixed).
    [MenuItem(Menu + "Everything (no version bump)", priority = 5)]
    public static void ShipEverything() => BuildHandoff.Launch(new[] { "webgl", "windows", "mac" });

    // ---- version ------------------------------------------------------------
    // Shipping is PER-PLATFORM and does NOT auto-bump — every ship uses the current
    // PlayerSettings version, so you can ship WebGL, Windows and macOS one at a time and
    // they all land on the SAME version. Bump deliberately, once, when you start a new
    // release. For an unattended all-platform run, the "Build & Ship EVERYTHING" item
    // above (or `./ship.sh all` with the editor closed) builds each target headless.

    [MenuItem(Menu + "Bump Patch Version", priority = 10)]
    public static void BumpPatchVersion() {
        var cur = PlayerSettings.bundleVersion;
        var next = VersionTools.NextPatch(cur);
        if (EditorUtility.DisplayDialog("Bump patch version?",
                $"{cur}  →  {next}\n\nThis is the version your next ships will use (until you bump again).",
                $"Bump to {next}", "Cancel"))
            VersionTools.Apply(next);
    }

    // ---- WebGL --------------------------------------------------------------

    private static async Task<bool> RunWebGL(bool standalone) {
        var cfg = BunnyConfigLoader.Load();
        var remoteDir = $"WebGL/{Pixygon.BuildTools.BuildTools.GameName}_WebGL";
        var liveUrl = $"https://{cfg.PullZoneHost}/{remoteDir}/index.html";

        if (standalone) {
            if (!Confirm("WebGL", $"v{PlayerSettings.bundleVersion}\n{remoteDir}/  →  {liveUrl}", cfg)) return false;
        }

        EditorUtility.DisplayProgressBar("Build & Ship WebGL", "Building player…", 0.05f);
        var buildRoot = Pixygon.BuildTools.BuildTools.BuildWebGLInternal();
        if (buildRoot == null) { EditorUtility.ClearProgressBar(); Warn("WebGL build failed — nothing uploaded."); return false; }

        return await UploadAndPurgeWebGL(buildRoot, cfg);
    }

    /// <summary>
    /// Upload an already-built WebGL folder to the live path and purge the cache.
    /// Shared by a fresh ship and by rollback (which feeds an archived build root).
    /// </summary>
    internal static async Task<bool> UploadAndPurgeWebGL(string buildRoot, BunnyConfig cfg) {
        var remoteDir = $"WebGL/{Pixygon.BuildTools.BuildTools.GameName}_WebGL";
        var liveUrl = $"https://{cfg.PullZoneHost}/{remoteDir}/index.html";

        // Never ship a build the site can't read.
        if (!File.Exists(Path.Combine(buildRoot, "build-manifest.json"))) {
            EditorUtility.ClearProgressBar();
            Warn($"build-manifest.json missing in {buildRoot} — aborting upload.");
            return false;
        }

        try {
            await BunnyUploader.UploadTree(buildRoot, remoteDir, cfg, ReportProgress);
            await BunnyUploader.PurgePaths(new[] {
                $"https://{cfg.PullZoneHost}/{remoteDir}/build-manifest.json",
                $"https://{cfg.PullZoneHost}/{remoteDir}/changelog.json",
                $"https://{cfg.PullZoneHost}/{remoteDir}/index.html",
            }, cfg);
            Debug.Log($"[Ship] WebGL live → {liveUrl}");
            return true;
        } catch (OperationCanceledException) {
            Warn("WebGL ship cancelled — the previous build is still live (manifest was not replaced).");
            return false;
        } catch (Exception e) {
            Debug.LogError($"[Ship] WebGL failed: {e.Message}\n{e}");
            return false;
        } finally {
            EditorUtility.ClearProgressBar();
        }
    }

    // ---- Windows ------------------------------------------------------------

    private static async Task<bool> RunWindows(bool standalone) {
        var cfg = BunnyConfigLoader.Load();
        var remotePath = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_win.zip";
        var downloadUrl = $"https://{cfg.PullZoneHost}/{remotePath}";

        if (standalone) {
            if (!Confirm("Windows", $"v{PlayerSettings.bundleVersion}\n{remotePath}  →  {downloadUrl}", cfg)) return false;
        }

        EditorUtility.DisplayProgressBar("Build & Ship Windows", "Building player…", 0.05f);
        var buildDir = Pixygon.BuildTools.BuildTools.BuildWindowsMonoInternal();
        if (buildDir == null) { EditorUtility.ClearProgressBar(); Warn("Windows build failed — nothing uploaded."); return false; }

        return await UploadAndPurgeWindows(buildDir, cfg);
    }

    /// <summary>
    /// Zip an already-built Windows folder (.exe at zip root) and upload+purge.
    /// Shared by the interactive ship and the batchmode <see cref="BuildCLI"/>.
    /// </summary>
    internal static async Task<bool> UploadAndPurgeWindows(string buildDir, BunnyConfig cfg) {
        var remotePath = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_win.zip";
        var downloadUrl = $"https://{cfg.PullZoneHost}/{remotePath}";
        try {
            // Zip the build folder so the .exe sits at the zip's ROOT (no sub-folder).
            EditorUtility.DisplayProgressBar("Build & Ship Windows", "Zipping…", 0.5f);
            var zipPath = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_win.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            CreateCleanZip(buildDir, zipPath);

            EditorUtility.DisplayProgressBar("Build & Ship Windows", "Uploading zip…", 0.7f);
            await BunnyUploader.UploadFile(zipPath, remotePath, cfg);
            await BunnyUploader.PurgePaths(new[] { downloadUrl }, cfg);

            Debug.Log($"[Ship] Windows live → {downloadUrl}");
            return true;
        } catch (Exception e) {
            Debug.LogError($"[Ship] Windows upload failed: {e.Message}\n{e}");
            return false;
        } finally {
            EditorUtility.ClearProgressBar();
        }
    }

    // ---- macOS --------------------------------------------------------------

    private static async Task<bool> RunMac(bool standalone) {
        var cfg = BunnyConfigLoader.Load();
        var remotePath = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_mac.zip";
        var downloadUrl = $"https://{cfg.PullZoneHost}/{remotePath}";

        if (standalone) {
            if (!Confirm("macOS", $"v{PlayerSettings.bundleVersion}\n{remotePath}  →  {downloadUrl}", cfg)) return false;
        }

        EditorUtility.DisplayProgressBar("Build & Ship macOS", "Building player…", 0.05f);
        var appPath = Pixygon.BuildTools.BuildTools.BuildMacInternal();
        if (appPath == null) { EditorUtility.ClearProgressBar(); Warn("macOS build failed — nothing uploaded."); return false; }

        return await UploadAndPurgeMac(appPath, cfg);
    }

    /// <summary>
    /// Zip an already-built <c>.app</c> with <c>ditto</c> and upload+purge. Shared by
    /// the interactive ship and the batchmode <see cref="BuildCLI"/>. NOTE: this is the
    /// UN-notarized path (a plain ditto zip of whatever was built). The notarized ship
    /// goes through ship.sh → notarize-mac.sh, which signs/notarizes/staples first and
    /// then uploads the resulting zip via <see cref="UploadMacZipFile"/>.
    /// </summary>
    internal static async Task<bool> UploadAndPurgeMac(string appPath, BunnyConfig cfg) {
        var zipPath = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_mac.zip");
        try {
            EditorUtility.DisplayProgressBar("Build & Ship macOS", "Zipping .app (ditto)…", 0.5f);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (!CreateMacAppZip(appPath, zipPath)) { Warn("ditto zip failed — nothing uploaded."); return false; }

            EditorUtility.DisplayProgressBar("Build & Ship macOS", "Uploading zip…", 0.7f);
            return await UploadMacZipFile(zipPath, cfg);
        } catch (Exception e) {
            Debug.LogError($"[Ship] macOS failed: {e.Message}\n{e}");
            return false;
        } finally {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// Upload an already-prepared Mac zip (e.g. the notarized + stapled artifact from
    /// notarize-mac.sh) to the live download path and purge. No zipping — the bytes
    /// uploaded are exactly the bytes passed in.
    /// </summary>
    internal static async Task<bool> UploadMacZipFile(string zipPath, BunnyConfig cfg) {
        var remotePath = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_mac.zip";
        var downloadUrl = $"https://{cfg.PullZoneHost}/{remotePath}";
        try {
            await BunnyUploader.UploadFile(zipPath, remotePath, cfg);
            await BunnyUploader.PurgePaths(new[] { downloadUrl }, cfg);
            Debug.Log($"[Ship] macOS live → {downloadUrl}");
            return true;
        } catch (Exception e) {
            Debug.LogError($"[Ship] macOS upload failed: {e.Message}\n{e}");
            return false;
        }
    }

    /// <summary>
    /// Zip a macOS <c>.app</c> with <c>ditto</c>. We must NOT use the .NET zip here:
    /// a .app needs its executable bit (Contents/MacOS/&lt;bin&gt;) and internal
    /// symlinks (Contents/Frameworks) preserved, and .NET's ZipArchive drops both —
    /// the result opens as "damaged" on the user's Mac. ditto is the canonical tool
    /// that preserves bundle integrity; <c>--keepParent</c> makes the .app the root
    /// entry so unzipping yields the .app directly.
    /// </summary>
    /// <summary>
    /// tar.gz a Linux build folder. We use <c>tar</c> (not a .NET zip) because the Linux
    /// player executable needs its +x bit, which .NET's ZipArchive drops. Archives the
    /// folder's CONTENTS at the root (so it extracts into a folder of the user's choosing).
    /// <c>COPYFILE_DISABLE=1</c> + excludes keep macOS AppleDouble / .DS_Store cruft out.
    /// </summary>
    internal static bool CreateTarGz(string sourceDir, string tgzPath) {
        var psi = new System.Diagnostics.ProcessStartInfo {
            FileName = "/usr/bin/tar",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = sourceDir,
        };
        psi.EnvironmentVariables["COPYFILE_DISABLE"] = "1"; // no ._AppleDouble entries
        psi.ArgumentList.Add("--exclude");
        psi.ArgumentList.Add(".DS_Store");
        // Burst writes "<product>_BurstDebugInformation_DoNotShip/" next to the player (symbol
        // files for crash decoding). Unity names it DoNotShip for a reason — keep it local.
        psi.ArgumentList.Add("--exclude");
        psi.ArgumentList.Add("*_BurstDebugInformation_DoNotShip");
        psi.ArgumentList.Add("-czf");
        psi.ArgumentList.Add(tgzPath);
        psi.ArgumentList.Add("."); // contents of sourceDir (WorkingDirectory)

        try {
            using var p = System.Diagnostics.Process.Start(psi);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) return true;
            Debug.LogError($"[Ship] tar failed (exit {p.ExitCode}): {err}");
            return false;
        } catch (Exception e) {
            Debug.LogError($"[Ship] Could not run tar: {e.Message}");
            return false;
        }
    }

    internal static bool CreateMacAppZip(string appPath, string zipPath) {
        var psi = new System.Diagnostics.ProcessStartInfo {
            FileName = "/usr/bin/ditto",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("-k");
        psi.ArgumentList.Add("--sequesterRsrc");
        psi.ArgumentList.Add("--keepParent");
        psi.ArgumentList.Add(appPath);
        psi.ArgumentList.Add(zipPath);

        try {
            using var p = System.Diagnostics.Process.Start(psi);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) return true;
            Debug.LogError($"[Ship] ditto failed (exit {p.ExitCode}): {err}");
            return false;
        } catch (Exception e) {
            Debug.LogError($"[Ship] Could not run ditto (macOS only): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Codesign + notarize + staple the built <c>.app</c> by shelling out to
    /// <c>notarize-mac.sh</c>, producing the distributable zip at <paramref name="zipPath"/>.
    /// The signing identity + notary profile come from <paramref name="cfg"/> (env this
    /// process did not inherit — e.g. the Hub-launched editor — so we pass them in
    /// explicitly). Returns true only if the script reports success and the zip exists.
    /// The notary submit blocks for minutes; the caller runs this synchronously and the
    /// editor stays frozen until it returns (no Unity API is touched meanwhile).
    /// </summary>
    internal static bool NotarizeMacApp(string appPath, string zipPath, BunnyConfig cfg) {
        var script = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "notarize-mac.sh");
        if (!File.Exists(script)) { Debug.LogError($"[Ship] notarize-mac.sh not found at {script}"); return false; }
        if (!cfg.HasMacSigning) {
            Debug.LogError("[Ship] No macOS signing identity. Set it in Pixygon → Build & Ship → Bunny Credentials " +
                           "(or SIGN_IDENTITY).");
            return false;
        }

        var psi = new System.Diagnostics.ProcessStartInfo {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(appPath);
        psi.ArgumentList.Add(zipPath);
        psi.EnvironmentVariables["SIGN_IDENTITY"] = cfg.MacSignIdentity;
        if (!string.IsNullOrEmpty(cfg.MacNotaryProfile))
            psi.EnvironmentVariables["NOTARY_PROFILE"] = cfg.MacNotaryProfile;

        try {
            using var p = System.Diagnostics.Process.Start(psi);
            // Async readers so a chatty notarytool can't fill a pipe and deadlock.
            // Debug.Log is thread-safe in Unity, so logging from the reader threads is fine.
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log($"[notarize] {e.Data}"); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) Debug.LogWarning($"[notarize] {e.Data}"); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode == 0 && File.Exists(zipPath)) {
                Debug.Log($"[Ship] macOS notarized → {zipPath}");
                return true;
            }
            Debug.LogError($"[Ship] notarize-mac.sh failed (exit {p.ExitCode}). See [notarize] lines above.");
            return false;
        } catch (Exception e) {
            Debug.LogError($"[Ship] Could not run notarize-mac.sh: {e.Message}");
            return false;
        }
    }

    // ---- shared -------------------------------------------------------------

    private static bool Confirm(string target, string pathLine, BunnyConfig cfg) {
        if (!cfg.HasStorageKey) {
            EditorUtility.DisplayDialog(
                "Bunny credentials missing",
                "No storage-zone API key is configured. Set one in\n" +
                "Pixygon → Build & Ship → Bunny Credentials…",
                "OK");
            return false;
        }
        var purgeNote = cfg.HasAccountKey ? "" : "\n\n⚠ No account key — edge cache will NOT be purged.";
        return EditorUtility.DisplayDialog(
            $"Ship {target} to LIVE?",
            $"Builds at the CURRENT version and OVERWRITES the live build on {cfg.PullZoneHost}.\n" +
            $"(No auto-bump — use Pixygon → Build & Ship → Bump Patch Version for a new release.)\n\n{pathLine}{purgeNote}",
            $"Build & Ship {target}", "Cancel");
    }

    /// <summary>
    /// Runs on the Unity main thread (continuations aren't detached in BunnyUploader),
    /// so it's safe to touch EditorUtility here. Throws to cancel an in-flight ship.
    /// </summary>
    internal static void ReportProgress(int done, int total, string file) {
        var frac = total > 0 ? done / (float)total : 1f;
        if (EditorUtility.DisplayCancelableProgressBar(
                "Build & Ship WebGL", $"Uploading {done}/{total}: {file}", frac))
            throw new OperationCanceledException();
    }

    private static void Warn(string msg) => Debug.LogWarning($"[Ship] {msg}");

    /// <summary>
    /// Zip <paramref name="sourceDir"/> with the .exe at the zip ROOT, explicitly
    /// skipping macOS metadata cruft. Building a Windows player ON macOS and then
    /// compressing via Finder/ditto/zip injects __MACOSX/ resource-fork shadows,
    /// ._AppleDouble files, and .DS_Store — junk that litters the archive on the
    /// player's Windows machine. A programmatic .NET zip never creates those, and
    /// this skip-list also drops any that already snuck into the build folder.
    /// </summary>
    internal static void CreateCleanZip(string sourceDir, string zipPath) {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var baseLen = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)) {
            var name = Path.GetFileName(file);
            if (name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal)) continue;
            var entryName = file.Substring(baseLen).Replace('\\', '/');
            if (entryName.StartsWith("__MACOSX/") || entryName.Contains("/__MACOSX/")) continue;
            if (entryName.Contains("_BurstDebugInformation_DoNotShip/")) continue; // Burst symbols — local only
            zip.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Optimal);
        }
    }
}
#endif
