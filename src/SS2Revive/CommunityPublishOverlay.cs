using System;
using System.Threading;
using Data;
using HarmonyLib;
using InControl;
using Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SS2Revive
{
    /// <summary>
    /// Full-size terminal overlay for the online publication flow. The first implementation is a
    /// deliberately local preflight: it proves the modal lifecycle, controller navigation and
    /// bundle export, Steam device authentication, private quarantine upload, server validation
    /// and publication without ever putting an R2 credential in the mod.
    /// </summary>
    internal sealed class CommunityPublishOverlay : MonoBehaviour
    {
        private const string ObjectName = "SS2ReviveCommunityPublishOverlay";

        private static CommunityPublishOverlay _active;

        private InputService _inputService;
        private InputTarget _inputTarget;
        private DynamicPlayerActionSet _actionSet;
        private PlayerAction _menuBackAction;
        private Selectable _previousSelectable;
        private ExtendedButton _prepareButton;
        private ExtendedButton _closeButton;
        private TextMeshProUGUI _status;
        private string _serverLevelId;
        private bool _released;
        private bool _closed;
        private CommunityPublishOperation _operation;
        private float _lastProgressUpdate;
        private bool _wasMenuInputEnabled;
        private bool _menuInputStateCaptured;

        internal static void Show(TerminalLevelModalController modal, LevelSummaryData summary)
        {
            if (modal == null || summary == null || string.IsNullOrEmpty(summary.serverLevelId)) return;
            HideActive();

            var parent = modal.transform.parent;
            var template = AccessTools.Field(typeof(TerminalLevelModalController), "_cancelButton")
                ?.GetValue(modal) as ExtendedButton;
            if (parent == null || template == null)
            {
                TerminalMessage.Show("The online publishing panel is unavailable in this terminal.",
                                     isWarning: true);
                return;
            }

            try
            {
                var root = new GameObject(ObjectName, typeof(RectTransform), typeof(Canvas),
                                          typeof(GraphicRaycaster), typeof(CanvasRenderer),
                                          typeof(Image), typeof(CanvasGroup));
                root.transform.SetParent(parent, false);
                var overlay = root.AddComponent<CommunityPublishOverlay>();
                _active = overlay;
                overlay.Build(modal, summary, template);
                modal.Hide();
                root.transform.SetAsLastSibling();
                overlay.AcquireInput();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Opening the online publishing overlay threw: " + ex);
                HideActive();
                TerminalMessage.Show("Could not open online publishing. See the log.", isWarning: true);
            }
        }

        internal static void HideActive()
        {
            var active = _active;
            _active = null;
            if (active == null) return;
            active._closed = true;
            active._operation?.Cancel();
            active.ReleaseInput();
            if (active.gameObject != null) UnityEngine.Object.Destroy(active.gameObject);
        }

        private void Build(TerminalLevelModalController modal, LevelSummaryData summary,
                           ExtendedButton template)
        {
            _serverLevelId = summary.serverLevelId;

            var rootRect = transform as RectTransform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.localScale = Vector3.one;

            // This is a sibling of a stock modal whose close animation and InputTarget disable
            // CanvasGroups asynchronously. Give the overlay its own raycaster/sort boundary and
            // ignore those parent groups so it stays genuinely interactive while the stock modal
            // finishes closing underneath it.
            var canvas = GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 30000;

            var backdrop = GetComponent<Image>();
            backdrop.color = new Color(0.015f, 0.02f, 0.035f, 0.97f);
            backdrop.raycastTarget = true;

            var canvasGroup = GetComponent<CanvasGroup>();
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
            canvasGroup.ignoreParentGroups = true;

            var panel = Panel("PublishPanel", transform, new Vector2(1040f, 610f),
                              new Color(0.055f, 0.075f, 0.11f, 1f));
            var sourceText = template.GetComponentInChildren<TextMeshProUGUI>(true);

            Text("Header", panel, sourceText, "PUBLISH ONLINE", 38f,
                 new Vector2(40f, -30f), new Vector2(-40f, -90f), TextAlignmentOptions.MidlineLeft);
            Text("LevelName", panel, sourceText, Safe(summary.levelName, 128), 29f,
                 new Vector2(410f, -125f), new Vector2(-45f, -175f), TextAlignmentOptions.MidlineLeft);
            Text("Description", panel, sourceText,
                 string.IsNullOrWhiteSpace(summary.levelDescription)
                     ? "No description has been set for this level."
                     : Safe(summary.levelDescription, 600),
                 20f, new Vector2(410f, -180f), new Vector2(-45f, -330f),
                 TextAlignmentOptions.TopLeft);

            var sourceImage = AccessTools.Field(typeof(TerminalLevelModalController), "_levelImageInfoPanel")
                ?.GetValue(modal) as Image;
            var preview = Image("Thumbnail", panel, sourceImage == null ? null : sourceImage.sprite,
                                new Vector2(325f, 183f));
            var previewRect = preview.transform as RectTransform;
            previewRect.anchorMin = new Vector2(0f, 1f);
            previewRect.anchorMax = new Vector2(0f, 1f);
            previewRect.pivot = new Vector2(0f, 1f);
            previewRect.anchoredPosition = new Vector2(45f, -130f);

            Text("ValidationHeader", panel, sourceText, "SECURE UPLOAD PREFLIGHT", 22f,
                 new Vector2(45f, -350f), new Vector2(-45f, -390f), TextAlignmentOptions.MidlineLeft);
            _status = Text("Status", panel, sourceText,
                "The level will be packaged locally, linked to your Steam account in the system "
                + "browser, uploaded into private quarantine, validated, and then published.",
                18f, new Vector2(45f, -395f), new Vector2(-45f, -485f), TextAlignmentOptions.TopLeft);

            _prepareButton = CloneButton(template, panel, "PrepareButton", "PUBLISH");
            _closeButton = CloneButton(template, panel, "CloseButton", "CLOSE");
            PositionButton(_prepareButton, new Vector2(-265f, 42f));
            PositionButton(_closeButton, new Vector2(-45f, 42f));
            _prepareButton.OnPointerClickButton = Prepare;
            _closeButton.OnPointerClickButton = Close;

            var prepareNavigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnRight = _closeButton,
            };
            _prepareButton.navigation = prepareNavigation;
            var closeNavigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnLeft = _prepareButton,
            };
            _closeButton.navigation = closeNavigation;
        }

        private void AcquireInput()
        {
            _inputService = Shell.Instance.GetInputService();
            _wasMenuInputEnabled = _inputService.MainMenuInputActionSet.Enabled;
            _menuInputStateCaptured = true;
            _previousSelectable = _inputService.GetCurrentDefaultSelectable();
            var menuBack = _inputService.GetInputProfile()._levelEditor.MenuBack;
            _actionSet = new DynamicPlayerActionSet();
            _menuBackAction = _actionSet.AddPlayerAction("MenuBack", menuBack);
            _inputTarget = new InputTarget(InputCursorMode.WithinWindow,
                new PlayerActionSet[] { _actionSet }, new[] { GetComponent<CanvasGroup>() },
                _prepareButton);
            _inputService.PushInputTarget(_inputTarget);
            if (!_wasMenuInputEnabled) _inputService.EnableMenuInput();
            _inputService.SetDefaultSelectable(_prepareButton);
        }

        private void ReleaseInput()
        {
            if (_released) return;
            _released = true;
            if (_inputService != null && _inputTarget != null
                && _inputService.IsInputTargetActive(_inputTarget))
            {
                _inputService.PopInputTarget();
            }
            if (_inputService != null && _previousSelectable != null)
                _inputService.SetDefaultSelectable(_previousSelectable);
            if (_inputService != null && _menuInputStateCaptured && !_wasMenuInputEnabled)
                _inputService.DisableMenuInput();
        }

        private void Prepare(ExtendedButton ignored)
        {
            _prepareButton.interactable = false;
            _status.text = "Preparing and hashing the current level package...";
            var levelId = _serverLevelId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LevelSharing.UploadPackage package;
                string message;
                var prepared = LevelSharing.PrepareUpload(levelId, out package, out message);
                Dispatcher.NextFrame(() =>
                {
                    if (_closed || this == null) return;
                    if (!prepared)
                    {
                        Failed("Preflight failed: " + Safe(message, 360));
                        return;
                    }
                    _status.text = "Preflight passed. Starting protected Steam authentication...";
                    CommunityPublishOperation operation = null;
                    var callbacks = new CommunityPublishCallbacks
                    {
                        Status = value => { if (IsCurrent(operation)) _status.text = Safe(value, 400); },
                        Progress = value => { if (IsCurrent(operation)) UploadProgress(value); },
                        AuthenticationRequired = (code, uri) =>
                        {
                            if (!IsCurrent(operation)) return;
                            _status.text = "Steam code " + Safe(code, 16)
                                + " opened in your browser. Approve the device, then return here.";
                            try { Application.OpenURL(uri.AbsoluteUri); }
                            catch (Exception ex) { Failed("Could not open Steam login: " + ex.Message); }
                        },
                        Published = (mapId, revision) =>
                        {
                            if (!IsCurrent(operation)) return;
                            if (!string.Equals(mapId, package.LevelId,
                                               StringComparison.OrdinalIgnoreCase)
                                || revision != package.Revision)
                            {
                                Failed("The server published a different map revision than the prepared package.");
                                return;
                            }
                            CommunityCatalogClient.NotePublished(mapId, revision);
                            UgcBackend.ConfirmCommunityPublished(mapId, revision);
                            SharingPatches.RefreshCurrentCreateScreen();
                            _status.text = "Published successfully as revision " + revision
                                + ". This local map is now marked Published and Online.";
                            SharingPatches.SetLabel(_prepareButton, "PUBLISHED");
                        },
                        Failed = value => { if (IsCurrent(operation)) Failed(value); },
                    };
                    operation = CommunityPublishClient.Publish(package, callbacks);
                    _operation = operation;
                });
            });
        }

        private bool IsCurrent(CommunityPublishOperation operation) =>
            !_closed && operation != null && ReferenceEquals(_operation, operation);

        private void UploadProgress(float value)
        {
            if (value < 1f && Time.realtimeSinceStartup - _lastProgressUpdate < 0.1f) return;
            _lastProgressUpdate = Time.realtimeSinceStartup;
            _status.text = "Uploading to private quarantine... "
                + Math.Round(Math.Max(0f, Math.Min(1f, value)) * 100f) + "%";
        }

        private void Failed(string value)
        {
            _status.text = "Publishing failed: " + Safe(value, 360);
            _operation?.Cancel();
            _operation = null;
            _prepareButton.interactable = true;
            SharingPatches.SetLabel(_prepareButton, "RETRY");
        }

        private void Close(ExtendedButton ignored) => HideActive();

        private void Update()
        {
            if (_menuBackAction != null && _menuBackAction.WasPressed) HideActive();
        }

        private void OnDestroy()
        {
            _closed = true;
            _operation?.Cancel();
            if (ReferenceEquals(_active, this)) _active = null;
            ReleaseInput();
        }

        private static RectTransform Panel(string name, Transform parent, Vector2 size, Color color)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.transform as RectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            gameObject.GetComponent<Image>().color = color;
            return rect;
        }

        private static TextMeshProUGUI Text(string name, Transform parent, TextMeshProUGUI style,
                                            string value, float size, Vector2 offsetMin,
                                            Vector2 offsetMax, TextAlignmentOptions alignment)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                                            typeof(TextMeshProUGUI));
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.transform as RectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            // Callers provide top-left then bottom-right coordinates from the panel's top edge.
            rect.offsetMin = new Vector2(offsetMin.x, offsetMax.y);
            rect.offsetMax = new Vector2(offsetMax.x, offsetMin.y);
            var text = gameObject.GetComponent<TextMeshProUGUI>();
            if (style != null)
            {
                text.font = style.font;
                text.fontSharedMaterial = style.fontSharedMaterial;
            }
            text.text = value ?? string.Empty;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = alignment;
            text.enableWordWrapping = true;
            text.richText = false;
            text.raycastTarget = false;
            return text;
        }

        private static Image Image(string name, Transform parent, Sprite sprite, Vector2 size)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.transform as RectTransform;
            rect.sizeDelta = size;
            var image = gameObject.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.color = sprite == null ? new Color(0.11f, 0.14f, 0.18f, 1f) : Color.white;
            image.raycastTarget = false;
            return image;
        }

        private static ExtendedButton CloneButton(ExtendedButton template, Transform parent,
                                                   string name, string label)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            clone.name = name;
            var button = clone.GetComponent<ExtendedButton>();
            button.onClick = new Button.ButtonClickedEvent();
            button.OnPointerClickButton = null;
            button.OnPointerEnterButton = null;
            button.OnPointerExitButton = null;
            button.interactable = true;
            SharingPatches.StripLocalisation(clone);
            SharingPatches.SetLabel(button, label);
            return button;
        }

        private static void PositionButton(ExtendedButton button, Vector2 position)
        {
            var rect = button.transform as RectTransform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = position;
        }

        private static string Safe(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var length = Math.Min(value.Length, maximum);
            var result = value.Substring(0, length);
            for (var i = 0; i < result.Length; i++)
            {
                if (result[i] < ' ') result = result.Replace(result[i], ' ');
            }
            return result.Trim();
        }
    }
}
