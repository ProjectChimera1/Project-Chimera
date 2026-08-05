#nullable enable
using System;
using System.Reflection;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-218 — null-CHECKED white-box reflection lookups for the determinism tests that must assert on internal
    /// state (emission order, buffer contents) which is deliberately not on any public API.
    ///
    /// <para>The idiom this replaces was <c>typeof(X).GetField("_y", …)!</c>: the null-forgiving <c>!</c> suppresses
    /// the compiler's warning but does NOTHING at runtime, so RENAMING or removing the probed member turned the test
    /// into an opaque <see cref="NullReferenceException"/> at the *use* site — no member name, no owner type, no clue
    /// that the test had simply gone stale. Every lookup here fails LOUD and NAMED instead, so the diagnostic tells
    /// the next reader exactly which member moved and where to fix it.</para>
    ///
    /// <para>This is test-only infrastructure: it compiles into the Tier-1 (Godot-free) test assembly and touches no
    /// simulation code, so it can never move a checksum or a golden.</para>
    /// </summary>
    internal static class ReflectionProbe
    {
        private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags NonPublicStatic   = BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>A non-public instance field, or a named failure naming owner + member.</summary>
        public static FieldInfo Field(Type owner, string name)
            => owner.GetField(name, NonPublicInstance) ?? throw Missing(owner, "non-public instance field", name);

        /// <summary>
        /// DW-501 — a non-public STATIC field, or a named failure naming owner + member. The static half of
        /// <see cref="Field"/>: white-box tests that pin a type's private *shared* state (e.g. a validator's
        /// vocabulary tables, which must ALIAS the registry arrays rather than copy them) probe by name too, and
        /// a rename there was the same opaque <see cref="NullReferenceException"/>.
        /// </summary>
        public static FieldInfo StaticField(Type owner, string name)
            => owner.GetField(name, NonPublicStatic) ?? throw Missing(owner, "non-public static field", name);

        /// <summary>A public instance field (e.g. a nested payload struct's members), or a named failure.</summary>
        public static FieldInfo PublicField(Type owner, string name)
            => owner.GetField(name, BindingFlags.Public | BindingFlags.Instance)
               ?? throw Missing(owner, "public instance field", name);

        /// <summary>
        /// A non-public instance method with EXACTLY <paramref name="parameterTypes"/>. Matching the signature (not
        /// just the name) is deliberate: an added/reordered parameter would otherwise survive the lookup and only
        /// surface later as a <see cref="TargetParameterCountException"/> from <c>Invoke</c>, which names nothing.
        /// </summary>
        public static MethodInfo Method(Type owner, string name, params Type[] parameterTypes)
            => owner.GetMethod(name, NonPublicInstance, binder: null, types: parameterTypes, modifiers: null)
               ?? throw Missing(owner, $"non-public instance method taking ({Describe(parameterTypes)})", name);

        /// <summary>A non-public nested type, or a named failure.</summary>
        public static Type NestedType(Type owner, string name)
            => owner.GetNestedType(name, BindingFlags.NonPublic) ?? throw Missing(owner, "non-public nested type", name);

        /// <summary>
        /// Read <paramref name="field"/> off <paramref name="instance"/> as <typeparamref name="T"/>. A null value or
        /// a changed field TYPE fails with a named diagnostic rather than an unexplained cast/NRE at the use site.
        /// </summary>
        public static T Read<T>(FieldInfo field, object? instance)
        {
            object? raw = field.GetValue(instance);
            if (raw is null)
                throw new InvalidOperationException(
                    $"Reflection probe: {Describe(field.DeclaringType)}.{field.Name} read back NULL — the white-box " +
                    "test expected live state here. The field was repurposed, or the fixture no longer initializes it.");
            if (raw is not T typed)
                throw new InvalidOperationException(
                    $"Reflection probe: {Describe(field.DeclaringType)}.{field.Name} is {Describe(raw.GetType())}, " +
                    $"not the expected {Describe(typeof(T))} — the field's type changed; update this test's probe.");
            return typed;
        }

        /// <summary>
        /// DW-501 — read a STATIC <paramref name="field"/> as <typeparamref name="T"/>. Rejects an instance field up
        /// front: <c>FieldInfo.GetValue(null)</c> on one throws a bare <see cref="TargetException"/> that names
        /// neither the field nor the mistake, which is the very diagnostic gap this probe exists to close.
        /// </summary>
        public static T ReadStatic<T>(FieldInfo field)
        {
            if (!field.IsStatic)
                throw new InvalidOperationException(
                    $"Reflection probe: {Describe(field.DeclaringType)}.{field.Name} is an INSTANCE field, not a " +
                    "static one — the member changed shape. Read it with Read<T>(field, instance) instead, or " +
                    "re-point this probe at whatever now holds the shared state.");
            return Read<T>(field, instance: null);
        }

        /// <summary>Read element <paramref name="index"/> of a reflected array, with a named failure on a null slot.</summary>
        public static object ReadElement(Array array, int index, string what)
            => array.GetValue(index)
               ?? throw new InvalidOperationException(
                   $"Reflection probe: {what}[{index}] was NULL inside the live range — the buffer's population " +
                   "contract changed; update this test's probe.");

        private static InvalidOperationException Missing(Type owner, string kind, string name)
            => new InvalidOperationException(
                $"Reflection probe: {Describe(owner)} has no {kind} named '{name}'. A white-box determinism test " +
                "probes it by name — the member was RENAMED, removed, or had its signature changed. Re-point the " +
                "probe at the new member (or restore the old one); never delete the assertion to make this pass.");

        private static string Describe(Type? t) => t?.Name ?? "<unknown type>";

        private static string Describe(Type[] types)
        {
            if (types.Length == 0) return "";
            var parts = new string[types.Length];
            for (int i = 0; i < types.Length; i++) parts[i] = Describe(types[i]);
            return string.Join(", ", parts);
        }
    }
}
