using System.Collections.Generic;

namespace StandaloneExtractor.Models
{
    public sealed class NpcShopRow
    {
        public NpcShopRow()
        {
            Items = new List<NpcShopItemRow>();
        }

        public int NpcId { get; set; }

        public string NpcName { get; set; }

        public string ShopName { get; set; }

        public List<NpcShopItemRow> Items { get; set; }
    }
}
