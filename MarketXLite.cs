/*
 * Copyright 2026
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

// Requires: Economics
using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MarketX Lite", "-Samir-", "1.2.5")]
    [Description("Basic global economy plugin with random market events.")]
    public class MarketXLite : RustPlugin
    {
        /*
         * Copyright 2026 Samir
         */

        // External plugins. Economics is required, ImageLibrary is optional.

        [PluginReference] private Plugin Economics;
        [PluginReference] private Plugin ImageLibrary;

        // Quick flag so we don't keep checking ImageLibrary null state over and over.
        private bool _imageLibraryReady;
        private readonly object _cacheLock = new object();
        // Main item price data cache (itemid -> price info).
        private Dictionary<int, PriceData> _priceCache = new Dictionary<int, PriceData>();
        // Per-player UI selections.
        private Dictionary<ulong, int> _playerQuantities = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> _playerCategories = new Dictionary<ulong, string>();

        // Current market event label and multiplier.
        private string _currentEvent = "STABLE ECONOMY";
        private decimal _eventMultiplier = 1.0m;
        private string _eventColor = "#00ffff";

        // Random helper object (kept as a field instead of static calls everywhere).
        private System.Random _eventRandom = new System.Random();

        public class PriceData
        {
            // Item id from Rust item definitions.
            public int ItemID;
            // Base configured price before dynamic changes.
            public decimal BasePrice;
            // Current runtime price after supply/demand math.
            public decimal CurrentPrice;
            // Previous price used for trend arrow.
            public decimal LastPrice;
            // Simulated market stock and demand counters.
            public int Supply;
            public int Demand;
            public string Category;

            public string GetTrendArrow()
            {
                if (CurrentPrice > LastPrice)
                {
                    return "<color=#44ff44>▲</color>";
                }

                if (CurrentPrice < LastPrice)
                {
                    return "<color=#ff4444>▼</color>";
                }

                return "<color=#aaaaaa>▬</color>";
            }

            public void UpdatePrice(decimal eventMultiplier)
            {
                // Save last price first so UI can show up/down arrow.
                LastPrice = CurrentPrice;

                // Junior-ish market pressure approach (not strict demand/supply ratio formula).
                decimal demandScore = Demand + 1m;
                decimal supplyScore = Supply + 1m;
                decimal pressure = 1m;
                decimal total = demandScore + supplyScore;
                if (total > 0m)
                {
                    decimal delta = demandScore - supplyScore;
                    pressure = 1m + (delta / total);
                }

                // Clamp price to avoid going super high or zero-ish.
                decimal nextPrice = BasePrice * pressure * eventMultiplier;
                decimal minPrice = BasePrice * 0.1m;
                decimal maxPrice = BasePrice * 20m;

                if (nextPrice < minPrice)
                {
                    nextPrice = minPrice;
                }

                if (nextPrice > maxPrice)
                {
                    nextPrice = maxPrice;
                }

                CurrentPrice = nextPrice;
            }

            public decimal GetPriceForAmount(int amount, decimal eventMultiplier, bool isBuy)
            {
                // For single buy/sell we can just return current.
                if (amount <= 1) return CurrentPrice;
                
                decimal startPrice = CurrentPrice;
                // Rough projection for stack trade impact.
                decimal projectedDemand = Demand + (isBuy ? amount : 0);
                decimal projectedSupply = Supply + (isBuy ? 0 : amount);

                decimal demandScore = projectedDemand + 1m;
                decimal supplyScore = projectedSupply + 1m;
                decimal pressure = 1m;
                decimal total = demandScore + supplyScore;
                if (total > 0m)
                {
                    decimal delta = demandScore - supplyScore;
                    pressure = 1m + (delta / total);
                }

                decimal endPrice = BasePrice * pressure * eventMultiplier;
                decimal minPrice = BasePrice * 0.1m;
                decimal maxPrice = BasePrice * 20m;

                if (endPrice < minPrice)
                {
                    endPrice = minPrice;
                }

                if (endPrice > maxPrice)
                {
                    endPrice = maxPrice;
                }
                
                // Just average start/end as a simple stack pricing approximation.
                return (startPrice + endPrice) / 2m;
            }
        }

        private class StoredData
        {
            // All item price records saved to data file.
            public List<PriceData> Prices = new List<PriceData>();
        }

        private StoredData _data;

        private void SaveData()
        {
            // Copy cache into serializable list before saving.
            lock (_cacheLock)
            {
                _data.Prices.Clear();
                foreach (var entry in _priceCache)
                {
                    _data.Prices.Add(entry.Value);
                }
            }

            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private void LoadData()
        {
            // Basic try/catch so broken data file does not kill plugin load.
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
            }
            catch
            {
                _data = new StoredData();
            }

            lock (_cacheLock)
            {
                _priceCache.Clear();
                for (int i = 0; i < _data.Prices.Count; i++)
                {
                    PriceData priceData = _data.Prices[i];
                    if (priceData == null)
                    {
                        continue;
                    }

                    _priceCache[priceData.ItemID] = priceData;
                }
            }
        }

        private Configuration _config;
        public class Configuration
        {
            [Newtonsoft.Json.JsonProperty("Market Tax Percentage")]
            public decimal MarketTax = 5.0m;

            [Newtonsoft.Json.JsonProperty("Initial Market Items (Shortname:BasePrice)")]
            public Dictionary<string, decimal> InitialItems = new Dictionary<string, decimal>
            {
                ["wood"] = 1.0m, ["stone"] = 2.0m, ["metal.fragments"] = 5.0m, ["scrap"] = 50.0m,
                ["rifle.ak"] = 500.0m, ["pickaxe.metal"] = 50.0m, ["lowgradefuel"] = 2.0m
            };

            [Newtonsoft.Json.JsonProperty("Custom Icon Overrides (Shortname:URL)")]
            public Dictionary<string, string> CustomIcons = new Dictionary<string, string>();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            // Kept in helper to avoid one-method template look.
            _config = TryReadConfig();
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        private void Init()
        {
            // Permissions used by chat command/admin icon command.
            permission.RegisterPermission("marketxlite.admin", this);
            permission.RegisterPermission("marketxlite.use", this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            BootstrapRuntime();
        }

        private void TriggerRandomEvent()
        {
            // Simple random event picker.
            // TODO: could make this weighted instead of pure random.
            int r = _eventRandom.Next(0, 5);
            if (r == 0)
            {
                _currentEvent = "INDUSTRIAL BOOM";
                _eventMultiplier = 1.25m;
                _eventColor = "#55ff55";
            }
            else if (r == 1)
            {
                _currentEvent = "ECONOMIC CRASH";
                _eventMultiplier = 0.75m;
                _eventColor = "#ff5555";
            }
            else if (r == 2)
            {
                _currentEvent = "SCRAP SHORTAGE";
                _eventMultiplier = 1.5m;
                _eventColor = "#ffaa00";
            }
            else if (r == 3)
            {
                _currentEvent = "PEACEFUL ERA";
                _eventMultiplier = 0.9m;
                _eventColor = "#aaaaff";
            }
            else
            {
                _currentEvent = "STABLE ECONOMY";
                _eventMultiplier = 1.0m;
                _eventColor = "#00ffff";
            }

            Puts($"Market Event: {_currentEvent} (x{_eventMultiplier})");
            // Recalculate every item's current price using new event multiplier.
            foreach (var p in _priceCache.Values)
            {
                p.UpdatePrice(_eventMultiplier);
            }
        }

        private void SyncMarketItems()
        {
            // Walk configured items and make sure cache has each one.
            foreach (var kvp in _config.InitialItems)
            {
                ItemDefinition def = ItemManager.FindItemDefinition(kvp.Key);
                if (def == null) continue;

                // Missing item in cache -> create fresh market data row.
                if (!_priceCache.ContainsKey(def.itemid))
                {
                    _priceCache[def.itemid] = new PriceData { ItemID = def.itemid, BasePrice = kvp.Value, CurrentPrice = kvp.Value, LastPrice = kvp.Value, Supply = 2000, Demand = 200, Category = def.category.ToString() };
                }
                // If old data has no category, fix it from definition.
                else if (string.IsNullOrEmpty(_priceCache[def.itemid].Category))
                {
                    _priceCache[def.itemid].Category = def.category.ToString();
                }
            }
        }

        private void RegisterAllIcons()
        {
            if (!_imageLibraryReady)
            {
                return;
            }

            // Register icons once so CUI can load faster from ImageLibrary cache.
            foreach (var p in _priceCache.Values)
            {
                var def = ItemManager.FindItemDefinition(p.ItemID);
                if (def != null) ImageLibrary.Call("AddImage", GetItemIconUrl(def.shortname), def.shortname, 0UL);
            }
        }

        private string GetItemIconUrl(string shortname)
        {
            string url;
            if (_config.CustomIcons.TryGetValue(shortname, out url))
            {
                return url;
            }

            return $"https://rustlabs.com/img/items180/{shortname}.png";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoBalance"] = "Insufficient funds! Total Cost: ${0:F2}",
                ["BuySuccess"] = "Bought {0}x {1} for ${2:F2}.",
                ["NoItems"] = "Insufficient items! Have: {0}",
                ["SellSuccess"] = "Sold {0}x {1} for ${2:F2}."
            }, this);
        }

        private void Unload()
        {
            ClearAllUiPanels();
            PersistBeforeExit();
        }

        private Configuration TryReadConfig()
        {
            Configuration cfg = null;
            try
            {
                cfg = Config.ReadObject<Configuration>();
            }
            catch
            {
                cfg = null;
            }

            if (cfg == null)
            {
                cfg = new Configuration();
            }

            return cfg;
        }

        private void BootstrapRuntime()
        {
            // This is optional plugin, so keep a bool for easier checks later.
            _imageLibraryReady = ImageLibrary != null && ImageLibrary.IsLoaded;

            // Fill initial configured items into cache if missing.
            SyncMarketItems();

            // Slight delay so ImageLibrary has time to be ready on startup.
            timer.Once(5f, RegisterAllIcons);

            StartBackgroundTimers();

            // Fire one event right away so market is not always stable after restart.
            TriggerRandomEvent(); // Initial event
        }

        private void StartBackgroundTimers()
        {
            // Periodic autosave + event loop.
            timer.Every(3600f, SaveData);
            timer.Every(1800f, TriggerRandomEvent);
        }

        private void ClearAllUiPanels()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, "MarketLiteUI");
            }
        }

        private void PersistBeforeExit()
        {
            SaveData();
        }

        private static string Truncate(string value, int maxChars)
        {
            // Tiny helper so labels don't overflow market card.
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }

        [ChatCommand("market")]
        private void cmdMarket(BasePlayer player)
        {
            // Player needs permission unless they are admin.
            if (!permission.UserHasPermission(player.UserIDString, "marketxlite.use") && !player.IsAdmin) return;
            OpenMarketUI(player);
        }

        private void OpenMarketUI(BasePlayer player)
        {
            // Pull player money from Economics. Fallback 0 if plugin missing.
            double balance = Economics != null ? Convert.ToDouble(Economics.Call("Balance", player.UserIDString)) : 0;

            // Keep last selected quantity and category for each player.
            int qty = _playerQuantities.ContainsKey(player.userID) ? _playerQuantities[player.userID] : 1;
            string cat = _playerCategories.ContainsKey(player.userID) ? _playerCategories[player.userID] : "ALL";

            // Destroy previous UI frame before redrawing.
            CuiHelper.DestroyUi(player, "MarketLiteUI");
            var container = new CuiElementContainer();

            // Main Panel
            container.Add(new CuiPanel { Image = { Color = "0.05 0.05 0.05 0.98" }, RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" }, CursorEnabled = true }, "Overlay", "MarketLiteUI");
            
            // Header
            container.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 1" }, RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 1" } }, "MarketLiteUI", "Header");
            container.Add(new CuiLabel { Text = { Text = "MARKETX <color=#00ffff>LITE</color>", FontSize = 22, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.4 1" } }, "Header");
            container.Add(new CuiLabel { Text = { Text = $"TICKER: <color={_eventColor}>{_currentEvent}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.3 0", AnchorMax = "0.7 1" } }, "Header");
            container.Add(new CuiLabel { Text = { Text = $"BALANCE: <color=#55ff55>${balance:F2}</color>", FontSize = 14, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = "0.6 0", AnchorMax = "0.95 1" } }, "Header");
            container.Add(new CuiButton { Button = { Color = "0.8 0.2 0.2 0.8", Close = "MarketLiteUI" }, Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.965 0.15", AnchorMax = "0.99 0.85" } }, "Header");

            // Category Sidebar
            string[] categories = { "ALL", "RESOURCES", "WEAPONS", "TOOLS", "FOOD" };
            for (int i = 0; i < categories.Length; i++)
            {
                // Highlight selected category with different color.
                string color = cat == categories[i] ? "0.0 0.6 0.6 0.4" : "0.15 0.15 0.15 1";
                container.Add(new CuiButton { Button = { Color = color, Command = $"marketlite.cat {categories[i]}" }, Text = { Text = categories[i], FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.02 {0.83f - (i * 0.05f)}", AnchorMax = $"0.15 {0.87f - (i * 0.05f)}" } }, "MarketLiteUI");
            }

            // Qty Buttons
            int[] qtys = { 1, 10, 100, 1000 };
            for (int i = 0; i < qtys.Length; i++)
            {
                // Highlight selected trade amount.
                string color = qty == qtys[i] ? "0.2 0.6 0.2 0.8" : "0.15 0.15 0.15 1";
                container.Add(new CuiButton { Button = { Color = color, Command = $"marketlite.qty {qtys[i]}" }, Text = { Text = qtys[i].ToString(), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"{0.75f + (i * 0.05f)} 0.84", AnchorMax = $"{0.79f + (i * 0.05f)} 0.88" } }, "MarketLiteUI");
            }

            // Item grid list build (manual loop style)
            List<PriceData> list = new List<PriceData>();
            foreach (var cacheEntry in _priceCache)
            {
                PriceData priceRow = cacheEntry.Value;
                bool includeItem = false;

                if (cat == "ALL")
                {
                    includeItem = true;
                }
                else if (priceRow.Category != null)
                {
                    if (cat == "WEAPONS" && priceRow.Category.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
                    {
                        includeItem = true;
                    }
                    else if (cat == "RESOURCES" && priceRow.Category.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                    {
                        includeItem = true;
                    }
                    else if (priceRow.Category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        includeItem = true;
                    }
                }

                if (includeItem)
                {
                    list.Add(priceRow);
                }

                if (list.Count >= 12)
                {
                    break;
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i]; int r = i / 4, c = i % 4;
                // Basic row/column card placement math.
                float x = 0.18f + (c * 0.20f), y = 0.78f - (r * 0.26f);
                string panel = $"Item_{p.ItemID}";
                var def = ItemManager.FindItemDefinition(p.ItemID);
                
                decimal avgBuyPrice = p.GetPriceForAmount(qty, _eventMultiplier, true);
                decimal avgSellPrice = p.GetPriceForAmount(qty, _eventMultiplier, false);

                container.Add(new CuiPanel { Image = { Color = "0.12 0.12 0.12 1" }, RectTransform = { AnchorMin = $"{x} {y - 0.24f}", AnchorMax = $"{x + 0.19f} {y}" } }, "MarketLiteUI", panel);
                
                if (_imageLibraryReady && def != null)
                {
                    string png = ImageLibrary.Call("GetImage", def.shortname, 0UL) as string;
                    if (!string.IsNullOrEmpty(png)) container.Add(new CuiElement { Parent = panel, Components = { new CuiRawImageComponent { Png = png }, new CuiRectTransformComponent { AnchorMin = "0.38 0.48", AnchorMax = "0.62 0.92" } } });
                }

                container.Add(new CuiLabel { Text = { Text = Truncate(def?.displayName.english.ToUpper() ?? "UNKNOWN", 14), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0.32", AnchorMax = "1 0.48" } }, panel);
                
                // Show average stack price if qty > 1, else normal current price with trend arrow.
                string priceText = string.Empty;
                if (qty > 1)
                {
                    priceText = $"${avgBuyPrice:F2} | Total: ${(avgBuyPrice * qty):F0}";
                }
                else
                {
                    priceText = $"${p.CurrentPrice:F2} {p.GetTrendArrow()}";
                }
                container.Add(new CuiLabel { Text = { Text = priceText, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0 1 1 1" }, RectTransform = { AnchorMin = "0 0.18", AnchorMax = "1 0.32" } }, panel);
                
                container.Add(new CuiButton { Button = { Color = "0.15 0.35 0.15 0.8", Command = $"marketlite.buy {p.ItemID} {qty}" }, Text = { Text = "BUY", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.08 0.04", AnchorMax = "0.48 0.16" } }, panel);
                container.Add(new CuiButton { Button = { Color = "0.35 0.15 0.15 0.8", Command = $"marketlite.sell {p.ItemID} {qty}" }, Text = { Text = "SELL", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.52 0.04", AnchorMax = "0.92 0.16" } }, panel);
            }

            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("marketlite.cat")]
        private void cmdCat(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            // Guard invalid calls.
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }

            _playerCategories[player.userID] = arg.Args[0].ToUpper();
            OpenMarketUI(player);
        }

        [ConsoleCommand("marketlite.qty")]
        private void cmdQty(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            // Guard invalid calls.
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }

            int qty;
            if (int.TryParse(arg.Args[0], out qty))
            {
                _playerQuantities[player.userID] = qty;
                OpenMarketUI(player);
            }
        }

        [ConsoleCommand("marketlite.buy")]
        private void cmdBuy(ConsoleSystem.Arg arg)
        {
            // Parse console args from UI button command.
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            if (arg.Args == null || arg.Args.Length < 2)
            {
                return;
            }

            int id = 0;
            int amount = 0;
            bool parsedId = int.TryParse(arg.Args[0], out id);
            bool parsedAmount = int.TryParse(arg.Args[1], out amount);
            if (!parsedId || !parsedAmount)
            {
                return;
            }

            PriceData p;
            if (!_priceCache.TryGetValue(id, out p))
            {
                return;
            }

            // Include tax in buy total.
            decimal avgPrice = p.GetPriceForAmount(amount, _eventMultiplier, true);
            double totalCost = (double)avgPrice * amount * (double)(1m + (_config.MarketTax / 100m));
            
            double balance = Convert.ToDouble(Economics?.Call("Balance", player.UserIDString));
            if (balance < totalCost)
            {
                player.ChatMessage(string.Format(lang.GetMessage("NoBalance", this, player.UserIDString), totalCost));
                return;
            }

            Economics?.Call("Withdraw", player.UserIDString, totalCost);
            player.GiveItem(ItemManager.CreateByItemID(id, amount));
            lock (_cacheLock)
            {
                // Buying increases demand.
                p.Demand += amount;
                p.UpdatePrice(_eventMultiplier);
            }
            OpenMarketUI(player);
        }

        [ConsoleCommand("marketlite.sell")]
        private void cmdSell(ConsoleSystem.Arg arg)
        {
            // Parse console args from UI button command.
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            if (arg.Args == null || arg.Args.Length < 2)
            {
                return;
            }

            int id = 0;
            int amount = 0;
            bool parsedId = int.TryParse(arg.Args[0], out id);
            bool parsedAmount = int.TryParse(arg.Args[1], out amount);
            if (!parsedId || !parsedAmount)
            {
                return;
            }

            PriceData p;
            if (!_priceCache.TryGetValue(id, out p))
            {
                return;
            }

            // Don't continue if player does not have enough items.
            int playerAmount = player.inventory.GetAmount(id);
            if (playerAmount < amount)
            {
                return;
            }
            
            // Sell gives less due to tax.
            decimal avgPrice = p.GetPriceForAmount(amount, _eventMultiplier, false);
            double totalEarned = (double)avgPrice * amount * (double)(1m - (_config.MarketTax / 100m));
            
            player.inventory.Take(null, id, amount);
            Economics?.Call("Deposit", player.UserIDString, totalEarned);
            lock (_cacheLock)
            {
                // Selling increases supply.
                p.Supply += amount;
                p.UpdatePrice(_eventMultiplier);
            }
            OpenMarketUI(player);
        }

        [ChatCommand("marketlite.icon")]
        private void cmdIcon(BasePlayer player, string cmd, string[] args)
        {
            // Admin-only utility command.
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin")) return;
            if (args.Length < 2) return;

            // Save custom icon URL in config by item shortname.
            _config.CustomIcons[args[0]] = args[1];
            SaveConfig();
            if (_imageLibraryReady)
            {
                ImageLibrary.Call("AddImage", args[1], args[0], 0UL);
            }
            player.ChatMessage($"Icon updated for {args[0]}.");
        }
    }
}
