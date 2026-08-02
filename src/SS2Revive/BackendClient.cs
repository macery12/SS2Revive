using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using Data;
using Services;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Sends Bossa's HTTP requests to a self-hosted replacement backend instead of to hosts that
    /// no longer resolve.
    ///
    /// This has to intercept at <c>CrappyHttpsRequestService.Request</c> rather than by
    /// redirecting the hostname, because the scheme is not configurable anywhere: both
    /// <c>ConvertAndSend</c> and <c>MultipartFormRequest</c> build their target as the literal
    /// <c>"https://" + host + path</c>. Pointing <c>ServerEnvironmentService.GetHttpHost()</c> at
    /// a loopback address would therefore demand TLS on localhost. Owning the whole request lets
    /// the base URL carry its own scheme and port.
    ///
    /// The contract with the game is <see cref="CompleteDelegate"/>: one call per request, with a
    /// status code the scheduler can act on, delivered on the Unity main thread. Status codes are
    /// the part that matters most - see <c>PatchSet.DeadEndpointStatus</c> for why anything
    /// outside 2xx/4xx wedges the request scheduler permanently.
    /// </summary>
    internal static class BackendClient
    {
        private const int RequestTimeoutMs = 8000;
        private const int ProbeTimeoutMs = 1500;

        /// <summary>Transport failures answer with this, for the same reason dead endpoints do.</summary>
        private const int TransportFailureStatus = 404;

        private static string _baseUrl;
        private static bool _available;
        private static bool _local;
        private static int _nextRequestId = 0x4242_0000;

        /// <summary>True when something will answer the routed endpoints - either the in-process
        /// backend or a reachable remote one.</summary>
        internal static bool Available => _available;

        internal static string BaseUrl => _local ? "in-process" : _baseUrl;

        internal static void Initialise(BackendMode mode)
        {
            _available = false;
            _local = false;
            _baseUrl = string.Empty;

            switch (mode)
            {
                case BackendMode.Local:
                    LocalBackendHost.Initialise();
                    _local = LocalBackendHost.Available;
                    _available = _local;
                    if (!_local)
                    {
                        Plugin.Log.LogWarning("Local backend could not start; Bossa endpoints will "
                                              + "fail fast instead.");
                    }
                    return;

                case BackendMode.Remote:
                    InitialiseRemote(Plugin.BackendBaseUrl.Value);
                    return;

                default:
                    Plugin.Log.LogInfo("Backend mode is Off; every Bossa endpoint fails immediately.");
                    return;
            }
        }

        /// <summary>
        /// Probes /health once, synchronously, before any game code can issue a request.
        ///
        /// Synchronous on purpose. The alternative is discovering the server is down one request
        /// at a time, paying a connect timeout for each - during startup, serially, which is the
        /// exact stall the fail-fast patch exists to remove. One bounded wait at load, and one
        /// unambiguous line in the log, is the better trade.
        /// </summary>
        private static void InitialiseRemote(string baseUrl)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');

            if (_baseUrl.Length == 0)
            {
                Plugin.Log.LogWarning("Backend base URL is empty; staying on fail-fast.");
                return;
            }

            if (!_baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !_baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning("Backend base URL '" + _baseUrl
                                      + "' has no scheme; staying on fail-fast.");
                return;
            }

            if (Plugin.BackendAcceptAnyCertificate.Value
                && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                AcceptOurCertificateOnly();
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(_baseUrl + "/health");
                request.Method = "GET";
                request.Timeout = ProbeTimeoutMs;
                request.ReadWriteTimeout = ProbeTimeoutMs;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    _available = (int)response.StatusCode == 200;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Backend at " + _baseUrl + " did not answer /health ("
                                      + ex.Message + "). Bossa endpoints will fail fast instead.");
                return;
            }

            Plugin.Log.LogInfo(_available
                ? "Backend at " + _baseUrl + " is up; routing Bossa HTTP calls to it."
                : "Backend at " + _baseUrl + " answered /health with a non-200; staying on fail-fast.");
        }

        /// <summary>
        /// Trusts exactly the host we were configured to talk to, and only when the user asked
        /// for it. A blanket <c>return true</c> callback would also disable validation for every
        /// other TLS connection the process makes, Steam's included.
        /// </summary>
        private static void AcceptOurCertificateOnly()
        {
            string expectedHost;
            try
            {
                expectedHost = new Uri(_baseUrl).Host;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not parse backend host for certificate pinning: " + ex.Message);
                return;
            }

            var previous = ServicePointManager.ServerCertificateValidationCallback;
            ServicePointManager.ServerCertificateValidationCallback =
                (sender, certificate, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;

                    var request = sender as HttpWebRequest;
                    if (request != null
                        && string.Equals(request.RequestUri.Host, expectedHost,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return previous != null && previous(sender, certificate, chain, errors);
                };

            Plugin.Log.LogWarning("Accepting any TLS certificate presented by " + expectedHost
                                  + ". Only do this for a backend you host yourself.");
        }

        /// <summary>
        /// Rebuilds one of the eight <c>Request</c> overloads from its loose arguments and either
        /// sends it, answers it here, or declines it. Arguments are matched by parameter name
        /// rather than position - the overloads differ in both the path type (string or
        /// StringBuilder) and the body type (none, byte[], char[] or StringBuilder), so position
        /// is not stable across them.
        ///
        /// Returning false means "not handled": the caller falls back to the fail-fast path.
        /// <see cref="BackendRoutes"/> decides which requests are worth a round trip, so this runs
        /// even when the backend is unavailable - a dead endpoint should not reach the network
        /// whether or not there is a server listening.
        /// </summary>
        internal static bool TrySend(MethodBase original, object[] args, out int requestId)
        {
            requestId = 0;

            var byName = new Dictionary<string, object>();
            var parameters = original.GetParameters();
            for (var i = 0; i < parameters.Length && i < args.Length; i++)
                byName[parameters[i].Name] = args[i];

            object onCompleteObject;
            byName.TryGetValue("onComplete", out onCompleteObject);
            var onComplete = onCompleteObject as CompleteDelegate;

            string path;
            if (!TryGetPath(byName, out path))
                return false;

            var verb = VerbFor(byName);

            string localBody;
            var disposition = BackendRoutes.Resolve(verb, path, _available, out localBody);

            if (disposition == BackendRoutes.Disposition.Block)
                return false;

            if (disposition == BackendRoutes.Disposition.AnswerLocally)
            {
                requestId = ++_nextRequestId;
                Complete(requestId, 200, localBody, onComplete);
                return true;
            }

            // Only the forwarding path needs the body, and an unreadable body has to fall through
            // to fail-fast rather than silently sending an empty request.
            string body;
            if (!TryGetBody(byName, out body))
                return false;

            requestId = ++_nextRequestId;

            if (_local)
            {
                SendLocal(requestId, verb, path, body, onComplete);
                return true;
            }

            var token = Get<string>(byName, "serverAuthenticationToken");
            var gameName = Get<string>(byName, "gameName");
            var playerId = byName.ContainsKey("playerId") ? byName["playerId"] : null;

            Send(requestId, verb, path, token, playerId, gameName, body, onComplete);
            return true;
        }

        /// <summary>
        /// Answers in this process, but still on a worker thread and still a frame later.
        ///
        /// It is tempting to answer inline - there is no socket, and the work is a dictionary
        /// lookup plus a JSON write. Two reasons not to. The store writes its file on the same
        /// call, which is disk I/O that has no business on the frame thread; and every one of
        /// these callers was written against an asynchronous HTTP client, so a completion that
        /// fires inside the caller's own stack reaches code that has not finished setting itself
        /// up. That second hazard is exactly what <see cref="Dispatcher"/> exists for, and it is
        /// not hypothetical - it is what made local authentication crash before it was deferred.
        /// </summary>
        private static void SendLocal(int requestId, string verb, string path, string body,
                                      CompleteDelegate onComplete)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                LocalResponse response;
                try
                {
                    response = LocalBackendHost.Handle(verb, path, body);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Local backend " + verb + " " + path + " threw: " + ex);
                    response = new LocalResponse { Status = TransportFailureStatus, Body = null };
                }

                Complete(requestId, response.Status, response.Body, onComplete);
            });
        }

        private static T Get<T>(Dictionary<string, object> byName, string key) where T : class
        {
            object value;
            return byName.TryGetValue(key, out value) ? value as T : null;
        }

        private static bool TryGetPath(Dictionary<string, object> byName, out string path)
        {
            path = null;
            object value;
            if (!byName.TryGetValue("path", out value) || value == null)
                return false;

            path = value.ToString();
            return path.Length > 0;
        }

        private static string VerbFor(Dictionary<string, object> byName)
        {
            object value;
            if (!byName.TryGetValue("method", out value) || value == null)
                return "GET";

            switch ((Services.HttpMethod)value)
            {
                case Services.HttpMethod.Post: return "POST";
                case Services.HttpMethod.Put: return "PUT";
                case Services.HttpMethod.Delete: return "DELETE";
                case Services.HttpMethod.Options: return "OPTIONS";
                default: return "GET";
            }
        }

        /// <summary>
        /// Bodies are always UTF-8 JSON on the wire. The char[] and StringBuilder overloads carry
        /// text directly; the byte[] overload carries what ManualJsonSerializer produced, which is
        /// already UTF-8 bytes of JSON, so it is decoded here and re-encoded on send rather than
        /// being special-cased through the whole path.
        /// </summary>
        private static bool TryGetBody(Dictionary<string, object> byName, out string body)
        {
            body = null;

            object content;
            if (!byName.TryGetValue("httpContent", out content) || content == null)
                return true;

            var offset = IntArg(byName, "httpContentOffset", 0);

            var bytes = content as byte[];
            if (bytes != null)
            {
                var length = IntArg(byName, "httpContentLength", bytes.Length - offset);
                if (offset < 0 || length < 0 || offset + length > bytes.Length)
                    return false;
                body = Encoding.UTF8.GetString(bytes, offset, length);
                return true;
            }

            var chars = content as char[];
            if (chars != null)
            {
                var length = IntArg(byName, "httpContentLength", chars.Length - offset);
                if (offset < 0 || length < 0 || offset + length > chars.Length)
                    return false;
                body = new string(chars, offset, length);
                return true;
            }

            var builder = content as StringBuilder;
            if (builder != null)
            {
                body = builder.ToString();
                return true;
            }

            Plugin.Log.LogWarning("Unrecognised HTTP body type " + content.GetType().Name
                                  + "; falling back to fail-fast for this request.");
            return false;
        }

        private static int IntArg(Dictionary<string, object> byName, string key, int fallback)
        {
            object value;
            if (!byName.TryGetValue(key, out value) || value == null)
                return fallback;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static void Send(int requestId, string verb, string path, string token,
                                 object playerId, string gameName, string body,
                                 CompleteDelegate onComplete)
        {
            var url = _baseUrl + path;

            // A worker thread, because HttpWebRequest here is synchronous and the caller is the
            // Unity main thread. The completion is marshalled back through Dispatcher - the game
            // reads the response with pooled, main-thread-only JSON machinery.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var status = TransportFailureStatus;
                string responseBody = null;

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = verb;
                    request.Timeout = RequestTimeoutMs;
                    request.ReadWriteTimeout = RequestTimeoutMs;
                    request.KeepAlive = true;
                    request.UserAgent = "SS2Revive/" + Plugin.PluginVersion;

                    if (!string.IsNullOrEmpty(token))
                        request.Headers["Security"] = token;
                    if (playerId != null)
                        request.Headers["playerId"] = playerId.ToString();
                    if (!string.IsNullOrEmpty(gameName))
                        request.Headers["gameName"] = gameName;

                    if (body != null)
                    {
                        var payload = Encoding.UTF8.GetBytes(body);
                        request.ContentType = "application/json";
                        request.ContentLength = payload.Length;
                        using (var stream = request.GetRequestStream())
                            stream.Write(payload, 0, payload.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                        ReadResponse(response, out status, out responseBody);
                }
                catch (WebException ex)
                {
                    // A 4xx arrives as an exception. That is a real answer from the server and
                    // has to reach the caller as one - the request scheduler treats 4xx as a
                    // terminal failure and releases its resource locks, which is what we want.
                    var http = ex.Response as HttpWebResponse;
                    if (http != null)
                    {
                        using (http)
                            ReadResponse(http, out status, out responseBody);
                    }
                    else
                    {
                        Plugin.Log.LogWarning("Backend " + verb + " " + path + " failed: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Backend " + verb + " " + path + " threw: " + ex);
                }

                Complete(requestId, status, responseBody, onComplete);
            });
        }

        private static void ReadResponse(HttpWebResponse response, out int status, out string body)
        {
            status = (int)response.StatusCode;

            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    body = null;
                    return;
                }

                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                    body = reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Hands the response to the game exactly the way its own client does: a NativeByteBuffer
        /// holding UTF-16 code units, disposed as soon as the callback returns. The buffer is not
        /// the wire encoding - it is an in-process handoff, and ManualJsonDeserializer reads chars
        /// out of it, so writing UTF-8 here would produce garbage on the first non-ASCII byte.
        /// </summary>
        private static void Complete(int requestId, int status, string body, CompleteDelegate onComplete)
        {
            if (onComplete == null)
                return;

            Dispatcher.NextFrame(() =>
            {
                var text = body ?? string.Empty;
                var buffer = new NativeByteBuffer(Math.Max(1, text.Length * 2));

                try
                {
                    if (text.Length > 0)
                        buffer.WriteUnicode(text);

                    onComplete(requestId, status >= 200 && status < 300, status, buffer);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Backend completion callback threw: " + ex);
                }
                finally
                {
                    buffer.Dispose();
                }
            });
        }
    }
}
