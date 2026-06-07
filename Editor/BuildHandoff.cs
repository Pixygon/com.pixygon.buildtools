#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// "Auto-handoff" desktop shipping. Interactive Unity can't build Addressables for a
/// target it switches to mid-session (verified the hard way), but the batchmode CLI
/// can — it just needs the editor closed for the project lock. So this CLOSES the
/// editor, runs <c>ship.sh</c> headless for the requested desktop targets, then
/// REOPENS the editor when done. One click; the editor blinks out and back, and a
/// dialog reports the result on reopen. WebGL still ships directly in-editor (no switch
/// needed, so no wall).
/// </summary>
public static class BuildHandoff {
    // Project-relative; Library survives editor restarts and is gitignored.
    private const string ResultFile = "Library/pixiel-handoff-result.txt";

    /// <summary>Confirm, save, spawn the detached handoff, then quit the editor.</summary>
    public static void Launch(string[] targets) {
        var cfg = BunnyConfigLoader.Load();
        if (!cfg.HasStorageKey) {
            EditorUtility.DisplayDialog("Bunny credentials missing",
                "No storage-zone API key. Set one in Pixygon → Build & Ship → Bunny Credentials…", "OK");
            return;
        }

        var hasMac = Array.IndexOf(targets, "mac") >= 0;
        var notarize = hasMac && cfg.HasMacSigning;
        var list = string.Join(", ", targets);
        var ver = PlayerSettings.bundleVersion;
        var macNote = !hasMac ? "" :
            notarize ? "\nmacOS will be notarized + stapled." :
                       "\nmacOS will NOT be notarized (set a Signing Identity in Bunny Credentials).";

        if (!EditorUtility.DisplayDialog("Build & Ship desktop (auto)?",
                $"Unity will CLOSE, build + ship [{list}] at v{ver} headless, then REOPEN automatically.\n{macNote}\n\n" +
                "Takes several minutes. A dialog reports the result when Unity comes back.",
                "Close & build", "Cancel"))
            return;

        // Give the user a chance to save dirty scenes (the headless build uses on-disk
        // scenes; EditorApplication.Exit would otherwise drop unsaved changes silently).
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return; // cancelled
        AssetDatabase.SaveAssets();

        var project = Pixygon.BuildTools.BuildTools.ProjectRoot;
        var handoff = Path.Combine(project, "ship-handoff.sh");
        if (!File.Exists(handoff)) {
            EditorUtility.DisplayDialog("Missing script",
                $"ship-handoff.sh not found at {handoff}.", "OK");
            return;
        }

        var resultPath = Path.Combine(project, ResultFile);
        if (File.Exists(resultPath)) File.Delete(resultPath); // clear any stale result

        var pid = Process.GetCurrentProcess().Id;
        var extra = notarize ? "--notarize" : "";
        var targetsArg = string.Join(" ", targets);

        // nohup so the handoff outlives THIS editor quitting (it waits for our PID to die).
        var inner = $"nohup bash '{handoff}' {pid} '{project}' '{targetsArg}' '{extra}' >/dev/null 2>&1 &";
        var psi = new ProcessStartInfo {
            FileName = "/bin/bash",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(inner);
        // A Hub-launched editor has no shell env, so pass signing creds through for ship.sh.
        if (cfg.HasMacSigning) {
            psi.EnvironmentVariables["SIGN_IDENTITY"] = cfg.MacSignIdentity;
            if (!string.IsNullOrEmpty(cfg.MacNotaryProfile))
                psi.EnvironmentVariables["NOTARY_PROFILE"] = cfg.MacNotaryProfile;
        }

        try {
            using var p = Process.Start(psi);
            p.WaitForExit(); // returns as soon as nohup is backgrounded
        } catch (Exception e) {
            EditorUtility.DisplayDialog("Could not start build", e.Message, "OK");
            return;
        }

        Debug.Log($"[Handoff] Desktop build launched for [{list}]. Closing editor; it will reopen when done.");
        EditorApplication.Exit(0);
    }

    /// <summary>On the next editor load, surface the headless ship's result (if any).</summary>
    [InitializeOnLoadMethod]
    private static void ShowResultOnReopen() {
        EditorApplication.delayCall += () => {
            try {
                var path = Path.Combine(Pixygon.BuildTools.BuildTools.ProjectRoot, ResultFile);
                if (!File.Exists(path)) return;
                var msg = File.ReadAllText(path).Trim();
                File.Delete(path);
                if (!string.IsNullOrEmpty(msg))
                    EditorUtility.DisplayDialog("Desktop ship finished", msg, "OK");
            } catch { /* best-effort; never block startup */ }
        };
    }
}
#endif
