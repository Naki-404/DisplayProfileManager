using System.Globalization;
using System.IO;

namespace Visor.Services;

/// <summary>
/// Pure, allocation-light parser for the PresentMon CSV stream (2.x column names, with the 1.x
/// names accepted as fallbacks). Columns are resolved by header NAME, never by index, so extra,
/// missing or reordered columns are tolerated: a row only needs a frame-time cell to yield a
/// <see cref="FrameRecord"/>. Feed it line by line (<see cref="Feed"/>) or a whole reader
/// (<see cref="Parse"/>). Not thread-safe; one instance per capture.
/// </summary>
public sealed class PresentMonCsvParser
{
    // 2.x name first, then 1.x names (PresentMon 1.x wrote them in this exact casing).
    private static readonly string[] TimeNames = { "CPUStartTime", "TimeInSeconds" };
    private static readonly string[] FrameTimeNames = { "FrameTime", "MsBetweenPresents", "msBetweenPresents" };
    private static readonly string[] GpuBusyNames = { "GPUBusy", "GPUTime", "MsGPUActive", "msGPUActive" };
    private static readonly string[] CpuBusyNames = { "CPUBusy" };
    private static readonly string[] DisplayLatencyNames = { "DisplayLatency", "MsUntilDisplayed", "msUntilDisplayed" };
    private static readonly string[] ClickToPhotonNames = { "ClickToPhotonLatency", "AllInputToPhotonLatency" };
    private static readonly string[] DroppedNames = { "Dropped" };
    private static readonly string[] DisplayedTimeNames = { "DisplayedTime" };
    private static readonly string[] ApplicationNames = { "Application" };
    private static readonly string[] ProcessIdNames = { "ProcessID", "ProcessId" };

    private int _time = -1, _frameTime = -1, _gpuBusy = -1, _cpuBusy = -1, _displayLatency = -1,
                _clickToPhoton = -1, _dropped = -1, _displayedTime = -1, _application = -1, _processId = -1;
    private string[] _header = Array.Empty<string>();
    private double _runningTime;        // fallback clock when there is no time column
    private bool _haveTime;

    /// <summary>True once a header line with a frame-time column has been accepted.</summary>
    public bool HasHeader { get; private set; }

    /// <summary>Header cells as read (empty before <see cref="HasHeader"/>).</summary>
    public IReadOnlyList<string> Header => _header;

    /// <summary>Rows that could not be parsed after the header (malformed / too short).</summary>
    public int RejectedRows { get; private set; }

    /// <summary>Rows that parsed into a <see cref="FrameRecord"/>.</summary>
    public int AcceptedRows { get; private set; }

    public bool HasGpuBusy => _gpuBusy >= 0;
    public bool HasCpuBusy => _cpuBusy >= 0;
    public bool HasDisplayLatency => _displayLatency >= 0;
    public bool HasClickToPhoton => _clickToPhoton >= 0;

    /// <summary>
    /// Accepts a header line. Returns false when the line has no recognised frame-time column
    /// (then the parser stays in its previous state, so a stray non-header line is harmless).
    /// </summary>
    public bool TryParseHeader(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var cells = Split(line.TrimStart('﻿'));
        int ft = IndexOf(cells, FrameTimeNames);
        if (ft < 0) return false;

        _header = cells;
        _frameTime = ft;
        _time = IndexOf(cells, TimeNames);
        _gpuBusy = IndexOf(cells, GpuBusyNames);
        _cpuBusy = IndexOf(cells, CpuBusyNames);
        _displayLatency = IndexOf(cells, DisplayLatencyNames);
        _clickToPhoton = IndexOf(cells, ClickToPhotonNames);
        _dropped = IndexOf(cells, DroppedNames);
        _displayedTime = IndexOf(cells, DisplayedTimeNames);
        _application = IndexOf(cells, ApplicationNames);
        _processId = IndexOf(cells, ProcessIdNames);
        _runningTime = 0;
        _haveTime = false;
        HasHeader = true;
        AcceptedRows = 0;
        RejectedRows = 0;
        return true;
    }

