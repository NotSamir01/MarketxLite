# MarketX Lite

**Version:** 1.2.4  
**Developer:** Samir

MarketX Lite brings a living, breathing economy to your Rust server — prices move with player activity, economic events, and bulk purchases.

## 🚀 Features

*   **Dynamic Economy:** Prices shift based on real player behavior. Buying increases demand and price. Selling increases supply and lowers price.
*   **Economic Events:** Every 30 minutes, a random event impacts the entire market:
    *   **Industrial Boom:** +25% prices
    *   **Economic Crash:** -25% prices
    *   **Scrap Shortage:** +50% prices
    *   **Peaceful Era:** -10% prices
    *   **Stable Economy:** Standard pricing
*   **Stack Pricing (Slippage):** Bulk purchases cost more per unit. Buying 1000 wood costs slightly more than buying 1 — prevents market exploits naturally.
*   **Modern UI:** Dark-themed interface with category filters (Resources, Weapons, Tools, Food), quantity selector (1, 10, 100, 1000), and price trend arrows (▲/▼).
*   **Icon Support:** Auto-pulls images from RustLabs. Optional ImageLibrary integration for better performance. Custom URL overrides supported.

## 📦 Dependencies

*   [Economics](https://umod.org/plugins/economics) (Required)
*   [ImageLibrary](https://umod.org/plugins/image-library) (Optional, for faster icons)

## 🛠️ Installation

1.  Download `MarketXLite.cs`.
2.  Place it in `oxide/plugins`.
3.  Install Economics if not already present.
4.  Configure starting items and prices in `oxide/config/MarketXLite.json`.

## ⌨️ Commands

**Players:**
*   `/market` — Opens the market UI.

**Admins:**
*   `/marketlite.icon <shortname> <url>` — Set custom icon for any item.

## 🔐 Permissions

*   `marketxlite.use` — Can open market and trade.
*   `marketxlite.admin` — Can use admin commands.

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
