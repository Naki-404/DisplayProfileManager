using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Visor.Services.Optimize;

/// <summary>Registry value kinds the Optimize targets know how to snapshot and restore byte-exactly.</summary>
public enum RegValueKind
{
    Dword,
    String,
    Binary,
    /// <summary>Any other kind (QWORD, multi-string, expand-string): snapshotted as opaque text, never rewritten.</summary>
    Other
}

/// <summary>
/// One registry value exactly as stored: kind + canonical text (DWORD → decimal, string → text, binary → hex).
/// Absence is modelled as <c>null</c> at the call site, never as a sentinel.
/// </summary>
public sealed record RegValue(RegValueKind Kind, string Data)
{
    public static RegValue Dword(int value) => new(RegValueKind.Dword, value.ToString(CultureInfo.InvariantCulture));
    public static RegValue String(string value) => new(RegValueKind.String, value ?? "");
    public static RegValue Binary(byte[] bytes) => new(RegValueKind.Binary, Convert.ToHexString(bytes ?? Array.Empty<byte>()));

    public int? AsDword => Kind == RegValueKind.Dword && int.TryParse(Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    public string? AsString => Kind == RegValueKind.String ? Data : null;

    /// <summary>"d:0", "s:text", "b:0A1B" — the journal form.</summary>
    public string Serialize() => Kind switch
    {
        RegValueKind.Dword => "d:" + Data,
        RegValueKind.String => "s:" + Data,
        RegValueKind.Binary => "b:" + Data,
        _ => "o:" + Data
    };

    public static bool TryParse(string? text, out RegValue? value)
    {
        value = null;
        if (text == null) return false;
        if (text.Length < 2 || text[1] != ':') return false;
        string data = text[2..];
        switch (text[0])
        {
            case 'd':
                if (!int.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
                value = new RegValue(RegValueKind.Dword, data);
                return true;
            case 's':
                value = new RegValue(RegValueKind.String, data);
                return true;
            case 'b':
                if (data.Length % 2 != 0 || !data.All(Uri.IsHexDigit)) return false;
                value = new RegValue(RegValueKind.Binary, data.ToUpperInvariant());
                return true;
            case 'o':
                value = new RegValue(RegValueKind.Other, data);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// HKCU registry seam for the Optimize targets. Tests inject a dictionary-backed fake; nothing in the
/// test suite ever touches the real hive. All paths are relative to HKEY_CURRENT_USER.
/// </summary>
public interface IOptimizeRegistry
{
    /// <summary>The value exactly as stored, or null when the value (or its key) is absent.</summary>
    RegValue? GetValue(string keyPath, string valueName);

    /// <summary>Creates the key when missing and writes the value with its kind.</summary>
    void SetValue(string keyPath, string valueName, RegValue value);

    /// <summary>Deletes the value; no-op when absent (the key itself is never deleted).</summary>
    void DeleteValue(string keyPath, string valueName);

    /// <summary>Read-only HKLM lookup (status display only — Visor never writes HKLM).</summary>
    RegValue? GetMachineValue(string keyPath, string valueName);
}

/// <summary>Real HKCU implementation (plus read-only HKLM for status).</summary>
public sealed class HkcuOptimizeRegistry : IOptimizeRegistry
{
    public static readonly HkcuOptimizeRegistry Instance = new();

    public RegValue? GetValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return Read(key, valueName);
    }

    public RegValue? GetMachineValue(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return Read(key, valueName);
    }

    private static RegValue? Read(RegistryKey? key, string valueName)
    {
        if (key == null) return null;
        var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null) return null;
        var kind = key.GetValueKind(valueName);
        return kind switch
        {
            RegistryValueKind.DWord => RegValue.Dword(Convert.ToInt32(raw, CultureInfo.InvariantCulture)),
            RegistryValueKind.String => RegValue.String(raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""),
            RegistryValueKind.Binary => RegValue.Binary(raw as byte[] ?? Array.Empty<byte>()),
            _ => new RegValue(RegValueKind.Other, Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "")
        };
    }

    public void SetValue(string keyPath, string valueName, RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath)
                        ?? throw new InvalidOperationException("HKCU\\" + keyPath + " unavailable");
        switch (value.Kind)
        {
            case RegValueKind.Dword:
                key.SetValue(valueName, value.AsDword ?? throw new FormatException("DWORD " + value.Data), RegistryValueKind.DWord);
                break;
            case RegValueKind.String:
                key.SetValue(valueName, value.Data, RegistryValueKind.String);
                break;
            case RegValueKind.Binary:
                key.SetValue(valueName, Convert.FromHexString(value.Data), RegistryValueKind.Binary);
                break;
            default:
                // Opaque kinds are snapshot-only: refusing is safer than guessing the encoding.
                throw new NotSupportedException("Cannot write registry kind " + value.Kind);
        }
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>One (key, value name) address a target owns.</summary>
public readonly record struct RegAddress(string KeyPath, string ValueName)
{
    public override string ToString() => "HKCU\\" + KeyPath + "\\" + ValueName;
}

/// <summary>
/// Ordered snapshot of several registry values (null = absent) with a stable JSON journal form.
/// Targets serialize their whole state through this so "value absent" survives a restart exactly.
/// </summary>
public sealed class RegistryValueSet
{
    private readonly List<KeyValuePair<RegAddress, RegValue?>> _items = new();

    public IReadOnlyList<KeyValuePair<RegAddress, RegValue?>> Items => _items;

    public RegistryValueSet Add(RegAddress address, RegValue? value)
    {
        _items.Add(new KeyValuePair<RegAddress, RegValue?>(address, value));
        return this;
    }

    public RegValue? Get(RegAddress address)
    {
        foreach (var kv in _items)
            if (kv.Key == address) return kv.Value;
        return null;
    }

    /// <summary>Reads every address from <paramref name="registry"/>; null when any read throws.</summary>
    public static RegistryValueSet? Read(IOptimizeRegistry registry, IReadOnlyList<RegAddress> addresses, string logLabel)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var set = new RegistryValueSet();
        try
        {
            foreach (var a in addresses)
                set.Add(a, registry.GetValue(a.KeyPath, a.ValueName));
            return set;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, logLabel + " read");
            return null;
        }
    }

    /// <summary>Writes every present value and deletes every absent one. False when any write throws.</summary>
    public bool Write(IOptimizeRegistry registry, string logLabel)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            foreach (var (addr, value) in _items)
            {
                if (value == null) registry.DeleteValue(addr.KeyPath, addr.ValueName);
                else registry.SetValue(addr.KeyPath, addr.ValueName, value);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, logLabel + " write");
            return false;
        }
    }

