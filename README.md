# MarketX Lite

**Version:** 1.2.4  
**Developer:** Samir

MarketX Lite is a lightweight but powerful global economy plugin for Rust. It features a dynamic marketplace where prices fluctuate based on real-time supply and demand, influenced by random economic events and stack-based pricing logic.

## 🚀 Features

*   **Dynamic Economy:** Prices aren't static. They shift based on player activity (buying increases demand/price, selling increases supply/price).
*   **Economic Events:** Random events trigger every 30 minutes, impacting the entire market:
    *   **Industrial Boom:** Prices increase (1.25x).
    *   **Economic Crash:** Prices drop (0.75x).
    *   **Scrap Shortage:** Dramatic price hikes (1.5x).
    *   **Peaceful Era:** Slight discounts (0.9x).
    *   **Stable Economy:** Standard pricing.
*   **Stack Pricing:** Unlike basic shops, MarketX Lite calculates the "average" price for a stack. Buying 1000 wood will cost slightly more per unit than buying 1, simulating market slippage.
*   **Modern CUI:** A clean, dark-themed user interface with:
    *   **Category Sidebar:** Filter by Resources, Weapons, Tools, and Food.
    *   **Quick Quantity Selector:** Switch between 1, 10, 100, and 1000 unit views.
    *   **Trend Indicators:** Visual arrows (▲/▼) showing if a price has risen or fallen since the last update.
*   **Icon Support:** Automatically pulls item icons from RustLabs, with support for `ImageLibrary` and custom URL overrides.

## 📦 Dependencies

*   [Economics](https://umod.org/plugins/economics) (Required)
*   [ImageLibrary](https://umod.org/plugins/image-library) (Optional, for improved icon performance)

## 🛠️ Installation

1.  Download `MarketXLite.cs`.
2.  Place it in your server's `oxide/plugins` folder.
3.  Ensure `Economics` is also installed.
4.  Configure your starting items and base prices in `oxide/config/MarketXLite.json`.

## ⌨️ Commands

### Chat Commands
*   `/market` - Opens the main Market UI.
*   `/marketlite.icon <shortname> <url>` - (Admin) Set a custom icon for a specific item.

### Console Commands
*   `marketlite.buy <itemid> <amount>` - Internal UI command for purchasing.
*   `marketlite.sell <itemid> <amount>` - Internal UI command for selling.

## 🔐 Permissions

*   `marketxlite.use` - Allows players to open the market and trade.
*   `marketxlite.admin` - Allows usage of admin configuration commands.

## ⚙️ Configuration

```json
{
  "Market Tax Percentage": 5.0,
  "Initial Market Items (Shortname:BasePrice)": {
    "wood": 1.0,
    "stone": 2.0,
    "metal.fragments": 5.0,
    "scrap": 50.0,
    "rifle.ak": 500.0,
    "pickaxe.metal": 50.0,
    "lowgradefuel": 2.0
  },
  "Custom Icon Overrides (Shortname:URL)": {}
}
```

## 📄 License

This project is licensed under the Apache License 2.0 - see the file for details.
