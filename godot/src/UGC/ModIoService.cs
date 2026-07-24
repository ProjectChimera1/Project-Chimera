#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ProjectChimera.UGC
{
    // ── JSON response models ──────────────────────────────────────────────────

    /// <summary>A mod entry from the mod.io /games/{id}/mods response.</summary>
    public class ModIoMod
    {
        [JsonPropertyName("id")]            public int          Id          { get; set; }
        [JsonPropertyName("name")]          public string       Name        { get; set; } = "";
        [JsonPropertyName("summary")]       public string       Summary     { get; set; } = "";
        [JsonPropertyName("submitted_by")]  public ModIoUser    SubmittedBy { get; set; } = new();
        [JsonPropertyName("modfile")]       public ModIoFile?   Modfile     { get; set; }
        [JsonPropertyName("stats")]         public ModIoStats   Stats       { get; set; } = new();
        [JsonPropertyName("tags")]          public List<ModIoTag> Tags      { get; set; } = new();
        /// <summary>Story 9.10: the mod's logo image set (thumbnails + original), used for the card thumbnail.</summary>
        [JsonPropertyName("logo")]          public ModIoLogo?   Logo        { get; set; }
    }

    /// <summary>
    /// A mod's logo image set from mod.io. Story 9.10 renders <see cref="Thumb320x180"/> on the browse card.
    /// See: https://docs.mod.io/restapiref/#logo-object
    /// </summary>
    public class ModIoLogo
    {
        [JsonPropertyName("filename")]      public string Filename     { get; set; } = "";
        [JsonPropertyName("thumb_320x180")] public string Thumb320x180 { get; set; } = "";
        [JsonPropertyName("thumb_640x360")] public string Thumb640x360 { get; set; } = "";
        [JsonPropertyName("original")]      public string Original     { get; set; } = "";
    }

    public class ModIoUser
    {
        [JsonPropertyName("username")]    public string Username   { get; set; } = "";
        /// <summary>URL to the author's mod.io profile page, e.g. https://mod.io/u/username.</summary>
        [JsonPropertyName("profile_url")] public string ProfileUrl { get; set; } = "";
    }

    public class ModIoFile
    {
        [JsonPropertyName("id")]       public int           Id       { get; set; }
        [JsonPropertyName("version")]  public string?       Version  { get; set; }
        [JsonPropertyName("filesize")] public long          Filesize { get; set; }
        [JsonPropertyName("download")] public ModIoDownload Download { get; set; } = new();
    }

    public class ModIoDownload
    {
        [JsonPropertyName("binary_url")]   public string BinaryUrl   { get; set; } = "";
        [JsonPropertyName("date_expires")] public long   DateExpires { get; set; }
    }

    public class ModIoStats
    {
        [JsonPropertyName("ratings_positive")] public int RatingsPositive { get; set; }
        [JsonPropertyName("ratings_negative")] public int RatingsNegative { get; set; }
        [JsonPropertyName("downloads_total")]  public int DownloadsTotal  { get; set; }
        // Story 9.10: mod.io-native rating summary. RatingsDisplayText is mod.io's own human-readable weighted
        // string (e.g. "94% (128)"); when present we show it verbatim instead of computing a local score.
        [JsonPropertyName("ratings_percentage_positive")] public int    RatingsPercentagePositive { get; set; }
        [JsonPropertyName("ratings_display_text")]        public string RatingsDisplayText        { get; set; } = "";
    }

    public class ModIoTag
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    /// <summary>
    /// Story 9.10: a game tag-option group from <c>GET /games/{id}/tags</c>. Each group carries a display name and
    /// the tag values under it; the browser flattens all groups into one chip list (no local hardcoded tag index).
    /// </summary>
    public class ModIoTagOption
    {
        [JsonPropertyName("name")] public string       Name { get; set; } = "";
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    }

    public class ModIoListResponse<T>
    {
        [JsonPropertyName("data")]          public List<T> Data          { get; set; } = new();
        [JsonPropertyName("result_count")]  public int     ResultCount   { get; set; }
        [JsonPropertyName("result_total")]  public int     ResultTotal   { get; set; }
        [JsonPropertyName("result_offset")] public int     ResultOffset  { get; set; }
        [JsonPropertyName("result_limit")]  public int     ResultLimit   { get; set; }
    }

    // ── Service ───────────────────────────────────────────────────────────────

    /// <summary>
    /// mod.io REST API client for Project Chimera's UGC pipeline.
    /// Pure C# — no Godot dependency.
    ///
    /// Read-only operations (browse, download) need only an API key.
    /// Write operations (upload, subscribe, rate) require an OAuth2 access token:
    ///   1. Call AuthenticateEmailRequestAsync(email) → security code sent to email.
    ///   2. Call AuthenticateEmailExchangeAsync(code) → sets IsLoggedIn = true.
    ///
    /// All async results are delivered via events on the main thread.
    /// Call DrainEvents() from _Process each frame to dispatch them.
    ///
    /// See: https://docs.mod.io/restapiref/
    /// </summary>
    public class ModIoService
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const string BASE_URL = "https://api.mod.io/v1";
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling         = JsonCommentHandling.Skip,
        };

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when BrowseModsAsync completes. Returns the mod list.</summary>
        public event Action<List<ModIoMod>>? OnBrowseComplete;
        /// <summary>Fired periodically during download. Args: (modId, 0.0–1.0 progress).</summary>
        public event Action<int, float>?    OnDownloadProgress;
        /// <summary>Fired when a mod download finishes. Args: (modId, localFilePath).</summary>
        public event Action<int, string>?   OnDownloadComplete;
        /// <summary>Fired when UploadModAsync completes. Returns the new modId on mod.io.</summary>
        public event Action<int>?           OnUploadComplete;
        /// <summary>Fired after AuthenticateEmailRequestAsync succeeds (code was sent).</summary>
        public event Action?                OnAuthCodeSent;
        /// <summary>Fired after AuthenticateEmailExchangeAsync succeeds. Returns username.</summary>
        public event Action<string>?        OnLoginSuccess;
        /// <summary>Story 9.10: fired when GetGameTagsAsync completes. Returns the flat game tag-name list.</summary>
        public event Action<List<string>>?  OnTagOptionsReady;
        /// <summary>Story 9.10: fired when DownloadThumbnailAsync completes. Args: (modId, raw image bytes).</summary>
        public event Action<int, byte[]>?   OnThumbnailReady;
        /// <summary>Story 9.10: fired on a 2xx from SubscribeAsync. Args: (modId).</summary>
        public event Action<int>?           OnSubscribeComplete;
        /// <summary>Story 9.10: fired on a 2xx from RateAsync. Args: (modId, positive).</summary>
        public event Action<int, bool>?     OnRateComplete;
        /// <summary>Fired on any operation error. Args: (operation, message).</summary>
        public event Action<string, string>? OnError;

        // ── State ─────────────────────────────────────────────────────────────

        private readonly int    _gameId;
        private readonly string _apiKey;
        private string?  _accessToken;

        public bool IsLoggedIn => _accessToken != null;

        private readonly HttpClient              _http;
        private readonly ConcurrentQueue<Action> _queue = new();

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="gameId">Your mod.io game ID (found in the Mod Manager dashboard).</param>
        /// <param name="apiKey">Your read-only API key from mod.io > API Access.</param>
        public ModIoService(int gameId, string apiKey)
        {
            _gameId = gameId;
            _apiKey = apiKey;
            _http   = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "ProjectChimera/0.1 (modio-csharp)");
        }

        // ── Main-thread dispatch ──────────────────────────────────────────────

        /// <summary>
        /// Dispatch pending event callbacks on the calling thread (i.e. the Godot main thread).
        /// Call once per frame from a Node's _Process override.
        /// </summary>
        public void DrainEvents()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }

        // ── Browse ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a page of public mods for this game from mod.io.
        /// Results arrive via <see cref="OnBrowseComplete"/>.
        /// No authentication required.
        /// </summary>
        /// <param name="limit">Number of results per page (max 100).</param>
        /// <param name="offset">Pagination offset.</param>
        /// <param name="searchQuery">Optional free-text search (mod name / summary).</param>
        /// <param name="sort">Optional mod.io-native <c>_sort</c> token (e.g. <c>-downloads</c>). Null/blank ⇒ default
        /// <c>-popular</c>.</param>
        /// <param name="tags">Optional mod.io tag names; each becomes one <c>tags=</c> param (mod.io ANDs them).</param>
        public void BrowseModsAsync(int limit = 20, int offset = 0, string? searchQuery = null,
                                    string? sort = null, IReadOnlyList<string>? tags = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string url = BuildModsUrl(BASE_URL, _gameId, _apiKey, limit, offset, searchQuery, sort, tags);

                    var response = await _http.GetAsync(url);
                    var body     = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("browse", err));
                        return;
                    }

                    var result = JsonSerializer.Deserialize<ModIoListResponse<ModIoMod>>(body, _json);
                    var mods   = result?.Data ?? new List<ModIoMod>();
                    _queue.Enqueue(() => OnBrowseComplete?.Invoke(mods));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("browse", ex.Message));
                }
            });
        }

        /// <summary>
        /// Pure builder for the browse request URL — the Story 9.10 Tier-1 testable seam that proves the six
        /// discovery verbs become mod.io-native query params rather than a local index. Threads the search text,
        /// sort key, and tag set straight into the mod.io request:
        ///   • <paramref name="sort"/> null/blank ⇒ default <c>-popular</c> (already-shipped, known-good).
        ///   • <paramref name="searchQuery"/> ⇒ escaped <c>_q</c> (omitted when blank).
        ///   • each non-blank tag ⇒ one escaped <c>tags=</c> param (mod.io AND semantics).
        /// No client-side re-sort/re-filter: whatever mod.io returns is what the panel shows.
        /// </summary>
        public static string BuildModsUrl(string baseUrl, int gameId, string apiKey, int limit, int offset,
                                          string? searchQuery, string? sort, IReadOnlyList<string>? tags)
        {
            string sortToken = string.IsNullOrWhiteSpace(sort) ? "-popular" : sort;

            var sb = new StringBuilder();
            sb.Append($"{baseUrl}/games/{gameId}/mods");
            sb.Append($"?api_key={apiKey}&_limit={limit}&_offset={offset}");
            sb.Append($"&_sort={Uri.EscapeDataString(sortToken)}");
            if (!string.IsNullOrWhiteSpace(searchQuery))
                sb.Append($"&_q={Uri.EscapeDataString(searchQuery)}");
            if (tags != null)
                foreach (var tag in tags)
                    if (!string.IsNullOrWhiteSpace(tag))
                        sb.Append($"&tags={Uri.EscapeDataString(tag)}");
            return sb.ToString();
        }

        // ── Tag options ───────────────────────────────────────────────────────

        /// <summary>
        /// Fetch this game's tag options from mod.io (<c>GET /games/{id}/tags</c>) and deliver a flattened tag-name
        /// list via <see cref="OnTagOptionsReady"/>. The browser builds its filter chips from this — there is no
        /// hardcoded/local tag index. No authentication required. On failure, fires <see cref="OnError"/> and no
        /// chips are produced (browse/sort still work).
        /// </summary>
        public void GetGameTagsAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string url = $"{BASE_URL}/games/{_gameId}/tags?api_key={_apiKey}";
                    var response = await _http.GetAsync(url);
                    var body     = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("tags", err));
                        return;
                    }

                    var result = JsonSerializer.Deserialize<ModIoListResponse<ModIoTagOption>>(body, _json);
                    var names  = FlattenTagNames(result?.Data);
                    _queue.Enqueue(() => OnTagOptionsReady?.Invoke(names));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("tags", ex.Message));
                }
            });
        }

        /// <summary>
        /// Pure builder for the flat chip-name list from a <c>GET /games/{id}/tags</c> response — the Story 9.10
        /// Tier-1 testable seam that pins the "no local tag index" contract and the malformed-group guard. Flattens
        /// every group's tag values into one list, skipping a null/malformed group (<c>"tags":null</c>) so one bad
        /// group never drops all tags, and dropping blank/whitespace tag names.
        /// </summary>
        public static List<string> FlattenTagNames(IReadOnlyList<ModIoTagOption>? groups)
        {
            var names = new List<string>();
            if (groups == null) return names;
            foreach (var group in groups)
            {
                if (group?.Tags == null) continue; // one malformed group ("tags":null) must not drop all tags
                foreach (var t in group.Tags)
                    if (!string.IsNullOrWhiteSpace(t))
                        names.Add(t);
            }
            return names;
        }

        // ── Thumbnail ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a mod's logo thumbnail bytes (raw, undecoded) and deliver them via <see cref="OnThumbnailReady"/>.
        /// The presentation layer decodes them into a texture. No authentication required. On failure, fires
        /// <see cref="OnError"/> and no bytes are delivered (the card keeps its placeholder).
        /// </summary>
        /// <param name="modId">mod.io mod ID (used to route the result back to the right card).</param>
        /// <param name="url">The <c>logo.thumb_320x180</c> (or similar) URL from a browse result.</param>
        public void DownloadThumbnailAsync(int modId, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await _http.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        _queue.Enqueue(() =>
                            OnError?.Invoke("thumbnail", $"HTTP {(int)response.StatusCode}"));
                        return;
                    }
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    _queue.Enqueue(() => OnThumbnailReady?.Invoke(modId, bytes));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("thumbnail", ex.Message));
                }
            });
        }

        // ── Download ──────────────────────────────────────────────────────────

        /// <summary>
        /// Download a mod file to <paramref name="destPath"/>.
        /// Fires <see cref="OnDownloadProgress"/> during the transfer and
        /// <see cref="OnDownloadComplete"/> on success.
        /// No authentication required for public mods.
        /// </summary>
        /// <param name="modId">mod.io mod ID (used for progress/completion events).</param>
        /// <param name="binaryUrl">The <c>modfile.download.binary_url</c> from browse results.</param>
        /// <param name="destPath">Absolute OS path where the file should be saved.</param>
        public void DownloadModFileAsync(int modId, string binaryUrl, string destPath)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    using var response = await _http.GetAsync(
                        binaryUrl, HttpCompletionOption.ResponseHeadersRead);

                    if (!response.IsSuccessStatusCode)
                    {
                        _queue.Enqueue(() =>
                            OnError?.Invoke("download", $"HTTP {(int)response.StatusCode}"));
                        return;
                    }

                    long total = response.Content.Headers.ContentLength ?? -1;
                    long read  = 0;

                    using var netStream  = await response.Content.ReadAsStreamAsync();
                    using var fileStream = File.Create(destPath);
                    var buf = new byte[81920]; // 80 KB read buffer
                    int n;
                    while ((n = await netStream.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buf, 0, n);
                        read += n;
                        if (total > 0)
                        {
                            float pct = (float)read / total;
                            _queue.Enqueue(() => OnDownloadProgress?.Invoke(modId, pct));
                        }
                    }

                    string capturedPath = destPath;
                    _queue.Enqueue(() => OnDownloadComplete?.Invoke(modId, capturedPath));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("download", ex.Message));
                }
            });
        }

        // ── Authentication ────────────────────────────────────────────────────

        /// <summary>
        /// Request a security code be sent to the given email address.
        /// Fires <see cref="OnAuthCodeSent"/> on success, <see cref="OnError"/> on failure.
        /// The user then enters the code in-game and calls
        /// <see cref="AuthenticateEmailExchangeAsync"/>.
        /// </summary>
        public void AuthenticateEmailRequestAsync(string email)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("api_key", _apiKey),
                        new KeyValuePair<string, string>("email",   email),
                    });

                    var response = await _http.PostAsync($"{BASE_URL}/oauth/emailrequest", payload);
                    var body     = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                        _queue.Enqueue(() => OnAuthCodeSent?.Invoke());
                    else
                    {
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("auth_request", err));
                    }
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("auth_request", ex.Message));
                }
            });
        }

        /// <summary>
        /// Exchange a security code for an OAuth2 access token.
        /// Fires <see cref="OnLoginSuccess"/> (with username) on success.
        /// After this call, <see cref="IsLoggedIn"/> is true and upload/subscribe/rate
        /// methods become available.
        /// </summary>
        public void AuthenticateEmailExchangeAsync(string securityCode)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("api_key",       _apiKey),
                        new KeyValuePair<string, string>("security_code", securityCode),
                    });

                    var response = await _http.PostAsync($"{BASE_URL}/oauth/emailexchange", payload);
                    var body     = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("auth_exchange", err));
                        return;
                    }

                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("access_token", out var tokenProp))
                    {
                        _queue.Enqueue(() => OnError?.Invoke("auth_exchange", "No token in response."));
                        return;
                    }

                    _accessToken = tokenProp.GetString() ?? "";
                    string username = await FetchUsernameAsync();
                    _queue.Enqueue(() => OnLoginSuccess?.Invoke(username));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("auth_exchange", ex.Message));
                }
            });
        }

        /// <summary>Clear the OAuth2 token — subsequent write operations will fail until re-login.</summary>
        public void Logout() => _accessToken = null;

        // ── Upload ────────────────────────────────────────────────────────────

        /// <summary>
        /// Create a new mod on mod.io and upload <paramref name="zipPath"/> as its first file.
        /// Requires <see cref="IsLoggedIn"/> = true.
        /// Fires <see cref="OnUploadComplete"/> (with new modId) on success.
        /// </summary>
        /// <param name="zipPath">Absolute path to the .chimera.zip to upload.</param>
        /// <param name="displayName">Human-readable map name shown on mod.io.</param>
        /// <param name="summary">Short description (max 250 chars).</param>
        /// <param name="version">Semantic version string e.g. "1.0.0".</param>
        /// <param name="tags">Tags to apply to the mod entry.</param>
        public void UploadModAsync(string zipPath, string displayName, string summary,
                                   string version, List<string> tags)
        {
            if (!IsLoggedIn)
            {
                _queue.Enqueue(() => OnError?.Invoke("upload", "Not logged in. Authenticate first."));
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    int modId = await CreateModEntryAsync(displayName, summary, tags);
                    if (modId <= 0) return; // error already enqueued by CreateModEntryAsync

                    await UploadModFileAsync(modId, zipPath, version);
                    _queue.Enqueue(() => OnUploadComplete?.Invoke(modId));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("upload", ex.Message));
                }
            });
        }

        // ── Subscribe / Unsubscribe ───────────────────────────────────────────

        /// <summary>Subscribe to a mod on mod.io (adds to user's subscription list).</summary>
        public void SubscribeAsync(int modId)
        {
            if (!IsLoggedIn) { _queue.Enqueue(() => OnError?.Invoke("subscribe", "Not logged in.")); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var req = AuthRequest(HttpMethod.Post,
                        $"{BASE_URL}/games/{_gameId}/mods/{modId}/subscribe");
                    req.Content = new StringContent("", Encoding.UTF8,
                        "application/x-www-form-urlencoded");
                    var response = await _http.SendAsync(req);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("subscribe", err));
                    }
                    else
                    {
                        _queue.Enqueue(() => OnSubscribeComplete?.Invoke(modId));
                    }
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("subscribe", ex.Message));
                }
            });
        }

        /// <summary>Remove subscription from a mod on mod.io.</summary>
        public void UnsubscribeAsync(int modId)
        {
            if (!IsLoggedIn) { _queue.Enqueue(() => OnError?.Invoke("unsubscribe", "Not logged in.")); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var req = AuthRequest(HttpMethod.Delete,
                        $"{BASE_URL}/games/{_gameId}/mods/{modId}/subscribe");
                    await _http.SendAsync(req);
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("unsubscribe", ex.Message));
                }
            });
        }

        // ── Rate ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Submit a rating for a mod. Replaces any existing rating from this user.
        /// </summary>
        /// <param name="positive">true = thumbs up (+1); false = thumbs down (−1).</param>
        public void RateAsync(int modId, bool positive)
        {
            if (!IsLoggedIn) { _queue.Enqueue(() => OnError?.Invoke("rate", "Not logged in.")); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("rating", positive ? "1" : "-1"),
                    });
                    var req = AuthRequest(HttpMethod.Post,
                        $"{BASE_URL}/games/{_gameId}/mods/{modId}/ratings");
                    req.Content = payload;
                    var response = await _http.SendAsync(req);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                        _queue.Enqueue(() => OnError?.Invoke("rate", err));
                    }
                    else
                    {
                        _queue.Enqueue(() => OnRateComplete?.Invoke(modId, positive));
                    }
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => OnError?.Invoke("rate", ex.Message));
                }
            });
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private HttpRequestMessage AuthRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken!);
            return req;
        }

        private async Task<string> FetchUsernameAsync()
        {
            try
            {
                var req = AuthRequest(HttpMethod.Get, $"{BASE_URL}/me");
                var response = await _http.SendAsync(req);
                var body     = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("username", out var u))
                    return u.GetString() ?? "unknown";
            }
            catch { /* best-effort */ }
            return "unknown";
        }

        private async Task<int> CreateModEntryAsync(string displayName, string summary,
                                                     List<string> tags)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(displayName), "name");
            form.Add(new StringContent(summary),     "summary");
            form.Add(new StringContent("1"),         "visible"); // 1 = public
            foreach (var tag in tags)
                form.Add(new StringContent(tag), "tags[]");

            var req = AuthRequest(HttpMethod.Post, $"{BASE_URL}/games/{_gameId}/mods");
            req.Content = form;

            var response = await _http.SendAsync(req);
            var body     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                _queue.Enqueue(() => OnError?.Invoke("upload_create", err));
                return -1;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
                return idProp.GetInt32();
            return -1;
        }

        private async Task UploadModFileAsync(int modId, string zipPath, string version)
        {
            using var fileStream = File.OpenRead(zipPath);
            var form = new MultipartFormDataContent();
            form.Add(new StreamContent(fileStream), "filedata", Path.GetFileName(zipPath));
            form.Add(new StringContent(version),    "version");
            form.Add(new StringContent("1"),        "active"); // mark as current file

            var req = AuthRequest(HttpMethod.Post,
                $"{BASE_URL}/games/{_gameId}/mods/{modId}/files");
            req.Content = form;

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                string err = ParseError(body) ?? $"HTTP {(int)response.StatusCode}";
                _queue.Enqueue(() => OnError?.Invoke("upload_file", err));
            }
        }

        /// <summary>Extract the error message from a mod.io error JSON body.</summary>
        private static string? ParseError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var msg))
                    return msg.GetString();
            }
            catch { /* ignore */ }
            return null;
        }
    }
}
