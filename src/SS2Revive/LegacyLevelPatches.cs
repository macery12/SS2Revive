using System;
using Bossa.Localization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SS2Revive
{
    /// <summary>Terminal affordances for the read-only local legacy catalogue.</summary>
    internal static class LegacyLevelPatches
    {
        private const string ButtonName = "SS2ReviveLegacyMapsButton";
        private const int ReplacedFilterIndex = 1; // Community Spotlight

        private static TerminalLevelBrowseOptions _browseScreen;
        private static TerminalCustomGameScreen _customGameScreen;
        private static TerminalUIController _terminalUi;

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("TerminalLevelBrowseOptions.InitializeScreen -> Legacy Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelBrowseOptions), "InitializeScreen"),
                    null, new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(BrowseInitialised_Postfix))));
            });

            PatchSet.Try("TerminalLevelModalController.DisplayLevelData -> read-only legacy actions", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelModalController),
                        "DisplayLevelData"), null,
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(LevelDisplayed_Postfix))));
            });

            PatchSet.Try("TerminalLevelButton.Init() -> legacy previews and readable titles", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelButton), "Init",
                        Type.EmptyTypes), null,
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(LevelButtonInitialised_Postfix))));
            });

            PatchSet.Try("TerminalLevelBrowseOptions.OnLanguageUpdated -> retain Legacy Maps tile", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelBrowseOptions),
                        "OnLanguageUpdated"),
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(LanguageUpdated_Prefix))));
            });

            PatchSet.Try("TerminalCustomGameScreen.LoadMoreResults -> preserve legacy scroll", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen),
                        "LoadMoreResults"),
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(LoadMore_Prefix))));
            });

            PatchSet.Try("TerminalCustomGameScreen.Populate -> restore legacy scroll", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "Populate"), null,
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(Populate_Postfix))));
            });

            PatchSet.Try("TerminalCustomGameScreen.NewSearch -> clear preserved scroll", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "NewSearch"),
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(NewSearch_Prefix))));
            });

            PatchSet.Try("TerminalCustomGameScreen.OnScreenLeft -> leave Legacy Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "OnScreenLeft",
                        new[] { typeof(bool) }), null,
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(CustomGameScreenLeft_Postfix))));
            });

            PatchSet.Try("TerminalCustomGameScreen.OnTerminalExited -> leave Legacy Maps browser", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalCustomGameScreen), "OnTerminalExited",
                        new[] { typeof(bool) }), null,
                    new HarmonyMethod(AccessTools.Method(typeof(LegacyLevelPatches),
                        nameof(CustomGameScreenLeft_Postfix))));
            });
        }

        private static void BrowseInitialised_Postfix(TerminalLevelBrowseOptions __instance)
        {
            try
            {
                if (__instance == null || LegacyLevelCatalog.Count == 0) return;
                LegacyLevelCatalog.EndBrowse();

                _browseScreen = __instance;
                _customGameScreen = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_customGameScreen")?.GetValue(__instance) as TerminalCustomGameScreen;
                _terminalUi = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_terminalUIController")?.GetValue(__instance) as TerminalUIController;
                if (_customGameScreen == null || _terminalUi == null) return;

                var buttons = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_discoverButtons")?.GetValue(__instance) as TerminalDiscoverButton[];
                if (buttons == null || buttons.Length <= ReplacedFilterIndex
                    || buttons[ReplacedFilterIndex] == null) return;

                var parent = buttons[ReplacedFilterIndex].transform.parent;
                if (parent == null) return;

                var replacement = buttons[ReplacedFilterIndex];
                var existing = parent.Find(ButtonName);
                if (existing != null && existing != replacement.transform)
                    UnityEngine.Object.Destroy(existing.gameObject);

                // Community Spotlight depended on the retired service and has no unique local
                // catalogue to show. Reusing its authored tile avoids all runtime grid arithmetic
                // and ensures there is only one title and description renderer in this position.
                replacement.gameObject.name = ButtonName;
                ConfigureLegacyButton(replacement);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Creating the Legacy Maps browser button threw: " + ex);
            }
        }

        private static bool LanguageUpdated_Prefix(TerminalLevelBrowseOptions __instance)
        {
            if (__instance == null || LegacyLevelCatalog.Count == 0) return true;
            try
            {
                var buttons = AccessTools.Field(typeof(TerminalLevelBrowseOptions),
                    "_discoverButtons")?.GetValue(__instance) as TerminalDiscoverButton[];
                if (buttons == null) return true;

                var filters = TerminalLevelBrowseOptions.LevelFilters;
                for (var i = 0; i < filters.Length && i < buttons.Length; i++)
                {
                    if (i == ReplacedFilterIndex) continue;
                    if (buttons[i] == null) continue;
                    buttons[i].SetText(
                        StaticLocalizedText.Localize(filters[i].DisplayNameTranslationKey),
                        StaticLocalizedText.Localize(filters[i].DescriptionTranslationKey));
                }

                var parent = buttons.Length > 0 && buttons[0] != null
                    ? buttons[0].transform.parent : null;
                var legacy = parent?.Find(ButtonName)?.GetComponent<TerminalDiscoverButton>();
                if (legacy != null) ConfigureLegacyButton(legacy);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Refreshing Legacy Maps browser text threw: " + ex.Message);
                return true;
            }
        }

        private static void ConfigureLegacyButton(TerminalDiscoverButton legacyButton)
        {
            legacyButton.gameObject.SetActive(true);
            legacyButton.SetText("LEGACY MAPS",
                "Play maps preserved inside the game's local LegacyLevels archive.");

            var title = AccessTools.Field(typeof(TerminalDiscoverButton), "_titleText")
                ?.GetValue(legacyButton) as TMP_Text;
            var description = AccessTools.Field(typeof(TerminalDiscoverButton), "_descriptionText")
                ?.GetValue(legacyButton) as TMP_Text;
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

            // A few terminal-prefab variants carry presentation-only TMP duplicates inside the
            // tile. They are harmless on stock localized buttons but become visibly offset after
            // a runtime clone. The serialized title and description are the only text this tile
            // needs, so remove the redundant renderers from this tile alone.
            var texts = legacyButton.GetComponentsInChildren<TMP_Text>(true);
            for (var i = 0; i < texts.Length; i++)
                if (texts[i] != title && texts[i] != description)
                    texts[i].gameObject.SetActive(false);

            var extended = legacyButton.ExtendedButton;
            if (extended == null) return;
            extended.onClick = new Button.ButtonClickedEvent();
            extended.OnPointerClickButton = LegacyBrowseClicked;
            extended.interactable = true;
            extended.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        }

        private static void LevelButtonInitialised_Postfix(TerminalLevelButton __instance)
        {
            try
            {
                if (__instance == null) return;
                var summary = __instance.GetLevelSummary();
                var layout = __instance.GetComponent<LegacyLevelCardLayout>();
                if (summary == null || !LegacyLevelCatalog.IsLegacy(summary.serverLevelId))
                {
                    layout?.Restore();
                    return;
                }

                if (layout == null) layout = __instance.gameObject.AddComponent<LegacyLevelCardLayout>();
                var levelName = AccessTools.Field(typeof(TerminalLevelButton), "_levelName")
                    ?.GetValue(__instance) as TMP_Text;
                var lastEdited = AccessTools.Field(typeof(TerminalLevelButton), "_lastEditedTime")
                    ?.GetValue(__instance) as TMP_Text;
                layout.Apply(levelName, lastEdited);

                var rawImage = AccessTools.Field(typeof(TerminalLevelButton), "_levelImage")
                    ?.GetValue(__instance) as RawImage;
                if (rawImage != null && !summary.legacyLevelImage.IsTextureNull())
                    rawImage.texture = summary.legacyLevelImage.GetTexture();
                else if (rawImage != null)
                {
                    var fallback = AccessTools.Field(typeof(TerminalLevelButton),
                        "_defaultLevelImage")?.GetValue(__instance) as Sprite;
                    if (fallback != null) rawImage.texture = fallback.texture;
                }

                // A legacy entry deliberately has no remote URL. Mark its local result complete
                // whether it has an archived screenshot or the stock placeholder.
                AccessTools.Field(typeof(TerminalLevelButton), "_imageSet")
                    ?.SetValue(__instance, true);
                var loading = AccessTools.Field(typeof(TerminalLevelButton), "_loadingOverlay")
                    ?.GetValue(__instance) as GameObject;
                loading?.SetActive(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Displaying a legacy map card threw: " + ex.Message);
            }
        }

        private static void LoadMore_Prefix(TerminalCustomGameScreen __instance)
        {
            try
            {
                if (!LegacyLevelCatalog.BrowseActive) return;

                var scroll = AccessTools.Field(typeof(TerminalCustomGameScreen), "_scrollRect")
                    ?.GetValue(__instance) as ScrollRect;
                if (scroll == null || scroll.content == null) return;
                var keeper = __instance.GetComponent<LegacyScrollPositionKeeper>();
                if (keeper == null)
                    keeper = __instance.gameObject.AddComponent<LegacyScrollPositionKeeper>();
                keeper.Capture(scroll);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Capturing the legacy browser position threw: "
                                      + ex.Message);
            }
        }

        private static void Populate_Postfix(TerminalCustomGameScreen __instance)
        {
            __instance?.GetComponent<LegacyScrollPositionKeeper>()?.RestoreAfterRedraw();
        }

        private static void NewSearch_Prefix(TerminalCustomGameScreen __instance)
        {
            __instance?.GetComponent<LegacyScrollPositionKeeper>()?.Cancel();
        }

        private static void CustomGameScreenLeft_Postfix(TerminalCustomGameScreen __instance)
        {
            if (__instance != _customGameScreen) return;
            LegacyLevelCatalog.EndBrowse();
            __instance.GetComponent<LegacyScrollPositionKeeper>()?.Cancel();
        }

        private static void LegacyBrowseClicked(ExtendedButton ignored)
        {
            if (_browseScreen == null || _customGameScreen == null || _terminalUi == null) return;
            try
            {
                _customGameScreen.SetFilter(TerminalCreateScreenData.LevelSortBy.Alphabetical,
                    TerminalCreateScreenData.LevelFilterBy.Search, "LEGACY MAPS");

                // This state remains active until the custom-game screen is left. Every search,
                // sort change and subsequent page is therefore legacy-only, even if the player
                // edits the visible search text.
                LegacyLevelCatalog.BeginBrowse();
                _terminalUi.TransitionBetweenScreens(_browseScreen, _customGameScreen);

                var term = LegacyLevelCatalog.DisplayPrefix.Trim();
                AccessTools.Field(typeof(TerminalCustomGameScreen), "_searchTerm")
                    ?.SetValue(_customGameScreen, term);
                var search = AccessTools.Field(typeof(TerminalCustomGameScreen), "_searchBar")
                    ?.GetValue(_customGameScreen) as TMP_InputField;
                if (search != null) search.SetTextWithoutNotify(term);
            }
            catch (Exception ex)
            {
                LegacyLevelCatalog.EndBrowse();
                Plugin.Log.LogError("Opening the Legacy Maps browser threw: " + ex);
            }
        }

        private static void LevelDisplayed_Postfix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_currentDisplayedLevel")?.GetValue(__instance) as Data.LevelSummaryData;
                var imageState = __instance.GetComponent<LegacyModalImageState>();
                if (summary == null || !LegacyLevelCatalog.IsLegacy(summary.serverLevelId))
                {
                    imageState?.Release();
                    return;
                }

                if (imageState == null)
                    imageState = __instance.gameObject.AddComponent<LegacyModalImageState>();
                var imagePanel = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_levelImageInfoPanel")?.GetValue(__instance) as Image;
                var levelData = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_currentLevelData")?.GetValue(__instance) as TerminalLevelButtonData;
                Sprite fallback = null;
                if (levelData?.AttachedButton != null)
                    fallback = AccessTools.Field(typeof(TerminalLevelButton), "_defaultLevelImage")
                        ?.GetValue(levelData.AttachedButton) as Sprite;
                imageState.Show(imagePanel, summary, fallback);

                // A legacy map is an immutable game asset. Playing, queueing and favouriting are
                // useful; reporting, sharing, deleting, editing and publishing are misleading.
                Hide(__instance, "_shareButton");
                Hide(__instance, "_reportButton");
                Hide(__instance, "_deleteButton");
                Hide(__instance, "_editButton");
                Hide(__instance, "_publishButton");
                Hide(__instance, "_cancelValidationButton");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Applying read-only legacy map actions threw: " + ex);
            }
        }

        private static void Hide(TerminalLevelModalController modal, string fieldName)
        {
            var component = AccessTools.Field(typeof(TerminalLevelModalController), fieldName)
                ?.GetValue(modal) as Component;
            if (component != null) component.gameObject.SetActive(false);
        }
    }

    /// <summary>Captures and restores pooled card layout when a button changes data source.</summary>
    internal sealed class LegacyLevelCardLayout : MonoBehaviour
    {
        private TMP_Text _name;
        private TMP_Text _date;
        private bool _captured;
        private bool _dateActive;
        private bool _wordWrapping;
        private bool _autoSizing;
        private TextOverflowModes _overflow;
        private int _maxVisibleLines;
        private float _fontSizeMin;
        private float _fontSizeMax;
        private Vector2 _namePosition;
        private Vector2 _nameSize;

        internal void Apply(TMP_Text name, TMP_Text date)
        {
            if (!_captured)
            {
                _name = name;
                _date = date;
                if (_name == null) return;
                _captured = true;
                _dateActive = _date != null && _date.gameObject.activeSelf;
                _wordWrapping = _name.enableWordWrapping;
                _autoSizing = _name.enableAutoSizing;
                _overflow = _name.overflowMode;
                _maxVisibleLines = _name.maxVisibleLines;
                _fontSizeMin = _name.fontSizeMin;
                _fontSizeMax = _name.fontSizeMax;
                _namePosition = _name.rectTransform.anchoredPosition;
                _nameSize = _name.rectTransform.sizeDelta;
            }
            if (!_captured) return;

            _name.enableWordWrapping = true;
            _name.enableAutoSizing = true;
            _name.fontSizeMin = Math.Max(12f, Math.Min(_name.fontSize, 18f));
            _name.fontSizeMax = Math.Max(_name.fontSize, _name.fontSizeMin);
            _name.overflowMode = TextOverflowModes.Ellipsis;
            _name.maxVisibleLines = 2;

            if (_date != null && _date.rectTransform.parent == _name.rectTransform.parent
                && _date.rectTransform.anchorMin == _name.rectTransform.anchorMin
                && _date.rectTransform.anchorMax == _name.rectTransform.anchorMax)
            {
                var nameRect = _name.rectTransform;
                var dateRect = _date.rectTransform;
                var top = Math.Max(
                    nameRect.anchoredPosition.y + nameRect.rect.height * (1f - nameRect.pivot.y),
                    dateRect.anchoredPosition.y + dateRect.rect.height * (1f - dateRect.pivot.y));
                var bottom = Math.Min(
                    nameRect.anchoredPosition.y - nameRect.rect.height * nameRect.pivot.y,
                    dateRect.anchoredPosition.y - dateRect.rect.height * dateRect.pivot.y);
                nameRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, top - bottom);
                var position = nameRect.anchoredPosition;
                position.y = bottom + (top - bottom) * nameRect.pivot.y;
                nameRect.anchoredPosition = position;
                _date.gameObject.SetActive(false);
            }
        }

        internal void Restore()
        {
            if (!_captured || _name == null) return;
            _name.enableWordWrapping = _wordWrapping;
            _name.enableAutoSizing = _autoSizing;
            _name.overflowMode = _overflow;
            _name.maxVisibleLines = _maxVisibleLines;
            _name.fontSizeMin = _fontSizeMin;
            _name.fontSizeMax = _fontSizeMax;
            _name.rectTransform.anchoredPosition = _namePosition;
            _name.rectTransform.sizeDelta = _nameSize;
            if (_date != null) _date.gameObject.SetActive(_dateActive);
        }
    }

    /// <summary>Keeps a paged legacy grid anchored to the same visible content.</summary>
    internal sealed class LegacyScrollPositionKeeper : MonoBehaviour
    {
        private ScrollRect _scroll;
        private Vector2 _contentPosition;
        private bool _captured;
        private int _restoreFrames;

        internal void Capture(ScrollRect scroll)
        {
            _scroll = scroll;
            _scroll.StopMovement();
            _contentPosition = scroll.content.anchoredPosition;
            _captured = true;
            _restoreFrames = 0;
        }

        internal void RestoreAfterRedraw()
        {
            if (_captured) _restoreFrames = 2;
        }

        internal void Cancel()
        {
            _captured = false;
            _restoreFrames = 0;
            _scroll = null;
        }

        private void LateUpdate()
        {
            if (!_captured || _restoreFrames <= 0 || _scroll == null
                || _scroll.content == null) return;

            _scroll.StopMovement();
            _scroll.velocity = Vector2.zero;
            _scroll.content.anchoredPosition = _contentPosition;
            _restoreFrames--;
            if (_restoreFrames == 0) Cancel();
        }
    }

    /// <summary>Owns only sprites created for the legacy detail modal.</summary>
    internal sealed class LegacyModalImageState : MonoBehaviour
    {
        private Sprite _ownedSprite;
        private Image _panel;
        private bool _previousPreserveAspect;

        internal void Show(Image panel, Data.LevelSummaryData summary, Sprite fallback)
        {
            Release();
            if (panel == null || summary == null) return;
            _panel = panel;
            _previousPreserveAspect = panel.preserveAspect;

            var image = summary.legacyLevelImage;
            if (!image.IsTextureNull() && image.data != null && image.data.Length > 0
                && image.width > 0 && image.height > 0)
            {
                _ownedSprite = Sprite.Create(image.GetTexture(),
                    new Rect(0f, 0f, image.width, image.height), new Vector2(0.5f, 0.5f));
                panel.sprite = _ownedSprite;
            }
            else
            {
                panel.sprite = fallback;
            }
            panel.preserveAspect = true;
        }

        internal void Release()
        {
            if (_ownedSprite != null)
            {
                UnityEngine.Object.Destroy(_ownedSprite);
                _ownedSprite = null;
            }
            if (_panel != null)
            {
                _panel.preserveAspect = _previousPreserveAspect;
                _panel = null;
            }
        }

        private void OnDestroy()
        {
            Release();
        }
    }
}
