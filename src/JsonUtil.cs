using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace IsCodexWorking
{
    internal static class JsonUtil
    {
        private static readonly JavaScriptSerializer Serializer = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 2 * 1024 * 1024;
            serializer.RecursionLimit = 64;
            return serializer;
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (json.Length > 1024 * 1024) return null;
            try
            {
                return Serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key)) return null;
            return obj[key] as Dictionary<string, object>;
        }

        public static string String(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return null;
            return Convert.ToString(obj[key], CultureInfo.InvariantCulture);
        }


        public static bool TryGetLong(Dictionary<string, object> obj, string key, out long result)
        {
            result = 0;
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return false;
            try
            {
                result = Convert.ToInt64(obj[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        public static bool HasNull(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.ContainsKey(key) && obj[key] == null;
        }

        public static DateTime TimestampUtc(Dictionary<string, object> obj, DateTime fallbackUtc)
        {
            string raw = String(obj, "timestamp");
            DateTime parsed;
            if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            return fallbackUtc;
        }

        public static DateTime ParseUtc(string raw, DateTime fallbackUtc)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                return parsed.ToUniversalTime();
            return fallbackUtc;
        }

        public static bool ContainsKeyValueRecursive(object value, string key, string expected, int depth)
        {
            if (value == null || depth < 0) return false;
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value != null)
                    {
                        string text = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                        if (string.Equals(text, expected, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (ContainsKeyValueRecursive(pair.Value, key, expected, depth - 1)) return true;
                }
                return false;
            }
            object[] array = value as object[];
            if (array != null)
            {
                int i;
                for (i = 0; i < array.Length; i++)
                {
                    if (ContainsKeyValueRecursive(array[i], key, expected, depth - 1)) return true;
                }
            }
            ArrayList list = value as ArrayList;
            if (list != null)
            {
                foreach (object item in list)
                {
                    if (ContainsKeyValueRecursive(item, key, expected, depth - 1)) return true;
                }
            }
            return false;
        }


        public static bool TryFindLongRecursive(object value, string key, int depth, out long result)
        {
            result = 0;
            if (value == null || depth < 0) return false;
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value != null)
                    {
                        try
                        {
                            result = Convert.ToInt64(pair.Value, CultureInfo.InvariantCulture);
                            return true;
                        }
                        catch { }
                    }
                    if (TryFindLongRecursive(pair.Value, key, depth - 1, out result)) return true;
                }
            }
            object[] array = value as object[];
            if (array != null)
            {
                int i;
                for (i = 0; i < array.Length; i++)
                    if (TryFindLongRecursive(array[i], key, depth - 1, out result)) return true;
            }
            return false;
        }


        public static void CollectStringsRecursive(object value, int depth, int maxStrings, List<string> output)
        {
            if (value == null || depth < 0 || output == null || output.Count >= maxStrings) return;
            string text = value as string;
            if (text != null)
            {
                if (text.Length > 0) output.Add(text.Length > 131072 ? text.Substring(0, 131072) : text);
                return;
            }
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    CollectStringsRecursive(pair.Value, depth - 1, maxStrings, output);
                    if (output.Count >= maxStrings) break;
                }
                return;
            }
            object[] array = value as object[];
            if (array != null)
            {
                int i;
                for (i = 0; i < array.Length && output.Count < maxStrings; i++)
                    CollectStringsRecursive(array[i], depth - 1, maxStrings, output);
                return;
            }
            ArrayList list = value as ArrayList;
            if (list != null)
            {
                foreach (object item in list)
                {
                    CollectStringsRecursive(item, depth - 1, maxStrings, output);
                    if (output.Count >= maxStrings) break;
                }
            }
        }

        public static string ExtractJsonStringField(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int keyPos = text.IndexOf(needle, StringComparison.Ordinal);
            if (keyPos < 0) return null;
            int colon = text.IndexOf(':', keyPos + needle.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return null;
            if (i + 4 <= text.Length && string.Compare(text, i, "null", 0, 4, StringComparison.Ordinal) == 0) return null;
            if (text[i] != '"') return null;
            int start = i;
            i++;
            bool escape = false;
            while (i < text.Length)
            {
                char ch = text[i];
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"')
                {
                    string token = text.Substring(start, i - start + 1);
                    try { return Serializer.Deserialize<string>(token); }
                    catch { return null; }
                }
                i++;
            }
            return null;
        }


        public static bool ContainsKeyRecursive(object value, string key, int depth)
        {
            if (value == null || depth < 0) return false;
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
                    if (ContainsKeyRecursive(pair.Value, key, depth - 1)) return true;
                }
                return false;
            }
            object[] array = value as object[];
            if (array != null)
            {
                int i;
                for (i = 0; i < array.Length; i++)
                    if (ContainsKeyRecursive(array[i], key, depth - 1)) return true;
            }
            ArrayList list = value as ArrayList;
            if (list != null)
            {
                foreach (object item in list)
                    if (ContainsKeyRecursive(item, key, depth - 1)) return true;
            }
            return false;
        }

        public static bool HasNonFatalErrorCode(Dictionary<string, object> payload)
        {
            if (payload == null) return false;
            string[] rollbackValues = new string[] { "thread_rollback_failed", "ThreadRollbackFailed" };
            string[] steerValues = new string[] { "active_turn_not_steerable", "ActiveTurnNotSteerable" };
            int i;
            for (i = 0; i < rollbackValues.Length; i++)
            {
                string v = rollbackValues[i];
                if (ContainsKeyValueRecursive(payload, "codex_error_info", v, 5) ||
                    ContainsKeyValueRecursive(payload, "codexErrorInfo", v, 5) ||
                    ContainsKeyValueRecursive(payload, "code", v, 5) ||
                    ContainsKeyValueRecursive(payload, "type", v, 5)) return true;
            }
            for (i = 0; i < steerValues.Length; i++)
            {
                string v = steerValues[i];
                if (ContainsKeyValueRecursive(payload, "codex_error_info", v, 5) ||
                    ContainsKeyValueRecursive(payload, "codexErrorInfo", v, 5) ||
                    ContainsKeyValueRecursive(payload, "code", v, 5) ||
                    ContainsKeyValueRecursive(payload, "type", v, 5)) return true;
            }
            // Structured enum variants can serialize with the variant name as a key.
            if (ContainsKeyRecursive(payload, "thread_rollback_failed", 5) ||
                ContainsKeyRecursive(payload, "ThreadRollbackFailed", 5) ||
                ContainsKeyRecursive(payload, "active_turn_not_steerable", 5) ||
                ContainsKeyRecursive(payload, "ActiveTurnNotSteerable", 5)) return true;
            return false;
        }

        public static bool HasUsageLimitCode(Dictionary<string, object> payload)
        {
            if (payload == null) return false;
            string[] values = new string[] { "usage_limit_exceeded", "session_budget_exceeded", "UsageLimitExceeded", "SessionBudgetExceeded" };
            int i;
            for (i = 0; i < values.Length; i++)
            {
                string v = values[i];
                if (ContainsKeyValueRecursive(payload, "codex_error_info", v, 5) ||
                    ContainsKeyValueRecursive(payload, "codexErrorInfo", v, 5) ||
                    ContainsKeyValueRecursive(payload, "code", v, 5) ||
                    ContainsKeyValueRecursive(payload, "type", v, 5)) return true;
                if (ContainsKeyRecursive(payload, v, 5)) return true;
            }
            return false;
        }
    }
}
