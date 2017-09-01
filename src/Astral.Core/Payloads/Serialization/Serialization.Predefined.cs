using System.Net.Mime;
using System.Text;
using LanguageExt;
using Newtonsoft.Json;
using static LanguageExt.Prelude;

namespace Astral.Payloads.Serialization
{
    public static partial class Serialization
    {
        public static SerializeProvider<string> JsonTextSerializeProvider(JsonSerializerSettings settings)
        {
            return SerializeProvider(CommonExtensions.IsJson,
                o => Try(() => JsonConvert.SerializeObject(o, settings)));
        }

        public static SerializeProvider<byte[]> JsonRawSerializeProvider(JsonSerializerSettings settings)
        {
            return SerializeProvider(CommonExtensions.IsJson,
                (ct, o) => Try(() => JsonConvert.SerializeObject(o, settings))
                    .Bind(p => Try(() => Encode(ct, p))));
        }

        public static DeserializeProvider<string> JsonTextDeserializeProvider(JsonSerializerSettings settings)
        {
            return DeserializeProvider<string>(CommonExtensions.IsJson,
                (type, data) => Try(() => JsonConvert.DeserializeObject(data, type, settings)));
        }

        public static DeserializeProvider<byte[]> JsonRawDeserializeProvider(JsonSerializerSettings settings)
        {
            return DeserializeProvider(CommonExtensions.IsJson,
                ct => Deserialize<byte[]>((type, data) => Try(() => Decode(ct, data)).Bind(txt =>
                    Try(() => JsonConvert.DeserializeObject(txt, type, settings)))));
        }


        private static byte[] Encode(ContentType ct, string text)
        {
            var encodingName = ct.CharSet ?? Encoding.UTF8.WebName;
            var encoding = Encoding.GetEncoding(encodingName);
            return encoding.GetBytes(text);
        }

        private static string Decode(Option<ContentType> ct, byte[] data)
        {
            var encodingName = ct.Bind(p => Optional(p.CharSet)).IfNone(Encoding.UTF8.WebName);
            var encoding = Encoding.GetEncoding(encodingName);
            return encoding.GetString(data);
        }
    }
}