    /// <summary>Byte-exact comparison (kind + data + absence), order-sensitive.</summary>
    public bool EqualsExactly(RegistryValueSet other)
    {
        if (other == null || other._items.Count != _items.Count) return false;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Key != other._items[i].Key) return false;
            var a = _items[i].Value;
            var b = other._items[i].Value;
            if (a == null || b == null)
            {
                if (a != b) return false;
                continue;
            }
            if (a.Kind != b.Kind || !string.Equals(a.Data, b.Data, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>JSON array of {k, n, v} where v is <see cref="RegValue.Serialize"/> or null for absent.</summary>
    public string Serialize()
    {
        // Hand-rolled to keep the journal form stable and tiny.
        var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var (addr, value) in _items)
            {
                writer.WriteStartObject();
                writer.WriteString("k", addr.KeyPath);
                writer.WriteString("n", addr.ValueName);
                if (value == null) writer.WriteNull("v");
                else writer.WriteString("v", value.Serialize());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static bool TryParse(string? text, out RegistryValueSet set)
    {
        set = new RegistryValueSet();
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) return false;
                if (!el.TryGetProperty("k", out var k) || !el.TryGetProperty("n", out var n) || !el.TryGetProperty("v", out var v))
                    return false;
                string? key = k.GetString();
                string? name = n.GetString();
                if (key == null || name == null) return false;
                RegValue? value = null;
                if (v.ValueKind != JsonValueKind.Null)
                {
                    if (!RegValue.TryParse(v.GetString(), out value)) return false;
                }
                set.Add(new RegAddress(key, name), value);
            }
            return set._items.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when the set covers exactly <paramref name="addresses"/> in order (guards against a journal from another target).</summary>
    public bool Covers(IReadOnlyList<RegAddress> addresses)
    {
        if (addresses.Count != _items.Count) return false;
        for (int i = 0; i < addresses.Count; i++)
            if (addresses[i] != _items[i].Key) return false;
        return true;
    }
}
