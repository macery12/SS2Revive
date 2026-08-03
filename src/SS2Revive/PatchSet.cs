using System;
using System.Collections.Generic;
using System.Reflection;
using Analytics_;
using Data;
using HarmonyLib;
using Services;
using WellFired.Promise;

namespace SS2Revive
{
    /// <summary>
    /// Patches are applied manually rather than by attribute so that a signature we inferred from
    /// decompiled source can fail on its own without aborting the whole plugin. Every attempt is
    /// recorded and dumped to the log, which doubles as our verification that the targets exist.
    /// </summary>
    internal static class PatchSet
    {
        private static readonly List<string> Report = new List<string>();

        internal static void ApplyAll(Harmony harmony)
        {
            // First, because it is the only patch the game has to survive before any of the others
            // can matter. Everything below runs inside a Shell that will not exist without it.
            if (Plugin.BypassConnectionCheck.Value)
                ApplyConnectionCheckBypass(harmony);

            if (Plugin.BypassAuth.Value)
                ApplyAuthBypass(harmony);

            if (Plugin.BypassVersionGate.Value)
                ApplyVersionGateBypass(harmony);

            if (Plugin.DisableVoip.Value)
                ApplyVoipDisable(harmony);

            if (Plugin.NewsFeedEnabled.Value)
                ApplyNewsFeed(harmony);

            if (Plugin.StubDeadBackends.Value)
                ApplyDeadBackendStubs(harmony);

            if (Plugin.LocalParty.Value)
            {
                PartyBackend.Initialise();
                ApplyPartyBackend(harmony);
            }

            if (Plugin.SteamTransport.Value)
            {
                SS2Revive.SteamTransport.Initialise();
                ApplySteamTransport(harmony);
            }

            // The transition screen is patched for two unrelated reasons - matchmaking that can
            // never arrive, and a creator-mode party that can never be joined - so it goes in
            // whenever either of those is being handled. The prefix checks both settings itself.
            if (Plugin.SkipMatchmaking.Value || Plugin.CreationMode.Value)
                ApplyTransitionScreenFixes(harmony);

            if (Plugin.SkipMatchmaking.Value)
                ApplyMatchmakingSkip(harmony);

            // Before the HTTP layer below, because the level library decides whether Creation Mode
            // has anywhere to save to at all - and if it does, none of its traffic should ever
            // reach the request scheduler.
            if (Plugin.CreationMode.Value)
            {
                UgcBackend.Initialise(Plugin.SaveDirectory.Value);
                if (UgcBackend.Available)
                    UgcPatches.Apply(harmony);
                else
                    Report.Add("FAIL Creation Mode -> the level library could not be opened");
            }

            // Start the backend before patching: whether anything is going to answer decides what
            // the HTTP prefix below does with each request. In Local mode this reads the game's
            // Inventory.dat and opens the save file, both of which want to have happened before
            // the first request rather than during it.
            BackendClient.Initialise(Plugin.Backend.Value);

            if (BackendClient.Available)
                ApplyProgressionMirror(harmony);

            // Applied last: the party backend replaces PartyApi above this layer, so party traffic
            // never reaches the HTTP service and is unaffected by either behaviour here.
            if (Plugin.HttpFailFast.Value || BackendClient.Available)
                ApplyHttpInterception(harmony);
        }

        internal static void LogReport()
        {
            Plugin.Log.LogInfo("---- patch report ----");
            foreach (var line in Report)
                Plugin.Log.LogInfo("  " + line);
            Plugin.Log.LogInfo("----------------------");
        }

