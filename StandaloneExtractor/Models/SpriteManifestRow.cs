using System.Runtime.Serialization;

namespace StandaloneExtractor.Models
{
    [DataContract]
    public sealed class SpriteManifestRow
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "category")]
        public string Category { get; set; }

        [DataMember(Name = "internalName")]
        public string InternalName { get; set; }

        [DataMember(Name = "spriteFile")]
        public string SpriteFile { get; set; }

        [DataMember(Name = "width")]
        public int Width { get; set; }

        [DataMember(Name = "height")]
        public int Height { get; set; }
    }
}
