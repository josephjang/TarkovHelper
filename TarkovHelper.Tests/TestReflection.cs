using System.Reflection;
using System.Runtime.CompilerServices;

namespace TarkovHelper.Tests;

/// <summary>
/// The one home for the "build a singleton service without running its constructor" trick these
/// tests lean on. The services under test are lazy singletons whose constructors open
/// user_data.db and subscribe to other singletons, so a test that wants a plain object with two
/// fields set cannot call one. Creating the instance uninitialized and seeding the fields it
/// actually reads gives a faithful object with none of the process-wide state.
/// <para>
/// Centralised because the same two lines were copied into <c>ProgressServiceHarness</c>,
/// <c>ProfileSwitchingTests</c> and <c>TestLocalization</c>, each with its own spelling of the
/// "field is missing" message - and a renamed field must fail loudly, not silently leave a
/// default in place.
/// </para>
/// </summary>
internal static class TestReflection
{
    /// <summary>
    /// An instance of <typeparamref name="T"/> with every field at its default and no
    /// constructor run. Seed the fields the test needs with <see cref="SetPrivateField{T}"/>.
    /// </summary>
    internal static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    /// <summary>
    /// Sets a private instance field declared on <typeparamref name="T"/>, failing the test when
    /// no such field exists so a rename cannot quietly turn the seed into a no-op.
    /// </summary>
    internal static void SetPrivateField<T>(T instance, string name, object? value)
    {
        var field = typeof(T).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(field != null, $"{typeof(T).Name} has no private field '{name}'");
        field!.SetValue(instance, value);
    }
}
