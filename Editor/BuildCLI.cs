#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batchmode "build + ship" entry points for unattended / CLI runs, driven by
/// <c>ship.sh</c>. A single Unity process can build EVERY platform: switching the
/// active build target mid-run does not reload the domain in batchmode (verified —
/// Win→OSX→WebGL switches showed <c>domain reloads=0, compile=0ms</c>), so
/// <c>Pixygon.BuildTools.BuildTools.EnsureActiveTarget</c> switches-and-continues here instead of
/// aborting. <see cref="BuildAll"/> walks the requested targets, switching as it goes.
///
/// <para><b>Why one process beats one-process-per-target:</b> Unity starts up once
/// (not 3–4×), a compile error fails the whole run in a single startup, and there's
/// no version-bump-across-processes dance.</para>
///
/// <para><b>Upload concurrency note (load-bearing):</b> uploads are async and Unity's
/// main thread carries a <c>UnitySynchronizationContext</c>. Blocking it directly on
/// an upload (<c>upload().GetAwaiter().GetResult()</c>) DEADLOCKS — the continuation
/// is queued back to the blocked main thread (verified: a direct block hung until
/// timeout; the same work via <see cref="Task.Run"/> completed). So every upload runs
/// through <see cref="RunBlocking"/>, which hops to a thread-pool thread first.
/// BunnyUploader only touches <c>System.IO</c> / <c>HttpClient</c> / <c>Debug.Log</c>
/// (all thread-safe), never EditorUtility, so it's safe off the main thread.</para>
///
/// <para><b>Flags</b> (from <see cref="Environment.GetCommandLineArgs"/>):
/// <c>-shipTargets webgl,windows,mac</c>, <c>-shipNoBump</c>, <c>-shipNoUpload</c>,
/// <c>-shipMacBuildOnly</c> (build the .app but don't upload — for the notarize tail),
/// <c>-shipZipPath &lt;path&gt;</c> (for <see cref="UploadMacZip"/>).</para>
///
/// <para><b>Exit codes:</b> 0 = every requested target shipped, 1 = something failed.
/// We call <see cref="EditorApplication.Exit"/> explicitly (batchmode's own status is
/// unreliable). Machine-readable markers — <c>##SHIP_VERSION=</c>, <c>##BUILD_OUTPUT=</c>,
/// <c>##APP_PATH=</c>, <c>##SHIP_RESULT=ok</c>, <c>##SHIP_FAILED=…</c> — are logged for
/// ship.sh to grep.</para>
/// </summary>
public static class BuildCLI {
    private static bool NoUpload => HasFlag("-shipNoUpload");

    // ---- public entry points (called by ship.sh / ad-hoc) -------------------

    // ship.sh runs ONE of these single-target methods per Unity process (with
    // -buildTarget). Never combine targets in one process: building Addressables for
    // one build-target group then switching to another poisons SBP ("Unable to build
    // with the current configuration"), even in batchmode. BuildAll stays for ad-hoc
    // single-group use but ship.sh no longer calls it.
    public static void BuildAll()     => Guarded("BuildAll",  () => ShipTargets(ParseTargets()));
    public static void BuildWebGL()   => Guarded("WebGL",     () => ShipTargets(new[] { "webgl" }));
    public static void BuildWindows() => Guarded("Windows",   () => ShipTargets(new[] { "windows" }));
    public static void BuildMac()     => Guarded("macOS",     () => ShipTargets(new[] { "mac" }));
    public static void BuildLinux()   => Guarded("Linux",     () => ShipTargets(new[] { "linux" }));

    /// <summary>Bump the patch version and exit (standalone; BuildAll already bumps
    /// unless <c>-shipNoBump</c>). Kept for ad-hoc use.</summary>
    public static void BumpPatch() => Guarded("BumpPatch", () => {
        var next = VersionTools.NextPatch(PlayerSettings.bundleVersion);
        VersionTools.Apply(next);
        Marker("SHIP_VERSION", next);
        Done($"version bumped → {next}");
    });

