#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace SpikeNs
{
    /// <summary>Approximates STJ's public-property type-graph walk to count DTO types reachable from each root.</summary>
    public static class Walker
    {
        public static readonly HashSet<Type> ConverterLeaves = new()
        {
            typeof(Fixed), typeof(EffectNode), typeof(NodeBase), typeof(DataEdge), typeof(ExecEdge), 
            typeof(ProfileInventoryItem), typeof(System.Text.Json.JsonElement),
        };

        public sealed class Result
        {
            public SortedSet<string> Poco = new();
            public SortedSet<string> Enums = new();
            public SortedSet<string> ConverterLeafHits = new();
            public SortedSet<string> NonPublic = new();
            public SortedSet<string> NotesSetters = new();
        }

        public static Result Walk(Type root)
        {
            var r = new Result();
            Visit(root, r, new HashSet<Type>());
            return r;
        }

        private static void Visit(Type t, Result r, HashSet<Type> seen)
        {
            if (t == typeof(string) || t == typeof(object) || t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid)) return;
            Type? u = Nullable.GetUnderlyingType(t);
            if (u != null) { Visit(u, r, seen); return; }
            if (t.IsEnum) { r.Enums.Add(t.FullName!); return; }
            if (ConverterLeaves.Contains(t)) { r.ConverterLeafHits.Add(t.Name); return; }
            if (t.IsArray) { Visit(t.GetElementType()!, r, seen); return; }
            if (t.IsGenericType)
            {
                foreach (Type a in t.GetGenericArguments()) Visit(a, r, seen);
                Type gd = t.GetGenericTypeDefinition();
                if (typeof(IEnumerable).IsAssignableFrom(t)) return;
            }
            if (!seen.Add(t)) return;
            if (!t.IsVisible) r.NonPublic.Add(t.FullName!);
            r.Poco.Add(t.FullName!);
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                if (p.GetMethod == null || !p.GetMethod.IsPublic) continue;
                MethodInfo? set = p.SetMethod;
                if (set == null) r.NotesSetters.Add($"{t.Name}.{p.Name} getter-only");
                else if (!set.IsPublic) r.NotesSetters.Add($"{t.Name}.{p.Name} non-public-setter");
                else if (set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit")) r.NotesSetters.Add($"{t.Name}.{p.Name} init-only");
                Visit(p.PropertyType, r, seen);
            }
        }

        public static string Report(string name, Type root)
        {
            Result r = Walk(root);
            var sb = new StringBuilder();
            sb.AppendLine($"ROOT {name}: poco/struct types={r.Poco.Count} enums={r.Enums.Count} converterLeaves=[{string.Join(",", r.ConverterLeafHits)}] nonPublic=[{string.Join(",", r.NonPublic)}]");
            return sb.ToString();
        }
    }
}
