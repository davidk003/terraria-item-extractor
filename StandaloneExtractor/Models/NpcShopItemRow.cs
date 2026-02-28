using System.Collections.Generic;

namespace StandaloneExtractor.Models
{
    public sealed class NpcShopItemRow
    {
        public NpcShopItemRow()
        {
            Conditions = new List<string>();
        }

        public int ItemId { get; set; }

        public string Name { get; set; }

        public int BuyPrice { get; set; }

        public List<string> Conditions { get; set; }

        public override string ToString()
        {
            string encodedConditions = Conditions == null || Conditions.Count == 0
                ? string.Empty
                : string.Join("&", Conditions);

            return ItemId + "|" + Name + "|" + BuyPrice + "|" + encodedConditions;
        }
    }
}
