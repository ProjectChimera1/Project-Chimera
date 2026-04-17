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
    }

    public class ModIoTag
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
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
        public void BrowseModsAsync(int limit = 20, int offset = 0, string? searchQuery = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string url = $"{BASE_URL}/games/{_gameId}/mods" +
                                 $"?api_key={_apiKey}&_limit={limit}&_offset={offset}&_sort=-popular";
                    if (!string.IsNullOrWhiteSpace(searchQuery))
                        url += $"&_q={Uri.EscapeDataString(searchQuery)}";

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
                    await _http.SendAsync(req);
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
