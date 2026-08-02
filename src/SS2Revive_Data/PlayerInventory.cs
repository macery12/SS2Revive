using System;
using System.Collections.Generic;

namespace SS2ReviveData
{
    public sealed class CharacterRecord
    {
        public string CharacterId;
        public List<string> EquippedItemIds = new List<string>();
        public long CreatedAt;
    }

    /// <summary>
    /// Cosmetics: owned item sets, characters, and what each character has equipped.
    ///
    /// ## Why an empty answer would be worse than no answer
    ///
    /// <c>InventoryService.OnHttpRequestComplete</c> treats the response as authoritative in one
    /// direction: any item the client had predicted, but that is missing from the list it gets back
    /// sixty seconds later, is deleted with "Discarding item unlock for ItemId ...". A failed
    /// request returns early and leaves the prediction alone.
    ///
    /// So refusing to answer is safe and answering with a short list is destructive. That asymmetry
    /// is the whole reason <see cref="LocalBackendOptions.GrantAllCosmetics"/> defaults on.
    ///
    /// ## Why granting everything is the honest default
    ///
    /// The client grants season rewards locally, from a local XP total, and tells the backend only
    /// "a quickplay level finished, won or not" - never its XP, never whether the level was rated,
    /// and never anything at all about campaign A++ reward sets. A backend tracking its own XP will
    /// therefore drift from the save file, and every drift is a withdrawn unlock rather than a
    /// cosmetic mismatch. The full catalogue is a superset of anything the client can predict, so
    /// nothing is ever withdrawn.
    ///
    /// Serverless makes the alternative genuinely viable for the first time - see
    /// <c>LocalBackend.MirrorProgression</c>, which keeps the recorded XP equal to the client's own
    /// - so turning it off is now a real choice rather than a way to lose hats.
    /// </summary>
    public sealed class PlayerInventory
    {
        /// <summary>setId -> when it was unlocked, in epoch milliseconds.</summary>
        public readonly Dictionary<string, long> UnlockedSets =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public readonly List<CharacterRecord> Characters = new List<CharacterRecord>();

        /// <summary>
        /// The season level for a cumulative XP total, using the client's own curve.
        ///
        /// <c>CurrentSeasonConfig.GetCumulativeXpToComplete</c> reads <c>SeasonRewards[level - 1]</c>,
        /// so level N is held until XP reaches entry N-1's threshold. Levels are 1-based and the
        /// track is finite; past the last entry the level stops climbing, which is what the client
        /// does too.
        /// </summary>
        public static int SeasonLevelForXp(GameCatalogue catalogue, long xp)
        {
            var track = catalogue?.RewardTrack;
            if (track == null || track.Count == 0)
                return (int)Math.Max(1, xp / 1000 + 1);

            var level = 1;
            for (var i = 0; i < track.Count; i++)
            {
                if (xp < track[i].CumulativeXp) break;
                level = track[i].Level + 1;
            }

            return Math.Min(level, track.Count);
        }

        /// <summary>
        /// Every reward-track set earned by a cumulative XP total.
        ///
        /// Keyed on XP rather than on level, because the two are off by one.
        /// <c>GetLevelCompletionReward</c> grants entry N when level N is *completed*, and
        /// completing level N is exactly the moment XP reaches entry N's threshold - at which point
        /// the player's level already reads N+1. Granting everything at or below the current level
        /// hands out one set too many.
        /// </summary>
        public static List<string> RewardSetsForXp(GameCatalogue catalogue, long xp)
        {
            var granted = new List<string>();
            if (catalogue == null) return granted;

            for (var i = 0; i < catalogue.RewardTrack.Count; i++)
            {
                var entry = catalogue.RewardTrack[i];
                if (entry.SetId != null && xp >= entry.CumulativeXp) granted.Add(entry.SetId);
            }

            return granted;
        }

        public int GrantSets(GameCatalogue catalogue, IEnumerable<string> setIds, long nowMs)
        {
            var granted = 0;
            if (catalogue == null) return granted;

            foreach (var setId in setIds)
            {
                if (!catalogue.HasItemSet(setId)) continue;
                if (UnlockedSets.ContainsKey(setId)) continue;
                UnlockedSets[setId] = nowMs;
                granted++;
            }

            return granted;
        }

        /// <summary>
        /// Owned items as itemId -> obtainedAt. Items inherit the timestamp of the set that granted
        /// them, because the client only reads <c>obtainedAt</c> to order the "new item" queue.
        /// </summary>
        private Dictionary<string, long> OwnedItems(GameCatalogue catalogue, bool grantAll)
        {
            var owned = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (catalogue == null) return owned;

            // The sets the client assumes everyone has. It unlocks these with or without a
            // backend, so omitting them would be a withdrawal on the very first response.
            for (var i = 0; i < catalogue.AssumeUnlockedItemSets.Count; i++)
                AddSet(catalogue, owned, catalogue.AssumeUnlockedItemSets[i], 0);

            if (grantAll)
            {
                for (var i = 0; i < catalogue.ItemSets.Count; i++)
                    AddSet(catalogue, owned, catalogue.ItemSets[i].SetId, 0);
                return owned;
            }

            foreach (var pair in UnlockedSets)
                AddSet(catalogue, owned, pair.Key, pair.Value);

            return owned;
        }

