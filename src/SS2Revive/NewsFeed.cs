using System;
using System.IO;
using System.Reflection;

namespace SS2Revive
{
    /// <summary>
    /// Repoints the main menu's three news tiles at content we control.
    ///
    /// This one does not go through <see cref="BackendClient"/>. The news feed is the only part of
    /// the game that fetches over <c>UnityWebRequest</c> rather than
    /// <c>CrappyHttpsRequestService</c>, so the HTTP interception patch never sees it - and because
    /// UnityWebRequest speaks <c>file://</c>, it does not need a server at all.
    ///
    /// Bossa left the mechanism in place: their Local and Development environments already set
    /// <c>newsFeedUrl</c> to <c>"file:///" + Application.dataPath + "/StreamingAssets/NewsFeed/"</c>,
    /// and the game still ships that folder with a working NewsFeed.json and three images. All this
    /// does is point the same loader at a folder under the plugin directory instead, so the game's
    /// own files are left alone.
    ///
    /// The feed is plain JSON read by Newtonsoft, not by Bossa's hand-rolled deserializer, so the
    /// usual single-line-ASCII rule does not apply here - newlines, escapes and non-ASCII are all
    /// fine.
    ///
    /// To change what the tiles say, edit <c>assets\newsfeed\NewsFeed.json</c> in the repository.
    /// The build copies it in beside the plugin, so it ships in a release and a player can edit
    /// their own copy afterwards. <see cref="DefaultFeed"/> below is only the fallback for a loose
    /// DLL with no newsfeed folder next to it.
    /// </summary>
    internal static class NewsFeed
    {
        private const string FolderName = "newsfeed";
        private const string FeedFileName = "NewsFeed.json";

        /// <summary>
        /// Three, because <c>NewsFeed.SetData</c> walks the shorter of the JSON array and the
        /// prefab's tile list. A fourth entry is read and discarded; a missing third leaves that
        /// tile blank.
        /// </summary>
        private const string DefaultFeed = @"{
    ""NewsTiles"": [
        {
            ""ImageUrl"": ""tile-1.png"",
            ""ClickUrl"": """",
            ""Title"": ""Multiplayer Restored"",
            ""SubTitle"": ""Parties, invites and peer-to-peer play now run over Steam. Press F10 in a lobby to invite a friend.""
        },
        {
            ""ImageUrl"": ""tile-2.png"",
            ""ClickUrl"": """",
            ""Title"": ""Daily Challenges Are Back"",
            ""SubTitle"": ""Three challenges a day, served by your own backend. They roll over at UTC midnight.""
        },
        {
            ""ImageUrl"": ""tile-3.png"",
            ""ClickUrl"": """",
            ""Title"": ""Edit This Feed"",
            ""SubTitle"": ""Change the text, links and images in BepInEx/plugins/SS2Revive/newsfeed/. Images are 512x289 PNG. Set ClickUrl to open a link when a tile is clicked.""
        }
    ]
}
";

        /// <summary>Where the tiles are read from, once <see cref="Initialise"/> has settled it.
        /// The patch reads this rather than keeping its own copy.</summary>
        internal static string BaseUrl { get; private set; }

        /// <summary>
        /// Returns the URL to hand the game, or null to leave Bossa's dead host in place.
        ///
        /// A configured URL wins and is used as given; it must end in a slash, because both call
        /// sites concatenate directly onto it (<c>+ "NewsFeed.json"</c> and <c>+ "images/"</c>).
        /// </summary>
        internal static string Initialise(string configuredUrl)
        {
            if (!string.IsNullOrEmpty(configuredUrl))
            {
                BaseUrl = configuredUrl.EndsWith("/") ? configuredUrl : configuredUrl + "/";
                Plugin.Log.LogInfo("News feed served from " + BaseUrl);
                return BaseUrl;
            }

            try
            {
                var folder = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                    FolderName);

                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder, "images"));

                var feedFile = Path.Combine(folder, FeedFileName);
                if (!File.Exists(feedFile))
                {
                    File.WriteAllText(feedFile, DefaultFeed);
                    Plugin.Log.LogInfo("Wrote a starter news feed to " + feedFile);
                }

                SeedPlaceholderImages(folder);

                // Uri does the percent-encoding. The plugin path routinely contains spaces, and an
                // unescaped one makes UnityWebRequest fail with a bare "Cannot resolve destination
                // host" that looks nothing like the actual problem.
                BaseUrl = new Uri(folder + Path.DirectorySeparatorChar).AbsoluteUri;
                Plugin.Log.LogInfo("News feed served from " + BaseUrl);
                return BaseUrl;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not prepare the local news feed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Copies the game's own shipped tile images in as placeholders, once, if the user has not
        /// supplied any.
        ///
        /// Without an image the tile renders its error state, so an out-of-the-box feed with no
        /// artwork looks broken rather than empty. These are the user's own installed files being
        /// copied within their own machine, and any file they drop in themselves is left alone.
        /// </summary>
        private static void SeedPlaceholderImages(string folder)
        {
            var images = Path.Combine(folder, "images");
            if (Directory.GetFiles(images, "*.png").Length > 0)
                return;

            var shipped = Path.Combine(
                UnityEngine.Application.dataPath, "StreamingAssets/NewsFeed/images");
            if (!Directory.Exists(shipped))
                return;

            var sources = Directory.GetFiles(shipped, "*.png");
            for (var i = 0; i < sources.Length && i < 3; i++)
            {
                try
                {
                    File.Copy(sources[i], Path.Combine(images, "tile-" + (i + 1) + ".png"), false);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not seed news tile image: " + ex.Message);
                    return;
                }
            }

            Plugin.Log.LogInfo("Seeded placeholder news tile images into " + images);
        }
    }
}
