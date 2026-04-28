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
    [Info("MarketX Lite", "-Samir-", "1.2.4")]
    [Description("Simple global economy with Dynamic Events and Dynamic Stack Pricing.")]
    public class MarketXLite : RustPlugin
    {
        /*
         * Copyright 2026 Samir
         */

        #region Fields

        [PluginReference] private Plugin Economics;
        [PluginReference] private Plugin ImageLibrary;

        private bool ImageLibraryReady;
        private readonly object _cacheLock = new object();
        private Dictionary<int, PriceData> _priceCache = new Dictionary<int, PriceData>();
        private Dictionary<ulong, int> _playerQuantities = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> _playerCategories = new Dictionary<ulong, string>();

        private string _currentEvent = "STABLE ECONOMY";
        private decimal _eventMultiplier = 1.0m;
        private string _eventColor = "#00ffff";

        public class PriceData
        {
            public int ItemID;
            public decimal BasePrice;
            public decimal CurrentPrice;
            public decimal LastPrice;
            public int Supply;
            public int Demand;
            public string Category;

            public string GetTrendArrow()
            {
                if (CurrentPrice > LastPrice) return "<color=#44ff44>▲</color>";
                if (CurrentPrice < LastPrice) return "<color=#ff4444>▼</color>";
                return "<color=#aaaaaa>▬</color>";
            }

            public void UpdatePrice(decimal eventMultiplier)
            {
                LastPrice = CurrentPrice;
                decimal ratio = (decimal)(Demand + 1) / (Supply + 1);
                CurrentPrice = Math.Max(BasePrice * 0.1m, Math.Min(BasePrice * 20m, BasePrice * ratio * eventMultiplier));
            }

            public decimal GetPriceForAmount(int amount, decimal eventMultiplier, bool isBuy)
            {
                if (amount <= 1) return CurrentPrice;
                
                decimal startPrice = CurrentPrice;
                decimal projectedDemand = Demand + (isBuy ? amount : 0);
                decimal projectedSupply = Supply + (isBuy ? 0 : amount);
                
                decimal ratio = (decimal)(projectedDemand + 1) / (projectedSupply + 1);
                decimal endPrice = Math.Max(BasePrice * 0.1m, Math.Min(BasePrice * 20m, BasePrice * ratio * eventMultiplier));
                
                // Average price across the stack shift
                return (startPrice + endPrice) / 2m;
            }
        }

        #endregion

        #region Data Persistence

        private class StoredData
        {
            public List<PriceData> Prices = new List<PriceData>();
        }

        private StoredData _data;

        private void SaveData()
        {
            lock (_cacheLock) { _data.Prices = _priceCache.Values.ToList(); }
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData(); }
            catch { _data = new StoredData(); }
            lock (_cacheLock) { _priceCache = _data.Prices.ToDictionary(p => p.ItemID, p => p); }
        }

        #endregion

        #region Configuration

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
            _config = Config.ReadObject<Configuration>() ?? new Configuration();
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();
        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Initialization

        private void Init()
        {
            permission.RegisterPermission("marketxlite.admin", this);
            permission.RegisterPermission("marketxlite.use", this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            ImageLibraryReady = ImageLibrary != null && ImageLibrary.IsLoaded;
            SyncMarketItems();
            timer.Once(5f, RegisterAllIcons);
            timer.Every(3600f, SaveData);
            timer.Every(1800f, TriggerRandomEvent);
            TriggerRandomEvent(); // Initial event
        }

        private void TriggerRandomEvent()
        {
            int r = UnityEngine.Random.Range(0, 5);
            switch (r)
            {
                case 0: _currentEvent = "INDUSTRIAL BOOM"; _eventMultiplier = 1.25m; _eventColor = "#55ff55"; break;
                case 1: _currentEvent = "ECONOMIC CRASH"; _eventMultiplier = 0.75m; _eventColor = "#ff5555"; break;
                case 2: _currentEvent = "SCRAP SHORTAGE"; _eventMultiplier = 1.5m; _eventColor = "#ffaa00"; break;
                case 3: _currentEvent = "PEACEFUL ERA"; _eventMultiplier = 0.9m; _eventColor = "#aaaaff"; break;
                default: _currentEvent = "STABLE ECONOMY"; _eventMultiplier = 1.0m; _eventColor = "#00ffff"; break;
            }
            Puts($"Market Event: {_currentEvent} (x{_eventMultiplier})");
            foreach (var p in _priceCache.Values) p.UpdatePrice(_eventMultiplier);
        }

        private void SyncMarketItems()
        {
            foreach (var kvp in _config.InitialItems)
            {
                ItemDefinition def = ItemManager.FindItemDefinition(kvp.Key);
                if (def == null) continue;
                if (!_priceCache.ContainsKey(def.itemid))
                {
                    _priceCache[def.itemid] = new PriceData { ItemID = def.itemid, BasePrice = kvp.Value, CurrentPrice = kvp.Value, LastPrice = kvp.Value, Supply = 2000, Demand = 200, Category = def.category.ToString() };
                }
                else if (string.IsNullOrEmpty(_priceCache[def.itemid].Category))
                {
                    _priceCache[def.itemid].Category = def.category.ToString();
                }
            }
        }

        private void RegisterAllIcons()
        {
            if (!ImageLibraryReady) return;
            foreach (var p in _priceCache.Values)
            {
                var def = ItemManager.FindItemDefinition(p.ItemID);
                if (def != null) ImageLibrary.Call("AddImage", GetItemIconUrl(def.shortname), def.shortname, 0UL);
            }
        }

        private string GetItemIconUrl(string shortname) => _config.CustomIcons.TryGetValue(shortname, out string url) ? url : $"https://rustlabs.com/img/items180/{shortname}.png";

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

        private void Unload() { foreach (var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, "MarketLiteUI"); SaveData(); }

        #endregion

        #region Helpers

        private static string Truncate(string value, int maxChars)
        {
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }

        #endregion

        #region UI Construction

        [ChatCommand("market")]
        private void cmdMarket(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, "marketxlite.use") && !player.IsAdmin) return;
            OpenMarketUI(player);
        }

        private void OpenMarketUI(BasePlayer player)
        {
            double balance = Economics != null ? Convert.ToDouble(Economics.Call("Balance", player.UserIDString)) : 0;
            int qty = _playerQuantities.ContainsKey(player.userID) ? _playerQuantities[player.userID] : 1;
            string cat = _playerCategories.ContainsKey(player.userID) ? _playerCategories[player.userID] : "ALL";

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
                string color = cat == categories[i] ? "0.0 0.6 0.6 0.4" : "0.15 0.15 0.15 1";
                container.Add(new CuiButton { Button = { Color = color, Command = $"marketlite.cat {categories[i]}" }, Text = { Text = categories[i], FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.02 {0.83f - (i * 0.05f)}", AnchorMax = $"0.15 {0.87f - (i * 0.05f)}" } }, "MarketLiteUI");
            }

            // Qty Buttons
            int[] qtys = { 1, 10, 100, 1000 };
            for (int i = 0; i < qtys.Length; i++)
            {
                string color = qty == qtys[i] ? "0.2 0.6 0.2 0.8" : "0.15 0.15 0.15 1";
                container.Add(new CuiButton { Button = { Color = color, Command = $"marketlite.qty {qtys[i]}" }, Text = { Text = qtys[i].ToString(), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"{0.75f + (i * 0.05f)} 0.84", AnchorMax = $"{0.79f + (i * 0.05f)} 0.88" } }, "MarketLiteUI");
            }

            // Grid Logic
            var list = _priceCache.Values.Where(p => {
                if (cat == "ALL") return true;
                if (p.Category == null) return false;
                if (cat == "WEAPONS" && p.Category.Equals("Weapon", StringComparison.OrdinalIgnoreCase)) return true;
                if (cat == "RESOURCES" && p.Category.Equals("Resources", StringComparison.OrdinalIgnoreCase)) return true;
                return p.Category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0;
            }).Take(12).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i]; int r = i / 4, c = i % 4;
                float x = 0.18f + (c * 0.20f), y = 0.78f - (r * 0.26f);
                string panel = $"Item_{p.ItemID}";
                var def = ItemManager.FindItemDefinition(p.ItemID);
                
                decimal avgBuyPrice = p.GetPriceForAmount(qty, _eventMultiplier, true);
                decimal avgSellPrice = p.GetPriceForAmount(qty, _eventMultiplier, false);

                container.Add(new CuiPanel { Image = { Color = "0.12 0.12 0.12 1" }, RectTransform = { AnchorMin = $"{x} {y - 0.24f}", AnchorMax = $"{x + 0.19f} {y}" } }, "MarketLiteUI", panel);
                
                if (ImageLibraryReady && def != null)
                {
                    string png = ImageLibrary.Call("GetImage", def.shortname, 0UL) as string;
                    if (!string.IsNullOrEmpty(png)) container.Add(new CuiElement { Parent = panel, Components = { new CuiRawImageComponent { Png = png }, new CuiRectTransformComponent { AnchorMin = "0.38 0.48", AnchorMax = "0.62 0.92" } } });
                }

                container.Add(new CuiLabel { Text = { Text = Truncate(def?.displayName.english.ToUpper() ?? "UNKNOWN", 14), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0.32", AnchorMax = "1 0.48" } }, panel);
                
                string priceText = qty > 1 ? $"${avgBuyPrice:F2} | Total: ${(avgBuyPrice * qty):F0}" : $"${p.CurrentPrice:F2} {p.GetTrendArrow()}";
                container.Add(new CuiLabel { Text = { Text = priceText, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0 1 1 1" }, RectTransform = { AnchorMin = "0 0.18", AnchorMax = "1 0.32" } }, panel);
                
                container.Add(new CuiButton { Button = { Color = "0.15 0.35 0.15 0.8", Command = $"marketlite.buy {p.ItemID} {qty}" }, Text = { Text = "BUY", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.08 0.04", AnchorMax = "0.48 0.16" } }, panel);
                container.Add(new CuiButton { Button = { Color = "0.35 0.15 0.15 0.8", Command = $"marketlite.sell {p.ItemID} {qty}" }, Text = { Text = "SELL", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.52 0.04", AnchorMax = "0.92 0.16" } }, panel);
            }

            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Logic

        [ConsoleCommand("marketlite.cat")] private void cmdCat(ConsoleSystem.Arg arg) { var p = arg.Player(); if (p == null) return; _playerCategories[p.userID] = arg.Args[0].ToUpper(); OpenMarketUI(p); }
        [ConsoleCommand("marketlite.qty")] private void cmdQty(ConsoleSystem.Arg arg) { var p = arg.Player(); if (p == null) return; if (int.TryParse(arg.Args[0], out int qty)) { _playerQuantities[p.userID] = qty; OpenMarketUI(p); } }

        [ConsoleCommand("marketlite.buy")]
        private void cmdBuy(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out int id) || !int.TryParse(arg.Args[1], out int amount)) return;
            if (!_priceCache.TryGetValue(id, out var p)) return;

            decimal avgPrice = p.GetPriceForAmount(amount, _eventMultiplier, true);
            double totalCost = (double)avgPrice * amount * (double)(1m + (_config.MarketTax / 100m));
            
            if (Convert.ToDouble(Economics?.Call("Balance", player.UserIDString)) < totalCost) 
            {
                player.ChatMessage(string.Format(lang.GetMessage("NoBalance", this, player.UserIDString), totalCost));
                return;
            }

            Economics?.Call("Withdraw", player.UserIDString, totalCost);
            player.GiveItem(ItemManager.CreateByItemID(id, amount));
            lock(_cacheLock) { p.Demand += amount; p.UpdatePrice(_eventMultiplier); }
            OpenMarketUI(player);
        }

        [ConsoleCommand("marketlite.sell")]
        private void cmdSell(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out int id) || !int.TryParse(arg.Args[1], out int amount)) return;
            if (!_priceCache.TryGetValue(id, out var p)) return;

            if (player.inventory.GetAmount(id) < amount) return;
            
            decimal avgPrice = p.GetPriceForAmount(amount, _eventMultiplier, false);
            double totalEarned = (double)avgPrice * amount * (double)(1m - (_config.MarketTax / 100m));
            
            player.inventory.Take(null, id, amount);
            Economics?.Call("Deposit", player.UserIDString, totalEarned);
            lock(_cacheLock) { p.Supply += amount; p.UpdatePrice(_eventMultiplier); }
            OpenMarketUI(player);
        }

        [ChatCommand("marketlite.icon")]
        private void cmdIcon(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin")) return;
            if (args.Length < 2) return;
            _config.CustomIcons[args[0]] = args[1];
            SaveConfig();
            if (ImageLibraryReady) ImageLibrary.Call("AddImage", args[1], args[0], 0UL);
            player.ChatMessage($"Icon updated for {args[0]}.");
        }

        #endregion
    }
}
