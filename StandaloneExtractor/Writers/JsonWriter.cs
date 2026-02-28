using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace StandaloneExtractor.Writers
{
    public sealed class JsonWriter : IJsonWriter
    {
        public void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows)
        {
            Directory.CreateDirectory(outputDirectory);

            string filePath = Path.Combine(outputDirectory, fileName);
            List<T> materializedRows = rows == null ? new List<T>() : rows.ToList();

            var serializer = new DataContractJsonSerializer(typeof(List<T>));
            using (var stream = File.Create(filePath))
            {
                serializer.WriteObject(stream, materializedRows);
            }
        }
    }
}
