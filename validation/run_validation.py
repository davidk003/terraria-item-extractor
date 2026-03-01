#!/usr/bin/env python3
"""Deterministic validation entrypoint for extractor outputs."""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any


REQUIRED_FILES = (
    "items.json",
    "items.csv",
    "recipes.json",
    "recipes.csv",
    "shimmer.json",
    "shimmer.csv",
    "npc_shops.json",
    "npc_shops.csv",
    "sprite_manifest.json",
    "sprite_manifest.csv",
)

ITEMS_MIN = 5300
ITEMS_MAX = 5600
RECIPES_MIN = 2800
RECIPES_MAX = 3100
NPC_SHOPS_MIN = 25
ITEM_TRANSFORM_MIN = 200
ITEM_TRANSFORM_MAX = 350

# Deconstruct count was observed near 4227 in a reference baseline run.
# Keep a bounded, explicit envelope around this known level so validation
# catches real regressions while allowing minor drift from runtime ordering
# or patch-level content changes.
DECONSTRUCT_MIN = 3800
DECONSTRUCT_MAX = 4600

SPRITE_MANIFEST_MIN = 5500
SPRITE_MANIFEST_MAX = 7000
SPRITE_ITEM_MIN = 5000
SPRITE_NPC_MIN = 600


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate extractor output datasets.")
    parser.add_argument("--output-dir", required=True, help="Directory containing output JSON/CSV files")
    parser.add_argument("--json-out", required=True, help="Path for machine-readable report")
    parser.add_argument("--md-out", required=True, help="Path for markdown report")
    return parser.parse_args()


def log(message: str) -> None:
    print(f"[validation] {message}")


