// MarketX Lite by Samir
// A dynamic market plugin for Rust/Oxide servers

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
    [Info("MarketX Lite", "Samir", "1.3.0")]
    [Description("Basic global economy plugin with random market events.")]
    public class MarketXLite : RustPlugin
    {
        private const int CURRENT_CONFIG_VERSION = 2;

        /*
         * Copyright 2026 Samir
         */

        [PluginReference] private Plugin Economics;
        [PluginReference] private Plugin ImageLibrary;

        private bool _imageLibraryReady;
        private readonly object _cacheLock = new object();
        private Dictionary<int, PriceData> _priceCache = new Dictionary<int, PriceData>();
        private Dictionary<ulong, int> _playerQuantities = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> _playerCategories = new Dictionary<ulong, string>();

        private string _currentEvent = "STABLE ECONOMY";
        private decimal _eventMultiplier = 1.0m;
        private string _eventColor = "#00ffff";

        private System.Random _eventRandom = new System.Random();
        private List<MarketEventSpec> _eventPool = new List<MarketEventSpec>();
        private int _eventIndex = 0;

        private class MarketEventSpec
        {
            public string Name;
            public decimal Multiplier;
            public string Color;
        }

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
                LastPrice = CurrentPrice;

                
                decimal pressure = (Demand - Supply) / 5000m;
                decimal next = BasePrice * (1m + pressure) * eventMultiplier;
                next = (CurrentPrice * 0.5m) + (next * 0.5m);

                decimal minPrice = BasePrice * 0.1m;
                decimal maxPrice = BasePrice * 20m;
                if (next < minPrice)
                {
                    next = minPrice;
                }

                if (next > maxPrice)
                {
                    next = maxPrice;
                }

                CurrentPrice = next;
            }

            public decimal GetPriceForAmount(int amount, decimal eventMultiplier, bool isBuy)
            {
                if (amount <= 1) return CurrentPrice;

                decimal stackBump = amount / 1000m;
                if (stackBump > 0.25m) stackBump = 0.25m;

                decimal unit = CurrentPrice;
                if (isBuy)
                {
                    unit = unit * (1m + stackBump);
                }
                else
                {
                    unit = unit * (1m - (stackBump * 0.7m));
                }

                decimal minPrice = BasePrice * 0.1m;
                decimal maxPrice = BasePrice * 20m;
                if (unit < minPrice) unit = minPrice;
                if (unit > maxPrice) unit = maxPrice;
                return unit;
            }
        }

        private class StoredData
        {
            public List<PriceData> Prices = new List<PriceData>();
            public List<ulong> SeenPlayers = new List<ulong>();
        }

        private StoredData _data;
        private HashSet<ulong> _seenPlayers = new HashSet<ulong>();

        private void SaveData()
        {
            lock (_cacheLock)
            {
                if (_data == null)
                {
                    _data = new StoredData();
                }

                _data.Prices = _priceCache.Values.ToList();
                _data.SeenPlayers = _seenPlayers.ToList();
            }

            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private void LoadData()
        {
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

                _seenPlayers.Clear();
                for (int i = 0; i < _data.SeenPlayers.Count; i++)
                {
                    _seenPlayers.Add(_data.SeenPlayers[i]);
                }
            }
        }

        private Configuration _config;
        public class Configuration
        {
            [Newtonsoft.Json.JsonProperty("Config Version")]
            public int ConfigVersion = CURRENT_CONFIG_VERSION;

            [Newtonsoft.Json.JsonProperty("Market Tax Percentage")]
            public decimal MarketTax = 5.0m;

            [Newtonsoft.Json.JsonProperty("Event Interval Seconds")]
            public float EventIntervalSeconds = 1800f;

            [Newtonsoft.Json.JsonProperty("Save Interval Seconds")]
            public float SaveIntervalSeconds = 3600f;

            [Newtonsoft.Json.JsonProperty("Icon Register Delay Seconds")]
            public float IconRegisterDelaySeconds = 5f;

            [Newtonsoft.Json.JsonProperty("Default Starting Supply")]
            public int DefaultSupply = 2000;

            [Newtonsoft.Json.JsonProperty("Default Starting Demand")]
            public int DefaultDemand = 200;

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
            _config = ReadConfigOrDefault();
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
            permission.RegisterPermission("marketxlite.admin", this);
            permission.RegisterPermission("marketxlite.use", this);
            BuildEventPool();
            LoadData();
        }

        private void OnServerInitialized()
        {
            SetupMarket();
        }

        private void TriggerRandomEvent()
        {
            if (_eventPool.Count == 0)
            {
                BuildEventPool();
            }

            _eventIndex = (_eventIndex + 1) % _eventPool.Count;
            var chosenEvent = _eventPool[_eventIndex];
            if (chosenEvent != null)
            {
                _currentEvent = chosenEvent.Name;
                _eventMultiplier = chosenEvent.Multiplier;
                _eventColor = chosenEvent.Color;
            }

            Puts($"Market Event: {_currentEvent} (x{_eventMultiplier})");
            foreach (var itemPrice in _priceCache.Values)
            {
                itemPrice.UpdatePrice(_eventMultiplier);
            }
        }

        private void SyncMarketItems()
        {
            foreach (var configEntry in _config.InitialItems)
            {
                ItemDefinition itemDef = ItemManager.FindItemDefinition(configEntry.Key);
                if (itemDef == null) continue;

                if (!_priceCache.ContainsKey(itemDef.itemid))
                {
                    _priceCache[itemDef.itemid] = new PriceData
                    {
                        ItemID = itemDef.itemid,
                        BasePrice = configEntry.Value,
                        CurrentPrice = configEntry.Value,
                        LastPrice = configEntry.Value,
                        Supply = _config.DefaultSupply,
                        Demand = _config.DefaultDemand,
                        Category = itemDef.category.ToString()
                    };
                }
                else if (string.IsNullOrEmpty(_priceCache[itemDef.itemid].Category))
                {
                    _priceCache[itemDef.itemid].Category = itemDef.category.ToString();
                }
            }
        }

        private void RegisterAllIcons()
        {
            if (!_imageLibraryReady)
            {
                return;
            }

            foreach (var itemPrice in _priceCache.Values)
            {
                var itemDef = ItemManager.FindItemDefinition(itemPrice.ItemID);
                if (itemDef != null) ImageLibrary.Call("AddImage", GetItemIconUrl(itemDef.shortname), itemDef.shortname, 0UL);
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
            CloseUiForEveryone();
            SaveOnUnload();
        }

        private Configuration ReadConfigOrDefault()
        {
            Configuration configObject = null;
            try
            {
                configObject = Config.ReadObject<Configuration>();
            }
            catch (Exception ex)
            {
                configObject = null;
                PrintWarning($"Config read blew up, using defaults. {ex.Message}");
            }

            if (configObject == null)
            {
                configObject = new Configuration();
            }

            MigrateConfig(configObject);

            return configObject;
        }

        private void MigrateConfig(Configuration configObject)
        {
            if (configObject.ConfigVersion >= CURRENT_CONFIG_VERSION)
            {
                return;
            }

            if (configObject.EventIntervalSeconds <= 0f) configObject.EventIntervalSeconds = 1800f;
            if (configObject.SaveIntervalSeconds <= 0f) configObject.SaveIntervalSeconds = 3600f;
            if (configObject.IconRegisterDelaySeconds < 0f) configObject.IconRegisterDelaySeconds = 5f;
            if (configObject.DefaultSupply <= 0) configObject.DefaultSupply = 2000;
            if (configObject.DefaultDemand < 0) configObject.DefaultDemand = 200;

            configObject.ConfigVersion = CURRENT_CONFIG_VERSION;
            Puts($"Config migrated to v{CURRENT_CONFIG_VERSION}.");
        }

        private void SetupMarket()
        {
            _imageLibraryReady = ImageLibrary != null && ImageLibrary.IsLoaded;
            SyncMarketItems();
            timer.Once(_config.IconRegisterDelaySeconds, RegisterAllIcons);
            RunMarketTimers();
            TriggerRandomEvent();

            // HACK: quick scrub in case data has stale zero IDs from old saves.
            _seenPlayers.Remove(0UL);
        }

        private void RunMarketTimers()
        {
            timer.Every(_config.SaveIntervalSeconds, SaveData);
            timer.Every(_config.EventIntervalSeconds, TriggerRandomEvent);
        }

        private void BuildEventPool()
        {
            _eventPool.Clear();
            _eventPool.Add(new MarketEventSpec { Name = "INDUSTRIAL BOOM", Multiplier = 1.25m, Color = "#55ff55" });
            _eventPool.Add(new MarketEventSpec { Name = "ECONOMIC CRASH", Multiplier = 0.75m, Color = "#ff5555" });
            _eventPool.Add(new MarketEventSpec { Name = "SCRAP SHORTAGE", Multiplier = 1.5m, Color = "#ffaa00" });
            _eventPool.Add(new MarketEventSpec { Name = "PEACEFUL ERA", Multiplier = 0.9m, Color = "#aaaaff" });
            _eventPool.Add(new MarketEventSpec { Name = "STABLE ECONOMY", Multiplier = 1.0m, Color = "#00ffff" });
        }

        private void CloseUiForEveryone()
        {
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(activePlayer, "MarketLiteUI");
            }
        }

        private void SaveOnUnload()
        {
            SaveData();
        }

        private static string Truncate(string value, int maxChars)
        {
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }

        [ChatCommand("market")]
        private void cmdMarket(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, "marketxlite.use") && !player.IsAdmin) return;
            OpenMarketUI(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            bool usedMarketBefore = _playerQuantities.ContainsKey(player.userID) || _playerCategories.ContainsKey(player.userID);
            if (!usedMarketBefore) return;

            timer.Once(1.2f, () =>
            {
                if (player == null || !player.IsConnected) return;
                // FIXME: this always opens the ui after sleep
                OpenMarketUI(player);
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            bool isFirstJoin = false;
            lock (_cacheLock)
            {
                if (!_seenPlayers.Contains(player.userID))
                {
                    _seenPlayers.Add(player.userID);
                    isFirstJoin = true;
                }
            }

            if (!isFirstJoin) return;

            int welcomBonus = 125; // Dont ask
            if (Economics != null) Economics.Call("Deposit", player.UserIDString, welcomBonus);
            player.ChatMessage($"Welcome {player.displayName}! You got ${welcomBonus} starter cash. Type /market.");
            SaveData();
        }

        private void OpenMarketUI(BasePlayer player)
        {
            double balance = Economics != null ? Convert.ToDouble(Economics.Call("Balance", player.UserIDString)) : 0;

            int selectedAmount = _playerQuantities.ContainsKey(player.userID) ? _playerQuantities[player.userID] : 1;
            string categoryName = _playerCategories.ContainsKey(player.userID) ? _playerCategories[player.userID] : "ALL";

            CuiHelper.DestroyUi(player, "MarketLiteUI");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel { Image = { Color = "0.06 0.06 0.06 0.97" }, RectTransform = { AnchorMin = "0.095 0.095", AnchorMax = "0.905 0.905" }, CursorEnabled = true }, "Overlay", "MarketLiteUI");

            AddHeaderBar(container, balance);
            AddCategoryRail(container, categoryName);
            AddAmountRail(container, selectedAmount);
            AddMarketCards(player, container, categoryName, selectedAmount);

            CuiHelper.AddUi(player, container);
        }

        private void AddHeaderBar(CuiElementContainer container, double balance)
        {
            container.Add(new CuiPanel { Image = { Color = "0.12 0.12 0.12 1" }, RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 1" } }, "MarketLiteUI", "Header");
            container.Add(new CuiLabel { Text = { Text = "MARKETX Lite", FontSize = 20, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.025 0", AnchorMax = "0.35 1" } }, "Header");
            container.Add(new CuiLabel { Text = { Text = $"Event: <color={_eventColor}>{_currentEvent}</color>", FontSize = 12, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.33 0", AnchorMax = "0.67 1" } }, "Header");
            container.Add(new CuiLabel { Text = { Text = $"Funds: <color=#55ff55>${balance:F2}</color>", FontSize = 13, Align = TextAnchor.MiddleRight }, RectTransform = { AnchorMin = "0.58 0", AnchorMax = "0.91 1" } }, "Header");
            container.Add(new CuiButton { Button = { Color = "0.75 0.18 0.18 0.85", Command = "marketlite.close" }, Text = { Text = "X", FontSize = 15, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.965 0.17", AnchorMax = "0.993 0.83" } }, "Header");
        }

        private void AddCategoryRail(CuiElementContainer container, string categoryName)
        {
            string[] categoryNames = { "ALL", "RESOURCES", "TOOLS", "WEAPONS", "FOOD" };
            for (int categoryIndex = 0; categoryIndex < categoryNames.Length; categoryIndex++)
            {
                string categoryColor = categoryName == categoryNames[categoryIndex] ? "0.0 0.5 0.55 0.45" : "0.14 0.14 0.14 1";
                float top = 0.82f - (categoryIndex * 0.052f);
                float bottom = 0.86f - (categoryIndex * 0.052f);
                container.Add(new CuiButton { Button = { Color = categoryColor, Command = $"marketlite.cat {categoryNames[categoryIndex]}" }, Text = { Text = categoryNames[categoryIndex], FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"0.02 {top}", AnchorMax = $"0.15 {bottom}" } }, "MarketLiteUI");
            }
        }

        private void AddAmountRail(CuiElementContainer container, int selectedAmount)
        {
            int[] amountOptions = { 1, 10, 100, 1000 };
            for (int amountIndex = 0; amountIndex < amountOptions.Length; amountIndex++)
            {
                string amountColor = selectedAmount == amountOptions[amountIndex] ? "0.22 0.55 0.22 0.88" : "0.15 0.15 0.15 1";
                string left = $"{0.74f + (amountIndex * 0.052f)}";
                string right = $"{0.785f + (amountIndex * 0.052f)}";
                container.Add(new CuiButton { Button = { Color = amountColor, Command = $"marketlite.qty {amountOptions[amountIndex]}" }, Text = { Text = amountOptions[amountIndex].ToString(), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = $"{left} 0.835", AnchorMax = $"{right} 0.885" } }, "MarketLiteUI");
            }
        }

        private void AddMarketCards(BasePlayer player, CuiElementContainer container, string categoryName, int selectedAmount)
        {
            List<PriceData> visibleItems = new List<PriceData>();
            foreach (var priceEntry in _priceCache)
            {
                PriceData priceRow = priceEntry.Value;
                bool includeItem = false;

                if (categoryName == "ALL")
                {
                    includeItem = true;
                }
                else if (priceRow.Category != null)
                {
                    if (categoryName == "WEAPONS" && priceRow.Category.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
                    {
                        includeItem = true;
                    }
                    else if (categoryName == "RESOURCES" && priceRow.Category.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                    {
                        includeItem = true;
                    }
                    else if (priceRow.Category.IndexOf(categoryName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        includeItem = true;
                    }
                }

                if (includeItem)
                {
                    visibleItems.Add(priceRow);
                }

                if (visibleItems.Count >= 12)
                {
                    break;
                }
            }

            for (int itemIndex = 0; itemIndex < visibleItems.Count; itemIndex++)
            {
                var itemPrice = visibleItems[itemIndex];
                int rowIndex = itemIndex / 4;
                int columnIndex = itemIndex % 4;
                float x = 0.17f + (columnIndex * 0.205f);
                float y = 0.78f - (rowIndex * 0.255f);
                string panel = $"Item_{itemPrice.ItemID}";
                var itemDef = ItemManager.FindItemDefinition(itemPrice.ItemID);

                decimal avgBuyPrice = itemPrice.GetPriceForAmount(selectedAmount, _eventMultiplier, true);

                container.Add(new CuiPanel { Image = { Color = "0.13 0.13 0.13 1" }, RectTransform = { AnchorMin = $"{x} {y - 0.245f}", AnchorMax = $"{x + 0.192f} {y}" } }, "MarketLiteUI", panel);

                if (_imageLibraryReady && itemDef != null)
                {
                    string png = ImageLibrary.Call("GetImage", itemDef.shortname, 0UL) as string;
                    if (!string.IsNullOrEmpty(png)) container.Add(new CuiElement { Parent = panel, Components = { new CuiRawImageComponent { Png = png }, new CuiRectTransformComponent { AnchorMin = "0.36 0.46", AnchorMax = "0.64 0.91" } } });
                }

                container.Add(new CuiLabel { Text = { Text = Truncate(itemDef?.displayName.english.ToUpper() ?? "UNKNOWN", 13), FontSize = 10, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0.30", AnchorMax = "1 0.47" } }, panel);

                string priceText = string.Empty;
                if (selectedAmount > 1)
                {
                    priceText = $"${avgBuyPrice:F2} | Total: ${(avgBuyPrice * selectedAmount):F0}";
                }
                else
                {
                    priceText = $"${itemPrice.CurrentPrice:F2} {itemPrice.GetTrendArrow()}";
                }

                container.Add(new CuiLabel { Text = { Text = priceText, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0 1 1 1" }, RectTransform = { AnchorMin = "0 0.17", AnchorMax = "1 0.31" } }, panel);

                container.Add(new CuiButton { Button = { Color = "0.15 0.34 0.15 0.8", Command = $"marketlite.buy {itemPrice.ItemID} {selectedAmount}" }, Text = { Text = "BUY", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.07 0.04", AnchorMax = "0.46 0.155" } }, panel);
                container.Add(new CuiButton { Button = { Color = "0.34 0.15 0.15 0.8", Command = $"marketlite.sell {itemPrice.ItemID} {selectedAmount}" }, Text = { Text = "SELL", FontSize = 9, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.53 0.04", AnchorMax = "0.93 0.155" } }, panel);
            }

        }

        [ConsoleCommand("marketlite.cat")]
        private void cmdCat(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (arg.Args == null) return;
            if (arg.Args.Length == 0)
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
            if (player == null) return;
            if (arg.Args == null) return;
            if (arg.Args.Length == 0)
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

        // quick and dirty close handler - keeps the UI from sticking if the client-side close misses
        [ConsoleCommand("marketlite.close")]
        private void cmdCloseMarket(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            CuiHelper.DestroyUi(player, "MarketLiteUI");
        }

        [ConsoleCommand("marketlite.buy")]
        private void cmdBuy(ConsoleSystem.Arg arg)
        {
            TryProcessTransaction(arg.Player(), arg.Args, true);
        }

        [ConsoleCommand("marketlite.sell")]
        private void cmdSell(ConsoleSystem.Arg arg)
        {
            TryProcessTransaction(arg.Player(), arg.Args, false);
        }

        private void TryProcessTransaction(BasePlayer player, string[] args, bool isBuying)
        {
            if (player == null || args == null || args.Length < 2)
            {
                return;
            }

            int itemId;
            int amount;
            if (!int.TryParse(args[0], out itemId) || !int.TryParse(args[1], out amount))
            {
                return;
            }

            PriceData priceData;
            if (!_priceCache.TryGetValue(itemId, out priceData))
            {
                return;
            }

            if (isBuying)
            {
                decimal avgPrice = priceData.GetPriceForAmount(amount, _eventMultiplier, true);
                double totalCost = (double)avgPrice * amount * (double)(1m + (_config.MarketTax / 100m));
                double balance = Convert.ToDouble(Economics?.Call("Balance", player.UserIDString));
                if (balance < totalCost)
                {
                    player.ChatMessage(string.Format(lang.GetMessage("NoBalance", this, player.UserIDString), totalCost));
                    return;
                }

                Economics?.Call("Withdraw", player.UserIDString, totalCost);
                player.GiveItem(ItemManager.CreateByItemID(itemId, amount));
                lock (_cacheLock)
                {
                    priceData.Demand += amount;
                    priceData.UpdatePrice(_eventMultiplier);
                }
            }
            else
            {
                int playerAmount = player.inventory.GetAmount(itemId);
                if (playerAmount < amount)
                {
                    return;
                }

                decimal avgPrice = priceData.GetPriceForAmount(amount, _eventMultiplier, false);
                double totalEarned = (double)avgPrice * amount * (double)(1m - (_config.MarketTax / 100m));
                player.inventory.Take(null, itemId, amount);
                Economics?.Call("Deposit", player.UserIDString, totalEarned);
                lock (_cacheLock)
                {
                    priceData.Supply += amount;
                    priceData.UpdatePrice(_eventMultiplier);
                }
            }

            OpenMarketUI(player);
        }

        [ChatCommand("marketlite.icon")]
        private void cmdIcon(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin")) return;
            if (args.Length < 2) return;

            _config.CustomIcons[args[0]] = args[1];
            SaveConfig();
            if (_imageLibraryReady)
            {
                ImageLibrary.Call("AddImage", args[1], args[0], 0UL);
            }
            player.ChatMessage($"Icon updated for {args[0]}.");
        }

        [ChatCommand("marketlite.event")]
        private void cmdForceEvent(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin"))
            {
                return;
            }

            TriggerRandomEvent();
            player.ChatMessage($"Forced event: {_currentEvent}");
            foreach (var online in BasePlayer.activePlayerList)
            {
                OpenMarketUI(online);
            }
        }

        [ChatCommand("marketlite.reload")]
        private void cmdReloadMarket(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin"))
            {
                return;
            }

            LoadConfig();
            LoadData();
            BuildEventPool();
            SyncMarketItems();
            player.ChatMessage("Market config/data reloaded.");
        }

        [ChatCommand("marketlite.save")]
        private void cmdSaveNow(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "marketxlite.admin"))
            {
                return;
            }

            SaveData();
            player.ChatMessage("Market data saved.");
        }
    }
}
