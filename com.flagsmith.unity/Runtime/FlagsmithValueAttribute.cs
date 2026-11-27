using System;
using System.Reflection;

namespace Flagsmith
{
    /// <summary>
    /// Marks a field or property that takes its value from a Flagsmith flag.
    /// bool members follow the flag's enabled state; other types take the flag's value.
    /// When the flag is missing or its value can't be converted, the member keeps its current value.
    /// Enums (by name, any case) and nullable types work. With IL2CPP and High stripping, mark bound
    /// properties with [UnityEngine.Scripting.Preserve] so the linker keeps setters only reflection calls.
    /// </summary>
    /// <example>
    /// [FlagsmithValue("run_speed")] float runSpeed = 7f;
    /// void Start() => flagsmith.Bind(this);
    /// </example>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class FlagsmithValueAttribute : Attribute
    {
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public string FlagName { get; }

        public FlagsmithValueAttribute(string flagName) => FlagName = flagName;

        /// <summary>Copies flag values into every [FlagsmithValue] member of <paramref name="target"/>.</summary>
        public static void Apply(Flags flags, object target)
        {
            for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(Members | BindingFlags.DeclaredOnly))
                    if (field.GetCustomAttribute<FlagsmithValueAttribute>() is { } a && flags.Get(a.FlagName) != null)
                        field.SetValue(target, Read(flags, a.FlagName, field.FieldType, field.GetValue(target)));
                foreach (var prop in type.GetProperties(Members | BindingFlags.DeclaredOnly))
                    if (prop.CanWrite && prop.GetCustomAttribute<FlagsmithValueAttribute>() is { } a && flags.Get(a.FlagName) != null)
                        prop.SetValue(target, Read(flags, a.FlagName, prop.PropertyType, prop.CanRead ? prop.GetValue(target) : null));
            }
        }

        static object Read(Flags flags, string name, Type type, object current) =>
            type == typeof(bool)
                ? flags.IsEnabled(name)
                : Flags.Convert(flags.Get(name).Value, type, current);
    }
}
