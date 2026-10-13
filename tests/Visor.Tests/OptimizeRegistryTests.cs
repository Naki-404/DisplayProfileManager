using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

/// <summary>Dictionary-backed HKCU/HKLM fake: the Optimize tests never touch the real registry.</summary>
internal sealed class FakeOptimizeRegistry : IOptimizeRegistry
{
    public readonly Dictionary<string, RegValue> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, RegValue> MachineValues = new(StringComparer.OrdinalIgnoreCase);
    public bool FailWrites;
    public bool FailReads;
    /// <summary>Writes "succeed" but do not stick (policy / virtualization).</summary>
    public bool IgnoreWrites;
    public int Writes;

    private static string K(string keyPath, string valueName) => keyPath + "\\" + valueName;

    public RegValue? Get(RegAddress a) => Values.TryGetValue(K(a.KeyPath, a.ValueName), out var v) ? v : null;
    public FakeOptimizeRegistry Set(RegAddress a, RegValue v) { Values[K(a.KeyPath, a.ValueName)] = v; return this; }
    public FakeOptimizeRegistry Dword(RegAddress a, int v) => Set(a, RegValue.Dword(v));
    public FakeOptimizeRegistry Str(RegAddress a, string v) => Set(a, RegValue.String(v));
    public bool Has(RegAddress a) => Values.ContainsKey(K(a.KeyPath, a.ValueName));

    public RegValue? GetValue(string keyPath, string valueName)
    {
        if (FailReads) throw new UnauthorizedAccessException("read");
        return Values.TryGetValue(K(keyPath, valueName), out var v) ? v : null;
    }

    public void SetValue(string keyPath, string valueName, RegValue value)
    {
        Writes++;
        if (FailWrites) throw new UnauthorizedAccessException("write");
        if (IgnoreWrites) return;
        Values[K(keyPath, valueName)] = value;
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        Writes++;
        if (FailWrites) throw new UnauthorizedAccessException("delete");
        if (IgnoreWrites) return;
        Values.Remove(K(keyPath, valueName));
    }

    public RegValue? GetMachineValue(string keyPath, string valueName)
    {
        if (FailReads) throw new UnauthorizedAccessException("read");
        return MachineValues.TryGetValue(K(keyPath, valueName), out var v) ? v : null;
    }
}

public class OptimizeRegistryTests
{
    private static readonly RegAddress A = new(@"Software\Test", "Alpha");
    private static readonly RegAddress B = new(@"Software\Test", "Beta");

    [Theory]
    [InlineData("d:0")]
    [InlineData("d:-5")]
    [InlineData("s:")]
    [InlineData("s:~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("b:9E3E078012000000")]
    [InlineData("o:whatever")]
    public void RegValue_round_trips(string text)
    {
        Assert.True(RegValue.TryParse(text, out var v));
        Assert.Equal(text, v!.Serialize());
    }

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("d:x")]
    [InlineData("b:ABC")]
    [InlineData("b:ZZ")]
    [InlineData("q:1")]
    public void RegValue_rejects_garbage(string text)
    {
        Assert.False(RegValue.TryParse(text, out _));
    }

    [Fact]
    public void RegValue_typed_accessors()
    {
        Assert.Equal(7, RegValue.Dword(7).AsDword);
        Assert.Null(RegValue.String("7").AsDword);
        Assert.Equal("x y", RegValue.String("x y").AsString);
        Assert.Equal("0A1B", RegValue.Binary(new byte[] { 0x0A, 0x1B }).Data);
    }

    [Fact]
    public void ValueSet_serializes_absent_values_and_round_trips()
    {
        var set = new RegistryValueSet().Add(A, RegValue.Dword(1)).Add(B, null);
        var json = set.Serialize();
        Assert.Contains("\"v\":null", json);
        Assert.True(RegistryValueSet.TryParse(json, out var back));
        Assert.True(set.EqualsExactly(back));
        Assert.Equal(1, back.Get(A)!.AsDword);
        Assert.Null(back.Get(B));
        Assert.True(back.Covers(new[] { A, B }));
        Assert.False(back.Covers(new[] { B, A }));
        Assert.False(back.Covers(new[] { A }));
    }

    [Fact]
    public void ValueSet_equality_is_exact_kind_and_data()
    {
        var a = new RegistryValueSet().Add(A, RegValue.String("1"));
        var b = new RegistryValueSet().Add(A, RegValue.Dword(1));
        var c = new RegistryValueSet().Add(A, RegValue.String("1"));
        Assert.False(a.EqualsExactly(b));
        Assert.True(a.EqualsExactly(c));
        Assert.False(a.EqualsExactly(new RegistryValueSet().Add(A, null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"k\":\"a\"}]")]
    [InlineData("[{\"k\":\"a\",\"n\":\"b\",\"v\":\"zz\"}]")]
    [InlineData("not json")]
    public void ValueSet_rejects_garbage(string? text)
    {
        Assert.False(RegistryValueSet.TryParse(text, out _));
    }

    [Fact]
    public void ValueSet_read_and_write_through_registry()
    {
        var reg = new FakeOptimizeRegistry().Dword(A, 3);
        var read = RegistryValueSet.Read(reg, new[] { A, B }, "t")!;
        Assert.Equal(3, read.Get(A)!.AsDword);
        Assert.Null(read.Get(B));

        var target = new RegistryValueSet().Add(A, null).Add(B, RegValue.String("x"));
        Assert.True(target.Write(reg, "t"));
        Assert.False(reg.Has(A));
        Assert.Equal("x", reg.Get(B)!.AsString);

        reg.FailReads = true;
        Assert.Null(RegistryValueSet.Read(reg, new[] { A }, "t"));
        reg.FailReads = false;
        reg.FailWrites = true;
        Assert.False(target.Write(reg, "t"));
    }
}
