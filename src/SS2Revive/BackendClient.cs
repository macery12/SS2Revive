using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using Data;
using Services;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Answers Bossa's HTTP requests in this process instead of letting them reach hosts that no
    /// longer resolve.
    ///
    /// This has to intercept at <c>CrappyHttpsRequestService.Request</c> rather than by
    /// redirecting the hostname, because the scheme is not configurable anywhere: both
    /// <c>ConvertAndSend</c> and <c>MultipartFormRequest</c> build their target as the literal
    /// <c>"https://" + host + path</c>. Pointing <c>ServerEnvironmentService.GetHttpHost()</c> at
    /// a loopback address would therefore demand TLS on localhost. Owning the whole request means
    /// no address, scheme or port is involved at all.
    ///
    /// The contract with the game is <see cref="CompleteDelegate"/>: one call per request, with a
    /// status code the scheduler can act on, delivered on the Unity main thread. Status codes are
    /// the part that matters most - see <c>PatchSet.DeadEndpointStatus</c> for why anything
    /// outside 2xx/4xx wedges the request scheduler permanently.
    /// </summary>
    internal static class BackendClient
    {
        /// <summary>A backend that threw answers with this, for the same reason dead endpoints do.</summary>
        private const int BackendFailureStatus = 404;

        private static bool _available;
        private static int _nextRequestId = 0x4242_0000;

        /// <summary>True when the in-process backend came up and will answer the routed endpoints.</summary>
        internal static bool Available => _available;

        internal static string BaseUrl => "in-process";

        internal static void Initialise(BackendMode mode)
        {
            _available = false;

            if (mode != BackendMode.Local)
            {
                Plugin.Log.LogInfo("Backend mode is Off; every Bossa endpoint fails immediately. "
                                   + "Progression, daily challenges and cosmetics will not work.");
                return;
            }

            LocalBackendHost.Initialise();
            _available = LocalBackendHost.Available;

            if (!_available)
            {
                Plugin.Log.LogWarning("Local backend could not start; Bossa endpoints will "
                                      + "fail fast instead.");
            }
        }

        /// <summary>
        /// Rebuilds one of the eight <c>Request</c> overloads from its loose arguments and either
        /// answers it from the backend, answers it with a canned body, or declines it. Arguments
        /// are matched by parameter name rather than position - the overloads differ in both the
        /// path type (string or StringBuilder) and the body type (none, byte[], char[] or
        /// StringBuilder), so position is not stable across them.
        ///
        /// Returning false means "not handled": the caller falls back to the fail-fast path.
        /// <see cref="BackendRoutes"/> decides which requests the backend has an answer for, so
        /// this runs even when the backend is unavailable - a dead endpoint should not reach the
        /// network whether or not anything is going to answer it.
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
                requestId = NextRequestId();
                Complete(requestId, 200, localBody, onComplete);
                return true;
            }

            // Only the backend path needs the body, and an unreadable body has to fall through to
            // fail-fast rather than silently handing over an empty request.
            string body;
            if (!TryGetBody(byName, out body))
                return false;

            requestId = NextRequestId();
            SendLocal(requestId, verb, path, body, onComplete);
            return true;
        }

        /// <summary>
        /// Interlocked because the game issues requests from more than one thread and a torn
        /// increment would hand two in-flight requests the same id - which the scheduler matches
        /// responses on, so one of them would be completed twice and the other never.
        /// </summary>
        private static int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

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
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LocalResponse response;
                try
                {
                    response = LocalBackendHost.Handle(verb, path, body);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Local backend " + verb + " " + path + " threw: " + ex);
                    response = new LocalResponse { Status = BackendFailureStatus, Body = null };
                }

                Complete(requestId, response.Status, response.Body, onComplete);
            });
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

        /// <summary>
        /// The argument arrives boxed, so this is an unboxing cast rather than a conversion, and
        /// an unboxing cast of anything that is not exactly a <c>Services.HttpMethod</c> throws.
        /// Every overload passes one today; the guard is here because this runs inside a Harmony
        /// prefix over the game's entire HTTP surface, where an exception is not a failed request
        /// but a failed frame.
        /// </summary>
        private static string VerbFor(Dictionary<string, object> byName)
        {
            object value;
            if (!byName.TryGetValue("method", out value) || !(value is Services.HttpMethod method))
                return "GET";

            switch (method)
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
