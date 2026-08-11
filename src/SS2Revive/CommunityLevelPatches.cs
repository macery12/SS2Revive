using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>Adds a compact, prefab-native marker to maps present in the online catalogue.</summary>
    internal static class CommunityLevelPatches
    {
        private const string AuthorLabelName = "SS2ReviveCommunityAuthors";

        internal static void Apply(Harmony harmony)
        {
            PatchSet.Try("TerminalLevelButton.Init() -> online community marker", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelButton), "Init",
                        Type.EmptyTypes), null,
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityLevelPatches),
                        nameof(LevelButtonInitialised_Postfix))));
            });

            PatchSet.Try("TerminalLevelModalController.DisplayLevelData -> community author names", () =>
            {
                harmony.Patch(PatchSet.Method(typeof(TerminalLevelModalController),
                        "DisplayLevelData", Type.EmptyTypes), null,
                    new HarmonyMethod(AccessTools.Method(typeof(CommunityLevelPatches),
                        nameof(LevelModalDisplayed_Postfix))));
            });
        }

        private static void LevelButtonInitialised_Postfix(TerminalLevelButton __instance)
        {
            try
            {
                var summary = __instance?.GetLevelSummary();
                if (summary == null || !CommunityCatalogClient.IsPublishedMap(summary.serverLevelId))
                    return;

                // Community Spotlight no longer has its own browser and this card icon is otherwise
                // unused by the revived catalogue. Reusing it preserves the authored layout without
                // introducing a runtime sprite or another overlapping text object.
                var icon = AccessTools.Field(typeof(TerminalLevelButton), "_communitySpotlightIcon")
                    ?.GetValue(__instance) as GameObject;
                icon?.SetActive(true);

                var type = AccessTools.Field(typeof(TerminalLevelButton), "_LevelTypeText")
                    ?.GetValue(__instance) as TMP_Text;
                if (type != null && type.gameObject.activeSelf
                    && !type.text.StartsWith("ONLINE | ", StringComparison.Ordinal))
                    type.text = "ONLINE | " + type.text;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Displaying the online community marker threw: " + ex.Message);
            }
        }

        private static void LevelModalDisplayed_Postfix(TerminalLevelModalController __instance)
        {
            try
            {
                var summary = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_currentDisplayedLevel")?.GetValue(__instance) as Data.LevelSummaryData;
                var profiles = AccessTools.Field(typeof(TerminalLevelModalController),
                    "_profileImageEditPanel")?.GetValue(__instance) as ProfileIcon[];
                if (profiles == null || profiles.Length == 0 || profiles[0] == null) return;

                var parent = profiles[0].transform.parent as RectTransform;
                if (parent == null) return;
                var existing = parent.Find(AuthorLabelName);

                if (summary == null || !CommunityCatalogClient.IsPublishedMap(summary.serverLevelId)
                    || summary.creatorPlayerIds == null || summary.creatorPlayerIds.Count == 0)
                {
                    if (existing != null) existing.gameObject.SetActive(false);
                    return;
                }

                CommunityAuthorLabel authorLabel;
                if (existing == null)
                {
                    var sourceText = AccessTools.Field(typeof(TerminalLevelModalController),
                        "_saveTimeInfoPanel")?.GetValue(__instance) as TMP_Text;
                    if (sourceText == null) return;

                    var root = new GameObject(AuthorLabelName, typeof(RectTransform),
                        typeof(CanvasRenderer), typeof(TextMeshProUGUI),
                        typeof(CommunityAuthorLabel));
                    root.transform.SetParent(parent, false);
                    var text = root.GetComponent<TextMeshProUGUI>();
                    text.font = sourceText.font;
                    text.fontSharedMaterial = sourceText.fontSharedMaterial;
                    text.fontSize = Mathf.Max(18f, sourceText.fontSize * 0.9f);
                    text.color = sourceText.color;
                    text.alignment = TextAlignmentOptions.MidlineLeft;
                    text.enableWordWrapping = false;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                    text.raycastTarget = false;
                    authorLabel = root.GetComponent<CommunityAuthorLabel>();
                }
                else
                {
                    authorLabel = existing.GetComponent<CommunityAuthorLabel>();
                }
                if (authorLabel == null) return;

                var visibleProfiles = Math.Min(summary.creatorPlayerIds.Count, profiles.Length);
                var sourceRect = profiles[Math.Max(0, visibleProfiles - 1)].transform as RectTransform;
                var labelRect = authorLabel.transform as RectTransform;
                if (sourceRect != null && labelRect != null)
                {
                    labelRect.anchorMin = sourceRect.anchorMin;
                    labelRect.anchorMax = sourceRect.anchorMax;
                    labelRect.pivot = new Vector2(0f, 0.5f);
                    labelRect.anchoredPosition = sourceRect.anchoredPosition
                        + new Vector2(sourceRect.rect.width + 12f, 0f);
                    labelRect.sizeDelta = new Vector2(460f, Mathf.Max(36f, sourceRect.rect.height));
                    labelRect.SetAsLastSibling();
                }

                var steamIds = new List<ulong>(summary.creatorPlayerIds.Count);
                for (var i = 0; i < summary.creatorPlayerIds.Count; i++)
                {
                    var steamId = SteamIdentity.TryGetSteamId(summary.creatorPlayerIds[i]);
                    if (steamId != 0UL) steamIds.Add(steamId);
                }
                authorLabel.Show(steamIds);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Displaying community author names threw: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Resolves display-only Steam persona names without trusting a name embedded in an uploaded
    /// bundle. SteamID64 remains the ownership identity; this component only improves attribution.
    /// </summary>
    internal sealed class CommunityAuthorLabel : MonoBehaviour
    {
        private readonly List<ulong> _steamIds = new List<ulong>();
        private TMP_Text _text;
        private float _nextLookup;
        private float _stopLookup;

        internal void Show(List<ulong> steamIds)
        {
            _steamIds.Clear();
            if (steamIds != null) _steamIds.AddRange(steamIds);
            _text = GetComponent<TMP_Text>();
            gameObject.SetActive(_steamIds.Count > 0);
            _stopLookup = Time.unscaledTime + 15f;
            _nextLookup = 0f;
            Resolve();
        }

        private void Update()
        {
            if (_steamIds.Count == 0 || Time.unscaledTime < _nextLookup
                || Time.unscaledTime > _stopLookup) return;
            Resolve();
        }

        private void Resolve()
        {
            if (_text == null) return;
            var names = new List<string>(_steamIds.Count);
            var pending = false;
            for (var i = 0; i < _steamIds.Count; i++)
            {
                var name = SteamIdentity.GetPersonaName(_steamIds[i]);
                if (string.IsNullOrEmpty(name))
                {
                    pending = true;
                    name = "Steam " + _steamIds[i];
                }
                names.Add(name);
            }
            _text.text = string.Join(", ", names.ToArray());
            _nextLookup = pending ? Time.unscaledTime + 0.5f : float.PositiveInfinity;
        }
    }
}