        private static void AddSet(GameCatalogue catalogue, Dictionary<string, long> owned,
                                   string setId, long obtainedAt)
        {
            var set = catalogue.GetItemSet(setId);
            if (set == null) return;

            for (var i = 0; i < set.Items.Count; i++)
            {
                var itemId = set.Items[i];
                if (owned.TryGetValue(itemId, out var existing) && existing <= obtainedAt) continue;
                owned[itemId] = obtainedAt;
            }
        }

        /// <summary>
        /// <c>GetInventoryResponse</c> reads the key <c>"inventory"</c>, not
        /// <c>"inventoryItems"</c> - the wire name differs from the struct member it fills, and
        /// getting it wrong yields an empty inventory rather than an error.
        /// </summary>
        public Json ToResponse(string playerId, GameCatalogue catalogue, bool grantAll)
        {
            var items = Json.Array();
            foreach (var pair in OwnedItems(catalogue, grantAll))
            {
                items.Add(Json.Object()
                    .Add("itemId", pair.Key)
                    .Add("obtainedAt", pair.Value));
            }

            var characters = Json.Array();
            for (var i = 0; i < Characters.Count; i++)
            {
                var equipped = Json.Array();
                for (var e = 0; e < Characters[i].EquippedItemIds.Count; e++)
                    equipped.Add(Json.Str(Characters[i].EquippedItemIds[e]));

                characters.Add(Json.Object()
                    .Add("characterId", Characters[i].CharacterId)
                    .Add("equippedItemIds", equipped));
            }

            return Json.Object()
                .Add("playerId", playerId)
                .Add("inventory", items)
                .Add("characters", characters);
        }

        /// <summary><c>GetItemSetsResponse</c> expects an array of
        /// <c>{ setId, items: [{ itemId }] }</c>, with the items as objects rather than bare
        /// GUID strings.</summary>
        public static Json ItemSetsResponse(GameCatalogue catalogue)
        {
            var sets = Json.Array();
            if (catalogue == null) return sets;

            for (var i = 0; i < catalogue.ItemSets.Count; i++)
            {
                var definition = catalogue.ItemSets[i];
                var items = Json.Array();
                for (var e = 0; e < definition.Items.Count; e++)
                    items.Add(Json.Object().Add("itemId", definition.Items[e]));

                sets.Add(Json.Object()
                    .Add("setId", definition.SetId)
                    .Add("items", items));
            }

            return sets;
        }

        /// <summary>
        /// Creates or updates a character. The client can equip on a character the backend has not
        /// been told about yet - CreateCharacter and EquipItem are separate requests and either can
        /// be the one that arrives first - so this is deliberately upsert rather than insert.
        /// </summary>
        public CharacterRecord SetCharacter(GameCatalogue catalogue, string characterId,
                                            IEnumerable<string> itemsToEquip, long nowMs)
        {
            var equipped = KnownItemIds(catalogue, itemsToEquip);

            for (var i = 0; i < Characters.Count; i++)
            {
                if (!string.Equals(Characters[i].CharacterId, characterId, StringComparison.OrdinalIgnoreCase))
                    continue;

                Characters[i].EquippedItemIds = equipped;
                return Characters[i];
            }

            var record = new CharacterRecord
            {
                CharacterId = characterId,
                EquippedItemIds = equipped,
                CreatedAt = nowMs,
            };
            Characters.Add(record);
            return record;
        }

        /// <summary>Keeps only GUIDs the catalogue knows, mirroring
        /// <c>RemoveInvalidItemIdsFromList</c> on the client.</summary>
        private static List<string> KnownItemIds(GameCatalogue catalogue, IEnumerable<string> itemIds)
        {
            var kept = new List<string>();
            if (itemIds == null) return kept;

            foreach (var raw in itemIds)
            {
                if (raw == null) continue;
                var itemId = raw.ToLowerInvariant();
                if (catalogue != null && !catalogue.HasItem(itemId)) continue;
                kept.Add(itemId);
            }

            return kept;
        }

        public Json ToStorage()
        {
            var sets = Json.Object();
            foreach (var pair in UnlockedSets)
                sets.Add(pair.Key, pair.Value);

            var characters = Json.Array();
            for (var i = 0; i < Characters.Count; i++)
            {
                var equipped = Json.Array();
                for (var e = 0; e < Characters[i].EquippedItemIds.Count; e++)
                    equipped.Add(Json.Str(Characters[i].EquippedItemIds[e]));

                characters.Add(Json.Object()
                    .Add("characterId", Characters[i].CharacterId)
                    .Add("equippedItemIds", equipped)
                    .Add("createdAt", Characters[i].CreatedAt));
            }

            return Json.Object()
                .Add("unlockedSets", sets)
                .Add("characters", characters);
        }

        public static PlayerInventory FromStorage(Json value)
        {
            var inventory = new PlayerInventory();
            if (value == null) return inventory;

            var sets = value["unlockedSets"];
            if (sets != null)
            {
                foreach (var pair in sets.Members)
                    inventory.UnlockedSets[pair.Key] = pair.Value.AsLong(0);
            }

            var characters = value["characters"];
            if (characters != null)
            {
                foreach (var entry in characters.Items)
                {
                    var record = new CharacterRecord
                    {
                        CharacterId = entry["characterId"].AsStringOr(string.Empty),
                        CreatedAt = entry["createdAt"].AsLongOr(0),
                    };

                    var equipped = entry["equippedItemIds"];
                    if (equipped != null)
                    {
                        foreach (var itemId in equipped.Items)
                            record.EquippedItemIds.Add(itemId.AsString());
                    }

                    inventory.Characters.Add(record);
                }
            }

            return inventory;
        }
    }
}