    /// <summary>Parses one data row. Requires <see cref="HasHeader"/>.</summary>
    public bool TryParseRow(string? line, out FrameRecord record)
    {
        record = default;
        if (!HasHeader || string.IsNullOrWhiteSpace(line)) return false;
        var cells = Split(line);
        if (_frameTime >= cells.Length || !TryDouble(cells[_frameTime], out double frameTime))
        {
            RejectedRows++;
            return false;
        }

        double time;
        if (TryCell(cells, _time, out double t))
        {
            time = t;
            _haveTime = true;
        }
        else
        {
            // No clock column (or "NA"): synthesize one from the frame times so windows still work.
            if (_haveTime) { RejectedRows++; return false; }
            _runningTime += frameTime / 1000.0;
            time = _runningTime;
        }

        bool dropped = false;
        if (_dropped >= 0 && _dropped < cells.Length)
            dropped = IsTruthy(cells[_dropped]);
        else if (_displayedTime >= 0 && _displayedTime < cells.Length)
            dropped = IsNa(cells[_displayedTime]);   // 2.x: never-displayed frames have DisplayedTime = NA

        record = new FrameRecord(
            time,
            frameTime,
            TryCell(cells, _gpuBusy, out double g) ? g : null,
            TryCell(cells, _cpuBusy, out double c) ? c : null,
            TryCell(cells, _displayLatency, out double d) ? d : null,
            TryCell(cells, _clickToPhoton, out double k) ? k : null,
            dropped)
        {
            Application = _application >= 0 && _application < cells.Length ? cells[_application] : null,
            ProcessId = _processId >= 0 && _processId < cells.Length && int.TryParse(cells[_processId], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ? pid : 0
        };
        AcceptedRows++;
        return true;
    }

    /// <summary>
    /// Convenience for streaming: header lines are detected automatically (a line containing a
    /// frame-time column name while no header is known yet, or a repeated header after a restart).
    /// </summary>
    public bool Feed(string? line, out FrameRecord record)
    {
        record = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (!HasHeader || LooksLikeHeader(line))
        {
            TryParseHeader(line);
            return false;
        }
        return TryParseRow(line, out record);
    }

    /// <summary>Parses a complete CSV document (header + rows).</summary>
    public IEnumerable<FrameRecord> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (Feed(line, out var rec))
                yield return rec;
        }
    }

    /// <summary>Parses a complete CSV document held in a string.</summary>
    public static List<FrameRecord> ParseAll(string csv)
    {
        var parser = new PresentMonCsvParser();
        using var reader = new StringReader(csv ?? "");
        return parser.Parse(reader).ToList();
    }

    private static bool LooksLikeHeader(string line)
    {
        foreach (var name in FrameTimeNames)
        {
            int i = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            // Whole cell match: bounded by start/end or commas.
            bool startOk = i == 0 || line[i - 1] == ',';
            int end = i + name.Length;
            bool endOk = end == line.Length || line[end] == ',' || line[end] == '\r';
            if (startOk && endOk) return true;
        }
        return false;
    }

    private static int IndexOf(string[] cells, string[] names)
    {
        foreach (var name in names)
            for (int i = 0; i < cells.Length; i++)
                if (string.Equals(cells[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    private static bool TryCell(string[] cells, int idx, out double value)
    {
        value = 0;
        return idx >= 0 && idx < cells.Length && TryDouble(cells[idx], out value);
    }

    private static bool TryDouble(string cell, out double value)
    {
        value = 0;
        if (IsNa(cell)) return false;
        return double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value);
    }

    private static bool IsNa(string cell)
    {
        var c = cell.AsSpan().Trim();
        return c.Length == 0 || c.Equals("NA", StringComparison.OrdinalIgnoreCase) || c.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string cell)
    {
        var c = cell.AsSpan().Trim();
        return c.Equals("1", StringComparison.Ordinal) || c.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Comma split that honours double-quoted cells (PresentMon quotes nothing today, but be safe).</summary>
    internal static string[] Split(string line)
    {
        if (line.IndexOf('"') < 0)
        {
            var simple = line.Split(',');
            for (int i = 0; i < simple.Length; i++) simple[i] = simple[i].Trim();
            return simple;
        }
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return result.ToArray();
    }
}
