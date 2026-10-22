using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPGMissionCompanion
{
    // JavaScriptSerializer uses ArrayList for nested arrays in typed dictionaries.
    internal static class RuntimePayload
    {
        internal static object[] Array(Dictionary<string, object> payload, string key)
        {
            object value;
            if (payload == null || !payload.TryGetValue(key, out value) || value == null) return new object[0];
            var list = value as IList;
            if (list == null) throw new InvalidDataException(key + " must be an array.");
            return list.Cast<object>().ToArray();
        }
    }
}
