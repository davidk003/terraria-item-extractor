using System.Collections.Generic;

namespace StandaloneExtractor.Models
{
    public sealed class RecipeRow
    {
        public RecipeRow()
        {
            Ingredients = new List<RecipeIngredientRow>();
            CraftingStations = new List<string>();
            Conditions = new List<string>();
        }

        public int RecipeIndex { get; set; }

        public int ResultItemId { get; set; }

        public string ResultItemName { get; set; }

        public int ResultAmount { get; set; }

        public List<RecipeIngredientRow> Ingredients { get; set; }

        public List<string> CraftingStations { get; set; }

        public List<string> Conditions { get; set; }
    }
}
