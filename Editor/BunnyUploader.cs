#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Resolved BunnyCDN settings used by <see cref="BunnyUploader"/>. Two distinct
/// keys: <see cref="StorageApiKey"/> (the storage-zone "password" — write access
/// to every file in the bucket) and <see cref="AccountApiKey"/> (account-level
/// key, used only for cache purges). They are NOT interchangeable.
/// </summary>
internal sealed class BunnyConfig {
    public string StorageApiKey;
    public string AccountApiKey;
    public string StorageZone = "pixygontech";
    public string Endpoint = "storage.bunnycdn.com"; // region-specific, e.g. ny.storage.bunnycdn.com
    public string PullZoneHost = "pixygontech.b-cdn.net";

    // macOS code-signing / notarization (optional). The editor, when launched from
    // Unity Hub, does NOT inherit shell env vars, so these must be stored here for the
    // in-editor "Build & Ship ALL" notarize step to see them. See MACOS_NOTARIZATION.md.
    public string MacSignIdentity;                    // "Developer ID Application: … (TEAMID)"
    public string MacNotaryProfile = "Pixygon-notary"; // xcrun notarytool keychain profile

    public bool HasStorageKey => !string.IsNullOrEmpty(StorageApiKey);
    public bool HasAccountKey => !string.IsNullOrEmpty(AccountApiKey);
    public bool HasMacSigning => !string.IsNullOrEmpty(MacSignIdentity);
}

/// <summary>
/// Loads <see cref="BunnyConfig"/> from, in ascending order of precedence:
/// <list type="number">
/// <item>EditorPrefs (set via the Pixygon → Build &amp; Ship → Bunny Credentials window)</item>
/// <item><c>~/.config/pixygon/bunny.json</c> (per-developer file)</item>
/// <item>Environment variables (CI / shared machine — wins over everything)</item>
/// </list>
/// The storage-zone key is NEVER read from a committed <c>.cs</c> file — it would
/// grant write access to every Pixygon game's live build.
/// </summary>
internal static class BunnyConfigLoader {
    internal const string PrefStorageKey = "Pixygon.BunnyApiKey";
    internal const string PrefAccountKey = "Pixygon.BunnyAccountApiKey";
    internal const string PrefStorageZone = "Pixygon.BunnyStorageZone";
    internal const string PrefEndpoint = "Pixygon.BunnyEndpoint";
    internal const string PrefPullHost = "Pixygon.BunnyPullZoneHost";
    internal const string PrefMacSignIdentity = "Pixygon.MacSignIdentity";
    internal const string PrefMacNotaryProfile = "Pixygon.MacNotaryProfile";

    [Serializable]
    private class FileShape {
        public string apiKey;
        public string accountApiKey;
        public string storageZone;
        public string endpoint;
        public string pullZoneHost;
        public string macSignIdentity;
        public string macNotaryProfile;
    }

