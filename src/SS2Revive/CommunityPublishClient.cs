using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SS2ReviveData;

namespace SS2Revive
{
    internal sealed class CommunityPublishCallbacks
    {
        internal Action<string> Status;
        internal Action<float> Progress;
        internal Action<string, Uri> AuthenticationRequired;
        internal Action<string, int> Published;
        internal Action<string> Failed;
    }

    internal sealed class CommunityAccountCallbacks
    {
        internal Action<string> Status;
        internal Action<string, Uri> AuthenticationRequired;
        internal Action Completed;
        internal Action<string> Failed;
    }

    internal sealed class CommunityPublishOperation
    {
        private readonly ManualResetEvent _cancelled = new ManualResetEvent(false);
        private readonly object _gate = new object();
        private HttpWebRequest _activeRequest;
        private int _isCancelled;

        internal bool IsCancelled => Interlocked.CompareExchange(ref _isCancelled, 0, 0) != 0;

        internal void Cancel()
        {
            if (Interlocked.Exchange(ref _isCancelled, 1) != 0) return;
            _cancelled.Set();
            lock (_gate)
            {
                try { _activeRequest?.Abort(); }
                catch { }
            }
        }

        internal void SetRequest(HttpWebRequest request)
        {
            lock (_gate)
            {
                if (IsCancelled)
                {
                    request.Abort();
                    throw new OperationCanceledException();
                }
                _activeRequest = request;
            }
        }

        internal void ClearRequest(HttpWebRequest request)
        {
            lock (_gate)
                if (ReferenceEquals(_activeRequest, request)) _activeRequest = null;
        }

