#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Combat;
using ProjectChimera.Effects;

namespace SpikeNs
{
    public sealed class NotRegistered { public int A { get; set; } }

    public static class Controls
    {
        public static void Run()
        {
            var o = Sg.Strict();
            JsonTypeInfo ti = o.GetTypeInfo(typeof(UnitDefinition));
            Console.WriteLine("resolver=" + o.TypeInfoResolver!.GetType().Name + " typeinfoKind=" + ti.Kind + " props=" + ti.Properties.Count + " creator=" + (ti.CreateObject != null));
            Console.WriteLine("IsReflectionEnabledByDefault=" + JsonSerializer.IsReflectionEnabledByDefault);
            // Control 1: type not in context -> must throw (proves no reflection fallback with a context-only resolver)
            try { JsonSerializer.Deserialize("{\"A\":1}", (JsonTypeInfo<NotRegistered>)o.GetTypeInfo(typeof(NotRegistered))); Console.WriteLine("CONTROL1 unexpectedly succeeded"); }
            catch (Exception ex) { Console.WriteLine("CONTROL1 (unregistered type) -> " + ex.GetType().Name + ": " + ex.Message); }
            // Control 1b: reflection-style call with options carrying only the context
            try { var x = JsonSerializer.Deserialize<NotRegistered>("{\"A\":1}", o); Console.WriteLine("CONTROL1b succeeded A=" + x!.A); }
            catch (Exception ex) { Console.WriteLine("CONTROL1b -> " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
            // Control 2: harness sensitivity — a NON-strict enum converter must produce a diff on numeric enum input
            var loose = Sg.Strict();
            loose.Converters.Insert(0, new JsonStringEnumConverter<DamageType>()); // allowIntegerValues:true, earlier in list than StrictEnum<DamageType>
            string j = "{ \"id\":\"x\", \"effect\": { \"kind\":\"damage\", \"amount\": 5, \"damage_type\": 3 } }";
            Diff.Pass = 0; Diff.Fail = 0; Diff.Failures.Clear();
            Diff.Check("control2:numericEnum-loose", () => Dump.Of(JsonSerializer.Deserialize<AbilityDefinition>(j, ContentJson.Options)),
                                                    () => Dump.Of(Helpers.DeSg<AbilityDefinition>(j, loose)));
            Console.WriteLine("CONTROL2 expected 1 diff -> fail=" + Diff.Fail);
            foreach (var f in Diff.Failures) Console.WriteLine(f);
            // Show the actual messages for a few strict failures under both modes
            foreach (string c in new[] { "{ \"id\":\"x\", \"cooldwn\": 1 }", "{ \"id\":\"x\", \"effect\": { \"kind\":\"damage\", \"amount\": 5, \"damage_type\": 3 } }" })
            {
                try { JsonSerializer.Deserialize<AbilityDefinition>(c, ContentJson.Options); Console.WriteLine("refl ok"); } catch (Exception ex) { Console.WriteLine("refl: " + ex.GetType().Name + ": " + ex.Message); }
                try { Helpers.DeSg<AbilityDefinition>(c, Sg.Strict()); Console.WriteLine("sg ok"); } catch (Exception ex) { Console.WriteLine("sg  : " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}
