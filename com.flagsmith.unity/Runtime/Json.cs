using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flagsmith
{
    static class Json
    {
        /// <summary>Like JToken.Parse, but keeps ISO-8601 strings as strings instead of turning them into dates.</summary>
        internal static JToken Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                if (reader.Read()) throw new JsonReaderException("Unexpected content after the JSON value.");
                return token;
            }
        }
    }
}