def load_json_array(path: Path) -> tuple[list[dict[str, Any]], str | None]:
    if not path.exists():
        return [], f"missing file: {path.name}"
    try:
        with path.open("r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except Exception as exc:  # pragma: no cover - defensive logging path
        return [], f"invalid json in {path.name}: {exc}"
    if not isinstance(payload, list):
        return [], f"invalid json shape in {path.name}: expected array"
    normalized: list[dict[str, Any]] = []
    for record in payload:
        if isinstance(record, dict):
            normalized.append(record)
        else:
            normalized.append({"_raw": record})
    return normalized, None


def check_status(pass_check: bool, warning: bool = False) -> str:
    if not pass_check:
        return "FAIL"
    if warning:
        return "WARN"
    return "PASS"


def collect_foreign_key_failures(
    items: list[dict[str, Any]],
    recipes: list[dict[str, Any]],
    shimmer: list[dict[str, Any]],
    npc_shops: list[dict[str, Any]],
    sprite_manifest: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    valid_item_ids = {item.get("Id") for item in items if isinstance(item.get("Id"), int)}
    failures: list[dict[str, Any]] = []

    for index, recipe in enumerate(recipes):
        recipe_id = recipe.get("RecipeIndex", index)

        result_item_id = recipe.get("ResultItemId")
        if result_item_id not in valid_item_ids:
            failures.append(
                {
                    "dataset": "recipes",
                    "recordId": recipe_id,
                    "field": "ResultItemId",
                    "itemId": result_item_id,
                }
            )

        for ingredient_index, ingredient in enumerate(recipe.get("Ingredients", [])):
            if not isinstance(ingredient, dict):
                failures.append(
                    {
                        "dataset": "recipes",
                        "recordId": recipe_id,
                        "field": "Ingredients[]",
                        "itemId": None,
                        "ingredientIndex": ingredient_index,
                    }
                )
                continue

            ingredient_id = ingredient.get("ItemId")
            if ingredient_id not in valid_item_ids:
                failures.append(
                    {
                        "dataset": "recipes",
                        "recordId": recipe_id,
                        "field": "Ingredients[].ItemId",
                        "itemId": ingredient_id,
                        "ingredientIndex": ingredient_index,
                    }
                )

    for index, entry in enumerate(shimmer):
        shimmer_id = {
            "index": index,
            "input": entry.get("InputItemId"),
            "output": entry.get("OutputItemId"),
            "type": entry.get("Type"),
        }

        input_item_id = entry.get("InputItemId")
        output_item_id = entry.get("OutputItemId")

        if input_item_id not in valid_item_ids:
            failures.append(
                {
                    "dataset": "shimmer",
                    "recordId": shimmer_id,
                    "field": "InputItemId",
                    "itemId": input_item_id,
                }
            )
        if output_item_id not in valid_item_ids:
            failures.append(
                {
                    "dataset": "shimmer",
                    "recordId": shimmer_id,
                    "field": "OutputItemId",
                    "itemId": output_item_id,
                }
            )

    for shop_index, shop in enumerate(npc_shops):
        shop_id = {
            "index": shop_index,
            "npcId": shop.get("NpcId"),
            "shopName": shop.get("ShopName"),
        }
        for item_index, item in enumerate(shop.get("Items", [])):
            if not isinstance(item, dict):
                failures.append(
                    {
                        "dataset": "npc_shops",
                        "recordId": shop_id,
                        "field": "Items[]",
                        "itemId": None,
                        "itemIndex": item_index,
                    }
                )
                continue

            item_id = item.get("ItemId")
            if item_id not in valid_item_ids:
                failures.append(
                    {
                        "dataset": "npc_shops",
                        "recordId": shop_id,
                        "field": "Items[].ItemId",
                        "itemId": item_id,
                        "itemIndex": item_index,
                    }
                )

    for sprite_index, row in enumerate(sprite_manifest):
        if not isinstance(row, dict):
            failures.append(
                {
                    "dataset": "sprite_manifest",
                    "recordId": sprite_index,
                    "field": "row",
                    "itemId": None,
                }
            )
            continue

        category = str(row.get("category", "")).strip().lower()
        if category != "item":
            continue

        item_id = row.get("id")
        if isinstance(item_id, int) and item_id > 0 and item_id not in valid_item_ids:
            failures.append(
                {
                    "dataset": "sprite_manifest",
                    "recordId": sprite_index,
                    "field": "id",
                    "itemId": item_id,
                }
            )

    return failures


def collect_sprite_manifest_integrity_failures(
    output_dir: Path,
    sprite_manifest: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    failures: list[dict[str, Any]] = []

    for index, row in enumerate(sprite_manifest):
        if not isinstance(row, dict):
            failures.append(
                {
                    "recordIndex": index,
                    "field": "row",
                    "error": "record is not an object",
                }
            )
            continue

        category = str(row.get("category", "")).strip().lower()
        sprite_file = row.get("spriteFile")
        width = row.get("width")
        height = row.get("height")
        row_id = row.get("id")

        if not isinstance(row_id, int) or row_id < 0:
            failures.append(
                {
                    "recordIndex": index,
                    "field": "id",
                    "error": "id must be a non-negative integer",
                    "value": row_id,
                }
            )

        if category not in {"item", "npc"}:
            failures.append(
                {
                    "recordIndex": index,
                    "field": "category",
                    "error": "category must be 'item' or 'npc'",
                    "value": row.get("category"),
                }
            )

        if not isinstance(width, int) or width <= 0:
            failures.append(
                {
                    "recordIndex": index,
                    "field": "width",
                    "error": "width must be a positive integer",
                    "value": width,
                }
            )

        if not isinstance(height, int) or height <= 0:
            failures.append(
                {
                    "recordIndex": index,
                    "field": "height",
                    "error": "height must be a positive integer",
                    "value": height,
                }
            )

        if not isinstance(sprite_file, str) or not sprite_file.strip():
            failures.append(
                {
                    "recordIndex": index,
                    "field": "spriteFile",
                    "error": "spriteFile must be a non-empty string",
                    "value": sprite_file,
                }
            )
            continue

        normalized_path = sprite_file.replace("\\", "/").lstrip("/")
        expected_prefix = "sprites/items/" if category == "item" else "sprites/npcs/" if category == "npc" else None
        if expected_prefix is not None and not normalized_path.startswith(expected_prefix):
            failures.append(
                {
                    "recordIndex": index,
                    "field": "spriteFile",
                    "error": f"spriteFile must start with '{expected_prefix}' for category '{category}'",
                    "value": sprite_file,
                }
            )

        resolved_file = output_dir.joinpath(*[part for part in normalized_path.split("/") if part])
        if not resolved_file.exists():
            failures.append(
                {
                    "recordIndex": index,
                    "field": "spriteFile",
                    "error": "referenced sprite PNG is missing",
                    "value": sprite_file,
                }
            )

    return failures


def run_spot_checks(
    recipes: list[dict[str, Any]],
    shimmer: list[dict[str, Any]],
    npc_shops: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    checks: list[dict[str, Any]] = []
    failing_records: list[dict[str, Any]] = []

    torch_recipe = None
    for recipe in recipes:
        if recipe.get("ResultItemId") != 8:
            continue
        ingredient_ids = {
            ingredient.get("ItemId")
            for ingredient in recipe.get("Ingredients", [])
            if isinstance(ingredient, dict)
        }
        if {9, 23}.issubset(ingredient_ids):
            torch_recipe = {
                "RecipeIndex": recipe.get("RecipeIndex"),
                "ResultItemId": recipe.get("ResultItemId"),
                "IngredientItemIds": sorted(x for x in ingredient_ids if isinstance(x, int)),
            }
            break

    torch_check_pass = torch_recipe is not None
    torch_check_failure = [] if torch_check_pass else [{"check": "torch_recipe_exists", "required": {"ResultItemId": 8, "IngredientItemIdsContains": [9, 23]}}]
    checks.append(
        {
            "check": "torch_recipe_exists",
            "pass": torch_check_pass,
            "evidence": torch_recipe,
            "failingRecords": torch_check_failure,
        }
    )
    failing_records.extend(torch_check_failure)

    merchant_torch = None
    for shop in npc_shops:
        if shop.get("NpcId") != 17:
            continue
        for item in shop.get("Items", []):
            if isinstance(item, dict) and item.get("ItemId") == 8:
                merchant_torch = {
                    "NpcId": shop.get("NpcId"),
                    "ShopName": shop.get("ShopName"),
                    "ItemId": item.get("ItemId"),
                    "BuyPrice": item.get("BuyPrice"),
                }
                break
        if merchant_torch is not None:
            break

    merchant_check_pass = merchant_torch is not None
    merchant_check_failure = [] if merchant_check_pass else [{"check": "merchant_sells_torch", "required": {"NpcId": 17, "ItemId": 8}}]
    checks.append(
        {
            "check": "merchant_sells_torch",
            "pass": merchant_check_pass,
            "evidence": merchant_torch,
            "failingRecords": merchant_check_failure,
        }
    )
    failing_records.extend(merchant_check_failure)

    known_transform = None
    for entry in shimmer:
        if (
            entry.get("InputItemId") == 8
            and entry.get("OutputItemId") == 5353
            and entry.get("Type") == "item_transform"
        ):
            known_transform = {
                "InputItemId": entry.get("InputItemId"),
                "OutputItemId": entry.get("OutputItemId"),
                "Type": entry.get("Type"),
            }
            break

    known_transform_pass = known_transform is not None
    known_transform_failure = [] if known_transform_pass else [{"check": "known_shimmer_transform", "required": {"InputItemId": 8, "OutputItemId": 5353, "Type": "item_transform"}}]
    checks.append(
        {
            "check": "known_shimmer_transform",
            "pass": known_transform_pass,
            "evidence": known_transform,
            "failingRecords": known_transform_failure,
        }
    )
    failing_records.extend(known_transform_failure)

    return checks, failing_records


def build_markdown_report(report: dict[str, Any]) -> str:
    checks = report["checks"]
    counts = report["counts"]

    def pass_fail(value: bool) -> str:
        return "PASS" if value else "FAIL"

    def pass_warn_fail(detail: dict[str, Any]) -> str:
        return check_status(bool(detail.get("pass", False)), bool(detail.get("warning", False)))

    count_range_details = checks["count_ranges"]["details"]
    detail_by_name = {entry["check"]: entry for entry in count_range_details}

    lines: list[str] = [
        "# Validation Report",
        "",
        f"- Status: {report['status']}",
        f"- Output directory: `{report['outputDirectory']}`",
        "",
        "## Validation Rules",
        "",
        "- Shimmer validation is typed, not total-only:",
        f"  - `item_transform` count must be at least `{ITEM_TRANSFORM_MIN}`; above `{ITEM_TRANSFORM_MAX}` is a warning (historically around ~260)",
        f"  - `deconstruct` count must be at least `{DECONSTRUCT_MIN}`; above `{DECONSTRUCT_MAX}` is a warning (baseline around `4227`, bounded tolerance for drift)",
        f"- Sprite manifest count must be at least `{SPRITE_MANIFEST_MIN}`; above `{SPRITE_MANIFEST_MAX}` is a warning.",
        f"- Sprite category minimums: `item >= {SPRITE_ITEM_MIN}`, `npc >= {SPRITE_NPC_MIN}`.",
        "- All dataset count checks use hard minimums and soft upper-bound warnings.",
        "",
        "## Counts",
        "",
        f"- items: {counts['items']}",
        f"- recipes: {counts['recipes']}",
        f"- shimmer: {counts['shimmer']}",
        f"- npc shops: {counts['npc_shops']}",
        f"- sprite manifest: {counts['sprite_manifest']}",
        (
            "- sprite category breakdown: "
            f"`item={counts['spriteCategoryBreakdown']['item']}`, "
            f"`npc={counts['spriteCategoryBreakdown']['npc']}`"
        ),
        (
            "- shimmer type breakdown: "
            f"`item_transform={counts['shimmerTypeBreakdown']['item_transform']}`, "
            f"`deconstruct={counts['shimmerTypeBreakdown']['deconstruct']}`"
        ),
        "",
        "## Check Results",
        "",
        "### 1) Required files exist",
        f"- Result: {pass_fail(checks['required_files']['pass'])}",
        "- Evidence: all required files are present (`items.json`, `items.csv`, `recipes.json`, `recipes.csv`, `shimmer.json`, `shimmer.csv`, `npc_shops.json`, `npc_shops.csv`, `sprite_manifest.json`, `sprite_manifest.csv`)"
        if checks["required_files"]["pass"]
        else "- Evidence: one or more required files are missing",
    ]

    if checks["required_files"]["failingRecords"]:
        lines.extend(
            [
                "- Failing records (exact):",
                "",
                "```json",
                json.dumps(checks["required_files"]["failingRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Failing records: none")

    lines.extend(
        [
            "",
            "### 2) Count ranges",
            f"- Result: {pass_fail(checks['count_ranges']['pass'])}",
            (
                f"- Items count check (min `{ITEMS_MIN}`, soft max `{ITEMS_MAX}`): "
                f"{pass_warn_fail(detail_by_name['items_range'])} "
                f"(actual `{detail_by_name['items_range']['actual']}`)"
            ),
            (
                f"- Recipes count check (min `{RECIPES_MIN}`, soft max `{RECIPES_MAX}`): "
                f"{pass_warn_fail(detail_by_name['recipes_range'])} "
                f"(actual `{detail_by_name['recipes_range']['actual']}`)"
            ),
            (
                f"- Shimmer item_transform check (min `{ITEM_TRANSFORM_MIN}`, soft max `{ITEM_TRANSFORM_MAX}`): "
                f"{pass_warn_fail(detail_by_name['shimmer_item_transform_range'])} "
                f"(actual `{detail_by_name['shimmer_item_transform_range']['actual']}`)"
            ),
            (
                f"- Shimmer deconstruct check (min `{DECONSTRUCT_MIN}`, soft max `{DECONSTRUCT_MAX}`): "
                f"{pass_warn_fail(detail_by_name['shimmer_deconstruct_range'])} "
                f"(actual `{detail_by_name['shimmer_deconstruct_range']['actual']}`)"
            ),
            (
                f"- NPC shops minimum (`>={NPC_SHOPS_MIN}`): "
                f"{pass_warn_fail(detail_by_name['npc_shops_min'])} "
                f"(actual `{detail_by_name['npc_shops_min']['actual']}`)"
            ),
            (
                f"- Sprite manifest count check (min `{SPRITE_MANIFEST_MIN}`, soft max `{SPRITE_MANIFEST_MAX}`): "
                f"{pass_warn_fail(detail_by_name['sprite_manifest_range'])} "
                f"(actual `{detail_by_name['sprite_manifest_range']['actual']}`)"
            ),
            (
                f"- Sprite item rows minimum (`>={SPRITE_ITEM_MIN}`): "
                f"{pass_warn_fail(detail_by_name['sprite_items_min'])} "
                f"(actual `{detail_by_name['sprite_items_min']['actual']}`)"
            ),
            (
                f"- Sprite NPC rows minimum (`>={SPRITE_NPC_MIN}`): "
                f"{pass_warn_fail(detail_by_name['sprite_npcs_min'])} "
                f"(actual `{detail_by_name['sprite_npcs_min']['actual']}`)"
            ),
        ]
    )

    if checks["count_ranges"]["warningRecords"]:
        lines.extend(
            [
                "",
                "Warning records (soft upper bounds exceeded):",
                "",
                "```json",
                json.dumps(checks["count_ranges"]["warningRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Warning records: none")

    if checks["count_ranges"]["failingRecords"]:
        lines.extend(
            [
                "",
                "Failing records (exact):",
                "",
                "```json",
                json.dumps(checks["count_ranges"]["failingRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Failing records: none")

    lines.extend(
        [
            "",
            "### 3) Foreign-key integrity (referenced item IDs must exist)",
            f"- Result: {pass_fail(checks['foreign_key_integrity']['pass'])}",
            "- Datasets checked: `recipes`, `shimmer`, `npc_shops`, `sprite_manifest`",
            f"- Valid item ID set size: `{checks['foreign_key_integrity']['details']['validItemIdCount']}`",
            f"- Invalid references found: `{checks['foreign_key_integrity']['details']['invalidReferenceCount']}`",
        ]
    )

    if checks["foreign_key_integrity"]["failingRecords"]:
        lines.extend(
            [
                "- Failing records (exact):",
                "",
                "```json",
                json.dumps(checks["foreign_key_integrity"]["failingRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Failing records: none")

    lines.extend(
        [
            "",
            "### 4) Sprite manifest integrity",
            f"- Result: {pass_fail(checks['sprite_manifest_integrity']['pass'])}",
            f"- Rows checked: `{checks['sprite_manifest_integrity']['details']['checkedRows']}`",
            f"- Integrity failures found: `{checks['sprite_manifest_integrity']['details']['failureCount']}`",
        ]
    )

    if checks["sprite_manifest_integrity"]["failingRecords"]:
        lines.extend(
            [
                "- Failing records (exact):",
                "",
                "```json",
                json.dumps(checks["sprite_manifest_integrity"]["failingRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Failing records: none")

    lines.extend(
        [
            "",
            "### 5) Spot checks",
            f"- Result: {pass_fail(checks['spot_checks']['pass'])}",
        ]
    )

    for spot in checks["spot_checks"]["details"]:
        evidence = spot["evidence"]
        if spot["check"] == "torch_recipe_exists":
            if evidence:
                lines.append(
                    "- Torch recipe check: "
                    f"PASS (`RecipeIndex={evidence['RecipeIndex']}`, `ResultItemId={evidence['ResultItemId']}`, ingredients include `9` and `23`)"
                )
            else:
                lines.append("- Torch recipe check: FAIL (missing expected recipe for Torch using Wood + Gel)")
        elif spot["check"] == "merchant_sells_torch":
            if evidence:
                lines.append(
                    "- Merchant sells Torch check: "
                    f"PASS (`NpcId={evidence['NpcId']}`, `ShopName=\"{evidence['ShopName']}\"`, `ItemId={evidence['ItemId']}`, `BuyPrice={evidence['BuyPrice']}`)"
                )
            else:
                lines.append("- Merchant sells Torch check: FAIL (Merchant shop is missing Torch)")
        elif spot["check"] == "known_shimmer_transform":
            if evidence:
                lines.append(
                    "- Known shimmer transform check: "
                    "PASS "
                    f"(`InputItemId={evidence['InputItemId']} -> OutputItemId={evidence['OutputItemId']}`, `Type={evidence['Type']}`)"
                )
            else:
                lines.append("- Known shimmer transform check: FAIL (missing `8 -> 5353` item_transform record)")

    if checks["spot_checks"]["failingRecords"]:
        lines.extend(
            [
                "- Failing records (exact):",
                "",
                "```json",
                json.dumps(checks["spot_checks"]["failingRecords"], indent=2, sort_keys=True),
                "```",
            ]
        )
    else:
        lines.append("- Failing records: none")

    lines.extend(
        [
            "",
            "## Notes",
            "",
            "- This validator intentionally does not use a total shimmer count gate.",
            "- PASS/FAIL comes from documented typed shimmer expectations plus required integrity and spot checks.",
        ]
    )

    return "\n".join(lines) + "\n"


def validate(output_dir: Path) -> dict[str, Any]:
    required_details: list[dict[str, Any]] = []
    required_failures: list[dict[str, Any]] = []

    for file_name in REQUIRED_FILES:
        exists = (output_dir / file_name).exists()
        required_details.append({"file": file_name, "exists": exists})
        if not exists:
            required_failures.append({"file": file_name, "error": "missing"})

    items, items_load_error = load_json_array(output_dir / "items.json")
    recipes, recipes_load_error = load_json_array(output_dir / "recipes.json")
    shimmer, shimmer_load_error = load_json_array(output_dir / "shimmer.json")
    npc_shops, npc_shops_load_error = load_json_array(output_dir / "npc_shops.json")
    sprite_manifest, sprite_manifest_load_error = load_json_array(output_dir / "sprite_manifest.json")

    load_errors = [
        error
        for error in [items_load_error, recipes_load_error, shimmer_load_error, npc_shops_load_error, sprite_manifest_load_error]
        if error is not None
    ]
    for error in load_errors:
        required_failures.append({"file": error.split(":", 1)[0], "error": error})

    shimmer_type_counter = Counter(
        str(record.get("Type", "")).strip() for record in shimmer
    )

    counts = {
        "items": len(items),
        "recipes": len(recipes),
        "shimmer": len(shimmer),
        "npc_shops": len(npc_shops),
        "sprite_manifest": len(sprite_manifest),
        "shimmerTypeBreakdown": {
            "item_transform": int(shimmer_type_counter.get("item_transform", 0)),
            "deconstruct": int(shimmer_type_counter.get("deconstruct", 0)),
        },
        "spriteCategoryBreakdown": {
            "item": sum(1 for row in sprite_manifest if isinstance(row, dict) and str(row.get("category", "")).strip().lower() == "item"),
            "npc": sum(1 for row in sprite_manifest if isinstance(row, dict) and str(row.get("category", "")).strip().lower() == "npc"),
        },
    }

    count_range_details = [
        {
            "check": "items_range",
            "expected": f"{ITEMS_MIN}-{ITEMS_MAX}",
            "actual": counts["items"],
            "pass": counts["items"] >= ITEMS_MIN,
            "warning": counts["items"] > ITEMS_MAX,
            "status": check_status(counts["items"] >= ITEMS_MIN, counts["items"] > ITEMS_MAX),
        },
        {
            "check": "recipes_range",
            "expected": f"{RECIPES_MIN}-{RECIPES_MAX}",
            "actual": counts["recipes"],
            "pass": counts["recipes"] >= RECIPES_MIN,
            "warning": counts["recipes"] > RECIPES_MAX,
            "status": check_status(counts["recipes"] >= RECIPES_MIN, counts["recipes"] > RECIPES_MAX),
        },
        {
            "check": "shimmer_item_transform_range",
            "expected": f"{ITEM_TRANSFORM_MIN}-{ITEM_TRANSFORM_MAX}",
            "actual": counts["shimmerTypeBreakdown"]["item_transform"],
            "pass": counts["shimmerTypeBreakdown"]["item_transform"] >= ITEM_TRANSFORM_MIN,
            "warning": counts["shimmerTypeBreakdown"]["item_transform"] > ITEM_TRANSFORM_MAX,
            "status": check_status(
                counts["shimmerTypeBreakdown"]["item_transform"] >= ITEM_TRANSFORM_MIN,
                counts["shimmerTypeBreakdown"]["item_transform"] > ITEM_TRANSFORM_MAX,
            ),
        },
        {
            "check": "shimmer_deconstruct_range",
            "expected": f"{DECONSTRUCT_MIN}-{DECONSTRUCT_MAX}",
            "actual": counts["shimmerTypeBreakdown"]["deconstruct"],
            "pass": counts["shimmerTypeBreakdown"]["deconstruct"] >= DECONSTRUCT_MIN,
            "warning": counts["shimmerTypeBreakdown"]["deconstruct"] > DECONSTRUCT_MAX,
            "status": check_status(
                counts["shimmerTypeBreakdown"]["deconstruct"] >= DECONSTRUCT_MIN,
                counts["shimmerTypeBreakdown"]["deconstruct"] > DECONSTRUCT_MAX,
            ),
        },
        {
            "check": "npc_shops_min",
            "expected": f">={NPC_SHOPS_MIN}",
            "actual": counts["npc_shops"],
            "pass": counts["npc_shops"] >= NPC_SHOPS_MIN,
            "warning": False,
            "status": check_status(counts["npc_shops"] >= NPC_SHOPS_MIN, False),
        },
        {
            "check": "sprite_manifest_range",
            "expected": f"{SPRITE_MANIFEST_MIN}-{SPRITE_MANIFEST_MAX}",
            "actual": counts["sprite_manifest"],
            "pass": counts["sprite_manifest"] >= SPRITE_MANIFEST_MIN,
            "warning": counts["sprite_manifest"] > SPRITE_MANIFEST_MAX,
            "status": check_status(counts["sprite_manifest"] >= SPRITE_MANIFEST_MIN, counts["sprite_manifest"] > SPRITE_MANIFEST_MAX),
        },
        {
            "check": "sprite_items_min",
            "expected": f">={SPRITE_ITEM_MIN}",
            "actual": counts["spriteCategoryBreakdown"]["item"],
            "pass": counts["spriteCategoryBreakdown"]["item"] >= SPRITE_ITEM_MIN,
            "warning": False,
            "status": check_status(counts["spriteCategoryBreakdown"]["item"] >= SPRITE_ITEM_MIN, False),
        },
        {
            "check": "sprite_npcs_min",
            "expected": f">={SPRITE_NPC_MIN}",
            "actual": counts["spriteCategoryBreakdown"]["npc"],
            "pass": counts["spriteCategoryBreakdown"]["npc"] >= SPRITE_NPC_MIN,
            "warning": False,
            "status": check_status(counts["spriteCategoryBreakdown"]["npc"] >= SPRITE_NPC_MIN, False),
        },
    ]
    count_range_failures = [
        {
            "check": detail["check"],
            "expected": detail["expected"],
            "actual": detail["actual"],
        }
        for detail in count_range_details
        if not detail["pass"]
    ]
    count_range_warnings = [
        {
            "check": detail["check"],
            "expected": detail["expected"],
            "actual": detail["actual"],
        }
        for detail in count_range_details
        if detail["warning"]
    ]

    fk_failures = collect_foreign_key_failures(items, recipes, shimmer, npc_shops, sprite_manifest)
    spot_checks, spot_failures = run_spot_checks(recipes, shimmer, npc_shops)
    sprite_manifest_integrity_failures = collect_sprite_manifest_integrity_failures(output_dir, sprite_manifest)

    checks = {
        "required_files": {
            "pass": len(required_failures) == 0,
            "details": required_details,
            "failingRecords": required_failures,
        },
        "count_ranges": {
            "pass": all(detail["pass"] for detail in count_range_details),
            "details": count_range_details,
            "failingRecords": count_range_failures,
            "warningRecords": count_range_warnings,
        },
        "foreign_key_integrity": {
            "pass": len(fk_failures) == 0,
            "details": {
                "checkedDatasets": ["recipes", "shimmer", "npc_shops", "sprite_manifest"],
                "validItemIdCount": len({item.get("Id") for item in items if isinstance(item.get("Id"), int)}),
                "invalidReferenceCount": len(fk_failures),
            },
            "failingRecords": fk_failures,
        },
        "sprite_manifest_integrity": {
            "pass": len(sprite_manifest_integrity_failures) == 0,
            "details": {
                "checkedRows": len(sprite_manifest),
                "failureCount": len(sprite_manifest_integrity_failures),
            },
            "failingRecords": sprite_manifest_integrity_failures,
        },
        "spot_checks": {
            "pass": len(spot_failures) == 0,
            "details": spot_checks,
            "failingRecords": spot_failures,
        },
    }

    status = "PASS" if all(section["pass"] for section in checks.values()) else "FAIL"
    report = {
        "validator": "Terraria Extractor Validation Tool",
        "status": status,
        "outputDirectory": str(output_dir).replace("\\", "/"),
        "counts": counts,
        "checks": checks,
    }
    return report


def main() -> int:
    args = parse_args()
    output_dir = Path(args.output_dir)
    json_out = Path(args.json_out)
    md_out = Path(args.md_out)

    log(f"starting validation: output_dir={output_dir}")
    report = validate(output_dir)

    json_out.parent.mkdir(parents=True, exist_ok=True)
    md_out.parent.mkdir(parents=True, exist_ok=True)

    with json_out.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")

    markdown = build_markdown_report(report)
    with md_out.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(markdown)

    log(f"wrote json report: {json_out}")
    log(f"wrote markdown report: {md_out}")
    log(f"status={report['status']}")

    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
