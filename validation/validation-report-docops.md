# Validation Report (D2)

- Status: PASS
- Output directory: `extract-mod/StandaloneExtractor/Output-docops`

## Contract

- Shimmer validation is typed, not total-only:
  - `item_transform` count must be `200-350` (historically around ~260)
  - `deconstruct` count must be `3800-4600` (baseline `4227` from C2, bounded tolerance for drift)

## Counts

- items: 5455
- recipes: 2940
- shimmer: 4471
- npc shops: 25
- shimmer type breakdown: `item_transform=244`, `deconstruct=4227`

## Check Results

### 1) Required files exist
- Result: PASS
- Evidence: all required files are present (`items.json`, `items.csv`, `recipes.json`, `recipes.csv`, `shimmer.json`, `shimmer.csv`, `npc_shops.json`, `npc_shops.csv`)
- Failing records: none

### 2) Count ranges
- Result: PASS
- Items range check (`5300-5600`): PASS (actual `5455`)
- Recipes range check (`2800-3100`): PASS (actual `2940`)
- Shimmer item_transform range check (`200-350`): PASS (actual `244`)
- Shimmer deconstruct range check (`3800-4600`): PASS (actual `4227`)
- NPC shops minimum (`>=25`): PASS (actual `25`)
- Failing records: none

### 3) Foreign-key integrity (referenced item IDs must exist)
- Result: PASS
- Datasets checked: `recipes`, `shimmer`, `npc_shops`
- Valid item ID set size: `5455`
- Invalid references found: `0`
- Failing records: none

### 4) Spot checks
- Result: PASS
- Torch recipe check: PASS (`RecipeIndex=0`, `ResultItemId=8`, ingredients include `9` and `23`)
- Merchant sells Torch check: PASS (`NpcId=17`, `ShopName="Shop 1"`, `ItemId=8`, `BuyPrice=50`)
- Known shimmer transform check: PASS (`InputItemId=8 -> OutputItemId=5353`, `Type=item_transform`)
- Failing records: none

## Notes

- This validator intentionally does not use a total shimmer count gate.
- PASS/FAIL comes from documented typed shimmer expectations plus required integrity and spot checks.
