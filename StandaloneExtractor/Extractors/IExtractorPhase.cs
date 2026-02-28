using System.Collections.Generic;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public interface IExtractorPhase<T>
    {
        string PhaseName { get; }

        IEnumerable<T> Extract(ExtractionContext context);
    }
}