        internal void Wait(int milliseconds)
        {
            if (_cancelled.WaitOne(Math.Max(1, milliseconds))) throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Authenticated publishing client. Steam sign-in happens only in the system browser; this
    /// class receives short-lived API tokens, stores only the refresh token under Windows DPAPI,
    /// and streams the exact preflighted bundle to the Worker rather than exposing R2 credentials.
    /// </summary>
    internal static class CommunityPublishClient
    {
        private const int JsonLimit = 64 * 1024;
        private const int RequestTimeoutMs = 20000;
        private const int UploadTimeoutMs = 120000;
        private static readonly object AuthGate = new object();
        private static readonly byte[] TokenEntropy =
            Encoding.UTF8.GetBytes("SS2Revive|community.m12labs.net|v1");

        private static Uri _origin;
        private static Uri _apiBase;
        private static string _tokenPath;
        private static bool _tokenLoaded;
        private static string _refreshToken;
        private static string _accessToken;
        private static DateTime _accessExpiresUtc = DateTime.MinValue;

        internal static bool Enabled => _origin != null && _apiBase != null;

        internal static void Initialise(string saveRoot, string configuredCatalogUrl)
        {
            _origin = null;
            _apiBase = null;
            _tokenPath = null;
            _tokenLoaded = false;
            _refreshToken = null;
            _accessToken = null;
            _accessExpiresUtc = DateTime.MinValue;
            Uri catalog;
            if (string.IsNullOrWhiteSpace(saveRoot)
                || !Uri.TryCreate(configuredCatalogUrl, UriKind.Absolute, out catalog)
                || catalog.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(catalog.UserInfo)
                || !string.IsNullOrEmpty(catalog.Query) || !string.IsNullOrEmpty(catalog.Fragment)) return;
            _origin = new Uri(catalog.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
            _apiBase = new Uri(catalog, ".");
            _tokenPath = Path.Combine(saveRoot, "auth", "community-session.v1");
        }

        internal static CommunityPublishOperation Publish(LevelSharing.UploadPackage package,
                                                            CommunityPublishCallbacks callbacks)
        {
            var operation = new CommunityPublishOperation();
            // Steamworks calls remain on Unity's main thread. Only this immutable identity crosses
            // into the network worker for the browser-account mismatch check.
            var expectedSteamId64 = SteamIdentity.GetSteamId64().ToString();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!Enabled) throw new InvalidOperationException("The community API is disabled.");
                    if (package == null || string.IsNullOrEmpty(package.Path)
                        || package.SizeBytes < 1 || package.SizeBytes > LevelBundle.MaxBundleBytes
                        || string.IsNullOrEmpty(package.Sha256))
                    {
                        throw new InvalidDataException("The prepared upload package is invalid.");
                    }
                    Post(callbacks.Status, "Checking the protected Steam publishing session...");
                    if (expectedSteamId64 == "0")
                        throw new InvalidOperationException("Steam must be online before publishing.");
                    var accessToken = EnsureAccessToken(operation, callbacks.AuthenticationRequired,
                                                        expectedSteamId64);
                    operation.CancelIfRequested();

                    Post(callbacks.Status, "Reserving private upload space...");
                    var reservationJson = Json.Object()
                        .Add("sizeBytes", package.SizeBytes)
                        .Add("sha256", package.Sha256)
                        .ToString();
                    var reservation = SendJson("POST", new Uri(_apiBase, "uploads"), reservationJson,
                                               accessToken, operation, RequestTimeoutMs);
                    RequireStatus(reservation, 201);
                    var reservationBody = ParseObject(reservation);
                    var uploadId = reservationBody["uploadId"].AsStringOr(string.Empty);
                    var bundlePath = reservationBody["bundleUploadPath"].AsStringOr(string.Empty);
                    var statusPath = reservationBody["statusPath"].AsStringOr(string.Empty);
                    if (!IsGuid(uploadId) || !IsSafeApiPath(bundlePath, "/v1/uploads/", "/bundle")
                        || !IsSafeApiPath(statusPath, "/v1/uploads/", string.Empty))
                    {
                        throw new InvalidDataException("The server returned an invalid upload reservation.");
                    }

                    Post(callbacks.Status, "Uploading the validated package to private quarantine...");
                    var upload = UploadFile(new Uri(_origin, bundlePath), package, accessToken,
                                            operation, callbacks);
                    RequireStatus(upload, 202);

                    Post(callbacks.Status, "Upload complete. Server validation is running...");
                    PollPublication(new Uri(_origin, statusPath), uploadId, accessToken,
                                    operation, callbacks);
                }
                catch (OperationCanceledException)
                {
                    Post(callbacks.Status, "Publishing cancelled.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Community publishing failed: " + ex.Message);
                    Post(callbacks.Failed, SafeMessage(ex));
                }
            });
            return operation;
        }

        internal static CommunityPublishOperation Unpublish(string mapId,
                                                             CommunityAccountCallbacks callbacks)
        {
            return MapAction("POST", mapId, "unpublish", "{}", 200, true,
                             "Removing this map from the community catalogue...", callbacks);
        }

        internal static CommunityPublishOperation Archive(string mapId,
                                                           CommunityAccountCallbacks callbacks)
        {
            return MapAction("DELETE", mapId, string.Empty, null, 204, true,
                             "Archiving this community map...", callbacks);
        }

        internal static CommunityPublishOperation Report(string mapId, string reason,
                                                          CommunityAccountCallbacks callbacks)
        {
            var allowed = reason == "BROKEN" || reason == "NSFW_CONTENT" || reason == "COPY"
                          || reason == "LEVEL_DESIGN_COPYRIGHT" || reason == "FARMING";
            if (!allowed)
            {
                Dispatcher.NextFrame(() => callbacks?.Failed?.Invoke("The report reason is invalid."));
                return new CommunityPublishOperation();
            }
            return MapAction("POST", mapId, "reports",
                Json.Object().Add("reason", reason).ToString(), 201, false,
                "Sending the authenticated map report...", callbacks);
        }

