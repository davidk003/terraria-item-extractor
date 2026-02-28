using System.Collections.Generic;

namespace StandaloneExtractor.Writers
{
    public interface ICsvWriter
    {
        void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows);
    }
}
