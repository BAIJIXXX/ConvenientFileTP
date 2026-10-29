using System;
using System.Web.Script.Serialization;

namespace ConvenientFileTP.Core
{
    /// <summary>JSON helpers based on the in-box JavaScriptSerializer (no NuGet needed).</summary>
    public static class Json
    {
        private static JavaScriptSerializer Create()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = 8 * 1024 * 1024,
                RecursionLimit = 100
            };
        }

        public static string Serialize(object value)
        {
            return Create().Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            return Create().Deserialize<T>(json);
        }

        public static bool TryDeserialize<T>(string json, out T value)
        {
            try
            {
                value = Deserialize<T>(json);
                return value != null;
            }
            catch
            {
                value = default(T);
                return false;
            }
        }
    }
}
