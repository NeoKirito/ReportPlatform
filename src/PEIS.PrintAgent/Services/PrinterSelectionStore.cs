using System.Text.Json;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// Persists the physical Windows printer selected for each stable report/template Djid. The file lives under
/// ProgramData so upgrading or replacing the portable Agent directory does not lose workstation choices.
/// </summary>
public sealed class PrinterSelectionStore
{
    public const string FileName = "printer-selections.json";
    private readonly string _path;
    private readonly object _gate = new();

    public PrinterSelectionStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PEIS",
            "PrintAgent"))
    {
    }

    public PrinterSelectionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(directory, FileName);
    }

    public string? Get(string? djid)
    {
        var key = Normalize(djid);
        if (key is null) return null;
        lock (_gate)
        {
            var values = Read();
            return values.TryGetValue(key, out var printer) && !string.IsNullOrWhiteSpace(printer)
                ? printer.Trim()
                : null;
        }
    }

    public void Save(string? djid, string printerName)
    {
        var key = Normalize(djid);
        if (key is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        lock (_gate)
        {
            var values = Read();
            values[key] = printerName.Trim();
            Write(values);
        }
    }

    public void Remove(string? djid)
    {
        var key = Normalize(djid);
        if (key is null) return;
        lock (_gate)
        {
            var values = Read();
            if (values.Remove(key)) Write(values);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(_path);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A partially written or manually damaged file must not prevent previewing. The next selection repairs it.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write(Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string? Normalize(string? djid)
        => string.IsNullOrWhiteSpace(djid) ? null : djid.Trim();
}