        private static CommunityPublishOperation MapAction(
            string method, string mapId, string suffix, string json, int expectedStatus,
            bool removeFromCatalogue, string statusText, CommunityAccountCallbacks callbacks)
        {
            var operation = new CommunityPublishOperation();
            var expectedSteamId64 = SteamIdentity.GetSteamId64().ToString();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!Enabled) throw new InvalidOperationException("The community API is disabled.");
                    if (!IsGuid(mapId)) throw new InvalidDataException("The community map id is invalid.");
                    if (expectedSteamId64 == "0")
                        throw new InvalidOperationException("Steam must be online for this action.");
                    Post(callbacks?.Status, statusText);
                    var accessToken = EnsureAccessToken(operation, callbacks?.AuthenticationRequired,
                                                        expectedSteamId64);
                    operation.CancelIfRequested();
                    var path = "maps/" + mapId + (string.IsNullOrEmpty(suffix) ? string.Empty : "/" + suffix);
                    ApiResponse response;
                    if (json == null)
                        response = Send(method, new Uri(_apiBase, path), null, null, accessToken,
                                        operation, RequestTimeoutMs, null);
                    else
                        response = SendJson(method, new Uri(_apiBase, path), json, accessToken,
                                            operation, RequestTimeoutMs);
                    RequireStatus(response, expectedStatus);
                    if (removeFromCatalogue) CommunityCatalogClient.NoteRemoved(mapId);
                    Post(callbacks?.Completed);
                }
                catch (OperationCanceledException)
                {
                    Post(callbacks?.Status, "Community action cancelled.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Community account action failed: " + ex.Message);
                    Post(callbacks?.Failed, SafeMessage(ex));
                }
            });
            return operation;
        }

        private static string EnsureAccessToken(CommunityPublishOperation operation,
                                                Action<string, Uri> authenticationRequired,
                                                string expectedSteamId64)
        {
            lock (AuthGate)
            {
                operation.CancelIfRequested();
                LoadRefreshToken();
                if (!string.IsNullOrEmpty(_accessToken)
                    && _accessExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return _accessToken;
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    try
                    {
                        var refreshed = SendJson("POST", new Uri(_apiBase, "auth/refresh"),
                            Json.Object().Add("refreshToken", _refreshToken).ToString(), null,
                            operation, RequestTimeoutMs);
                        if (refreshed.Status == 200)
                        {
                            AcceptTokenResponse(refreshed);
                            ValidateIdentity(operation, expectedSteamId64);
                            return _accessToken;
                        }
                        if (refreshed.Status == 401 || refreshed.Status == 403) DeleteRefreshToken();
                        else RequireStatus(refreshed, 200);
                    }
                    catch (ApiException ex)
                    {
                        if (ex.Status == 401 || ex.Status == 403) DeleteRefreshToken();
                        else throw;
                    }
                }

                var created = SendJson("POST", new Uri(_apiBase, "auth/device-sessions"), "{}",
                                       null, operation, RequestTimeoutMs);
                RequireStatus(created, 201);
                var body = ParseObject(created);
                var sessionId = body["deviceSessionId"].AsStringOr(string.Empty);
                var secret = body["deviceSecret"].AsStringOr(string.Empty);
                var userCode = body["userCode"].AsStringOr(string.Empty);
                var activationPath = body["activationPath"].AsStringOr(string.Empty);
                var pollSeconds = body["pollIntervalSeconds"].AsIntOr(5);
                if (!IsGuid(sessionId) || secret.Length != 43 || userCode.Length != 9
                    || !activationPath.StartsWith("/activate?", StringComparison.Ordinal)
                    || pollSeconds < 1 || pollSeconds > 30)
                {
                    throw new InvalidDataException("The server returned an invalid Steam device session.");
                }
                var activationUri = new Uri(_origin, activationPath);
                if (activationUri.Scheme != Uri.UriSchemeHttps || activationUri.Authority != _origin.Authority)
                    throw new InvalidDataException("The Steam activation URL was not on the configured server.");
                Post(authenticationRequired, userCode, activationUri);

                while (true)
                {
                    operation.Wait(pollSeconds * 1000);
                    var poll = SendJson("POST", new Uri(_apiBase, "auth/device-sessions/token"),
                        Json.Object().Add("deviceSessionId", sessionId).Add("deviceSecret", secret).ToString(),
                        null, operation, RequestTimeoutMs);
                    if (poll.Status == 202) continue;
                    if (poll.Status == 429)
                    {
                        pollSeconds = Math.Max(pollSeconds, RetryAfter(poll, pollSeconds));
                        continue;
                    }
                    RequireStatus(poll, 200);
                    AcceptTokenResponse(poll);
                    ValidateIdentity(operation, expectedSteamId64);
                    return _accessToken;
                }
            }
        }

        private static void ValidateIdentity(CommunityPublishOperation operation,
                                             string expectedSteamId64)
        {
            var response = Send("GET", new Uri(_apiBase, "me"), null, null, _accessToken,
                                operation, RequestTimeoutMs, null);
            RequireStatus(response, 200);
            var steamId = ParseObject(response)["steamId64"].AsStringOr(string.Empty);
            if (steamId.Length < 17 || steamId != expectedSteamId64)
            {
                DeleteRefreshToken();
                throw new InvalidOperationException("The browser signed into a different Steam account than the game.");
            }
        }

        private static void AcceptTokenResponse(ApiResponse response)
        {
            var body = ParseObject(response);
            var access = body["accessToken"].AsStringOr(string.Empty);
            var refresh = body["refreshToken"].AsStringOr(string.Empty);
            var expires = body["expiresInSeconds"].AsIntOr(0);
            var scope = body["scope"].AsStringOr(string.Empty);
            if (access.Length < 32 || access.Length > 4096 || refresh.Length != 43
                || expires < 60 || expires > 3600 || !HasScope(scope, "maps:upload"))
            {
                throw new InvalidDataException("Steam authentication did not grant a valid publishing session.");
            }
            _accessToken = access;
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds(expires);
            _refreshToken = refresh;
            SaveRefreshToken();
        }

        private static void PollPublication(Uri statusUri, string uploadId, string accessToken,
                                            CommunityPublishOperation operation,
                                            CommunityPublishCallbacks callbacks)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                operation.Wait(attempt == 0 ? 500 : 2000);
                var response = Send("GET", statusUri, null, null, accessToken, operation,
                                    RequestTimeoutMs, null);
                RequireStatus(response, 200);
                var body = ParseObject(response);
                if (body["uploadId"].AsStringOr(string.Empty) != uploadId)
                    throw new InvalidDataException("The upload status identity changed.");
                var status = body["status"].AsStringOr(string.Empty);
                if (status == "published")
                {
                    var mapId = body["mapId"].AsStringOr(string.Empty);
                    var revision = body["revision"].AsIntOr(0);
                    if (!IsGuid(mapId) || revision < 1)
                        throw new InvalidDataException("The published map response is invalid.");
                    CommunityCatalogClient.NotePublished(mapId, revision);
                    CommunityCatalogClient.ForceRefresh();
                    Post(callbacks.Published, mapId, revision);
                    return;
                }
                if (status == "rejected" || status == "expired" || status == "cancelled")
                {
                    var message = body["errorMessage"].AsStringOr("The server rejected the upload.");
                    throw new InvalidDataException(message);
                }
                if (status != "uploaded" && status != "validating" && status != "reserved")
                    throw new InvalidDataException("The server returned an unknown upload state.");
            }
            throw new TimeoutException("Server validation did not finish in time.");
        }

        private static ApiResponse UploadFile(Uri uri, LevelSharing.UploadPackage package,
                                              string accessToken, CommunityPublishOperation operation,
                                              CommunityPublishCallbacks callbacks)
        {
            using (var input = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != package.SizeBytes) throw new IOException("The package changed after preflight.");
                return Send("PUT", uri, input, "application/vnd.ss2revive.level", accessToken,
                    operation, UploadTimeoutMs, delegate(long sent)
                    {
                        var progress = package.SizeBytes <= 0 ? 0f : (float)sent / package.SizeBytes;
                        Post(callbacks.Progress, Math.Max(0f, Math.Min(1f, progress)));
                    }, package.SizeBytes, package.Sha256);
            }
        }

        private static ApiResponse SendJson(string method, Uri uri, string json, string accessToken,
                                            CommunityPublishOperation operation, int timeout)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            using (var input = new MemoryStream(bytes, false))
                return Send(method, uri, input, "application/json", accessToken, operation,
                            timeout, null, bytes.LongLength, null);
        }

        private static ApiResponse Send(string method, Uri uri, Stream body, string contentType,
                                        string accessToken, CommunityPublishOperation operation,
                                        int timeout, Action<long> progress, long contentLength = 0,
                                        string sha256 = null)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || uri.Authority != _origin.Authority)
                throw new InvalidOperationException("Refusing an API request outside the configured HTTPS origin.");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = method;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.KeepAlive = false;
            request.UserAgent = "SS2Revive/" + Plugin.PluginVersion;
            request.Accept = "application/json";
            if (!string.IsNullOrEmpty(accessToken)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
            if (body != null)
            {
                request.ContentType = contentType;
                request.ContentLength = contentLength;
                request.AllowWriteStreamBuffering = false;
                if (!string.IsNullOrEmpty(sha256)) request.Headers["X-Content-SHA256"] = sha256;
            }
            operation.SetRequest(request);
            try
            {
                if (body != null)
                {
                    using (var output = request.GetRequestStream())
                    {
                        var buffer = new byte[64 * 1024];
                        long sent = 0;
                        while (true)
                        {
                            operation.CancelIfRequested();
                            var read = body.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            output.Write(buffer, 0, read);
                            sent += read;
                            progress?.Invoke(sent);
                        }
                        if (sent != contentLength) throw new IOException("The upload size changed while sending.");
                    }
                }
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                        return ReadResponse(response);
                }
                catch (WebException ex)
                {
                    var response = ex.Response as HttpWebResponse;
                    if (response == null) throw;
                    using (response) return ReadResponse(response);
                }
            }
            finally
            {
                operation.ClearRequest(request);
            }
        }

        private static ApiResponse ReadResponse(HttpWebResponse response)
        {
            using (var input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (input != null)
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        if (output.Length + read > JsonLimit)
                            throw new InvalidDataException("The server response exceeded its limit.");
                        output.Write(buffer, 0, read);
                    }
                }
                return new ApiResponse
                {
                    Status = (int)response.StatusCode,
                    Body = Encoding.UTF8.GetString(output.ToArray()),
                    RetryAfter = response.Headers["Retry-After"],
                };
            }
        }

        private static Json ParseObject(ApiResponse response)
        {
            var parsed = Json.TryParse(response.Body ?? string.Empty);
            if (parsed == null || parsed.Kind != JsonKind.Object)
                throw new InvalidDataException("The server returned invalid JSON.");
            return parsed;
        }

        private static void RequireStatus(ApiResponse response, int expected)
        {
            if (response.Status == expected) return;
            var code = "http_" + response.Status;
            var message = "The community server returned HTTP " + response.Status + ".";
            var parsed = Json.TryParse(response.Body ?? string.Empty);
            if (parsed != null)
            {
                var error = parsed["error"];
                if (error != null)
                {
                    code = error["code"].AsStringOr(code);
                    message = error["message"].AsStringOr(message);
                }
            }
            throw new ApiException(response.Status, code, message);
        }

        private static int RetryAfter(ApiResponse response, int fallback)
        {
            int seconds;
            return int.TryParse(response.RetryAfter, out seconds) && seconds >= 1 && seconds <= 60
                ? seconds : fallback;
        }

        private static bool HasScope(string scopes, string required)
        {
            var split = (scopes ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < split.Length; i++) if (split[i] == required) return true;
            return false;
        }

        private static bool IsGuid(string value)
        {
            Guid ignored;
            return !string.IsNullOrEmpty(value) && Guid.TryParseExact(value, "D", out ignored)
                   && value == value.ToLowerInvariant();
        }

        private static bool IsSafeApiPath(string value, string prefix, string suffix)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(prefix, StringComparison.Ordinal)
                   && value.EndsWith(suffix, StringComparison.Ordinal)
                   && value.IndexOf("..", StringComparison.Ordinal) < 0
                   && value.IndexOf('\\') < 0 && value.IndexOf("//", StringComparison.Ordinal) < 0;
        }

        private static void LoadRefreshToken()
        {
            if (_tokenLoaded) return;
            _tokenLoaded = true;
            if (string.IsNullOrEmpty(_tokenPath) || !File.Exists(_tokenPath)) return;
            try
            {
                var encrypted = File.ReadAllBytes(_tokenPath);
                if (encrypted.Length < 32 || encrypted.Length > 8192) throw new InvalidDataException();
                var plain = ProtectedData.Unprotect(encrypted, TokenEntropy, DataProtectionScope.CurrentUser);
                var value = Encoding.UTF8.GetString(plain);
                Array.Clear(plain, 0, plain.Length);
                if (!value.StartsWith("v1\n", StringComparison.Ordinal) || value.Length != 46)
                    throw new InvalidDataException();
                _refreshToken = value.Substring(3);
            }
            catch
            {
                DeleteRefreshToken();
            }
        }

        private static void SaveRefreshToken()
        {
            if (string.IsNullOrEmpty(_tokenPath) || string.IsNullOrEmpty(_refreshToken)) return;
            var plain = Encoding.UTF8.GetBytes("v1\n" + _refreshToken);
            try
            {
                var encrypted = ProtectedData.Protect(plain, TokenEntropy, DataProtectionScope.CurrentUser);
                AtomicFile.WriteAllBytes(_tokenPath, encrypted);
                Array.Clear(encrypted, 0, encrypted.Length);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        private static void DeleteRefreshToken()
        {
            _refreshToken = null;
            _accessToken = null;
            _accessExpiresUtc = DateTime.MinValue;
            try { if (!string.IsNullOrEmpty(_tokenPath) && File.Exists(_tokenPath)) File.Delete(_tokenPath); }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not remove the community session: " + ex.Message); }
        }

        private static string SafeMessage(Exception error)
        {
            var message = error?.Message ?? "Publishing failed.";
            if (message.Length > 400) message = message.Substring(0, 400);
            return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static void Post(Action<string> callback, string value)
        {
            if (callback != null) Dispatcher.NextFrame(() => callback(value));
        }

        private static void Post(Action callback)
        {
            if (callback != null) Dispatcher.NextFrame(callback);
        }

        private static void Post(Action<float> callback, float value)
        {
            if (callback != null) Dispatcher.NextFrame(() => callback(value));
        }

        private static void Post(Action<string, Uri> callback, string code, Uri uri)
        {
            if (callback != null) Dispatcher.NextFrame(() => callback(code, uri));
        }

        private static void Post(Action<string, int> callback, string mapId, int revision)
        {
            if (callback != null) Dispatcher.NextFrame(() => callback(mapId, revision));
        }

        private sealed class ApiResponse
        {
            internal int Status;
            internal string Body;
            internal string RetryAfter;
        }

        private sealed class ApiException : Exception
        {
            internal readonly int Status;
            internal readonly string Code;

            internal ApiException(int status, string code, string message) : base(message)
            {
                Status = status;
                Code = code;
            }
        }

        private static void CancelIfRequested(this CommunityPublishOperation operation)
        {
            if (operation.IsCancelled) throw new OperationCanceledException();
        }
    }
}
