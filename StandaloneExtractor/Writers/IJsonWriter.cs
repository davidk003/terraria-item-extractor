using System.Collections.Generic;

namespace StandaloneExtractor.Writers
{
    public interface IJsonWriter
    {
        void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows);
    }
}
