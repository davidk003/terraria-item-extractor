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
)

ITEMS_MIN = 5300
ITEMS_MAX = 5600
RECIPES_MIN = 2800
RECIPES_MAX = 3100
NPC_SHOPS_MIN = 25
ITEM_TRANSFORM_MIN = 200
ITEM_TRANSFORM_MAX = 350

# Explicitly documented D2 contract:
# C2 baseline data had deconstruct=4227. Keep a bounded, explicit envelope
# around this known level so validation catches real regressions while allowing
# minor drift from runtime ordering or patch-level content changes.
DECONSTRUCT_MIN = 3800
DECONSTRUCT_MAX = 4600


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


def in_range(value: int, minimum: int, maximum: int) -> bool:
    return minimum <= value <= maximum


def collect_foreign_key_failures(
    items: list[dict[str, Any]],
    recipes: list[dict[str, Any]],
    shimmer: list[dict[str, Any]],
    npc_shops: list[dict[str, Any]],
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

    count_range_details = checks["count_ranges"]["details"]
    detail_by_name = {entry["check"]: entry for entry in count_range_details}

    lines: list[str] = [
        "# Validation Report (D2)",
        "",
        f"- Status: {report['status']}",
        f"- Output directory: `{report['outputDirectory']}`",
        "",
        "## Contract",
        "",
        "- Shimmer validation is typed, not total-only:",
        f"  - `item_transform` count must be `{ITEM_TRANSFORM_MIN}-{ITEM_TRANSFORM_MAX}` (historically around ~260)",
        f"  - `deconstruct` count must be `{DECONSTRUCT_MIN}-{DECONSTRUCT_MAX}` (baseline `4227` from C2, bounded tolerance for drift)",
        "",
        "## Counts",
        "",
        f"- items: {counts['items']}",
        f"- recipes: {counts['recipes']}",
        f"- shimmer: {counts['shimmer']}",
        f"- npc shops: {counts['npc_shops']}",
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
        "- Evidence: all required files are present (`items.json`, `items.csv`, `recipes.json`, `recipes.csv`, `shimmer.json`, `shimmer.csv`, `npc_shops.json`, `npc_shops.csv`)"
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
                f"- Items range check (`{ITEMS_MIN}-{ITEMS_MAX}`): "
                f"{pass_fail(detail_by_name['items_range']['pass'])} "
                f"(actual `{detail_by_name['items_range']['actual']}`)"
            ),
            (
                f"- Recipes range check (`{RECIPES_MIN}-{RECIPES_MAX}`): "
                f"{pass_fail(detail_by_name['recipes_range']['pass'])} "
                f"(actual `{detail_by_name['recipes_range']['actual']}`)"
            ),
            (
                f"- Shimmer item_transform range check (`{ITEM_TRANSFORM_MIN}-{ITEM_TRANSFORM_MAX}`): "
                f"{pass_fail(detail_by_name['shimmer_item_transform_range']['pass'])} "
                f"(actual `{detail_by_name['shimmer_item_transform_range']['actual']}`)"
            ),
            (
                f"- Shimmer deconstruct range check (`{DECONSTRUCT_MIN}-{DECONSTRUCT_MAX}`): "
                f"{pass_fail(detail_by_name['shimmer_deconstruct_range']['pass'])} "
                f"(actual `{detail_by_name['shimmer_deconstruct_range']['actual']}`)"
            ),
            (
                f"- NPC shops minimum (`>={NPC_SHOPS_MIN}`): "
                f"{pass_fail(detail_by_name['npc_shops_min']['pass'])} "
                f"(actual `{detail_by_name['npc_shops_min']['actual']}`)"
            ),
        ]
    )

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
            "- Datasets checked: `recipes`, `shimmer`, `npc_shops`",
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
            "### 4) Spot checks",
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

    load_errors = [
        error
        for error in [items_load_error, recipes_load_error, shimmer_load_error, npc_shops_load_error]
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
        "shimmerTypeBreakdown": {
            "item_transform": int(shimmer_type_counter.get("item_transform", 0)),
            "deconstruct": int(shimmer_type_counter.get("deconstruct", 0)),
        },
    }

    count_range_details = [
        {
            "check": "items_range",
            "expected": f"{ITEMS_MIN}-{ITEMS_MAX}",
            "actual": counts["items"],
            "pass": in_range(counts["items"], ITEMS_MIN, ITEMS_MAX),
        },
        {
            "check": "recipes_range",
            "expected": f"{RECIPES_MIN}-{RECIPES_MAX}",
            "actual": counts["recipes"],
            "pass": in_range(counts["recipes"], RECIPES_MIN, RECIPES_MAX),
        },
        {
            "check": "shimmer_item_transform_range",
            "expected": f"{ITEM_TRANSFORM_MIN}-{ITEM_TRANSFORM_MAX}",
            "actual": counts["shimmerTypeBreakdown"]["item_transform"],
            "pass": in_range(
                counts["shimmerTypeBreakdown"]["item_transform"],
                ITEM_TRANSFORM_MIN,
                ITEM_TRANSFORM_MAX,
            ),
        },
        {
            "check": "shimmer_deconstruct_range",
            "expected": f"{DECONSTRUCT_MIN}-{DECONSTRUCT_MAX}",
            "actual": counts["shimmerTypeBreakdown"]["deconstruct"],
            "pass": in_range(
                counts["shimmerTypeBreakdown"]["deconstruct"],
                DECONSTRUCT_MIN,
                DECONSTRUCT_MAX,
            ),
        },
        {
            "check": "npc_shops_min",
            "expected": f">={NPC_SHOPS_MIN}",
            "actual": counts["npc_shops"],
            "pass": counts["npc_shops"] >= NPC_SHOPS_MIN,
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

    fk_failures = collect_foreign_key_failures(items, recipes, shimmer, npc_shops)
    spot_checks, spot_failures = run_spot_checks(recipes, shimmer, npc_shops)

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
        },
        "foreign_key_integrity": {
            "pass": len(fk_failures) == 0,
            "details": {
                "checkedDatasets": ["recipes", "shimmer", "npc_shops"],
                "validItemIdCount": len({item.get("Id") for item in items if isinstance(item.get("Id"), int)}),
                "invalidReferenceCount": len(fk_failures),
            },
            "failingRecords": fk_failures,
        },
        "spot_checks": {
            "pass": len(spot_failures) == 0,
            "details": spot_checks,
            "failingRecords": spot_failures,
        },
    }

    status = "PASS" if all(section["pass"] for section in checks.values()) else "FAIL"
    report = {
        "validator": "D2 - Validation Contract Alignment Agent",
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
