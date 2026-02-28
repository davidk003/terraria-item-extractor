using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StandaloneExtractor.Writers
{
    public sealed class CsvWriter : ICsvWriter
    {
        public void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows)
        {
            Directory.CreateDirectory(outputDirectory);

            string filePath = Path.Combine(outputDirectory, fileName);
            T[] materializedRows = rows == null ? new T[0] : rows.ToArray();
            PropertyInfo[] properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            using (var writer = new StreamWriter(filePath, false))
            {
                if (properties.Length > 0)
                {
                    writer.WriteLine(string.Join(",", properties.Select(p => Escape(p.Name))));
                }

                foreach (T row in materializedRows)
                {
                    string line = string.Join(",", properties.Select(p => Escape(ToCsvCell(p.GetValue(row, null)))));
                    writer.WriteLine(line);
                }
            }
        }

        private static string ToCsvCell(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (!(value is string) && enumerable != null)
            {
                var items = new List<string>();
                foreach (object item in enumerable)
                {
                    items.Add(item == null ? string.Empty : Convert.ToString(item, CultureInfo.InvariantCulture));
                }

                return string.Join(";", items);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string escaped = value.Replace("\"", "\"\"");
            if (escaped.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + escaped + "\"";
            }

            return escaped;
        }
    }
}
