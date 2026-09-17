using System.Reflection;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Determinism guard: the sim must never use float or double (they can differ across CPUs and
/// compilers). The only permitted exception is the view-only conversion methods named ToFloat.
/// </summary>
public class NoFloatGuardTests
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const string AllowedMemberName = "ToFloat";

    [Fact]
    public void SimAssembly_HasNoFloatOrDoubleInMemberSignatures()
    {
        Assembly sim = typeof(Fix).Assembly;
        var violations = new List<string>();

        foreach (Type type in sim.GetTypes()) // includes nested and compiler-generated types
        {
            foreach (FieldInfo field in type.GetFields(AllDeclared))
            {
                Check(field.FieldType, type, field.Name, "field", violations);
            }

            foreach (PropertyInfo property in type.GetProperties(AllDeclared))
            {
                Check(property.PropertyType, type, property.Name, "property", violations);
            }

            foreach (EventInfo evt in type.GetEvents(AllDeclared))
            {
                if (evt.EventHandlerType != null)
                {
                    Check(evt.EventHandlerType, type, evt.Name, "event", violations);
                }
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(AllDeclared))
            {
                foreach (ParameterInfo p in ctor.GetParameters())
                {
                    Check(p.ParameterType, type, ctor.Name, "constructor parameter " + p.Name, violations);
                }
            }

            foreach (MethodInfo method in type.GetMethods(AllDeclared))
            {
                if (method.Name == AllowedMemberName)
                {
                    continue;
                }
                Check(method.ReturnType, type, method.Name, "return type", violations);
                foreach (ParameterInfo p in method.GetParameters())
                {
                    Check(p.ParameterType, type, method.Name, "parameter " + p.Name, violations);
                }
            }
        }

        Assert.True(violations.Count == 0,
            "float/double found in NovaFaction.Sim (use Fix instead):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Guard_DetectsFloatTypesAndSeesTheAllowedException()
    {
        // Sanity-check the guard itself so it cannot silently pass.
        Assert.True(IsFloating(typeof(float)));
        Assert.True(IsFloating(typeof(double)));
        Assert.True(IsFloating(typeof(float[])));
        Assert.True(IsFloating(typeof(double?)));
        Assert.True(IsFloating(typeof(List<float>)));
        Assert.True(IsFloating(typeof(float).MakeByRefType()));
        Assert.True(IsFloating(typeof(Func<int, double>)));
        Assert.False(IsFloating(typeof(Fix)));
        Assert.False(IsFloating(typeof(long)));
        Assert.False(IsFloating(typeof(decimal)));

        MethodInfo? toFloat = typeof(Fix).GetMethod(AllowedMemberName);
        Assert.NotNull(toFloat);
        Assert.Equal(typeof(float), toFloat!.ReturnType);
    }

    private static void Check(Type candidate, Type owner, string member, string kind, List<string> violations)
    {
        if (member == AllowedMemberName)
        {
            return;
        }
        if (IsFloating(candidate))
        {
            violations.Add($"{owner.FullName}.{member} ({kind}: {candidate})");
        }
    }

    private static bool IsFloating(Type type)
    {
        if (type == typeof(float) || type == typeof(double))
        {
            return true;
        }
        if (type.HasElementType) // arrays, pointers, ref/out parameters
        {
            return IsFloating(type.GetElementType()!);
        }
        if (type.IsGenericType)
        {
            // Generic arguments cover Nullable<float>, List<float>, Func<..., double> and so on.
            return type.GetGenericArguments().Any(IsFloating);
        }
        return false;
    }
}
