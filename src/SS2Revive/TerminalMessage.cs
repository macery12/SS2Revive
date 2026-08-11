using System;
using System.Collections.Generic;
using HarmonyLib;
using Services;

namespace SS2Revive
{
    /// <summary>
    /// Says something to the player through the game's own prompt, with text of our own.
    ///
    /// <c>PromptController.ShowPrompt</c> looks like it takes a message and does not. Its parameter
    /// is named <c>promptDescriptionKey</c>, and it ends up on a <c>DynamicLocalizedText</c>, whose
    /// base resolves it through the translation table:
    /// <code>
    ///   public void UpdateLocalization() {
    ///       UpdateTranslation(Shell.Instance.GetLocalizationService().GetText(key));
    ///   }
    ///   // and GetText returns null for a key it does not have, which becomes:
    ///   Text.text = string.Empty;
    /// </code>
    /// A missing key therefore does not fall back to the string that was passed - it renders an
    /// empty prompt. Bossa's own code passes English sentences into it in a few places, which look
    /// like messages and have never displayed either.
    ///
    /// So the text is registered as a translation first and the prompt is given a key that resolves.
    /// Each message gets a fresh key, for two reasons: <c>LeanLocalizedBehaviour.Key</c> only
    /// refreshes when the value it is assigned actually differs, so reusing one key would leave the
    /// second message showing the first; and a prompt of the same <see cref="PromptType"/> is reused
    /// rather than rebuilt, which is the same trap from the other side.
    /// </summary>
    internal static class TerminalMessage
    {
        /// <summary>
        /// How many of our keys to leave in the table. They are small and few - one per export or
        /// import - but the table is the game's, not ours, and it should not grow without limit
        /// across a long session.
        /// </summary>
        private const int Keep = 16;

        private static readonly Queue<string> Issued = new Queue<string>();
        private static int _counter;

        internal static void Show(string message, bool isWarning = false, float seconds = 6f)
        {
            if (string.IsNullOrEmpty(message)) return;

            Plugin.Log.LogInfo("Terminal: " + message);

            try
            {
                var service = Shell.Instance.GetLocalizationService();
                var translations = AccessTools.Field(typeof(LocalizationService), "_translations")
                    ?.GetValue(service) as Dictionary<string, string>;

                if (translations == null)
                {
                    Plugin.Log.LogWarning("No translation table to register '" + message + "' in; "
                                          + "the prompt would come up blank, so it is being left "
                                          + "in the log only.");
                    return;
                }

                var key = "SS2REVIVE_MESSAGE_" + (++_counter);
                translations[key] = message;

                Issued.Enqueue(key);
                while (Issued.Count > Keep) translations.Remove(Issued.Dequeue());

                Shell.Instance.GetUIModel()
                     .GetUI<PromptController>(UIType.PromptController)
                     .ShowPrompt(PromptType.LevelSaveMessage, key, isWarning, seconds);
            }
            catch (Exception ex)
            {
                // The message is already in the log by this point, which is the part that matters.
                Plugin.Log.LogWarning("Could not show a prompt in game: " + ex.Message);
            }
        }
    }
}