    /// <summary>Upload an already-prepared (notarized + stapled) Mac zip and purge.
    /// ship.sh calls this AFTER notarize-mac.sh. Reads <c>-shipZipPath</c>.</summary>
    public static void UploadMacZip() => Guarded("UploadMacZip", () => {
        var zip = ArgValue("-shipZipPath");
        if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) {
            Fail("UploadMacZip", $"zip not found: {zip ?? "(no -shipZipPath)"}"); return;
        }
        if (UploadMacZipCore(zip, BunnyConfigLoader.Load())) Done("notarized Mac zip uploaded");
        else Fail("UploadMacZip", "upload failed");
    });

    // ---- core ---------------------------------------------------------------

    /// <summary>Bump once (unless suppressed), then build+ship each target in order in
    /// THIS process. Continues past a failed target so the log shows every problem, and
    /// exits non-zero if any failed.</summary>
    private static void ShipTargets(string[] targets) {
        if (targets.Length == 0) { Fail("ShipTargets", "no targets (-shipTargets empty)"); return; }

        // No auto-bump. Ships use the CURRENT version so every platform lands on the
        // same one; bump deliberately with -shipBump (ship.sh --bump) for a new release.
        if (HasFlag("-shipBump")) {
            var next = VersionTools.NextPatch(PlayerSettings.bundleVersion);
            VersionTools.Apply(next);
            Marker("SHIP_VERSION", next);
        } else {
            Marker("SHIP_VERSION", PlayerSettings.bundleVersion);
        }

        var notarize = HasFlag("-shipNotarize"); // only affects the mac target
        var failed = new List<string>();
        foreach (var t in targets) {
            Debug.Log($"[BuildCLI] ── target: {t} ──");
            // CLI walks targets in one process, so it always allows the in-process switch.
            try {
                if (!BuildAndShipOne(t, !NoUpload, allowSwitch: true, notarizeMac: notarize)) failed.Add(t);
            } catch (Exception e) {
                Debug.LogError($"[BuildCLI] {t} threw: {e.Message}\n{e}");
                failed.Add(t);
            }
        }

        if (failed.Count == 0) { Marker("SHIP_RESULT", "ok"); Done($"shipped: {string.Join(",", targets)}"); }
        else { Marker("SHIP_FAILED", string.Join(",", failed)); Fail("ShipTargets", $"failed: {string.Join(",", failed)}"); }
    }

    /// <summary>
    /// Build one target and (optionally) ship it. Shared by the CLI and the in-editor
    /// "Build (&amp; Ship) All" menu, so both follow the exact same path. Pure: reads no
    /// command-line flags and touches no EditorUtility — the caller owns the UI/progress.
    /// <paramref name="allowSwitch"/> lets the build switch the active target in-process
    /// (true for the all-platforms callers; the single async menu ships pass false).
    /// Returns true on success.
    /// </summary>
    internal static bool BuildAndShipOne(string target, bool upload, bool allowSwitch, bool notarizeMac = false) {
        var cfg = BunnyConfigLoader.Load();
        switch (target) {
            case "webgl": {
                var root = Pixygon.BuildTools.BuildTools.BuildWebGLInternal(allowSwitch);
                if (root == null) { Debug.LogError("[BuildCLI] WebGL build failed."); return false; }
                Marker("BUILD_OUTPUT", root);
                return !upload || ShipWebGL(root, cfg);
            }
            case "windows": {
                var dir = Pixygon.BuildTools.BuildTools.BuildWindowsMonoInternal(allowSwitch);
                if (dir == null) { Debug.LogError("[BuildCLI] Windows build failed."); return false; }
                Marker("BUILD_OUTPUT", dir);
                return !upload || ShipWindows(dir, cfg);
            }
            case "linux": {
                var dir = Pixygon.BuildTools.BuildTools.BuildLinuxInternal(allowSwitch);
                if (dir == null) { Debug.LogError("[BuildCLI] Linux build failed."); return false; }
                Marker("BUILD_OUTPUT", dir);
                return !upload || ShipLinux(dir, cfg);
            }
            case "mac": {
                var app = Pixygon.BuildTools.BuildTools.BuildMacInternal(allowSwitch);
                if (app == null) { Debug.LogError("[BuildCLI] macOS build failed."); return false; }
                Marker("APP_PATH", app);
                if (!upload) return true;
                if (notarizeMac) {
                    // Sign + notarize + staple the .app → notarized zip, then upload THAT.
                    var zip = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_mac.zip");
                    if (File.Exists(zip)) File.Delete(zip);
                    if (!BuildAndShip.NotarizeMacApp(app, zip, cfg)) return false;
                    return UploadMacZipCore(zip, cfg);
                }
                return ShipMac(app, cfg); // plain (un-notarized) ditto zip + upload
            }
            default:
                Debug.LogError($"[BuildCLI] unknown target '{target}' (expected webgl|windows|mac|linux).");
                return false;
        }
    }

    // ---- per-target ship (upload off the main thread) -----------------------

    private static bool ShipWebGL(string buildRoot, BunnyConfig cfg) {
        if (!File.Exists(Path.Combine(buildRoot, "build-manifest.json"))) {
            Debug.LogError($"[BuildCLI] build-manifest.json missing in {buildRoot} — not uploading WebGL.");
            return false;
        }
        var dir = $"WebGL/{Pixygon.BuildTools.BuildTools.GameName}_WebGL";
        var host = cfg.PullZoneHost;
        var ok = RunBlocking("WebGL upload", async () => {
            await BunnyUploader.UploadTree(buildRoot, dir, cfg, null); // manifest uploaded LAST inside
            await BunnyUploader.PurgePaths(new[] {
                $"https://{host}/{dir}/build-manifest.json",
                $"https://{host}/{dir}/changelog.json",
                $"https://{host}/{dir}/index.html",
            }, cfg);
        });
        if (ok) Debug.Log($"[BuildCLI] WebGL live → https://{host}/{dir}/index.html");
        return ok;
    }

    private static bool ShipWindows(string buildDir, BunnyConfig cfg) {
        var zip = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_win.zip");
        try {
            if (File.Exists(zip)) File.Delete(zip);
            BuildAndShip.CreateCleanZip(buildDir, zip); // synchronous; safe on main thread
        } catch (Exception e) {
            Debug.LogError($"[BuildCLI] Windows zip failed: {e.Message}"); return false;
        }
        var path = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_win.zip";
        var host = cfg.PullZoneHost;
        var ok = RunBlocking("Windows upload", async () => {
            await BunnyUploader.UploadFile(zip, path, cfg);
            await BunnyUploader.PurgePaths(new[] { $"https://{host}/{path}" }, cfg);
        });
        if (ok) Debug.Log($"[BuildCLI] Windows live → https://{host}/{path}");
        return ok;
    }

    private static bool ShipLinux(string buildDir, BunnyConfig cfg) {
        var tgz = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_linux.tar.gz");
        if (File.Exists(tgz)) File.Delete(tgz);
        if (!BuildAndShip.CreateTarGz(buildDir, tgz)) { Debug.LogError("[BuildCLI] Linux tar.gz failed."); return false; }
        var path = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_linux.tar.gz";
        var host = cfg.PullZoneHost;
        var ok = RunBlocking("Linux upload", async () => {
            await BunnyUploader.UploadFile(tgz, path, cfg);
            await BunnyUploader.PurgePaths(new[] { $"https://{host}/{path}" }, cfg);
        });
        if (ok) Debug.Log($"[BuildCLI] Linux live → https://{host}/{path}");
        return ok;
    }

    private static bool ShipMac(string appPath, BunnyConfig cfg) {
        var zip = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, "Builds", $"{Pixygon.BuildTools.BuildTools.GameName}_mac.zip");
        if (File.Exists(zip)) File.Delete(zip);
        if (!BuildAndShip.CreateMacAppZip(appPath, zip)) { Debug.LogError("[BuildCLI] ditto zip failed."); return false; }
        return UploadMacZipCore(zip, cfg);
    }

    private static bool UploadMacZipCore(string zip, BunnyConfig cfg) {
        var path = $"Builds/{Pixygon.BuildTools.BuildTools.GameName}_mac.zip";
        var host = cfg.PullZoneHost;
        var ok = RunBlocking("macOS upload", async () => {
            await BunnyUploader.UploadFile(zip, path, cfg);
            await BunnyUploader.PurgePaths(new[] { $"https://{host}/{path}" }, cfg);
        });
        if (ok) Debug.Log($"[BuildCLI] macOS live → https://{host}/{path}");
        return ok;
    }

    /// <summary>
    /// Run async work to completion from a synchronous batchmode method WITHOUT
    /// deadlocking. Hops to a thread-pool thread first (<see cref="Task.Run"/>), so the
    /// continuations never need Unity's main-thread sync context. Returns false (and
    /// logs) on any exception.
    /// </summary>
    private static bool RunBlocking(string label, Func<Task> work) {
        try {
            Task.Run(work).GetAwaiter().GetResult();
            return true;
        } catch (Exception e) {
            Debug.LogError($"[BuildCLI] {label} failed: {e.Message}\n{e}");
            return false;
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static string[] ParseTargets() {
        var raw = ArgValue("-shipTargets");
        if (string.IsNullOrEmpty(raw)) return new[] { "webgl", "windows", "mac" };
        return raw.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToArray();
    }

    private static bool HasFlag(string flag) =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.Ordinal));

    private static string ArgValue(string key) {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, key);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    /// <summary>Emit a grep-friendly <c>##KEY=value</c> line into the editor log.</summary>
    private static void Marker(string key, string value) => Debug.Log($"##{key}={value}");

    /// <summary>Wrap an entry point so an unexpected throw still exits non-zero
    /// (rather than leaving Unity hung waiting on -quit).</summary>
    private static void Guarded(string label, Action body) {
        try { body(); }
        catch (Exception e) { Fail(label, e); }
    }

    private static void Done(string msg) {
        Debug.Log($"[BuildCLI] {msg}.");
        EditorApplication.Exit(0);
    }

    private static void Fail(string label, string why) {
        Debug.LogError($"[BuildCLI] {label}: {why}");
        EditorApplication.Exit(1);
    }

    private static void Fail(string label, Exception e) {
        Debug.LogError($"[BuildCLI] {label} threw: {e.Message}\n{e}");
        EditorApplication.Exit(1);
    }
}
#endif