    internal static string ConfigFilePath() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "pixygon", "bunny.json");
    }

    public static BunnyConfig Load() {
        var cfg = new BunnyConfig {
            // 3) EditorPrefs baseline
            StorageApiKey = EditorPrefs.GetString(PrefStorageKey, ""),
            AccountApiKey = EditorPrefs.GetString(PrefAccountKey, ""),
        };
        cfg.StorageZone = EditorPrefs.GetString(PrefStorageZone, cfg.StorageZone);
        cfg.Endpoint = EditorPrefs.GetString(PrefEndpoint, cfg.Endpoint);
        cfg.PullZoneHost = EditorPrefs.GetString(PrefPullHost, cfg.PullZoneHost);
        cfg.MacSignIdentity = EditorPrefs.GetString(PrefMacSignIdentity, cfg.MacSignIdentity ?? "");
        cfg.MacNotaryProfile = EditorPrefs.GetString(PrefMacNotaryProfile, cfg.MacNotaryProfile);

        // 2) ~/.config/pixygon/bunny.json overrides EditorPrefs for any field set
        var jsonPath = ConfigFilePath();
        if (File.Exists(jsonPath)) {
            try {
                var shape = JsonUtility.FromJson<FileShape>(File.ReadAllText(jsonPath));
                if (shape != null) {
                    if (!string.IsNullOrEmpty(shape.apiKey)) cfg.StorageApiKey = shape.apiKey;
                    if (!string.IsNullOrEmpty(shape.accountApiKey)) cfg.AccountApiKey = shape.accountApiKey;
                    if (!string.IsNullOrEmpty(shape.storageZone)) cfg.StorageZone = shape.storageZone;
                    if (!string.IsNullOrEmpty(shape.endpoint)) cfg.Endpoint = shape.endpoint;
                    if (!string.IsNullOrEmpty(shape.pullZoneHost)) cfg.PullZoneHost = shape.pullZoneHost;
                    if (!string.IsNullOrEmpty(shape.macSignIdentity)) cfg.MacSignIdentity = shape.macSignIdentity;
                    if (!string.IsNullOrEmpty(shape.macNotaryProfile)) cfg.MacNotaryProfile = shape.macNotaryProfile;
                }
            } catch (Exception e) {
                Debug.LogWarning($"[Bunny] Could not parse {jsonPath}: {e.Message}");
            }
        }

        // 1) Environment variables win (CI / shared dev machine)
        Override(ref cfg.StorageApiKey, "BUNNY_STORAGE_API_KEY");
        Override(ref cfg.AccountApiKey, "BUNNY_ACCOUNT_API_KEY");
        Override(ref cfg.StorageZone, "BUNNY_STORAGE_ZONE");
        Override(ref cfg.Endpoint, "BUNNY_STORAGE_ENDPOINT");
        Override(ref cfg.PullZoneHost, "BUNNY_PULLZONE_HOST");
        Override(ref cfg.MacSignIdentity, "SIGN_IDENTITY");
        Override(ref cfg.MacNotaryProfile, "NOTARY_PROFILE");
        return cfg;
    }

    private static void Override(ref string field, string envVar) {
        var v = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(v)) field = v;
    }
}

/// <summary>Thrown for non-retryable upload failures (auth, 4xx) so retries don't waste time.</summary>
internal sealed class BunnyFatalException : Exception {
    public BunnyFatalException(string message) : base(message) { }
}

