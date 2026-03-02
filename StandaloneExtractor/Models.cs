using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace StandaloneExtractor
{
    public sealed class ExtractionContext
    {
        public ExtractionContext(string outputDirectory, string[] commandLineArgs, Assembly terrariaAssembly, string terrariaDirectory)
        {
            OutputDirectory = outputDirectory;
            CommandLineArgs = commandLineArgs ?? new string[0];
            TerrariaAssembly = terrariaAssembly;
            TerrariaDirectory = terrariaDirectory ?? string.Empty;
        }

        public string OutputDirectory { get; private set; }

        public string[] CommandLineArgs { get; private set; }

        public Assembly TerrariaAssembly { get; private set; }

        public string TerrariaDirectory { get; private set; }
    }

    public sealed class ItemRow
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string InternalName { get; set; }

        public int Value { get; set; }

        public int SellPrice { get; set; }
    }

    public sealed class ShimmerRow
    {
        public int InputItemId { get; set; }

        public string InputItemName { get; set; }

        public int OutputItemId { get; set; }

        public string OutputItemName { get; set; }

        public int OutputAmount { get; set; }

        public string Type { get; set; }
    }

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

    public sealed class PhaseDefinition
    {
        public PhaseDefinition(string key, string fileStem)
        {
            Key = key;
            FileStem = fileStem;
        }

        public string Key { get; private set; }

        public string FileStem { get; private set; }
    }

    [DataContract]
    public sealed class PhaseExecutionResult
    {
        public PhaseExecutionResult()
        {
            Errors = new List<string>();
        }

        [DataMember(Name = "phaseName")]
        public string PhaseName { get; set; }

        [DataMember(Name = "phaseKey")]
        public string PhaseKey { get; set; }

        [DataMember(Name = "rowCount")]
        public int RowCount { get; set; }

        [DataMember(Name = "succeeded")]
        public bool Succeeded { get; set; }

        [DataMember(Name = "jsonPath")]
        public string JsonPath { get; set; }

        [DataMember(Name = "csvPath")]
        public string CsvPath { get; set; }

        [DataMember(Name = "elapsedTicks")]
        public long ElapsedTicks { get; set; }

        public TimeSpan Elapsed
        {
            get { return TimeSpan.FromTicks(ElapsedTicks); }
            set { ElapsedTicks = value.Ticks; }
        }

        [DataMember(Name = "errors")]
        public List<string> Errors { get; set; }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (Errors == null) Errors = new List<string>();
        }
    }

    public sealed class BootstrapReport
    {
        public BootstrapReport()
        {
            Stages = new List<BootstrapStageResult>();
        }

        public string Scope { get; set; }

        public List<BootstrapStageResult> Stages { get; private set; }

        public DependencyProbeResult DependencyProbe { get; set; }

        public string DependencyProbeError { get; set; }
    }

    public sealed class BootstrapStageResult
    {
        public string Stage { get; set; }

        public bool Succeeded { get; set; }

        public string Detail { get; set; }
    }

    public sealed class DependencyProbeResult
    {
        public DependencyProbeResult()
        {
            MissingAssemblies = new List<string>();
        }

        public int ReferencedAssemblyCount { get; set; }

        public List<string> MissingAssemblies { get; private set; }

        public string ErrorMessage { get; set; }
    }
}
