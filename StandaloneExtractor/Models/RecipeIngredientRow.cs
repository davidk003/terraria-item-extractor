namespace StandaloneExtractor.Models
{
    public sealed class RecipeIngredientRow
    {
        public int ItemId { get; set; }

        public string Name { get; set; }

        public int Count { get; set; }

        public override string ToString()
        {
            return ItemId + ":" + Name + "x" + Count;
        }
    }
}
