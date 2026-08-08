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
    /// The two buttons that make <see cref="LevelSharing"/> reachable without leaving the game.
    ///
    /// Export takes over the Share button rather than the Publish button, which is the opposite of
    /// the obvious choice and the right one. The terminal already shows exactly one of the two at a
    /// time - Publish while a level is a draft, Share once it is published:
    /// <code>
    ///   _publishButton.gameObject.SetActive(... &amp;&amp; _isInCreateMode &amp;&amp; flag);   // flag  = Draft
    ///   _shareButton.gameObject.SetActive(flag3);                            // flag3 = Published
    /// </code>
    /// so the pair is already a two-stage flow, and Publish is not spare: publishing is what puts a
    /// level into Discover and into the free-for-all pool. Share, on the other hand, handed out a
    /// code for a level nobody else could obtain - the one button here that genuinely had nowhere
    /// left to point.
    ///
    /// Import has no button to take over, so it gets a new one cloned from the training videos
    /// button beside it. That clone is the fragile part of this file - it depends on a prefab's
    /// layout rather than on a method signature - so the import folder is also scanned silently
    /// every time the Create screen opens. If the button never appears, dropping a file in the
    /// folder and opening Create still works.
    /// </summary>
    internal static class SharingPatches
    {
        private const string ImportButtonName = "SS2ReviveImportButton";

        /// <summary>
        /// The Create screen currently open, so the Import button can redraw it. The button's
        /// handler is static - it is a delegate on a cloned GameObject, not a component of ours -
        /// and there is only ever one of these screens.
        /// </summary>
        private static TerminalCreateScreen _screen;

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

            PatchSet.Try("TerminalCreateScreen.SetupNavigation -> include Import", () =>
            {
                var target = PatchSet.Method(typeof(TerminalCreateScreen), "SetupNavigation");
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(SharingPatches), nameof(SetupNavigation_Postfix))));
            });
        }

        /// <summary>
        /// Shows the export button on a draft, which the original does not.
        ///
        /// Publishing is a local status change and always has been - it marks a level for Discover
        /// and for the free-for-all queue on this machine, and nothing about it ever leaves the
        /// computer now that there is no service to leave for. But the button that carries export
        /// only appeared once a level was published, which made publishing feel like a prerequisite
        /// for sharing, and a step that sounds irreversible is a bad thing to require.
        ///
        /// So a draft can be exported too. Publish goes back to being what it is: the way to get
        /// your own level into your own Discover list and free-for-all rotation.
        /// </summary>
        private static void DisplayLevelData_Postfix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = CurrentLevel(__instance);
                if (summary == null) return;

                ConfigureInstalledActions(__instance, summary);
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

                TerminalMessage.Show("Exported to the SS2Revive export folder. Code " + code
                                     + " copied to your clipboard.");
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

            try
            {
                EnsureImportButton(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not add the Import button; the import folder is still "
                                    + "scanned when this screen opens. " + ex);
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

        // -------------------------------------------------------------- the button

        /// <summary>
        /// Clones the training videos button into an Import button beside it.
        ///
        /// Cloning rather than building one from nothing is what keeps it looking like the rest of
        /// the terminal: the prefab carries the font, the colours, the hover and click sounds and
        /// the selection behaviour, none of which are worth reproducing by hand.
        ///
        /// Identified by name rather than by a field, because the screen is entered and left
        /// repeatedly and the clone outlives a single visit. Finding one already there is the
        /// normal case after the first time.
        /// </summary>
        private static void EnsureImportButton(TerminalCreateScreen screen)
        {
            var template = AccessTools.Field(typeof(TerminalCreateScreen), "_trainingVideosButton")
                ?.GetValue(screen) as ExtendedButton;

            if (template == null)
            {
                Plugin.Log.LogWarning("No button to clone for Import; the import folder is still "
                                      + "scanned when the Create screen opens.");
                return;
            }

            var parent = template.transform.parent;
            if (parent == null || parent.Find(ImportButtonName) != null) return;

            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            clone.name = ImportButtonName;
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            var button = clone.GetComponent<ExtendedButton>();
            if (button == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }

            // Instantiate carries serialised state across, and onClick is serialised. The delegate
            // the screen installs is not, so the clone starts with no handler of its own and would
            // otherwise inherit only the template's persistent listeners - the training videos URL.
            button.onClick = new Button.ButtonClickedEvent();
            button.OnPointerClickButton = OnImportClicked;

            // The stock screen wires controller navigation explicitly for its two header buttons;
            // a visual clone is otherwise unreachable without a mouse. Preserve the template's
            // vertical destination and add the clone to its horizontal chain.
            var templateNavigation = template.navigation;
            var importNavigation = button.navigation;
            importNavigation.mode = Navigation.Mode.Explicit;
            importNavigation.selectOnLeft = template;
            importNavigation.selectOnDown = templateNavigation.selectOnDown;
            importNavigation.selectOnUp = templateNavigation.selectOnUp;
            button.navigation = importNavigation;

            templateNavigation.mode = Navigation.Mode.Explicit;
            templateNavigation.selectOnRight = button;
            template.navigation = templateNavigation;

            StripLocalisation(clone);
            SetLabel(button, "IMPORT");

            // The parent may lay its children out itself, in which case the clone is already in the
            // right place. Without a layout group it would sit exactly on top of the template.
            if (parent.GetComponent<LayoutGroup>() == null)
            {
                var rect = clone.transform as RectTransform;
                var source = template.transform as RectTransform;
                if (rect != null && source != null)
                {
                    rect.anchoredPosition = source.anchoredPosition
                                            - new Vector2(0f, source.rect.height + 8f);
                }
            }

            Plugin.Log.LogInfo("Added an Import button to the Create screen.");
        }

        private static void SetupNavigation_Postfix(TerminalCreateScreen __instance)
        {
            var template = AccessTools.Field(typeof(TerminalCreateScreen), "_trainingVideosButton")
                ?.GetValue(__instance) as ExtendedButton;
            var clone = template == null || template.transform.parent == null
                ? null
                : template.transform.parent.Find(ImportButtonName);
            var button = clone == null ? null : clone.GetComponent<ExtendedButton>();
            if (template == null || button == null) return;

            var source = template.navigation;
            var navigation = button.navigation;
            navigation.selectOnDown = source.selectOnDown;
            navigation.selectOnUp = source.selectOnUp;
            navigation.selectOnLeft = template;
            button.navigation = navigation;

            source.selectOnRight = button;
            template.navigation = source;
        }

        /// <summary>
        /// A localised label rewrites itself from a key on enable, which would put the template's
        /// text back over ours. Nothing on this clone needs translating, so the components go.
        /// </summary>
        private static void StripLocalisation(GameObject clone)
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

        private static void SetLabel(ExtendedButton button, string label)
        {
            var text = AccessTools.Field(typeof(ExtendedButton), "_buttonText")
                ?.GetValue(button) as TextMeshProUGUI;

            if (text == null) text = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text == null) return;

            text.text = label;
        }

    }
}
