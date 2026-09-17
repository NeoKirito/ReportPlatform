using System.Text.Json;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// FastReport 专属单据的打印方式配置对象。
/// 支持单据级别的物理打印机路由、打印行为控制（静默/预览）、单双面、纸张方向以及份数设置。
/// </summary>
public sealed class DjidPrintPreference
{
    public string Djid { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>
    /// 打印行为：
    /// Auto = 跟随接口与全局默认；
    /// Silent = 强制直接静默出纸（无需用户确认）；
    /// Preview = 强制弹出预览窗口由用户确认。
    /// </summary>
    public string PrintBehavior { get; set; } = "Auto";

    /// <summary>
    /// 单双面：
    /// Auto = 跟随接口请求；
    /// Simplex = 强制单面；
    /// DuplexLong = 双面(长边翻转)；
    /// DuplexShort = 双面(短边翻转)。
    /// </summary>
    public string Duplex { get; set; } = "Auto";

    /// <summary>
    /// 纸张方向：
    /// Auto = 跟随模板/驱动默认；
    /// Portrait = 纵向；
    /// Landscape = 横向。
    /// </summary>
    public string Orientation { get; set; } = "Auto";

    /// <summary>
    /// 打印份数：默认为 1 份；大于 0 为强制固定份数。
    /// </summary>
    public int Copies { get; set; } = 1;
}

/// <summary>
/// 持久化每种稳定 FastReport 单据 (djid) 的打印机偏好和输出选项。
/// 文件存储于 ProgramData\PEIS\PrintAgent\printer-selections.json，避免升级丢失设置。
/// 具备向后兼容旧版纯字符串 { "djid": "printerName" } 格式的能力。
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
        var pref = GetPreference(djid);
        return string.IsNullOrWhiteSpace(pref?.PrinterName) ? null : pref.PrinterName.Trim();
    }

    public DjidPrintPreference? GetPreference(string? djid)
    {
        var key = Normalize(djid);
        if (key is null) return null;
        lock (_gate)
        {
            var values = Read();
            return values.TryGetValue(key, out var pref) ? pref : null;
        }
    }

    public Dictionary<string, DjidPrintPreference> GetAll()
    {
        lock (_gate)
        {
            return Read();
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
            if (!values.TryGetValue(key, out var existing))
            {
                existing = new DjidPrintPreference { Djid = key };
                values[key] = existing;
            }
            existing.PrinterName = printerName.Trim();
            Write(values);
        }
    }

    public void Save(DjidPrintPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var key = Normalize(preference.Djid);
        if (key is null) return;

        preference.Djid = key;
        lock (_gate)
        {
            var values = Read();
            values[key] = preference;
            Write(values);
        }
    }

    public void SaveAll(IEnumerable<DjidPrintPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var values = new Dictionary<string, DjidPrintPreference>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in preferences)
            {
                var key = Normalize(item.Djid);
                if (key is null) continue;
                item.Djid = key;
                values[key] = item;
            }
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

    private Dictionary<string, DjidPrintPreference> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, DjidPrintPreference>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var djid = prop.Name.Trim();
                if (string.IsNullOrEmpty(djid)) continue;

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    // 兼容旧版纯字符串 { "tjdjd": "HP LaserJet" }
                    result[djid] = new DjidPrintPreference
                    {
                        Djid = djid,
                        PrinterName = prop.Value.GetString()?.Trim() ?? string.Empty,
                        Copies = 1
                    };
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var pref = JsonSerializer.Deserialize<DjidPrintPreference>(prop.Value.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (pref is not null)
                    {
                        pref.Djid = djid;
                        if (pref.Copies <= 0) pref.Copies = 1;
                        result[djid] = pref;
                    }
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write(Dictionary<string, DjidPrintPreference> values)
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
