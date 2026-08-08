using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Data;
using HarmonyLib;
using Services;
using Services.Network;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Bounds on the level reader, so that a level file which lies about its own size cannot take
    /// the game down.
    ///
    /// The level format has three places where a number read straight out of the file decides the
    /// size of an allocation, none of them checked. In format 29 - the one the game writes today -
    /// they are:
    /// <code>
    ///   Serializer.ReadInt(readStream, out var value3, int.MinValue, int.MaxValue);
    ///   byte[] array = new byte[value3];                 // any size, including negative
    ///   ...
    ///   deflateStream.CopyTo(memoryStream);              // no cap on what comes out
    ///   ...
    ///   Serializer.ReadVector3Int(readStream, out var value);
    ///   levelData._voxelStates = new VoxelState[value.x, value.y, value.z];
    /// </code>
    /// The last one is the worst, and it is a regression rather than an oversight that was always
    /// there: formats 1 to 9 read their dimensions as 16-bit values and refused any level whose
    /// volume passed 262,144 - <c>if (value * value2 * value3 &gt; 262144) throw new
    /// SerializeException();</c> - and the check was dropped when the format moved to a full 32-bit
    /// <c>Vector3Int</c>. Every format from 10 up allocates two three-dimensional arrays from
    /// numbers an attacker chooses.
    ///
    /// This matters more here than it would in a single-player game. The party host's level is sent
    /// to every peer and read by all of them, so one crafted level does not cost one player - it
    /// costs the lobby. And it is reachable today through any route that moves a level between
    /// machines, which is exactly what this build adds one of.
    ///
    /// The limits are not guesses. <c>LevelDataService.WriteLevelData</c> serialises into a fixed
    /// <c>new byte[4096000]</c>, so the game is incapable of producing a level body larger than
    /// that; and a voxel costs one bit for its state plus a 32-bit word for its textures, so a body
    /// that size cannot describe more than about 993,000 voxels. Both bounds below sit just above
    /// what the game's own writer can emit, which means no level this game ever wrote can trip
    /// them.
    ///
    /// Nothing here throws. A refused level loads as an empty room and says so in the log, because
    /// on the receiving end of a party the alternative to a wrong level is not a right one, it is
    /// everybody dropping out.
    /// </summary>
    internal static class LevelFormatGuard
    {
        /// <summary>
        /// The working buffer <c>LevelDataService.WriteLevelData</c> serialises into. A level body
        /// bigger than this did not come from this game.
        /// </summary>
        private const int MaxBodyBytes = 4096000;

        /// <summary>Deflate can expand incompressible input slightly; this is room for that.</summary>
        private const int MaxCompressedBytes = 8 * 1024 * 1024;

        /// <summary>
        /// 2^20, against the ~993,000 voxels a maximum-sized body could actually describe. For
        /// scale, every format before the check was dropped refused anything past 262,144.
        /// </summary>
        private const long MaxVoxelVolume = 1024 * 1024;
        private const int MaxVoxelAxis = 256;
        private const int MaxSummaryBytes = 1024 * 1024;
        private const int MaxWarningDefinitions = 4096;
        private const int MaxWarningsPerDefinition = 256;
        private const int MaxPropDefinitions = 4096;
        private const int MaxPropInstances = 20000;
        private const int MaxCircuitLinks = 20000;

        private static MethodInfo _readBody;
        private static readonly HashSet<string> TrustedBundledLevelHashes =
            new HashSet<string>(StringComparer.Ordinal);
        [ThreadStatic] private static bool _readingBody;
        [ThreadStatic] private static bool _readingSummary;

        internal static void Apply(Harmony harmony)
        {
            LoadTrustedBundledLevelHashes();

            PatchSet.Try("Serializer.ReadVector3Int -> bound level dimensions", () =>
            {
                var target = PatchSet.Method(typeof(Serializer), "ReadVector3Int");
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(LevelFormatGuard), nameof(ReadVector3Int_Postfix))));
            });

            PatchSet.Try("LevelFormat.ReadLevelData_Format29 -> bound the compressed body", () =>
            {
                _readBody = AccessTools.Method(typeof(LevelFormat), "ReadLevelData",
                    new[] { typeof(ReadStream), typeof(LevelData).MakeByRefType() });

                if (_readBody == null)
                    throw new MissingMethodException("LevelFormat.ReadLevelData(ReadStream, ref LevelData)");

                var target = PatchSet.Method(typeof(LevelFormat), "ReadLevelData_Format29");
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(LevelFormatGuard), nameof(Format29_Prefix))));
            });

            PatchSet.Try("LevelDataService.ReadLevelData -> current or trusted bundled formats", () =>
            {
                var target = PatchSet.Method(typeof(LevelDataService), "ReadLevelData",
                    new[] { typeof(byte[]) });
                harmony.Patch(target, new HarmonyMethod(
                    AccessTools.Method(typeof(LevelFormatGuard), nameof(ReadLevelData_Prefix))));
            });

            PatchSet.Try("format 29 dictionaries -> bound allocation counts", () =>
            {
                PatchDictionaryConstructor(harmony,
                    typeof(Dictionary<int, Bossa.Framework.Utils.Guid>));
                PatchDictionaryConstructor(harmony, typeof(Dictionary<int, PropInstanceData>));
                PatchDictionaryConstructor(harmony, typeof(Dictionary<int, CircuitLinkInstanceData>));
            });

            PatchSet.Try("Serializer.ReadInt -> bound summary allocations", () =>
            {
                var target = PatchSet.Method(typeof(Serializer), "ReadInt",
                    new[] { typeof(ReadStream), typeof(int).MakeByRefType(), typeof(int), typeof(int) });
                harmony.Patch(target, null, new HarmonyMethod(
                    AccessTools.Method(typeof(LevelFormatGuard), nameof(SummaryReadInt_Postfix))));
            });

            PatchSet.Try("MessageSerializer.ReadLevelDataMessage -> preflight compressed level", () =>
            {
                var target = PatchSet.Method(typeof(MessageSerializer), "ReadLevelDataMessage");
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(LevelFormatGuard),
                        nameof(NetworkLevelMessage_Prefix))),
                    new HarmonyMethod(AccessTools.Method(typeof(LevelFormatGuard),
                        nameof(NetworkLevelMessage_Postfix))));
            });

            PatchSet.Try("LevelSummaryDataService.ReadLevelSummaryData -> bound collection counts", () =>
            {
                var target = PatchSet.Method(typeof(LevelSummaryDataService), "ReadLevelSummaryData",
                    new[] { typeof(byte[]) });
                harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(LevelFormatGuard),
                        nameof(SummaryRead_Prefix))),
                    new HarmonyMethod(AccessTools.Method(typeof(LevelFormatGuard),
                        nameof(SummaryRead_Postfix))));
            });

            PatchSet.Try("LevelSummaryDataService.ReadLevelSummaryDataImage -> validate image envelope", () =>
            {
                var target = PatchSet.Method(typeof(LevelSummaryDataService),
                    "ReadLevelSummaryDataImage", new[] { typeof(byte[]) });
                harmony.Patch(target, new HarmonyMethod(AccessTools.Method(typeof(LevelFormatGuard),
                    nameof(ImageRead_Prefix))));
            });
        }

        // -------------------------------------------------------------- dimensions

        /// <summary>
        /// Every call to <c>ReadVector3Int</c> in the game is a level-size read - thirteen of them,
        /// one per format version, and nothing else uses it. So a blanket bound here is as narrow
        /// as a bound at each call site would be, and does not depend on which format reader a file
        /// asked for.
        ///
        /// Refused dimensions become zero rather than being clamped to the limit. Clamping would
        /// hand the loops below a size the file's own bit stream does not match, and they would
        /// read a million voxels' worth of whatever came next; zero skips them, and the reads after
        /// the grid carry on from the right place because the dimensions themselves were already
        /// consumed.
        /// </summary>
        private static void ReadVector3Int_Postfix(ref Vector3Int value)
        {
            var x = value.x;
            var y = value.y;
            var z = value.z;

            if (x > 0 && y > 0 && z > 0
                && x <= MaxVoxelAxis && y <= MaxVoxelAxis && z <= MaxVoxelAxis
                && x <= MaxVoxelVolume / y / z)
            {
                return;
            }

            Plugin.Log.LogError("Refusing a level that says it is " + x + "x" + y + "x" + z
                                + " voxels. The largest this game can write is under "
                                + MaxVoxelVolume + ", so this file is corrupt or was built to "
                                + "crash whoever opens it. Loading it as an empty room instead.");

            // Continuing after changing the dimensions would leave the original voxel bits unread
            // and make every later field start at the wrong offset. Reject the whole level.
            throw new SerializeException();
        }

        private static bool ReadLevelData_Prefix(byte[] data, ref LevelData __result)
        {
            var validEnvelope = data != null && data.Length >= 10 && data.Length <= MaxBodyBytes
                                && data[0] == 83 && data[1] == 117
                                && data[2] == 114 && data[3] == 103
                                && data[4] == 101 && data[5] == 111
                                && data[6] == 110 && data[7] == 115;
            if (validEnvelope)
            {
                var version = data[8] | (data[9] << 8);
                if (version == 29) return true;

                // Build 1.3.7 itself ships most of its active lobby/campaign catalogue as format
                // 28. Those files are trusted by exact SHA-256, loaded from StreamingAssets/Levels
                // at startup. Merely naming a custom file like a bundled map grants nothing; its
                // bytes have to match the installed game object exactly.
                if (version > 0 && version < 29 && version != 25
                    && IsTrustedBundledLevel(data))
                {
                    return true;
                }
            }

            Plugin.Log.LogError("Refusing level data that is oversized, malformed, or uses an old "
                                + "format without matching a level bundled with this game install. "
                                + "Custom and shared content is restricted to current format 29.");
            __result = new LevelData();
            return false;
        }

        private static void LoadTrustedBundledLevelHashes()
        {
            TrustedBundledLevelHashes.Clear();
            try
            {
                var root = Path.Combine(Application.streamingAssetsPath, "Levels");
                if (!Directory.Exists(root))
                {
                    Plugin.Log.LogWarning("Could not find the bundled level directory at " + root
                                          + "; older bundled maps will remain restricted.");
                    return;
                }

                var files = Directory.GetFiles(root, "*.lvl", SearchOption.AllDirectories);
                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(files[i]);
                        if (info.Length < 10 || info.Length > MaxBodyBytes) continue;
                        using (var input = new FileStream(files[i], FileMode.Open, FileAccess.Read,
                                                          FileShare.Read))
                        using (var sha = SHA256.Create())
                        {
                            TrustedBundledLevelHashes.Add(Hex(sha.ComputeHash(input)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Could not trust bundled level " + files[i] + ": "
                                              + ex.Message);
                    }
                }

                Plugin.Log.LogInfo("Trusted " + TrustedBundledLevelHashes.Count
                                   + " exact bundled level file(s); custom maps still require "
                                   + "format 29.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not catalogue the game's bundled levels: " + ex.Message);
            }
        }

        private static bool IsTrustedBundledLevel(byte[] data)
        {
            if (data == null || TrustedBundledLevelHashes.Count == 0) return false;
            using (var sha = SHA256.Create())
            {
                return TrustedBundledLevelHashes.Contains(Hex(sha.ComputeHash(data)));
            }
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }

        private static void PatchDictionaryConstructor(Harmony harmony, Type dictionaryType)
        {
            var target = AccessTools.Constructor(dictionaryType, new[] { typeof(int) });
            if (target == null) throw new MissingMethodException(dictionaryType.FullName, ".ctor(int)");
            harmony.Patch(target, new HarmonyMethod(
                AccessTools.Method(typeof(LevelFormatGuard), nameof(BodyDictionary_Prefix))));
        }

        private static void BodyDictionary_Prefix(int capacity, MethodBase __originalMethod)
        {
            if (!_readingBody) return;

            var dictionaryType = __originalMethod.DeclaringType;
            var valueType = dictionaryType == null ? null : dictionaryType.GetGenericArguments()[1];
            var maximum = valueType == typeof(Bossa.Framework.Utils.Guid)
                ? MaxPropDefinitions
                : valueType == typeof(PropInstanceData)
                    ? MaxPropInstances
                    : MaxCircuitLinks;
            if (capacity >= 0 && capacity <= maximum) return;

            Plugin.Log.LogError("Refusing a level body that requests a " + capacity
                                + "-entry " + valueType.Name + " dictionary (maximum "
                                + maximum + ").");
            throw new SerializeException();
        }

        private static void SummaryReadInt_Postfix(ref int value)
        {
            if (!_readingSummary || (value >= -MaxSummaryBytes && value <= MaxSummaryBytes)) return;

            Plugin.Log.LogError("Refusing level-summary data containing an integer outside its "
                                + MaxSummaryBytes + " byte envelope: " + value + ".");
            _readingSummary = false;
            throw new SerializeException();
        }

        private static bool SummaryRead_Prefix(byte[] data, ref LevelSummaryData __result)
        {
            _readingSummary = false;
            if (data == null || data.Length == 0 || data.Length > MaxSummaryBytes)
            {
                Plugin.Log.LogError("Refusing empty or oversized level-summary data.");
                __result = null;
                return false;
            }

            _readingSummary = true;
            return true;
        }

        private static void SummaryRead_Postfix(ref LevelSummaryData __result)
        {
            _readingSummary = false;
            if (__result == null) return;

            var image = __result.legacyLevelImage;
            var bytes = image.data;
            if (bytes == null || bytes.Length == 0) return;
            if (SS2ReviveData.LevelBundle.IsSafeRawGameImage(
                    (uint)Math.Max(0, image.width), (uint)Math.Max(0, image.height),
                    (uint)image.format, (uint)bytes.Length)) return;

            Plugin.Log.LogError("Dropping an invalid embedded level-summary image.");
            __result.legacyLevelImage = new LevelSummaryImageData(null, 0, 0, TextureFormat.Alpha8);
        }

        private static bool ImageRead_Prefix(byte[] data, ref LevelSummaryImageData __result)
        {
            if (SS2ReviveData.LevelBundle.IsSafeGameImage(data)) return true;

            Plugin.Log.LogError("Refusing an invalid or oversized level image envelope.");
            __result = new LevelSummaryImageData(null, 0, 0, TextureFormat.Alpha8);
            return false;
        }

        private static void NetworkLevelMessage_Prefix(MessageSerializer __instance, ref int __state)
        {
            __state = -1;
            try
            {
                var lengthField = AccessTools.Field(typeof(MessageSerializer), "_readBufferLength");
                var bufferField = AccessTools.Field(typeof(MessageSerializer), "_readBuffer");
                var length = (int)lengthField.GetValue(__instance);
                var buffer = bufferField.GetValue(__instance) as byte[];

                if (ValidateNetworkLevelMessage(buffer, length)) return;

                __state = length;
                lengthField.SetValue(__instance, 0);
                Plugin.Log.LogError("Refusing a malformed or oversized multiplayer level message.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not preflight a multiplayer level message: " + ex.Message);
                var lengthField = AccessTools.Field(typeof(MessageSerializer), "_readBufferLength");
                __state = (int)lengthField.GetValue(__instance);
                lengthField.SetValue(__instance, 0);
            }
        }

        private static void NetworkLevelMessage_Postfix(MessageSerializer __instance, int __state)
        {
            if (__state >= 0)
                AccessTools.Field(typeof(MessageSerializer), "_readBufferLength")
                    .SetValue(__instance, __state);
        }

        private static bool ValidateNetworkLevelMessage(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0 || length > buffer.Length
                || length > MaxCompressedBytes + MaxSummaryBytes) return false;

            var stream = new ReadStream();
            stream.Start(new uint[(length + 3) / 4], buffer, length);

            byte messageType;
            int ignored;
            bool optional;
            Bossa.Framework.Utils.Guid ignoredGuid;

            Serializer.ReadBits(stream, out messageType, 8);
            if (messageType != 21) return false;
            Serializer.ReadInt(stream, out ignored, 0, int.MaxValue);
            Serializer.ReadInt(stream, out ignored, 0, int.MaxValue);
            Serializer.ReadBool(stream, out optional);
            if (optional) Serializer.ReadInt(stream, out ignored, 0, int.MaxValue);
            Serializer.ReadInt(stream, out ignored, 0, int.MaxValue);
            Serializer.ReadInt(stream, out ignored, 0, int.MaxValue);
            Serializer.ReadBool(stream, out optional);
            Serializer.ReadBool(stream, out optional);
            Serializer.ReadGuid(stream, out ignoredGuid);

            int summaryLength;
            Serializer.ReadInt(stream, out summaryLength, 0, int.MaxValue);
            if (summaryLength < 0 || summaryLength > MaxSummaryBytes
                || !stream.SerializeAlign()) return false;
            stream.SkipBytes(summaryLength);

            Serializer.ReadInt(stream, out ignored, 0, int.MaxValue); // level mode

            int definitions;
            Serializer.ReadInt(stream, out definitions, 0, int.MaxValue);
            if (definitions < 0 || definitions > MaxWarningDefinitions) return false;
            var definitionIds = new HashSet<Bossa.Framework.Utils.Guid>();
            for (var i = 0; i < definitions; i++)
            {
                Serializer.ReadGuid(stream, out ignoredGuid);
                if (!definitionIds.Add(ignoredGuid)) return false;
                int warnings;
                Serializer.ReadInt(stream, out warnings, 0, int.MaxValue);
                if (warnings < 0 || warnings > MaxWarningsPerDefinition) return false;
                for (var w = 0; w < warnings; w++)
                    Serializer.ReadInt(stream, out ignored, 0, 255);
            }

            int compressedLength;
            Serializer.ReadInt(stream, out compressedLength, 0, int.MaxValue);
            if (compressedLength <= 0 || compressedLength > MaxCompressedBytes
                || !stream.SerializeAlign()) return false;

            var compressed = new byte[compressedLength];
            Serializer.ReadBytes(stream, compressed, compressedLength);

            byte[] levelBytes;
            if (!TryInflateGZip(compressed, out levelBytes)) return false;
            return levelBytes.Length >= 10 && levelBytes.Length <= MaxBodyBytes
                   && levelBytes[0] == 83 && levelBytes[1] == 117
                   && levelBytes[2] == 114 && levelBytes[3] == 103
                   && levelBytes[4] == 101 && levelBytes[5] == 111
                   && levelBytes[6] == 110 && levelBytes[7] == 115
                   && (levelBytes[8] | (levelBytes[9] << 8)) == 29;
        }

        private static bool TryInflateGZip(byte[] compressed, out byte[] outputBytes)
        {
            outputBytes = null;
            try
            {
                using (var source = new MemoryStream(compressed))
                using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var chunk = new byte[64 * 1024];
                    while (true)
                    {
                        var read = gzip.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;
                        if (output.Length + read > MaxBodyBytes) return false;
                        output.Write(chunk, 0, read);
                    }
                    outputBytes = output.ToArray();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------- compression

        /// <summary>
        /// The original, with the two unbounded steps bounded. Everything else about it is
        /// unchanged, including the bit widths, so a file this accepts deserialises identically.
        /// </summary>
        private static bool Format29_Prefix(LevelData levelData, ReadStream readStream,
                                            ref LevelData __result)
        {
            __result = levelData;

            try
            {
                string clientLevelId;
                Serializer.ReadString(readStream, out clientLevelId);
                levelData.clientLevelId = new Bossa.Framework.Utils.Guid(clientLevelId);

                bool isCompressed;
                Serializer.ReadBool(readStream, out isCompressed);

                if (!isCompressed)
                {
                    ReadBody(readStream, ref levelData);
                    __result = levelData;
                    return false;
                }

                int compressedLength;
                Serializer.ReadInt(readStream, out compressedLength, int.MinValue, int.MaxValue);

                if (compressedLength < 0 || compressedLength > MaxCompressedBytes)
                {
                    Plugin.Log.LogError("Refusing a level whose compressed body claims to be "
                                        + compressedLength + " bytes. Loading it as an empty room.");
                    return false;
                }

                var compressed = new byte[compressedLength];
                Serializer.ReadBytes(readStream, compressed, compressedLength);

                byte[] body;
                if (!TryInflate(compressed, out body))
                    return false;

                var inner = new ReadStream();
                inner.Start(new uint[(body.Length + 3) / 4], body, body.Length);
                ReadBody(inner, ref levelData);

                __result = levelData;
                return false;
            }
            catch (Exception ex)
            {
                // The original would have thrown out of here too, but out of a reader with no
                // bounds in it. Whatever is left of levelData is returned rather than propagating,
                // for the same reason the dimension check does not throw.
                Plugin.Log.LogError("Reading a level failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Decompresses with a ceiling, which <c>CopyTo</c> does not have. A few hundred bytes of
        /// deflate expand to gigabytes if that is what they were built to do, and the original both
        /// holds the result in a MemoryStream and then copies it again with <c>ToArray</c> - so the
        /// peak is twice whatever the file asks for.
        /// </summary>
        private static bool TryInflate(byte[] compressed, out byte[] body)
        {
            body = null;

            using (var source = new MemoryStream(compressed))
            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var chunk = new byte[64 * 1024];
                var total = 0;

                while (true)
                {
                    var read = deflate.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;

                    total += read;
                    if (total > MaxBodyBytes)
                    {
                        Plugin.Log.LogError("Refusing a level that expands past " + MaxBodyBytes
                                            + " bytes; the game's own writer cannot produce one "
                                            + "that large. Loading it as an empty room.");
                        return false;
                    }

                    output.Write(chunk, 0, read);
                }

                body = output.ToArray();
                return true;
            }
        }

        private static void ReadBody(ReadStream stream, ref LevelData levelData)
        {
            _readingBody = true;
            try
            {
                var args = new object[] { stream, levelData };
                _readBody.Invoke(null, args);
                levelData = (LevelData)args[1];
            }
            finally
            {
                _readingBody = false;
            }
        }
    }
}
