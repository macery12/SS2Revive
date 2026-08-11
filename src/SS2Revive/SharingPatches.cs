using System;
using Data;
using HarmonyLib;
using Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SS2Revive
{
    /// <summary>
    /// Entry points that make local sharing and online publication reachable without leaving the
    /// game. Folder import remains automatic; online publication replaces the game's obsolete
    /// Publish action and opens a full-size overlay.
    ///
    /// Export takes over the Share button. The terminal normally shows Publish while a level is a
    /// draft and Share once it is published:
    /// <code>
    ///   _publishButton.gameObject.SetActive(... &amp;&amp; _isInCreateMode &amp;&amp; flag);   // flag  = Draft
    ///   _shareButton.gameObject.SetActive(flag3);                            // flag3 = Published
    /// </code>
    /// SS2Revive keeps Export available for both states and makes Publish the authenticated online
    /// action. This removes the duplicate Publish Online button while preserving the familiar
    /// modal layout and navigation.
    ///
    /// Import is scanned silently whenever Create opens. The previous cloned training-video button
    /// was intentionally removed because its large tile was the wrong interaction for publication.
    /// </summary>
    internal static class SharingPatches
    {
        private const string LogoutButtonName = "SS2ReviveCommunityLogoutButton";

        /// <summary>
        /// The Create screen currently open, so the Import button can redraw it. The button's
        /// handler is static - it is a delegate on a cloned GameObject, not a component of ours -
        /// and there is only ever one of these screens.
        /// </summary>
        private static TerminalCreateScreen _screen;
        private static TerminalLevelModalController _publishModal;
        private static ExtendedButton _accountButton;
        private static CommunityPublishOperation _accountOperation;

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("TerminalLevelModalController.OnShareBtnClicked -> export to a file", () =>
            {
                var target = PatchSet.Method(typeof(TerminalLevelModalController), "OnShareBtnClicked");
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(Share_Prefix))));
            });

            PatchSet.Try("TerminalCreateScreen.OnScreenEntered -> import folder and button", () =>
            {
                var target = PatchSet.Method(typeof(TerminalCreateScreen), "OnScreenEntered",
                    new[] { typeof(bool) });
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(CreateScreenEntered_Postfix))));
            });

            PatchSet.Try("TerminalLevelModalController.DisplayLevelData -> export drafts too", () =>
            {
                var target = PatchSet.Method(typeof(TerminalLevelModalController), "DisplayLevelData");
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(DisplayLevelData_Postfix))));
            });

            PatchSet.Try("TerminalCreateScreen.SetupNavigation -> community logout", () =>
            {
                var target = PatchSet.Method(typeof(TerminalCreateScreen), "SetupNavigation");
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(CreateNavigation_Postfix))));
            });

            PatchSet.Try("TerminalLevelModalController.OnPublishLevelClicked -> publish online", () =>
            {
                var target = PatchSet.Method(typeof(TerminalLevelModalController),
                    "OnPublishLevelClicked", new[] { typeof(ExtendedButton) });
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(Publish_Prefix))));
            });

            PatchSet.Try("TerminalUIController.DisableUI -> close community publish overlay", () =>
            {
                var target = PatchSet.Method(typeof(TerminalUIController), "DisableUI");
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(TerminalDisabled_Prefix))));
            });
        }

        /// <summary>
        /// Shows Export on saved drafts as well as published levels, then turns the stock Publish
        /// action into authenticated online publication. Neither operation depends on the retired
        /// Bossa UGC service.
        /// </summary>
        private static void DisplayLevelData_Postfix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = CurrentLevel(__instance);
                if (summary == null) return;

                ConfigureInstalledActions(__instance, summary);
                ConfigureOnlinePublishAction(__instance, summary);
                RefreshStockActionLabelsNextFrame(__instance, summary.serverLevelId);
                if (summary.LevelStatus != UGCApi.EStatus.Draft) return;

                var inCreateMode = AccessTools
                    .Field(typeof(TerminalLevelModalController), "_isInCreateMode")
                    ?.GetValue(__instance);

                if (!(inCreateMode is bool) || !(bool)inCreateMode) return;

                // A level with no server id has never been saved, so there is nothing to export.
                if (string.IsNullOrEmpty(summary.serverLevelId)) return;

                var button = AccessTools.Field(typeof(TerminalLevelModalController), "_shareButton")
                    ?.GetValue(__instance) as ExtendedButton;

                if (button != null)
                {
                    button.gameObject.SetActive(true);
                    SetLabel(button, "EXPORT");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Showing the export button on a draft threw: " + ex);
            }
        }

        private static LevelSummaryData CurrentLevel(TerminalLevelModalController modal) =>
            AccessTools.Field(typeof(TerminalLevelModalController), "_currentDisplayedLevel")
                ?.GetValue(modal) as LevelSummaryData;

        private static void RefreshStockActionLabelsNextFrame(
            TerminalLevelModalController modal, string expectedLevelId)
        {
            Dispatcher.NextFrame(() =>
            {
                var current = modal == null ? null : CurrentLevel(modal);
                if (current == null || current.serverLevelId != expectedLevelId) return;
                var share = AccessTools.Field(typeof(TerminalLevelModalController), "_shareButton")
                    ?.GetValue(modal) as ExtendedButton;
                if (share != null && share.gameObject.activeSelf) SetLabel(share, "EXPORT");
                var publish = AccessTools.Field(typeof(TerminalLevelModalController), "_publishButton")
                    ?.GetValue(modal) as ExtendedButton;
                if (publish != null && publish.gameObject.activeSelf) SetLabel(publish, "PUBLISH");
            });
        }

        private static void ConfigureInstalledActions(TerminalLevelModalController modal,
                                                      LevelSummaryData summary)
        {
            if (modal == null || summary == null) return;

            var share = AccessTools.Field(typeof(TerminalLevelModalController), "_shareButton")
                ?.GetValue(modal) as ExtendedButton;
            if (share != null && share.gameObject.activeSelf) SetLabel(share, "EXPORT");

            var imported = UgcBackend.IsImported(summary.serverLevelId);
            var remove = AccessTools.Field(typeof(TerminalLevelModalController), "_deleteButton")
                ?.GetValue(modal) as ExtendedButton;
            if (remove != null && imported)
            {
                remove.gameObject.SetActive(true);
                remove.interactable = true;
                SetLabel(remove, "REMOVE");
            }
            else if (remove != null && remove.gameObject.activeSelf)
            {
                SetLabel(remove, "DELETE");
            }

            // Reporting was a call to Bossa's dead moderation backend. An installed file can be
            // removed locally; presenting both actions would imply a report goes somewhere.
            if (imported)
            {
                var report = AccessTools.Field(typeof(TerminalLevelModalController), "_reportButton")
                    ?.GetValue(modal) as Button;
                if (report != null) report.gameObject.SetActive(false);
            }
        }

        private static void TerminalDisabled_Prefix()
        {
            CommunityPublishOverlay.HideActive();
            _accountOperation?.Cancel();
            _accountOperation = null;
        }

        private static void ConfigureOnlinePublishAction(TerminalLevelModalController modal,
                                                         LevelSummaryData summary)
        {
            var button = AccessTools.Field(typeof(TerminalLevelModalController), "_publishButton")
                ?.GetValue(modal) as ExtendedButton;
            if (button == null) return;

            var inCreateMode = AccessTools.Field(typeof(TerminalLevelModalController), "_isInCreateMode")
                ?.GetValue(modal) as bool? ?? false;
            var localPlayer = Shell.Instance.GetLocalPlayerService().GetPlayerId(0);
            var ownsLevel = summary.creatorPlayerIds != null && summary.creatorPlayerIds.Contains(localPlayer);
            var visible = Plugin.LevelSharingEnabled.Value && inCreateMode && ownsLevel
                          && !UgcBackend.IsImported(summary.serverLevelId)
                          && !string.IsNullOrEmpty(summary.serverLevelId);
            SetLabel(button, "PUBLISH");
            button.gameObject.SetActive(visible);
            button.interactable = visible;
            if (visible) _publishModal = modal;
        }

        private static bool Publish_Prefix(TerminalLevelModalController __instance)
        {
            var modal = __instance ?? _publishModal;
            var summary = modal == null ? null : CurrentLevel(modal);
            if (modal == null || summary == null)
            {
                TerminalMessage.Show("No saved level is selected for publication.", isWarning: true);
                return false;
            }
            CommunityPublishOverlay.Show(modal, summary);
            return false;
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Writes the level to a file and puts its code on the clipboard, which is what the window
        /// this replaces did with the code alone.
        ///
        /// The original is skipped rather than run afterwards. Its window is a modal whose only
        /// action is "copy the code", and leaving it in the way would mean the player dismisses a
        /// dialog to find out whether the file they came for was written.
        /// </summary>
        private static bool Share_Prefix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = CurrentLevel(__instance);

                if (summary == null)
                {
                    Plugin.Log.LogWarning("Export: the modal is not showing a level.");
                    return false;
                }

                string code, message;
                if (!LevelSharing.Export(summary.serverLevelId, out code, out message))
                {
                    TerminalMessage.Show(message, isWarning: true);
                    return false;
                }

                // The code is what gets pasted next to the file wherever it is being sent, so it is
                // the thing worth having on the clipboard - the file itself is picked out of a
                // folder, not pasted.
                try
                {
                    GUIUtility.systemCopyBuffer = code;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not put the share code on the clipboard: " + ex.Message);
                }

                OpenExportFolder();
                TerminalMessage.Show("Exported to the SS2Revive export folder and opened it. Code "
                                     + code + " copied to your clipboard.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Export threw: " + ex);
                TerminalMessage.Show("Export failed. See the log.", isWarning: true);
            }

            return false;
        }

        // ------------------------------------------------------------------ import

        private static void CreateScreenEntered_Postfix(TerminalCreateScreen __instance, bool isLocalPlayer)
        {
            if (!isLocalPlayer) return;

            _screen = __instance;
            ConfigureLogoutButton(__instance);

            try
            {
                // Silent: a screen that opens every time somebody walks up to the terminal must not
                // report "nothing to do" every time. Only an actual import is worth saying.
                var result = LevelSharing.ImportAll();
                var message = LevelSharing.Describe(result, sayNothingHappened: false);

                if (message != null)
                {
                    TerminalMessage.Show(message, isWarning: result.Added == 0);
                    if (result.Added > 0) Refresh(__instance);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Scanning the import folder threw: " + ex);
            }

        }

        private static void OnImportClicked(ExtendedButton button)
        {
            try
            {
                var result = LevelSharing.ImportAll();
                TerminalMessage.Show(LevelSharing.Describe(result, sayNothingHappened: true),
                                     isWarning: result.Added == 0 && result.Rejected > 0);

                // The screen is listing a search that ran before any of this arrived, so it is
                // redrawn rather than left to look as though nothing happened. It is also the
                // refresh half of this button: pressing it after dropping a file in re-runs the
                // search whether or not there was anything new to take in.
                Refresh(_screen);

                // Only when the folder was empty, and only on a deliberate press. Somebody who has
                // just been told to put files somewhere needs to be shown where that is; somebody
                // who already has files there does not want their game minimised.
                if (result.Seen == 0) OpenImportFolder();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Import threw: " + ex);
                TerminalMessage.Show("Import failed. See the log.", isWarning: true);
            }
        }

        /// <summary>
        /// Re-runs the screen's own search, which is what redraws the level grid. Imported levels
        /// belong to whoever built them, so somebody else's level appears under Discover rather than
        /// here - but a copy of one of your own does show up, and either way a stale list after a
        /// button press reads as a button that did nothing.
        /// </summary>
        private static void Refresh(TerminalCreateScreen screen)
        {
            if (screen == null) return;

            try
            {
                AccessTools.Method(typeof(TerminalCreateScreen), "NewSearch")?.Invoke(screen, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not refresh the Create screen: " + ex.Message);
            }
        }

        internal static void RefreshCurrentCreateScreen()
        {
            if (_screen == null) return;
            Refresh(_screen);
            ConfigureLogoutButton(_screen);
        }

        internal static void RefreshCommunitySessionButton()
        {
            Dispatcher.NextFrame(() =>
            {
                if (_screen != null) ConfigureLogoutButton(_screen);
            });
        }

        private static void ConfigureLogoutButton(TerminalCreateScreen screen)
        {
            if (screen == null) return;
            var existing = screen.transform.Find(LogoutButtonName);
            var button = existing == null ? null : existing.GetComponent<ExtendedButton>();
            if (button == null)
            {
                var template = AccessTools.Field(typeof(TerminalCreateScreen), "_loadNextPageBtn")
                    ?.GetValue(screen) as ExtendedButton;
                if (template == null) return;
                var clone = UnityEngine.Object.Instantiate(template.gameObject, screen.transform);
                clone.name = LogoutButtonName;
                clone.transform.SetAsLastSibling();
                button = clone.GetComponent<ExtendedButton>();
                if (button == null)
                {
                    UnityEngine.Object.Destroy(clone);
                    return;
                }
                StripLocalisation(clone);
                var rect = clone.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = new Vector2(1f, 1f);
                    rect.anchorMax = new Vector2(1f, 1f);
                    rect.pivot = new Vector2(1f, 1f);
                    rect.anchoredPosition = new Vector2(-34f, -34f);
                    rect.sizeDelta = new Vector2(180f, 54f);
                    rect.localScale = Vector3.one;
                }
                button.onClick = new Button.ButtonClickedEvent();
                button.OnPointerClickButton = AccountClicked;
                button.OnPointerEnterButton = null;
                button.OnPointerExitButton = null;
            }
            _accountButton = button;
            var signedIn = CommunityPublishClient.HasStoredSession;
            SetLabel(button, signedIn ? "LOG OUT" : "LOG IN");
            var visible = CommunityPublishClient.Enabled;
            button.gameObject.SetActive(visible);
            button.interactable = visible && _accountOperation == null;
            CreateNavigation_Postfix(screen);
        }

        private static void CreateNavigation_Postfix(TerminalCreateScreen __instance)
        {
            var account = _accountButton;
            if (__instance == null || account == null || !account.gameObject.activeSelf) return;
            var training = AccessTools.Field(typeof(TerminalCreateScreen), "_trainingVideosButton")
                ?.GetValue(__instance) as ExtendedButton;
            if (training == null) return;
            var trainingNavigation = training.navigation;
            trainingNavigation.selectOnRight = account;
            training.navigation = trainingNavigation;
            account.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnLeft = training,
                selectOnUp = training,
            };
        }

        private static void AccountClicked(ExtendedButton ignored)
        {
            if (_accountOperation != null || _accountButton == null) return;
            _accountButton.interactable = false;
            if (CommunityPublishClient.HasStoredSession)
            {
                SetLabel(_accountButton, "SIGNING OUT");
                _accountOperation = CommunityPublishClient.Logout(new CommunityAccountCallbacks
                {
                    Status = text => TerminalMessage.Show(text),
                    Completed = () => FinishAccountAction(null,
                        "Signed out of SS2Revive community publishing on this PC."),
                    Failed = message => FinishAccountAction(message, null),
                });
                return;
            }

            SetLabel(_accountButton, "SIGNING IN");
            _accountOperation = CommunityPublishClient.Login(new CommunityAccountCallbacks
            {
                Status = text => TerminalMessage.Show(text),
                AuthenticationRequired = OpenAccountAuthentication,
                Completed = () => FinishAccountAction(null,
                    "Signed in to SS2Revive community publishing."),
                Failed = message => FinishAccountAction(message, null),
            });
        }

        private static void OpenAccountAuthentication(string code, Uri uri)
        {
            TerminalMessage.Show("Finish Steam sign-in in your browser. Code " + code + ".",
                                 false, 12f);
            try { Application.OpenURL(uri.AbsoluteUri); }
            catch (Exception ex)
            {
                _accountOperation?.Cancel();
                FinishAccountAction("Could not open Steam login: " + ex.Message, null);
            }
        }

        private static void FinishAccountAction(string warning, string success)
        {
            _accountOperation = null;
            if (_accountButton != null)
            {
                SetLabel(_accountButton,
                    CommunityPublishClient.HasStoredSession ? "LOG OUT" : "LOG IN");
                _accountButton.gameObject.SetActive(CommunityPublishClient.Enabled);
                _accountButton.interactable = CommunityPublishClient.Enabled;
            }
            TerminalMessage.Show(string.IsNullOrEmpty(warning) ? success : warning,
                                 !string.IsNullOrEmpty(warning));
        }

        private static void OpenImportFolder()
        {
            if (LevelSharing.ImportDirectory == null) return;

            try
            {
                Application.OpenURL("file:///" + LevelSharing.ImportDirectory.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not open the import folder: " + ex.Message);
            }
        }

        private static void OpenExportFolder()
        {
            if (LevelSharing.ExportDirectory == null) return;

            try
            {
                Application.OpenURL("file:///" + LevelSharing.ExportDirectory.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not open the export folder: " + ex.Message);
            }
        }

        /// <summary>
        /// A localised label rewrites itself from a key on enable, which would put the template's
        /// text back over ours. Nothing on this clone needs translating, so the components go.
        /// </summary>
        internal static void StripLocalisation(GameObject clone)
        {
            var components = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) continue;

                var name = component.GetType().Name;
                if (name.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Localis", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                UnityEngine.Object.Destroy(component);
            }
        }

        internal static void SetLabel(ExtendedButton button, string label)
        {
            var text = AccessTools.Field(typeof(ExtendedButton), "_buttonText")
                ?.GetValue(button) as TextMeshProUGUI;

            if (text == null) text = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text == null) return;

            text.text = label;
        }

    }
}