/// <summary>
/// Uploads a build tree to BunnyCDN storage and purges the pull-zone edge cache.
///
/// <para><b>Order of operations is load-bearing:</b> every file EXCEPT
/// <c>build-manifest.json</c> uploads first (concurrently); the manifest uploads
/// LAST. The manifest names the binaries, so if it went live first the site would
/// 404 on files that aren't there yet. Uploading it last also makes a cancelled
/// or failed ship safe — the old manifest (and therefore the old live build)
/// survives untouched.</para>
///
/// <para>Continuations are intentionally NOT detached with ConfigureAwait(false):
/// in the Editor that keeps them on the Unity main thread, so a progress callback
/// can safely touch EditorUtility. HTTP I/O is still genuinely async, so up to
/// <see cref="MaxConcurrency"/> uploads overlap and saturate the uplink.</para>
/// </summary>
internal static class BunnyUploader {
    private const int MaxConcurrency = 6;
    private const int MaxRetries = 4; // total attempts = MaxRetries + 1

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Upload every file under <paramref name="localRoot"/> to
    /// <c>{remoteDir}/{relative-path}</c>. <paramref name="onProgress"/> is invoked
    /// on the main thread after each file (current, total, relativePath); it may
    /// throw <see cref="OperationCanceledException"/> to abort before the manifest
    /// is replaced.
    /// </summary>
    public static async Task UploadTree(string localRoot, string remoteDir, BunnyConfig cfg,
                                        Action<int, int, string> onProgress = null) {
        if (!cfg.HasStorageKey)
            throw new BunnyFatalException(
                "No storage-zone API key. Set it in Pixygon → Build & Ship → Bunny Credentials, " +
                "or via BUNNY_STORAGE_API_KEY / ~/.config/pixygon/bunny.json.");

        remoteDir = remoteDir.Trim('/');
        var allFiles = Directory.GetFiles(localRoot, "*", SearchOption.AllDirectories);
        var manifestLocal = Path.Combine(localRoot, "build-manifest.json");
        var firstPass = allFiles.Where(f => !PathsEqual(f, manifestLocal)).ToArray();

        int total = allFiles.Length;
        int done = 0;

        using (var sem = new SemaphoreSlim(MaxConcurrency)) {
            var tasks = firstPass.Select(async file => {
                await sem.WaitAsync();
                try {
                    var rel = RelativeRemote(localRoot, file);
                    await PutFileWithRetry($"{remoteDir}/{rel}", file, cfg);
                    var n = Interlocked.Increment(ref done);
                    onProgress?.Invoke(n, total, rel); // may throw to cancel
                } finally {
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        // Manifest LAST — flips the build live atomically-enough for our needs.
        if (File.Exists(manifestLocal)) {
            var rel = RelativeRemote(localRoot, manifestLocal);
            await PutFileWithRetry($"{remoteDir}/{rel}", manifestLocal, cfg);
            onProgress?.Invoke(Interlocked.Increment(ref done), total, rel);
        }
    }

    /// <summary>Upload a single local file to <c>{remoteDir}/{remoteName}</c> (used for the Windows zip).</summary>
    public static async Task UploadFile(string localFile, string remotePath, BunnyConfig cfg) {
        if (!cfg.HasStorageKey)
            throw new BunnyFatalException("No storage-zone API key configured.");
        await PutFileWithRetry(remotePath.Trim('/'), localFile, cfg);
    }

    /// <summary>
    /// Purge the given absolute CDN URLs from the pull-zone edge cache. Requires the
    /// ACCOUNT API key (not the storage key). Missing key or a failed purge is logged
    /// as a warning, never thrown — the upload already succeeded; a stale edge cache
    /// is a recoverable nuisance, not a failed ship.
    /// </summary>
    public static async Task PurgePaths(IEnumerable<string> urls, BunnyConfig cfg) {
        if (!cfg.HasAccountKey) {
            Debug.LogWarning(
                "[Bunny] No account API key — skipping cache purge. Edge caches may serve the OLD " +
                "build-manifest.json for up to 14 days. Set BUNNY_ACCOUNT_API_KEY (Account Settings → API) " +
                "or purge manually in the dashboard.");
            return;
        }
        foreach (var u in urls) {
            try {
                var purgeUrl = "https://api.bunny.net/purge?url=" + Uri.EscapeDataString(u);
                using var req = new HttpRequestMessage(HttpMethod.Post, purgeUrl);
                req.Headers.TryAddWithoutValidation("AccessKey", cfg.AccountApiKey);
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode) Debug.Log($"[Bunny] Purged {u}");
                else Debug.LogWarning($"[Bunny] Purge returned {(int)resp.StatusCode} for {u}");
            } catch (Exception e) {
                Debug.LogWarning($"[Bunny] Purge threw for {u}: {e.Message}");
            }
        }
    }

    private static async Task PutFileWithRetry(string remotePath, string localFile, BunnyConfig cfg) {
        var url = $"https://{cfg.Endpoint}/{cfg.StorageZone}/{remotePath}";
        var bytes = File.ReadAllBytes(localFile);
        Exception last = null;

        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++) {
            try {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.TryAddWithoutValidation("AccessKey", cfg.StorageApiKey);

                using var resp = await Http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (code == 200 || code == 201) return; // Bunny returns 201 Created
                if (code == 401)
                    throw new BunnyFatalException(
                        $"401 Unauthorized uploading {remotePath}. Check the storage-zone password AND that " +
                        $"endpoint '{cfg.Endpoint}' matches the zone's region (e.g. ny.storage.bunnycdn.com).");
                if (code != 429 && code < 500)
                    throw new BunnyFatalException($"{code} {resp.ReasonPhrase} uploading {remotePath}");
                last = new Exception($"{code} {resp.ReasonPhrase}"); // 429 / 5xx → retry
            } catch (BunnyFatalException) {
                throw;
            } catch (Exception e) {
                last = e; // network blip → retry
            }
            if (attempt <= MaxRetries) await Task.Delay(BackoffMs(attempt));
        }
        throw new Exception($"Upload failed after {MaxRetries} retries: {remotePath} — {last?.Message}");
    }

    private static int BackoffMs(int attempt) {
        var baseMs = (int)(Math.Pow(2, attempt) * 250); // 500, 1000, 2000, 4000...
        return baseMs + new System.Random().Next(0, 250);
    }

    private static string RelativeRemote(string root, string file) =>
        file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
#endif
