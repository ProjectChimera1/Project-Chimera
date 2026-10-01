#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace SpikeNs
{
    public static class Dump
    {
        /// <summary>Canonical reflective dump of an object graph (all public instance props with a public getter, dictionaries sorted by key).</summary>
        public static string Of(object? o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object? o, int depth)
        {
            if (depth > 40) { sb.Append("<deep>"); return; }
            switch (o)
            {
                case null: sb.Append("null"); return;
                case string s: sb.Append('"').Append(s).Append('"'); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f: sb.Append("f:").Append(BitConverter.SingleToInt32Bits(f)); return;
                case double d: sb.Append("d:").Append(BitConverter.DoubleToInt64Bits(d)); return;
                case Fixed fx: sb.Append("Fx:").Append(fx.Raw); return;
                case Enum e: sb.Append(e.GetType().Name).Append('.').Append(e.ToString()).Append('(').Append(Convert.ToInt64(e)).Append(')'); return;
                case IFormattable fm when o.GetType().IsPrimitive: sb.Append(fm.ToString(null, System.Globalization.CultureInfo.InvariantCulture)); return;
                case JsonElement je: sb.Append("je:").Append(je.GetRawText()); return;
                case IDictionary dict:
                {
                    var keys = new List<(string, object?)>();
                    foreach (DictionaryEntry de in dict) keys.Add((de.Key.ToString() ?? "", de.Value));
                    keys.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
                    sb.Append("{D ");
                    foreach (var (k, v) in keys) { sb.Append(k).Append('='); Write(sb, v, depth + 1); sb.Append(';'); }
                    sb.Append('}');
                    return;
                }
                case IEnumerable en:
                {
                    sb.Append('[').Append(o.GetType().Name).Append(' ');
                    foreach (object? x in en) { Write(sb, x, depth + 1); sb.Append(','); }
                    sb.Append(']');
                    return;
                }
            }
            Type t = o.GetType();
            sb.Append(t.Name).Append('{');
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod == null) continue;
                object? v;
                try { v = p.GetValue(o); } catch (Exception ex) { v = "<ex " + ex.GetType().Name + ">"; }
                sb.Append(p.Name).Append('=');
                Write(sb, v, depth + 1);
                sb.Append(';');
            }
            // public fields (effect leaves use readonly public fields)
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                sb.Append(f.Name).Append('=');
                Write(sb, f.GetValue(o), depth + 1);
                sb.Append(';');
            }
            sb.Append('}');
        }
    }

    public static class Helpers
    {
        public static T? DeSg<T>(string json, JsonSerializerOptions o) =>
            JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)));
        public static string SerSg<T>(T v, JsonSerializerOptions o) =>
            JsonSerializer.Serialize(v, (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)));
    }

    public static class Diff
    {
        public static int Pass, Fail;
        public static List<string> Failures = new();

        public static void Check(string name, Func<string> reflection, Func<string> sg)
        {
            string a, b;
            try { a = reflection(); } catch (Exception ex) { a = "EX:" + ex.GetType().Name + ":" + ex.Message; }
            try { b = sg(); } catch (Exception ex) { b = "EX:" + ex.GetType().Name + ":" + ex.Message; }
            if (a == b) { Pass++; return; }
            Fail++;
            string da = a.Length > 400 ? a.Substring(0, 400) + "..." : a;
            int idx = 0; while (idx < a.Length && idx < b.Length && a[idx] == b[idx]) idx++;
            string ctxA = a.Substring(Math.Max(0, idx - 80), Math.Min(200, a.Length - Math.Max(0, idx - 80)));
            string ctxB = b.Substring(Math.Max(0, idx - 80), Math.Min(200, b.Length - Math.Max(0, idx - 80)));
            Failures.Add($"DIFF {name} @{idx}\n   refl: {ctxA}\n   sg  : {ctxB}");
        }

        public static void Report()
        {
            Console.WriteLine($"DIFF RESULT pass={Pass} fail={Fail}");
            foreach (string f in Failures) Console.WriteLine(f);
        }
    }
}
