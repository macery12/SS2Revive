using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SS2Revive
{
    /// <summary>Adds a published-only Your Maps browser and authenticated account actions.</summary>
    internal static class CommunityManagementPatches
    {
        private const int ReplacedFilterIndex = 4; // Newest
        private const string TileName = "SS2ReviveYourMapsButton";
        private const string ManageButtonName = "SS2ReviveManageCommunityButton";
        private const string OldUnpublishButtonName = "SS2ReviveUnpublishCommunityButton";
        private const string OldArchiveButtonName = "SS2ReviveArchiveCommunityButton";

        private static bool _browseActive;
        private static TerminalLevelBrowseOptions _browseScreen;
        private static TerminalCustomGameScreen _customGameScreen;
        private static TerminalUIController _terminalUi;
        private static TerminalLevelModalController _managementModal;
        private static CommunityPublishOperation _activeOperation;

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("TerminalLevelBrowseOptions.InitializeScreen -> Your Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelBrowseOptions), "InitializeScreen"),
                    null, new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(BrowseInitialised_Postfix))));
            });
            PatchSet.Try("TerminalLevelBrowseOptions.OnLanguageUpdated -> retain Your Maps tile", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelBrowseOptions), "OnLanguageUpdated"),
                    null, new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(LanguageUpdated_Postfix))));
            });
            PatchSet.Try("TerminalCustomGameScreen.OnScreenLeft -> leave Your Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "OnScreenLeft",
                        new[] { typeof(bool) }), null,
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(CustomGameScreenLeft_Postfix))));
            });
            PatchSet.Try("TerminalCustomGameScreen.OnTerminalExited -> leave Your Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "OnTerminalExited",
                        new[] { typeof(bool) }), null,
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(CustomGameScreenLeft_Postfix))));
            });
            PatchSet.Try("TerminalLevelModalController.DisplayLevelData -> online owner actions", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelModalController), "DisplayLevelData",
                        Type.EmptyTypes), null,
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(LevelDisplayed_Postfix))));
            });
            PatchSet.Try("ReportLevelModalController.Submit -> authenticated community report", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(ReportLevelModalController), "Submit",
                        Type.EmptyTypes),
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(Report_Prefix))));
            });
            PatchSet.Try("TerminalUIController.DisableUI -> cancel community account action", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalUIController), "DisableUI"),
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityManagementPatches),
                        nameof(TerminalDisabled_Prefix))));
            });
        }

        internal static bool ApplyBrowseFilter(out string creatorId)
        {
            creatorId = string.Empty;
            if (!_browseActive) return false;
            var player = SteamIdentity.GetLocalPlayerId();
            creatorId = player == null ? string.Empty : player.ToString();
            return !string.IsNullOrEmpty(creatorId);
        }

        private static void BrowseInitialised_Postfix(TerminalLevelBrowseOptions __instance)
        {
            try
            {
                if (__instance == null || !CommunityCatalogClient.Enabled) return;
                _browseActive = false;
                _browseScreen = __instance;
                _customGameScreen = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_customGameScreen")?.GetValue(__instance) as TerminalCustomGameScreen;
                _terminalUi = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_terminalUIController")?.GetValue(__instance) as TerminalUIController;
                var buttons = AccessTools.Field(typeof(TerminalLevelBrowseOptions), "_discoverButtons")
                    ?.GetValue(__instance) as TerminalDiscoverButton[];
                if (buttons == null || buttons.Length <= ReplacedFilterIndex
                    || buttons[ReplacedFilterIndex] == null) return;
                buttons[ReplacedFilterIndex].gameObject.name = TileName;
                ConfigureTile(buttons[ReplacedFilterIndex]);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Creating the Your Maps browser button threw: " + ex);
            }
        }

        private static void LanguageUpdated_Postfix(TerminalLevelBrowseOptions __instance)
        {
            try
            {
                var buttons = AccessTools.Field(typeof(TerminalLevelBrowseOptions), "_discoverButtons")
                    ?.GetValue(__instance) as TerminalDiscoverButton[];
                if (buttons != null && buttons.Length > ReplacedFilterIndex
                    && buttons[ReplacedFilterIndex] != null)
                    ConfigureTile(buttons[ReplacedFilterIndex]);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Refreshing Your Maps browser text threw: " + ex.Message);
            }
        }

        private static void ConfigureTile(TerminalDiscoverButton tile)
        {
            tile.gameObject.SetActive(true);
            tile.SetText("YOUR MAPS", "Manage community maps published by your Steam account.");
            var title = AccessTools.Field(typeof(TerminalDiscoverButton), "_titleText")
                ?.GetValue(tile) as TMP_Text;
            var description = AccessTools.Field(typeof(TerminalDiscoverButton), "_descriptionText")
                ?.GetValue(tile) as TMP_Text;
            if (title != null)
            {
                title.enableWordWrapping = false;
                title.overflowMode = TextOverflowModes.Ellipsis;
            }
            if (description != null)
            {
                description.enableWordWrapping = true;
                description.overflowMode = TextOverflowModes.Ellipsis;
            }
            var extended = tile.ExtendedButton;
            if (extended == null) return;
            extended.onClick = new Button.ButtonClickedEvent();
            extended.OnPointerClickButton = YourMapsClicked;
            extended.interactable = true;
            extended.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        }

        private static void YourMapsClicked(ExtendedButton ignored)
        {
            if (_browseScreen == null || _customGameScreen == null || _terminalUi == null) return;
            if (SteamIdentity.GetSteamId64() == 0UL)
            {
                TerminalMessage.Show("Steam must be online to identify your published maps.", true);
                return;
            }
            try
            {
                _browseActive = true;
                _customGameScreen.SetFilter(TerminalCreateScreenData.LevelSortBy.Newest,
                    TerminalCreateScreenData.LevelFilterBy.AllLevels, "YOUR MAPS");
                _terminalUi.TransitionBetweenScreens(_browseScreen, _customGameScreen);
            }
            catch (Exception ex)
            {
                _browseActive = false;
                Plugin.Log.LogError("Opening the Your Maps browser threw: " + ex);
            }
        }

        private static void CustomGameScreenLeft_Postfix(TerminalCustomGameScreen __instance)
        {
            if (__instance == _customGameScreen) _browseActive = false;
        }

        internal static void RefreshBrowse()
        {
            if (!_browseActive || _customGameScreen == null) return;
            try
            {
                AccessTools.Method(typeof(TerminalCustomGameScreen), "NewSearch")
                    ?.Invoke(_customGameScreen, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Refreshing Your Maps after an account action threw: "
                                      + ex.Message);
            }
        }

        private static void LevelDisplayed_Postfix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_currentDisplayedLevel")?.GetValue(__instance) as Data.LevelSummaryData;
                var localPlayer = SteamIdentity.GetLocalPlayerId();
                var ownsOnlineMap = summary != null && localPlayer != null
                    && CommunityCatalogClient.IsPublishedMap(summary.serverLevelId)
                    && summary.creatorPlayerIds != null && summary.creatorPlayerIds.Contains(localPlayer);
                var template = AccessTools.Field(typeof(TerminalLevelModalController), "_editButton")
                    ?.GetValue(__instance) as ExtendedButton;
                HideObsoleteAction(__instance, OldUnpublishButtonName);
                HideObsoleteAction(__instance, OldArchiveButtonName);
                var manage = EnsureActionButton(__instance, template, ManageButtonName,
                    "ONLINE OPTIONS", ManageOnlineClicked, 1);
                SetVisible(manage, ownsOnlineMap);
                if (ownsOnlineMap) _managementModal = __instance;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Applying community owner actions threw: " + ex);
            }
        }

        private static ExtendedButton EnsureActionButton(TerminalLevelModalController modal,
            ExtendedButton template, string name, string label, PointerClick<ExtendedButton> callback,
            int offset)
        {
            if (template == null || template.transform.parent == null) return null;
            var parent = template.transform.parent;
            var existing = parent.Find(name);
            ExtendedButton button;
            if (existing == null)
            {
                var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
                clone.name = name;
                clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + offset);
                button = clone.GetComponent<ExtendedButton>();
                if (button == null)
                {
                    UnityEngine.Object.Destroy(clone);
                    return null;
                }
                SharingPatches.StripLocalisation(clone);
                var buttons = AccessTools.Field(typeof(TerminalLevelModalController), "_buttons")
                    ?.GetValue(modal) as List<ExtendedButton>;
                if (buttons != null && !buttons.Contains(button)) buttons.Add(button);
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    var rect = clone.transform as RectTransform;
                    var source = template.transform as RectTransform;
                    if (rect != null && source != null)
                        rect.anchoredPosition = source.anchoredPosition
                                                + new Vector2((source.rect.width + 8f) * offset, 0f);
                }
            }
            else button = existing.GetComponent<ExtendedButton>();
            if (button == null) return null;
            button.onClick = new Button.ButtonClickedEvent();
            button.OnPointerClickButton = callback;
            button.OnPointerEnterButton = null;
            button.OnPointerExitButton = null;
            SharingPatches.SetLabel(button, label);
            return button;
        }

        private static void SetVisible(ExtendedButton button, bool visible)
        {
            if (button == null) return;
            button.gameObject.SetActive(visible);
            button.interactable = visible && _activeOperation == null;
        }

        private static void HideObsoleteAction(TerminalLevelModalController modal, string name)
        {
            var template = AccessTools.Field(typeof(TerminalLevelModalController), "_editButton")
                ?.GetValue(modal) as ExtendedButton;
            var old = template == null || template.transform.parent == null
                ? null : template.transform.parent.Find(name)?.GetComponent<ExtendedButton>();
            if (old != null) old.gameObject.SetActive(false);
        }

        private static void ManageOnlineClicked(ExtendedButton ignored)
        {
            var modal = _managementModal;
            var terminal = AccessTools.Field(typeof(TerminalLevelModalController),
                "_owningTerminalUIController")?.GetValue(modal) as TerminalUIController;
            if (modal == null || terminal == null || _activeOperation != null) return;
            terminal.GetTerminalConfirmationModal().Show(
                "ONLINE OPTIONS",
                "UNPUBLISH",
                "REMOVE ONLINE",
                () => BeginOwnerAction(modal, false),
                () => BeginOwnerAction(modal, true));
        }

        private static void BeginOwnerAction(TerminalLevelModalController modal, bool archive)
        {
            var summary = AccessTools.Field(typeof(TerminalLevelModalController),
                "_currentDisplayedLevel")?.GetValue(modal) as Data.LevelSummaryData;
            if (summary == null || !CommunityCatalogClient.IsPublishedMap(summary.serverLevelId)) return;
            var mapId = summary.serverLevelId;
            var callbacks = new CommunityAccountCallbacks
            {
                Status = text => TerminalMessage.Show(text),
                AuthenticationRequired = (code, uri) => OpenAuthentication(code, uri),
                Completed = () =>
                {
                    _activeOperation = null;
                    TerminalMessage.Show(archive
                        ? "The map was removed from the community catalogue. Your local copy remains."
                        : "The map is now unpublished. Upload it again to republish it.");
                    try { modal.Hide(); } catch { }
                    RefreshBrowse();
                },
                Failed = message =>
                {
                    _activeOperation = null;
                    TerminalMessage.Show(message, true);
                    LevelDisplayed_Postfix(modal);
                },
            };
            _activeOperation = archive
                ? CommunityPublishClient.Archive(mapId, callbacks)
                : CommunityPublishClient.Unpublish(mapId, callbacks);
            LevelDisplayed_Postfix(modal);
        }

        private static bool Report_Prefix(ReportLevelModalController __instance)
        {
            try
            {
                var mapId = AccessTools.Field(typeof(ReportLevelModalController), "_serverLevelId")
                    ?.GetValue(__instance) as string;
                if (!CommunityCatalogClient.IsPublishedMap(mapId)) return true;
                var reasons = AccessTools.Field(typeof(ReportLevelModalController), "_reportReasons")
                    ?.GetValue(__instance) as string[];
                var dropdown = AccessTools.Field(typeof(ReportLevelModalController), "_issueDropdown")
                    ?.GetValue(__instance) as TMP_Dropdown;
                if (reasons == null || dropdown == null || dropdown.value < 0
                    || dropdown.value >= reasons.Length) return true;
                var reason = reasons[dropdown.value];
                __instance.Hide();
                _activeOperation = CommunityPublishClient.Report(mapId, reason,
                    new CommunityAccountCallbacks
                    {
                        Status = text => TerminalMessage.Show(text),
                        AuthenticationRequired = (code, uri) => OpenAuthentication(code, uri),
                        Completed = () =>
                        {
                            _activeOperation = null;
                            TerminalMessage.Show("Thank you. The map report was sent to the maintainer.");
                        },
                        Failed = message =>
                        {
                            _activeOperation = null;
                            TerminalMessage.Show(message, true);
                        },
                    });
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Submitting a community report threw: " + ex);
                return true;
            }
        }

        private static void OpenAuthentication(string code, Uri uri)
        {
            TerminalMessage.Show("Finish Steam sign-in in your browser. Code " + code + ".",
                                 false, 12f);
            try { Application.OpenURL(uri.AbsoluteUri); }
            catch (Exception ex)
            {
                _activeOperation?.Cancel();
                _activeOperation = null;
                TerminalMessage.Show("Could not open Steam login: " + ex.Message, true);
                if (_managementModal != null) LevelDisplayed_Postfix(_managementModal);
            }
        }

        private static void TerminalDisabled_Prefix()
        {
            _browseActive = false;
            _activeOperation?.Cancel();
            _activeOperation = null;
        }
    }
}