        internal static void Try(string label, Action action)
        {
            try
            {
                action();
                Report.Add("OK   " + label);
            }
            catch (Exception ex)
            {
                Report.Add("FAIL " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static MethodInfo Method(Type type, string name, Type[] args = null)
        {
            var m = args == null
                ? AccessTools.Method(type, name)
                : AccessTools.Method(type, name, args);
            if (m == null)
                throw new MissingMethodException(type.FullName + "." + name);
            return m;
        }

        // ---------------------------------------------------- boot connection check

        private static MethodInfo _passConnectionCheck;

        /// <summary>
        /// Lets the game boot without Bossa's health check answering.
        ///
        /// This is the one patch whose absence hides every other one. <c>Bootstrap</c> gates the
        /// whole game behind a single GET, and <c>PassConnectionCheck</c> - reached only when that
        /// GET succeeds - is the sole caller of <c>InitialiseShell</c>. Build 1.3.1 pointed the
        /// check at <c>https://www.example.com</c>, which always answered, so the gate was
        /// invisible. Build 1.3.7 repointed it at <c>https://ss2.bsprd.uk/auth/healthcheck</c>,
        /// which no longer resolves.
        ///
        /// A failed check therefore means no Shell, which means <c>Shell.OnStart</c> never runs,
        /// which means <c>PlatformService.InitPlatforms</c> never calls <c>SteamAPI.Init</c>. The
        /// visible symptom is a modal about needing an internet connection; the second, quieter one
        /// is that Steamworks reports itself uninitialised for the rest of the session, because
        /// nothing ever asked it to initialise.
        ///
        /// It goes through UnityWebRequest rather than CrappyHttpsRequestService, so none of the
        /// HTTP interception further down this file can see it.
        /// </summary>
        private static void ApplyConnectionCheckBypass(Harmony harmony)
        {
            Try("Bootstrap.BootGame -> pass the connection check", () =>
            {
                var type = AccessTools.TypeByName("GameStateMachine.Bootstrap");
                if (type == null) throw new TypeLoadException("GameStateMachine.Bootstrap");

                _passConnectionCheck = Method(type, "PassConnectionCheck");
                harmony.Patch(Method(type, "BootGame"),
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(BootGame_Prefix))));
            });

            // The same URL again, on a 35 second timer, once the game is running. Left alone it
            // drops the same modal over whatever you are doing for the rest of the session.
            Try("ConnectionService.Update -> no-op", () =>
            {
                harmony.Patch(Method(typeof(ConnectionService), "Update"),
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SkipOriginal))));
            });
        }

        /// <summary>
        /// Take the branch the original takes when the host answers. <c>PassConnectionCheck</c>
        /// guards on the same static flag the retry path relies on, so calling it directly is
        /// exactly what a successful check would have done, minus the round trip.
        /// </summary>
        private static bool BootGame_Prefix(object __instance)
        {
            try
            {
                _passConnectionCheck.Invoke(__instance, null);
                return false;
            }
            catch (Exception ex)
            {
                // Better a modal the player can see than a game that never boots and says nothing.
                Plugin.Log.LogError("Connection check bypass threw, letting the original run: " + ex);
                return true;
            }
        }

        // ---------------------------------------------------------------- auth

        private static void ApplyAuthBypass(Harmony harmony)
        {
            Try("AuthenticationService.AuthenticateUser -> local Steam identity", () =>
            {
                var target = Method(typeof(AuthenticationService), "AuthenticateUser",
                    new[] { typeof(PlatformLoggedInUser) });
                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(AuthenticateUser_Prefix)));
                harmony.Patch(target, prefix);
            });

            Try("AuthenticationService.RefreshAuth -> no-op", () =>
            {
                var target = Method(typeof(AuthenticationService), "RefreshAuth",
                    new[] { typeof(PlatformLoggedInUser) });
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SkipOriginal))));
            });
        }

        /// <summary>
        /// Replaces the HTTP round trip with the terminal half of the original success branch:
        /// mint a PlayerId, fire the same two events in the same order, flip IsAuthenticated,
        /// and notify the platform service. The progression remote-config call is intentionally
        /// skipped - that server is gone too, and the original already tolerates completing
        /// without it.
        ///
        /// The completion is deferred a frame. Bossa's version answered over HTTP, so no caller
        /// was ever reached inside AuthenticateUser's own stack; Shell.OnStart in particular runs
        /// InitPlatforms() (which triggers this) one line before it initialises analytics. See
        /// <see cref="Dispatcher"/>.
        /// </summary>
        private static bool AuthenticateUser_Prefix(AuthenticationService __instance,
                                                    PlatformLoggedInUser platformLoggedInUser)
        {
            var playerId = SteamIdentity.GetLocalPlayerId();

            Plugin.Log.LogInfo("Authenticating locally as " + playerId + " (steam "
                               + SteamIdentity.GetSteamId64() + ", '"
                               + SteamIdentity.GetPersonaName() + "')");

            Dispatcher.NextFrame(() => CompleteAuthentication(__instance, platformLoggedInUser, playerId));

            return false; // never call the original - it posts to a dead host
        }

        private static void CompleteAuthentication(AuthenticationService instance,
                                                   PlatformLoggedInUser user,
                                                   PlayerId playerId)
        {
            // Bossa's JWT gated their own services, and nothing left in the process reads it. It
            // stays a fixed non-empty string because the field is non-null on the original success
            // path and several callers only check that much - LocalPlayerService.
            // AssignAuthenticationToken stores it and the HTTP layer would put it in a 'Security'
            // header, but there is no longer anywhere for that header to go.
            const string token = "ss2revive-local";

            // Each step is guarded separately. A subscriber that dies on a dead backend must not
            // leave the session half-authenticated - IsAuthenticated still has to flip, and the
            // later steps still have to run.
            Step("OnProcessUserEvent", () => InvokeEvent(instance, "OnProcessUserEvent",
                user.platformId, user.platformUserId, playerId, token));

            Step("IsAuthenticated = true", () => SetIsAuthenticated(instance, true));

            Step("OnAuthenticateUserComplete", () => InvokeEvent(instance, "OnAuthenticateUserComplete",
                user.platformId, user.platformUserId, playerId, token));

            Step("OnPlayerBossaAuthenticated", () =>
            {
                var platformService = AccessTools.Field(typeof(AuthenticationService), "_platformService")
                    ?.GetValue(instance) as PlatformService;
                platformService?.OnPlayerBossaAuthenticated(playerId);
            });
        }

        private static void Step(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Authentication step '" + label + "' threw: " + ex);
            }
        }

        private static void SetIsAuthenticated(AuthenticationService instance, bool value)
        {
            var prop = AccessTools.Property(typeof(AuthenticationService), "IsAuthenticated");
            var setter = prop?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(instance, new object[] { value });
                return;
            }

            var backing = AccessTools.Field(typeof(AuthenticationService), "<IsAuthenticated>k__BackingField");
            if (backing == null)
                throw new MissingFieldException("AuthenticationService.IsAuthenticated backing field");
            backing.SetValue(instance, value);
        }

        /// <summary>
        /// Events are private delegate fields; reach the backing field and invoke it.
        ///
        /// Subscribers are invoked one at a time rather than through the multicast delegate.
        /// DynamicInvoke on the whole chain stops at the first subscriber that throws and silently
        /// drops the rest, which on this codebase means one dead-backend call can strand the whole
        /// startup sequence. Here a thrown subscriber is logged and the chain continues.
        /// </summary>
        private static void InvokeEvent(object instance, string eventName, params object[] args)
        {
            var field = AccessTools.Field(instance.GetType(), eventName);
            if (field == null)
            {
                Plugin.Log.LogWarning("Event field not found: " + eventName);
                return;
            }

            if (!(field.GetValue(instance) is Delegate handler))
            {
                Plugin.Log.LogWarning("No subscribers on " + eventName + "; skipping.");
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber.DynamicInvoke(args);
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    Plugin.Log.LogError(eventName + " subscriber '" + Describe(subscriber)
                                        + "' threw: " + inner);
                }
            }
        }

        private static string Describe(Delegate subscriber)
        {
            var method = subscriber.Method;
            return method.DeclaringType?.Name + "." + method.Name;
        }

        // ------------------------------------------------- version / maintenance

        private static void ApplyVersionGateBypass(Harmony harmony)
        {
            var svc = typeof(VersionControlAndMaintenanceModeService);

            Try("VersionControlAndMaintenanceModeService.PerformMaintenanceAndVersionChecks -> no-op", () =>
            {
                var target = Method(svc, "PerformMaintenanceAndVersionChecks", new[] { typeof(PlayerId) });
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SkipOriginal))));
            });

            // StartupState polls these every frame and will not advance until they read correctly.
            Try("VersionControlAndMaintenanceModeService.ValidGameVersion -> true", () =>
            {
                var getter = AccessTools.PropertyGetter(svc, "ValidGameVersion");
                if (getter == null) throw new MissingMethodException("ValidGameVersion getter");
                harmony.Patch(getter, null,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(ForceTrue))));
            });

            Try("VersionControlAndMaintenanceModeService.GameIsInMaintenance -> false", () =>
            {
                var getter = AccessTools.PropertyGetter(svc, "GameIsInMaintenance");
                if (getter == null) throw new MissingMethodException("GameIsInMaintenance getter");
                harmony.Patch(getter, null,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(ForceFalse))));
            });
        }

        // ----------------------------------------------------------- news feed

        private static void ApplyNewsFeed(Harmony harmony)
        {
            if (NewsFeed.Initialise(Plugin.NewsFeedUrl.Value) == null)
                return;

            // NetworkConfiguration is a struct returned by value, so the postfix takes __result by
            // ref and rewrites the one field. Patching here rather than at either call site covers
            // both the feed JSON and the tile images, which read the same field separately.
            Try("ServerEnvironmentService.GetNetworkConfiguration -> local newsFeedUrl", () =>
            {
                var target = Method(typeof(ServerEnvironmentService), "GetNetworkConfiguration");
                harmony.Patch(target, null,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(RewriteNewsFeedUrl))));
            });
        }

        private static void RewriteNewsFeedUrl(ref Services.Network.NetworkConfiguration __result)
        {
            __result.newsFeedUrl = NewsFeed.BaseUrl;
        }

        // -------------------------------------------------- progression mirror

        /// <summary>
        /// Keeps the backend's idea of the player's XP equal to the client's own.
        ///
        /// This closes a hole that could not be closed from behind an HTTP boundary. The client
        /// keeps its own <c>ProgressionData</c> file, and <c>RequestProgressionData</c> overwrites
        /// that file with whatever the progression endpoint answers:
        ///
        ///     GetPlayerLevelExperience(..., response =&gt; {
        ///         PopulateProgressionData(response, out var progressionData);
        ///         PersistProgressionData(playerId, progressionData);   // :141
        ///
        /// So a backend whose total had drifted low would silently roll the player's level back on
        /// the next read. And drift is unavoidable from the outside: <c>CompleteQuickPlayLevel</c>
        /// reports "a level finished, won or not" and nothing else - not the XP, not whether the
        /// level was rated, not campaign A++ rewards.
        ///
        /// In-process there is a better answer. Watch the client persist its own figure and copy
        /// it. The two can then never disagree, which is also what makes earning cosmetics along
        /// the reward track safe to turn on for the first time.
        /// </summary>
        private static void ApplyProgressionMirror(Harmony harmony)
        {
            Try("PlayerProgressionService.PersistProgressionData -> mirror into the backend", () =>
            {
                var target = Method(typeof(PlayerProgressionService), "PersistProgressionData",
                    new[] { typeof(PlayerId), typeof(PlayerProgressionService.ProgressionData) });
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(PersistProgressionData_Postfix))));
            });
        }

        private static void PersistProgressionData_Postfix(
            PlayerId playerId, PlayerProgressionService.ProgressionData progressionData)
        {
            try
            {
                LocalBackendHost.MirrorProgression(
                    playerId.ToString(),
                    progressionData.SeasonXp,
                    progressionData.Level.SeasonLevel,
                    progressionData.Level.GlobalLevel);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Progression mirror threw: " + ex);
            }
        }

        // ---------------------------------------------------------------- voip

        private static void ApplyVoipDisable(Harmony harmony)
        {
            // Vivox is a paid third-party service and Bossa's tenant is gone. Login() is the
            // first call that touches the network, so cutting it there leaves the local SDK
            // objects constructed and every caller's null checks satisfied.
            Try("VivoxPlatform.Login -> no-op", () =>
            {
                var type = AccessTools.TypeByName("Audio.Voip.VivoxPlatform");
                if (type == null) throw new TypeLoadException("Audio.Voip.VivoxPlatform");
                var target = Method(type, "Login");
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SkipOriginal))));
            });
        }

        // -------------------------------------------------- dead backend stubs

        private static void ApplyDeadBackendStubs(Harmony harmony)
        {
            // POSTs to /telemetry/players/{id}/add on Bossa's host. The host is gone, so the
            // request can only fail - but before it can even fail it dereferences the telemetry
            // service, which is initialised one line *after* the call that triggers auth. Nothing
            // reads the result, so cut it at the wrapper.
            Try("AnalyticsService.RegisterTelemetryOnServer -> no-op", () =>
            {
                var target = Method(typeof(AnalyticsService), "RegisterTelemetryOnServer",
                    new[] { typeof(PlayerId) });
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SkipOriginal))));
            });

            // GET /profile/players/{id} against a host that no longer answers. This one is load
            // bearing, not cosmetic: TutorialGameService only sets TutorialDataLoaded inside the
            // .Then() of this promise, and StartupState blocks forever on that flag when there is
            // no local save file to read instead. Serve a local profile so the promise resolves.
            Try("ProfileService.GetProfile -> local profile", () =>
            {
                var target = Method(typeof(ProfileService), "GetProfile",
                    new[] { typeof(PlayerId), typeof(bool) });
                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(GetProfile_Prefix)));
                harmony.Patch(target, prefix);
            });
        }

        /// <summary>
        /// Serves profiles out of the service's own cache, synthesising one on first request.
        /// A fresh synthesised profile has all-default json data, which is exactly what a new
        /// account looked like on Bossa's side - so the onboarding tutorial is offered, as it
        /// should be for a profile with no history.
        /// </summary>
        private static bool GetProfile_Prefix(ProfileService __instance,
                                              PlayerId targetPlayerId,
                                              ref IPromise<Profile> __result)
        {
            var cache = AccessTools.Field(typeof(ProfileService), "_cacheInfo")
                ?.GetValue(__instance) as Dictionary<PlayerId, Profile>;

            Profile profile = null;
            if (cache != null && cache.TryGetValue(targetPlayerId, out var cached))
            {
                profile = cached;
            }

            if (profile == null)
            {
                // The profile has to describe the player being asked about, not us. Answering with
                // the local identity for every id makes a party of four read as four copies of
                // yourself - which is exactly how it looked before this decoded the target.
                var steamId = SteamIdentity.TryGetSteamId(targetPlayerId);
                var name = SteamIdentity.GetPersonaName(steamId);
                var resolved = !string.IsNullOrEmpty(name);

                profile = new Profile
                {
                    PlayerId = targetPlayerId,
                    PlayerName = resolved ? name : "Surgeon",
                    PlatformName = "Steam",
                    PlatformUserId = steamId == 0UL ? string.Empty : steamId.ToString(),
                    Json = string.Empty,
                };

                // Only cache a name Steam actually gave us. Caching the placeholder would make a
                // player who was merely not downloaded yet stay "Surgeon" for the whole session.
                if (cache != null && resolved)
                    cache[targetPlayerId] = profile;
            }

            var completed = AccessTools.Field(typeof(ProfileService), "OnGetProfileComplete")
                ?.GetValue(__instance) as Delegate;
            if (completed != null)
            {
                foreach (var subscriber in completed.GetInvocationList())
                {
                    try
                    {
                        subscriber.DynamicInvoke(targetPlayerId, profile);
                    }
                    catch (Exception ex)
                    {
                        var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                        Plugin.Log.LogError("OnGetProfileComplete subscriber '"
                                            + Describe(subscriber) + "' threw: " + inner);
                    }
                }
            }

            __result = Promise<Profile>.Resolved(profile);
            return false;
        }

        // ------------------------------------------------------------ http

        private static int _failFastRequestId = 0x4646_0000;

        /// <summary>
        /// Interlocked because the game issues requests from more than one thread, and the request
        /// scheduler matches responses to requests by id - so two callers handed the same id means
        /// one completion delivered twice and one never delivered at all.
        ///
        /// The three id sources in this plugin start from distinct bases (0x4646, 0x4242, 0x5353)
        /// so that they cannot collide with each other either.
        /// </summary>
        private static int NextFailFastId() =>
            System.Threading.Interlocked.Increment(ref _failFastRequestId);

        /// <summary>
        /// The status code we answer with is load bearing, not decorative.
        /// HttpRequestScheduler.GetResultType maps 2xx to Success, 4xx to Failed, and everything
        /// else - 5xx, and the -1 the real client uses for timeouts - to Retry. A retried request
        /// stays in _queuedRequests, never reaches _onHttpRequestComplete, and holds the
        /// ServerResource locks it declared, which blocks every later request that reads or writes
        /// the same resource. Most call sites leave maxRetries unset, so that is forever, on a
        /// backoff that saturates at 512 seconds.
        ///
        /// 404 is the only honest answer anyway: the endpoint really is not there.
        /// </summary>
        private const int DeadEndpointStatus = 404;

        /// <summary>
        /// One prefix over every <c>Request</c> overload. <see cref="BackendRoutes"/> sorts each
        /// request into one of three outcomes.
        ///
        /// Endpoints the backend implements are rebuilt and answered in process by
        /// <see cref="BackendClient"/>. A couple that are known-dead but expensive to refuse are
        /// answered here with a canned 200. Everything else is failed immediately, without opening
        /// a socket: those endpoints resolve to hostnames that no longer exist, so each call would
        /// otherwise pay a full DNS timeout before failing - during startup, serially. Failing
        /// immediately produces the same outcome the callers already handle, just without the
        /// stall and without burying the log in NameResolutionFailure traces.
        ///
        /// Once this is applied the original never runs, including for requests the backend
        /// declines to rebuild. That is deliberate: falling through to the original would
        /// reintroduce exactly the timeout this exists to remove.
        /// </summary>
        private static void ApplyHttpInterception(Harmony harmony)
        {
            var label = BackendClient.Available
                ? "CrappyHttpsRequestService.Request -> " + BackendClient.BaseUrl + " backend"
                : "CrappyHttpsRequestService.Request -> fail fast";

            Try(label, () =>
            {
                var type = AccessTools.TypeByName("Services.CrappyHttpsRequestService");
                if (type == null) throw new TypeLoadException("Services.CrappyHttpsRequestService");

                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(HttpRequest_Prefix)));

                var patched = 0;
                foreach (var method in type.GetMethods(AccessTools.all))
                {
                    if (method.Name != "Request" || method.ReturnType != typeof(int))
                        continue;

                    harmony.Patch(method, prefix);
                    patched++;
                }

                if (patched == 0)
                    throw new MissingMethodException("No Request overloads found");

                Plugin.Log.LogInfo("HTTP interception applied to " + patched + " Request overloads.");
            });

            // MultipartFormRequest is a separate method name, so the loop above never saw it - and
            // it is the one the UGC upload used. Left alone it reached a host that no longer
            // resolves, and CrappyHttpsRequestService.Update reads task.Result with no try/catch,
            // so the WebException surfaced there instead of at either callback. Nothing completed
            // and nothing failed, which is how saving a new level ended at a black screen.
            //
            // Uploads are answered above this layer now, by UgcBackend. Anything still arriving
            // here is genuinely dead, so it gets the same immediate 404 as the rest.
            Try("CrappyHttpsRequestService.MultipartFormRequest -> fail fast", () =>
            {
                var type = AccessTools.TypeByName("Services.CrappyHttpsRequestService");
                if (type == null) throw new TypeLoadException("Services.CrappyHttpsRequestService");

                var target = Method(type, "MultipartFormRequest");
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(MultipartRequest_Prefix))));
            });
        }

        private static bool MultipartRequest_Prefix(CompleteDelegate onComplete, ref int __result)
        {
            var requestId = NextFailFastId();
            __result = requestId;

            if (onComplete != null)
            {
                Dispatcher.NextFrame(() =>
                {
                    var empty = new NativeByteBuffer(1);
                    try
                    {
                        onComplete(requestId, false, DeadEndpointStatus, empty);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Multipart fail-fast callback threw: " + ex);
                    }
                    finally
                    {
                        empty.Dispose();
                    }
                });
            }

            return false;
        }

        private static bool HttpRequest_Prefix(MethodBase __originalMethod,
                                               object[] __args,
                                               ref int __result)
        {
            // Guarded, unlike a normal call into BackendClient, because this prefix sits over the
            // game's entire HTTP surface: an exception escaping here is not one failed request, it
            // is an exception thrown out of whatever game code happened to be issuing it. Falling
            // through to fail-fast is always a safe answer, so there is nothing to gain by letting
            // it propagate.
            try
            {
                int handledId;
                if (BackendClient.TrySend(__originalMethod, __args, out handledId))
                {
                    __result = handledId;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Backend routing threw for " + __originalMethod.Name
                                    + "; failing the request instead. " + ex);
            }

            var requestId = NextFailFastId();
            __result = requestId;

            // The completion delegate is always the final argument across every overload.
            var onComplete = __args.Length > 0
                ? __args[__args.Length - 1] as CompleteDelegate
                : null;

            if (onComplete != null)
            {
                Dispatcher.NextFrame(() =>
                {
                    var empty = new NativeByteBuffer(1);
                    try
                    {
                        onComplete(requestId, false, DeadEndpointStatus, empty);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("HTTP fail-fast callback threw: " + ex);
                    }
                    finally
                    {
                        empty.Dispose();
                    }
                });
            }

            return false;
        }

        // ------------------------------------------------------ transition screen

        private static void ApplyTransitionScreenFixes(Harmony harmony)
        {
            Try("LevelTransitionScreen.Show -> no matchmaking wait, no creator-party wait", () =>
            {
                var target = Method(typeof(LevelTransitionScreen), "Show", new[]
                {
                    typeof(List<LevelTransitionScreen.VactubePlayerInfo>),
                    typeof(int),
                    typeof(LevelQueueService.EMode),
                    typeof(bool),
                });
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(TransitionShow_Prefix))));
            });
        }

        /// <summary>
        /// Two independent problems, both fixed by rewriting an argument on the way in.
        ///
        /// <paramref name="targetPlayerCount"/> - both entry points into the vactube screen
        /// (PlayersEnterTransitionCommand when a level starts, CheckRematchmakingCommand when one
        /// ends) ask it to hold for four players whenever matchmaking is wanted. Show only arms its
        /// six-second auto-load when the target is already met; otherwise it shows a 60-second
        /// countdown and waits on players that Bossa's matchmaker was supposed to send. That
        /// matchmaker is gone, so the countdown always expires, and the expiry branch keys off
        /// MatchmakingService's state - which never leaves Idle here, so neither timer gets rearmed
        /// and the screen sits there for good. Clamping the target to the players actually standing
        /// in the tubes takes the first branch instead: no countdown, no waiting-for-player tubes,
        /// auto-load after six seconds.
        ///
        /// <paramref name="waitingOnCreationPartyChange"/> - Creation Mode. Bossa's party server
        /// let you join a party *by id*, creating it if it did not exist, so the level editor put
        /// everyone editing a level into one party keyed by the level's own server id:
        ///
        ///     partyService.CreateOrJoinParty(playerId, new Guid(nextLevelSummaryOpt.serverLevelId));
        ///
        /// and the transition screen then refuses to load until the current party id equals that
        /// same level id. Our party ids are Steam lobby handles, so that comparison can never come
        /// out true and the editor never opens - Update just logs "Party has not been joined yet,
        /// wait another second" and pushes the auto-load a second further away, for ever.
        ///
        /// It also crashes on the way there. That branch reads <c>nextLevelSummaryOpt.serverLevelId</c>
        /// with no null check, from a <c>GetInfo</c> out-parameter the game itself marks
        /// <c>[CanBeNull]</c> - so a queue that has moved on by the time the timer expires throws
        /// NullReferenceException out of Update, every frame.
        ///
        /// Clearing the flag drops the whole branch, and loses nothing: there is no shared level to
        /// co-edit any more, because the level library is local. Keeping the party we already have
        /// is also better than what the original did, which was to leave your friends' party to
        /// join a level party none of them can see.
        /// </summary>
        private static void TransitionShow_Prefix(
            List<LevelTransitionScreen.VactubePlayerInfo> vactubePlayerInfo,
            ref int targetPlayerCount,
            ref bool waitingOnCreationPartyChange)
        {
            if (Plugin.CreationMode.Value && waitingOnCreationPartyChange)
            {
                Plugin.Log.LogInfo("Transition screen wanted to wait for a party named after the "
                                   + "level being edited; there is no such party here, so loading "
                                   + "the editor now.");
                waitingOnCreationPartyChange = false;
            }

            if (!Plugin.SkipMatchmaking.Value)
                return;

            var present = vactubePlayerInfo == null ? 0 : vactubePlayerInfo.Count;

            // present == 0 would make the screen think its target is met with nobody in it; leave
            // that case to the original, which has its own handling for an empty group.
            if (present <= 0 || targetPlayerCount <= present)
                return;

            Plugin.Log.LogInfo("Transition screen wanted " + targetPlayerCount + " players but "
                               + present + " are here; starting now instead of waiting on "
                               + "matchmaking.");
            targetPlayerCount = present;
        }

        // ----------------------------------------------------------- matchmaking

        private static void ApplyMatchmakingSkip(Harmony harmony)
        {
            Try("LevelQueueService.ReplayCurrentLevel -> requeue campaign level", () =>
            {
                var target = Method(typeof(LevelQueueService), "ReplayCurrentLevel");
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(ReplayCurrentLevel_Postfix))));
            });

            // Nothing downstream reads the result, and refusing here keeps the request out of the
            // scheduler entirely rather than letting it fail and be re-queued.
            foreach (var name in new[] { "StartMatchmaking", "StartRematchmaking" })
            {
                var methodName = name;
                Try("MatchmakingService." + methodName + " -> refused", () =>
                {
                    var target = Method(typeof(MatchmakingService), methodName,
                        new[] { typeof(PlayerId), typeof(int), typeof(string) });
                    harmony.Patch(target, new HarmonyMethod(
                        AccessTools.Method(typeof(PatchSet), nameof(RefuseMatchmaking_Prefix))));
                });
            }
        }

        private static bool RefuseMatchmaking_Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }

        /// <summary>
        /// LevelQueueService_CampaignMode.ReplayLevel is an empty method in this build, so
        /// ReplayCurrentLevel - which is what the "press [Interact] to retry" modal calls after a
        /// failed level - leaves the campaign queue holding the level that just finished. The very
        /// next thing to run is LevelResultService.OnFinishedLevelResults, whose
        /// RemoveCurrentLevelFromQueue then matches that level against the one just played, clears
        /// it, and drops CurrentMode to None. ReadyToGenerateQueue reads None and bails to the
        /// lobby, so nothing reloads.
        ///
        /// Bossa worked around the same hole in SkipAndRestartLevelService.StartRestartingLevel by
        /// special-casing campaign to CampaignService.ReplayCampaignLevel. Do the same here. That
        /// queues the level again under a fresh LevelInfo id, which is also what stops
        /// RemoveCurrentLevelFromQueue from recognising it.
        /// </summary>
        private static void ReplayCurrentLevel_Postfix(LevelQueueService __instance)
        {
            try
            {
                if (__instance.CurrentQueueMode != LevelQueueService.EMode.Campaign)
                    return;

                // Queue ownership is the host's. On a client this would only push a
                // CampaignModeLevel message at the host and desync its sequence number.
                if (!Shell.Instance.GetNetworkService().IsHosting())
                    return;

                Plugin.Log.LogInfo("Campaign replay requested; requeueing the level, because "
                                   + "LevelQueueService_CampaignMode.ReplayLevel does nothing.");
                Shell.Instance.GetCampaignService().ReplayCampaignLevel();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Campaign replay requeue threw: " + ex);
            }
        }

        // ---------------------------------------------------------------- party

        /// <summary>
        /// Captured from PartyApi so <see cref="PartyBackend"/> can complete requests through the
        /// exact delegate the request scheduler installed on it.
        /// </summary>
        private static CompleteDelegate _partyCompletion;

        internal static CompleteDelegate GetPartyCompletionDelegate() => _partyCompletion;

        private static void ApplyPartyBackend(Harmony harmony)
        {
            Try("PartyApi.SerializeAndSendHttpRequest -> Steam lobby", () =>
            {
                var target = Method(typeof(PartyApi), "SerializeAndSendHttpRequest",
                    new[] { typeof(PlayerId), typeof(PartyApi.HttpRequest) });
                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(PartyRequest_Prefix)));
                harmony.Patch(target, prefix);
            });

            // Pure diagnostics. When an invite does nothing, the whole question is which of the
            // three possibilities happened: the callback never arrived (Steam did not deliver it,
            // usually because the game was not launched by Steam), it arrived empty, or it arrived
            // and was refused because the player is not sitting in the game's lobby. The original
            // reports the last two through UI popups only, which is no help in a log.
            Try("SteamPlatform.PlatformTriggerJoiningGame -> log", () =>
            {
                var type = AccessTools.TypeByName("Services.SteamPlatform");
                if (type == null) throw new TypeLoadException("Services.SteamPlatform");
                var target = Method(type, "PlatformTriggerJoiningGame",
                    new[] { typeof(Steamworks.GameRichPresenceJoinRequested_t) });
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(JoiningGame_Prefix))));
            });

            // Watch the decision to join, not the request that eventually results from it. Between
            // the two, PartyService passes through LeavingParty and sends RemoveMember(self) - so
            // there is a window where the player is deliberately in no party. Only this call knows
            // that window is on purpose; by the time the join request arrives it is too late to
            // stop a lobby having been created in the meantime.
            Try("PartyService.CreateOrJoinParty -> note join intent, refuse ids we cannot join", () =>
            {
                var target = Method(typeof(PartyService), "CreateOrJoinParty",
                    new[] { typeof(PlayerId), typeof(Bossa.Framework.Utils.Guid) });
                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(CreateOrJoinParty_Prefix)));
                harmony.Patch(target, prefix);
            });

            // The party UI resolves a member's Steam account through the profile service before it
            // can send a Steam invite. Our player ids already carry the Steam id, so decode it
            // rather than asking a dead profile server.
            Try("ProfileService.GetPlayerPlatformUserIdAsync -> decode from PlayerId", () =>
            {
                var target = Method(typeof(ProfileService), "GetPlayerPlatformUserIdAsync",
                    new[] { typeof(PlayerId), typeof(PlayerId), typeof(Action<string>) });
                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(PatchSet), nameof(PlatformUserId_Prefix)));
                harmony.Patch(target, prefix);
            });
        }

        private static bool PartyRequest_Prefix(PartyApi __instance,
                                                PlayerId playerId,
                                                PartyApi.HttpRequest request,
                                                ref int __result)
        {
            if (_partyCompletion == null)
            {
                _partyCompletion = AccessTools.Field(typeof(PartyApi), "_onHttpRequestComplete")
                    ?.GetValue(__instance) as CompleteDelegate;

                if (_partyCompletion == null)
                {
                    Plugin.Log.LogError("PartyApi completion delegate missing; falling through to HTTP.");
                    return true;
                }
            }

            __result = PartyBackend.Handle(request, playerId);
            return false;
        }

        private static void JoiningGame_Prefix(Steamworks.GameRichPresenceJoinRequested_t joiningGame)
        {
            var connect = joiningGame.m_rgchConnect;
            Plugin.Log.LogInfo("Steam rich-presence join requested from "
                               + joiningGame.m_steamIDFriend.m_SteamID
                               + " with connect string '" + (connect ?? "<null>") + "'");

            if (string.IsNullOrEmpty(connect))
            {
                Plugin.Log.LogWarning("Connect string is empty; the host is not advertising a party.");
                return;
            }

            // The original silently refuses here and shows a popup - worth naming in the log,
            // because from the player's side it is indistinguishable from nothing happening.
            try
            {
                if (!Shell.Instance.GetLevelService().IsInLobby())
                    Plugin.Log.LogWarning("Join refused: you must be in the hub, not in a level.");
            }
            catch
            {
                // Diagnostics only - never let this interfere with the join itself.
            }
        }

        /// <summary>
        /// For a party id we can act on this observes only - the original still runs, because the
        /// entire leave-then-join sequence is Bossa's and works correctly once we stop competing
        /// with it for the lobby.
        ///
        /// An id that is not one of ours is refused outright instead. There are three callers, and
        /// two of them (the launch command line, and an accepted Steam invite) can only ever pass a
        /// lobby handle we minted. The third is the level editor, which asks to join a party named
        /// after the level being edited - a party that exists nowhere, because our party is a Steam
        /// lobby and Bossa's join-or-create-by-id is what made that trick work.
        ///
        /// Left to run, the original would leave the lobby the player is actually in, fail the
        /// join, and build a fresh party of one. Refusing keeps the party they have - including the
        /// friends in it, who are exactly who they would want to playtest with.
        /// </summary>
        private static bool CreateOrJoinParty_Prefix(Bossa.Framework.Utils.Guid partyId)
        {
            if (!PartyBackend.TryDecodeLobby(partyId, out _))
            {
                Plugin.Log.LogInfo("Ignoring a request to join party " + partyId
                                   + ": it is not a Steam lobby handle, so there is nothing to "
                                   + "join. Staying in the current party.");
                return false;
            }

            PartyBackend.NoteJoinIntent(partyId);
            return true;
        }

        private static bool PlatformUserId_Prefix(PlayerId targetPlayerId,
                                                  Action<string> onGetPlatformUserId)
        {
            var steamId = SteamIdentity.TryGetSteamId(targetPlayerId);
            onGetPlatformUserId?.Invoke(steamId == 0UL ? string.Empty : steamId.ToString());
            return false;
        }

        // ------------------------------------------------------- steam transport

        /// <summary>
        /// UdpClientManager is internal, and the enum it takes is nested inside it, so both patches
        /// work through loose arguments rather than typed parameters.
        /// </summary>
        private static void ApplySteamTransport(Harmony harmony)
        {
            var type = AccessTools.TypeByName("Services.Network.Internal.UdpClientManager");
            if (type == null)
            {
                Report.Add("FAIL Steam transport -> Services.Network.Internal.UdpClientManager not found");
                return;
            }

            // Receiving needs the live instance: packets are delivered by calling its own private
            // demultiplexer, which is what gives us its header parsing and statistics unchanged.
            Try("UdpClientManager.Initialise -> attach Steam transport", () =>
            {
                var target = Method(type, "Initialise");
                harmony.Patch(target, null,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(UdpInitialise_Postfix))));
            });

            Try("UdpClientManager.OverwriteHeaderAndSendPacket -> Steam P2P", () =>
            {
                var target = Method(type, "OverwriteHeaderAndSendPacket");
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(PatchSet), nameof(SendPacket_Prefix))));
            });
        }

        private static void UdpInitialise_Postfix(object __instance)
        {
            SS2Revive.SteamTransport.AttachClientManager(__instance);
        }

        /// <summary>
        /// The two-byte type header has to be written before the packet leaves, because the
        /// receiving end demultiplexes on it. The original writes it as part of sending; we are
        /// replacing the send, so we write it too - the values are the ones the original uses for
        /// each packet type, and the receiver rejects anything else.
        /// </summary>
        private static bool SendPacket_Prefix(object[] __args)
        {
            // Convert, do not cast: __args[0] is a boxed SendNetworkPacketType, and unboxing an
            // enum straight to its underlying type throws. The type is nested inside an internal
            // class, so naming it here is not an option either.
            var packetType = Convert.ToInt32(__args[0]);
            var recipient = __args[1] as System.Net.IPEndPoint;
            var buffer = __args[2] as byte[];
            var byteCount = Convert.ToInt32(__args[3]);

            if (buffer == null || byteCount < 2)
                return false;

            // StunPing, TurnPing, TurnPeerToPeer and NotificationPing all target Bossa's servers,
            // which are gone. Those are left on the original path to fail as they already do.
            // Only the two peer-to-peer types can be carried over Steam.
            byte a, b;
            switch (packetType)
            {
                case 4: a = 80; b = 67; break; // PeerToPeerConfiguration -> 'P','C'
                case 5: a = 80; b = 71; break; // PeerToPeerGame          -> 'P','G'
                default: return true;
            }

            if (!SS2Revive.SteamTransport.TryResolve(recipient, out _))
                return true;

            buffer[0] = a;
            buffer[1] = b;
            SS2Revive.SteamTransport.TrySend(recipient, buffer, byteCount);
            return false;
        }

        // ------------------------------------------------------------- shared

        private static bool SkipOriginal() => false;

        private static void ForceTrue(ref bool __result) => __result = true;

        private static void ForceFalse(ref bool __result) => __result = false;
    }
}
