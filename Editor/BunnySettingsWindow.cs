#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tiny editor window to store BunnyCDN credentials in EditorPrefs (per-machine,
/// never committed). Reachable via <c>Pixygon → Build &amp; Ship → Bunny Credentials…</c>.
///
/// <para>EditorPrefs is the lowest-precedence source: a <c>~/.config/pixygon/bunny.json</c>
/// file or <c>BUNNY_*</c> environment variables override whatever is set here, so a
/// CI/shared machine can inject keys without anyone editing prefs. The window shows
/// the effective (resolved) values so you can see what a ship will actually use.</para>
/// </summary>
public sealed class BunnySettingsWindow : EditorWindow {
    private string _storageKey;
    private string _accountKey;
    private string _storageZone;
    private string _endpoint;
    private string _pullHost;
    private string _macSignIdentity;
    private string _macNotaryProfile;

    [MenuItem("Pixygon/Build & Ship/Bunny Credentials…", priority = 100)]
    public static void Open() {
        var w = GetWindow<BunnySettingsWindow>(true, "Bunny Credentials", true);
        w.minSize = new Vector2(460, 320);
        w.Reload();
    }

    private void Reload() {
        _storageKey = EditorPrefs.GetString(BunnyConfigLoader.PrefStorageKey, "");
        _accountKey = EditorPrefs.GetString(BunnyConfigLoader.PrefAccountKey, "");
        _storageZone = EditorPrefs.GetString(BunnyConfigLoader.PrefStorageZone, "pixygontech");
        _endpoint = EditorPrefs.GetString(BunnyConfigLoader.PrefEndpoint, "storage.bunnycdn.com");
        _pullHost = EditorPrefs.GetString(BunnyConfigLoader.PrefPullHost, "pixygontech.b-cdn.net");
        _macSignIdentity = EditorPrefs.GetString(BunnyConfigLoader.PrefMacSignIdentity, "");
        _macNotaryProfile = EditorPrefs.GetString(BunnyConfigLoader.PrefMacNotaryProfile, "Pixygon-notary");
    }

    private void OnGUI() {
        EditorGUILayout.HelpBox(
            "Stored per-machine in EditorPrefs, never committed. A ~/.config/pixygon/bunny.json " +
            "file or BUNNY_* environment variables OVERRIDE these values.",
            MessageType.Info);

        EditorGUILayout.LabelField("Keys", EditorStyles.boldLabel);
        _storageKey = EditorGUILayout.PasswordField(
            new GUIContent("Storage Zone Password", "Storage → your zone → FTP & API Access → Password. Write access to the whole bucket."),
            _storageKey);
        _accountKey = EditorGUILayout.PasswordField(
            new GUIContent("Account API Key", "Account Settings → API. Used ONLY for cache purge."),
            _accountKey);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Zone", EditorStyles.boldLabel);
        _storageZone = EditorGUILayout.TextField("Storage Zone", _storageZone);
        _endpoint = EditorGUILayout.TextField(
            new GUIContent("Storage Endpoint", "Must match the zone's region or every upload 401s. e.g. ny.storage.bunnycdn.com"),
            _endpoint);
        _pullHost = EditorGUILayout.TextField("Pull Zone Host", _pullHost);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("macOS code signing (optional)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Needed only to notarize the macOS build from the in-editor \"Build & Ship ALL\". " +
            "Unity launched from the Hub does NOT inherit your shell's SIGN_IDENTITY, so set it here. " +
            "Store the notary password once via: xcrun notarytool store-credentials <profile> …  (see MACOS_NOTARIZATION.md).",
            MessageType.None);
        _macSignIdentity = EditorGUILayout.TextField(
            new GUIContent("Signing Identity", "security find-identity -v -p codesigning → e.g. \"Developer ID Application: Name (TEAMID)\""),
            _macSignIdentity);
        _macNotaryProfile = EditorGUILayout.TextField(
            new GUIContent("Notary Profile", "The keychain profile name from xcrun notarytool store-credentials. Default: Pixygon-notary"),
            _macNotaryProfile);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope()) {
            if (GUILayout.Button("Save")) Save();
            if (GUILayout.Button("Clear Keys")) ClearKeys();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Effective (resolved) config", EditorStyles.boldLabel);
        var cfg = BunnyConfigLoader.Load();
        EditorGUILayout.LabelField("Storage key", Mask(cfg.StorageApiKey));
        EditorGUILayout.LabelField("Account key", Mask(cfg.AccountApiKey));
        EditorGUILayout.LabelField("Zone / endpoint", $"{cfg.StorageZone} @ {cfg.Endpoint}");
        EditorGUILayout.LabelField("Pull host", cfg.PullZoneHost);
        EditorGUILayout.LabelField("macOS signing", cfg.HasMacSigning ? $"{cfg.MacSignIdentity}  (profile: {cfg.MacNotaryProfile})" : "(not set — Mac ships un-notarized)");
        var jsonPath = BunnyConfigLoader.ConfigFilePath();
        EditorGUILayout.LabelField("Config file", File.Exists(jsonPath) ? $"present: {jsonPath}" : "(none)");
    }

    private void Save() {
        EditorPrefs.SetString(BunnyConfigLoader.PrefStorageKey, _storageKey ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefAccountKey, _accountKey ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefStorageZone, _storageZone ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefEndpoint, _endpoint ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefPullHost, _pullHost ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefMacSignIdentity, _macSignIdentity ?? "");
        EditorPrefs.SetString(BunnyConfigLoader.PrefMacNotaryProfile, _macNotaryProfile ?? "");
        Debug.Log("[Bunny] Credentials saved to EditorPrefs.");
        Repaint();
    }

    private void ClearKeys() {
        EditorPrefs.DeleteKey(BunnyConfigLoader.PrefStorageKey);
        EditorPrefs.DeleteKey(BunnyConfigLoader.PrefAccountKey);
        _storageKey = _accountKey = "";
        Debug.Log("[Bunny] Cleared stored keys from EditorPrefs.");
        Repaint();
    }

    private static string Mask(string key) {
        if (string.IsNullOrEmpty(key)) return "(not set)";
        return key.Length <= 4 ? "••••" : $"••••{key.Substring(key.Length - 4)}";
    }
}
#endif
