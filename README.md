# Terraria Data Extraction Guide

This tool reads your local Terraria installation and produces structured JSON and CSV files
containing item data, crafting recipes, shimmer transforms, NPC shop inventories, and sprite metadata.
You can use the output files for wikis, modding references, data analysis, or any project
that needs machine-readable Terraria game data.

---

## Table of Contents

1. [What It Does](#1-what-it-does)
2. [Requirements](#2-requirements)
3. [Quick Start](#3-quick-start)
4. [Script Usage](#4-script-usage)
5. [Output Reference](#5-output-reference)
6. [Validating Your Output](#6-validating-your-output)
7. [Troubleshooting](#7-troubleshooting)
8. [FAQ](#8-faq)

---

## 1. What It Does

The extractor loads Terraria's game assemblies at runtime and pulls five datasets directly
from the game's own data structures — no manual parsing, no web scraping.

| Dataset | What it contains |
|---|---|
| **Items** | Every item in the game: numeric ID, display name, internal name, base value, and sell price |
| **Recipes** | Every crafting recipe: result item, required ingredients (with quantities), crafting stations, and conditions |
| **Shimmer transforms** | What each item becomes when dropped in Shimmer liquid (`item_transform` type), plus deconstruction yields (`deconstruct` type) |
| **NPC shops** | Every NPC merchant's shop inventory: NPC identity, shop name, items sold, buy prices, and any unlock conditions |
| **Sprites** | Sprite manifest metadata (`id`, `category`, `internalName`, `spriteFile`, `width`, `height`) plus extracted PNGs under `sprites/items/` and `sprites/npcs/` |

Each dataset is written to both a JSON file and a CSV file, giving you 10 primary output files,
plus thousands of sprite PNG files in subdirectories.

The sprite phase is optimized for throughput: it processes image assets in parallel and writes PNGs
with a lightweight built-in encoder (instead of `System.Drawing`/GDI+).

---

## 2. Requirements

| Requirement | Details |
|---|---|
| **Operating System** | Windows only. The extractor targets x86 .NET Framework and loads Terraria's Windows assemblies directly. |
| **Terraria** | Steam edition, installed locally. The default path is `C:\Program Files (x86)\Steam\steamapps\common\Terraria`. A non-default install location works fine — see [Quick Start](#3-quick-start). |
| **.NET SDK** | The project targets **.NET Framework 4.8** (`net48`). Install the [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) and any version of the .NET SDK (6, 7, 8, or 9) that supports building `net48` projects with `dotnet build`. |
| **Python 3.x** | Required only if you want to run the optional validation step. Any Python 3.6+ install works. |

> **Note:** Terraria must be a complete Steam installation. The extractor loads game DLLs
> from the Terraria directory at runtime. A stripped or partial install will cause dependency
> errors.

---

## 3. Quick Start

These steps take you from a fresh clone to a full set of output files.

**Step 1 — Clone the repository**

```
git clone <repo-url>
cd <repo-name>
```

**Step 2 — Build the extractor**

```
dotnet build StandaloneExtractor/StandaloneExtractor.csproj
```

Expected output (last few lines):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Step 3 — Run the extraction**

If Terraria is installed in the default Steam location:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- --terraria "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe"
```

If Terraria is installed somewhere else, adjust the path:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- --terraria "D:\Games\Terraria\Terraria.exe"
```

**Step 4 — Confirm it worked**

The extractor prints a summary when it finishes. A clean run looks like this:

```
========== Extraction Summary ==========
Phase items: PASS | rows=5455 | json=items.json | csv=items.csv | elapsed=2.31s
Phase shimmer: PASS | rows=4471 | json=shimmer.json | csv=shimmer.csv | elapsed=1.87s
Phase recipes: PASS | rows=2940 | json=recipes.json | csv=recipes.csv | elapsed=3.12s
Phase npc_shops: PASS | rows=25 | json=npc_shops.json | csv=npc_shops.csv | elapsed=0.94s
Phase sprites: PASS | rows=6102 | json=sprite_manifest.json | csv=sprite_manifest.csv | elapsed=9.90s
----------------------------------------
Output directory: ...\StandaloneExtractor\Output
Phases passed: 5/5, failed: 0
========================================
```

Your output files are in `StandaloneExtractor/Output/`.

> **Tip:** You may see `3-dependency-probe FAIL` messages in the bootstrap log even on a
> successful run. These are non-fatal warnings about XNA framework assemblies that cannot be
> fully probed in reflection-only mode. If all five phases show `PASS` in the summary,
> extraction succeeded. See [Troubleshooting](#7-troubleshooting) for details.

**Step 5 — (Optional) Validate the output**

```
python validation/run_validation.py ^
  --output-dir StandaloneExtractor/Output ^
  --json-out validation/validation-report.json ^
  --md-out validation/validation-report.md
```

A passing run ends with:

```
[validation] status=PASS
```

---

## 4. Script Usage

A PowerShell helper script at `scripts/run-extraction.ps1` automates the
build, run, and optional validation steps behind a single command.

### Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-TerrariaExe` | **Yes** | — | Full path to `Terraria.exe` |
| `-OutputDir` | No | `StandaloneExtractor/Output` | Where to write the output files (`*.json`, `*.csv`, and `sprites/`) |
| `-Validate` | No (switch) | off | Run validation after extraction |
| `-ValidateAfterExtraction` | No (switch) | off | Alias for `-Validate`; runs validation after extraction |
| `-ValidationJsonOut` | No | `validation/validation-report.json` | Path for the machine-readable validation report |
| `-ValidationMdOut` | No | `validation/validation-report.md` | Path for the human-readable validation report |

### Usage Examples

**Basic run (default output directory):**

```powershell
.\scripts\run-extraction.ps1 -TerrariaExe "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe"
```

**Custom output directory:**

```powershell
.\scripts\run-extraction.ps1 `
  -TerrariaExe "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" `
  -OutputDir "C:\MyData\terraria-extract"
```

**With validation:**

```powershell
.\scripts\run-extraction.ps1 `
  -TerrariaExe "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" `
  -Validate
```

**With validation (explicit option name):**

```powershell
.\scripts\run-extraction.ps1 `
  -TerrariaExe "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" `
  -ValidateAfterExtraction
```

**Custom validation report paths:**

```powershell
.\scripts\run-extraction.ps1 `
  -TerrariaExe "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" `
  -Validate `
  -ValidationJsonOut "C:\MyData\report.json" `
  -ValidationMdOut "C:\MyData\report.md"
```

### Exit Codes

| Code | Meaning |
|---|---|
| `0` | All phases passed (and validation passed, if `-Validate` or `-ValidateAfterExtraction` was used) |
| `1` | Build failed, extraction failed, or validation reported FAIL |

---

## 5. Output Reference

### Where files are written

By default, all output files go to:

```
StandaloneExtractor/Output/
```

You can override this with `--output <path>` (manual run) or `-OutputDir` (script).

### File listing

| File | Description | Typical row count |
|---|---|---|
| `items.json` | All items as a JSON array | ~5,455 |
| `items.csv` | Same data, CSV format | ~5,455 |
| `recipes.json` | All crafting recipes as a JSON array | ~2,940 |
| `recipes.csv` | Same data, CSV format | ~2,940 |
| `shimmer.json` | All shimmer mappings as a JSON array | ~4,471 |
| `shimmer.csv` | Same data, CSV format | ~4,471 |
| `npc_shops.json` | All NPC shops as a JSON array | ~25 |
| `npc_shops.csv` | Same data, CSV format | ~25 |
| `sprite_manifest.json` | Sprite index for all extracted item/NPC sprites | ~6,102 |
| `sprite_manifest.csv` | Same sprite index, CSV format | ~6,102 |

Sprite PNG files are also written under:

- `sprites/items/Item_<id>.png`
- `sprites/npcs/NPC_<id>.png`

Row counts reflect Terraria 1.4.x. They may vary slightly between game versions.

### File format notes

#### `items.json` / `items.csv`

Each record represents one item.

JSON fields:

```json
{
  "Id": 8,
  "InternalName": "Torch",
  "Name": "Torch",
  "Value": 50,
  "SellPrice": 10
}
```

CSV columns: `Id`, `Name`, `InternalName`, `Value`, `SellPrice`

`Value` is the item's base coin value. `SellPrice` is `Value / 5` (what you get selling it to an NPC).

---

#### `recipes.json` / `recipes.csv`

Each record represents one crafting recipe.

JSON fields:

```json
{
  "RecipeIndex": 0,
  "ResultItemId": 8,
  "ResultItemName": "ItemName.Torch",
  "ResultAmount": 3,
  "Ingredients": [
    { "ItemId": 23, "Name": "ItemName.Gel", "Count": 1 },
    { "ItemId": 9,  "Name": "ItemName.Wood", "Count": 1 }
  ],
  "CraftingStations": [],
  "Conditions": ["AnyWood"]
}
```

CSV columns: `RecipeIndex`, `ResultItemId`, `ResultItemName`, `ResultAmount`, `Ingredients`, `CraftingStations`, `Conditions`

In the CSV, `Ingredients` is a semicolon-separated list in the format `ItemId:Name x Count`.
An empty `CraftingStations` list means the recipe can be crafted by hand anywhere.

---

#### `shimmer.json` / `shimmer.csv`

Each record is one shimmer mapping — either a direct item transform or one component of a
deconstruction yield.

JSON fields:

```json
{ "InputItemId": 8, "InputItemName": "ItemName.Torch",
  "OutputItemId": 5353, "OutputItemName": "ItemName.Glowstick",
  "OutputAmount": 1, "Type": "item_transform" }
```

`Type` is one of:

- `item_transform` — the item directly becomes the output item when submerged in Shimmer
- `deconstruct` — the item breaks down and yields this component (one row per component)

CSV columns: `InputItemId`, `InputItemName`, `OutputItemId`, `OutputItemName`, `OutputAmount`, `Type`

---

#### `npc_shops.json` / `npc_shops.csv`

Each record represents one NPC's shop.

JSON fields:

```json
{
  "NpcId": 17,
  "NpcName": "Merchant",
  "ShopName": "Shop 1",
  "Items": [
    { "ItemId": 8, "Name": "Torch", "BuyPrice": 50, "Conditions": [] }
  ]
}
```

CSV columns: `NpcId`, `NpcName`, `ShopName`, `Items`

In the CSV, `Items` is a semicolon-separated list in the format `ItemId|Name|BuyPrice|Conditions`.
`BuyPrice` is in copper coins (100 copper = 1 silver, 100 silver = 1 gold).

---

#### `sprite_manifest.json` / `sprite_manifest.csv`

Each record represents one extracted sprite image and where its PNG was written.

JSON fields:

```json
{
  "id": 8,
  "category": "item",
  "internalName": "Torch",
  "spriteFile": "sprites/items/Item_8.png",
  "width": 14,
  "height": 16
}
```

CSV columns: `Id`, `Category`, `InternalName`, `SpriteFile`, `Width`, `Height`

`category` is either `item` or `npc`.

---

## 6. Validating Your Output

After extraction, run the validator to confirm the output files are complete and internally consistent.

### Command

```
python validation/run_validation.py ^
  --output-dir StandaloneExtractor/Output ^
  --json-out validation/validation-report.json ^
  --md-out validation/validation-report.md
```

Replace `StandaloneExtractor/Output` with your actual output directory if you
used a custom path.

### What a passing run looks like

```
[validation] starting validation: output_dir=StandaloneExtractor\Output
[validation] wrote json report: validation\validation-report.json
[validation] wrote markdown report: validation\validation-report.md
[validation] status=PASS
```

The script exits with code `0` on PASS and `1` on FAIL.

### What the validator checks

**1. Required files exist**
Confirms that all 10 expected output files (`items.json`, `items.csv`, `recipes.json`,
`recipes.csv`, `shimmer.json`, `shimmer.csv`, `npc_shops.json`, `npc_shops.csv`,
`sprite_manifest.json`, `sprite_manifest.csv`) are present in the output directory.

**2. Row count ranges**
Each dataset must fall within a documented range that reflects known Terraria 1.4.x content:

| Dataset | Expected range |
|---|---|
| Items | 5,300 – 5,600 |
| Recipes | 2,800 – 3,100 |
| Shimmer (item_transform type) | 200 – 350 |
| Shimmer (deconstruct type) | 3,800 – 4,600 |
| NPC shops | 25 or more |
| Sprite manifest rows | 5,500 – 7,000 |
| Sprite item rows | 5,000 or more |
| Sprite NPC rows | 600 or more |

**3. Foreign-key integrity**
Every item ID referenced in recipes, shimmer mappings, NPC shops, and item sprite manifest rows must exist in the
items dataset. Zero invalid references = pass.

**4. Sprite manifest integrity**
Every sprite manifest row must have valid shape and references:
- `id` is a non-negative integer
- `category` is `item` or `npc`
- `width` / `height` are positive integers
- `spriteFile` path matches the category directory and points to an existing PNG file

**5. Spot checks**
Three specific known-good records must be present:
- The Torch crafting recipe (result: Torch, ingredients: Gel + Wood)
- The Merchant's shop must contain Torch for sale
- The shimmer transform `Torch → Glowstick` (`item_transform` type) must exist

If any check fails, the report lists the exact failing records so you know what to investigate.

---

## 7. Troubleshooting

### "I see `3-dependency-probe FAIL` in the output"

This is almost always a **non-fatal warning**, not an error. It appears because Terraria
references XNA Framework assemblies (`Microsoft.Xna.Framework.*`) that cannot be fully
resolved in the reflection-only probe mode the extractor uses at startup.

If all five phases show `PASS` in the final summary, the extraction completed successfully
and you can ignore these messages.

If the probe warning is accompanied by a phase `FAIL`, see the next sections.

---

### "Terraria executable was not found" / `2-terraria-exe FAIL`

The extractor could not find `Terraria.exe` at the path you provided (or at the default
Steam location).

**Fix:** Pass the explicit path using `--terraria`:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- ^
  --terraria "D:\Games\Terraria\Terraria.exe"
```

Or use `--terraria-dir` if you only know the folder:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- ^
  --terraria-dir "D:\Games\Terraria"
```

Also check that your Terraria installation is complete and not corrupted. In Steam:
right-click Terraria → Properties → Local Files → Verify integrity of game files.

---

### "One dataset is empty" / a phase shows `rows=0`

A phase extracted zero rows, which usually means the Terraria assemblies didn't initialize
correctly for that phase.

Things to check:

1. Make sure `--terraria` points to the correct `Terraria.exe` file, not just the game's root folder.
2. Verify game files in Steam (see above).
3. Look at the per-phase log files in:
   ```
   StandaloneExtractor/Output/_runtime/phase-results/<phase>.json
   ```
   Each file contains a `Succeeded` flag and any `Error` messages from that phase.

---

### "`BadImageFormatException`"

This means there is an architecture mismatch. The extractor is built as an **x86** executable
and must load x86 versions of Terraria's assemblies.

**Fix:** Run the pre-built x86 binary directly instead of using `dotnet run`:

```
StandaloneExtractor\bin\Debug\net48\StandaloneExtractor.exe ^
  --terraria "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe"
```

Do not use the 64-bit version of the .NET runtime to launch the extractor explicitly.
`dotnet run` respects the `PlatformTarget=x86` project setting, but some environments
(e.g. certain CI configurations) may override this.

---

### Where to look for detailed phase logs

Each extraction phase runs in an isolated worker process and writes a result file:

```
StandaloneExtractor/Output/_runtime/phase-results/
  items.json
  shimmer.json
  recipes.json
  npc_shops.json
  sprites.json
```

Each file contains:
- `Succeeded` — true/false
- `RowCount` — number of rows extracted
- `ErrorCount` — number of errors logged
- `Error0`, `Error1`, ... — the error messages themselves

These files are the first place to look when a phase fails.

---

## 8. FAQ

**Can I run this without Steam / without Terraria installed?**

No. The extractor loads game logic directly from `Terraria.exe` and its bundled DLLs at
runtime. There is no bundled game data and no offline mode. You need a complete, working
Terraria installation.

---

**Can I point it at a Terraria install that's not in the default Steam location?**

Yes. Pass `--terraria <path>` or `-t <path>` with the full path to `Terraria.exe`:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- ^
  --terraria "E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe"
```

---

**Can I re-run it safely? Will it overwrite my previous output?**

Yes, re-running is safe. Each run writes the output files fresh. If you want to keep
previous output, copy it to a different folder before re-running, or use `--output` to
specify a different output directory:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- ^
  --terraria "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" ^
  --output "StandaloneExtractor/Output-v2"
```

---

**What Terraria version is supported?**

The extractor was built and validated against **Terraria 1.4.x** (the 1.4 "Journey's End"
era, including 1.4.4 "Labor of Love"). It should work on any 1.4.x patch.

It has not been tested on Terraria 1.3 or earlier, and is unlikely to work on those
versions because the shimmer system and many item IDs did not exist then.

---

**How do I customize the output directory?**

Pass `--output <path>` or `-o <path>` to the extractor:

```
dotnet run --project StandaloneExtractor/StandaloneExtractor.csproj -- ^
  --terraria "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe" ^
  --output "C:\MyProjects\terraria-data"
```

The directory will be created if it does not exist. All primary JSON/CSV outputs plus
the `sprites/` PNG subdirectories will be placed there.
Phase log files go into `<OutputDir>/_runtime/phase-results/`.
