namespace StandaloneExtractor.Models
{
    public sealed class ExtractionContext
    {
        public ExtractionContext(string outputDirectory, string[] commandLineArgs)
        {
            OutputDirectory = outputDirectory;
            CommandLineArgs = commandLineArgs ?? new string[0];
        }

        public string OutputDirectory { get; private set; }

        public string[] CommandLineArgs { get; private set; }
    }
}
