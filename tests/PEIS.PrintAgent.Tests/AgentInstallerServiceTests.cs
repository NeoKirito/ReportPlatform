using System.IO;
using PEIS.PrintAgent.Setup;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class AgentInstallerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "peis-installer-tests", Guid.NewGuid().ToString("N"));

    public AgentInstallerServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MergeConfigFile_creates_new_config_when_not_exists()
    {
        var iniPath = Path.Combine(_tempDir, "config.ini");
        var config = new AgentInstallerConfig
        {
            ServerUrl = "http://192.168.1.100:82",
            SilentPrint = true,
            PrintBackend = "WinPrint"
        };

        AgentInstallerService.MergeConfigFile(iniPath, config);

        Assert.True(File.Exists(iniPath));
        var lines = File.ReadAllLines(iniPath);
        Assert.Contains("ServerUrl=http://192.168.1.100:82", lines);
        Assert.Contains("SilentPrint=true", lines);
        Assert.Contains("PrintBackend=WinPrint", lines);
    }

    [Fact]
    public void MergeConfigFile_updates_server_url_while_preserving_existing_custom_settings()
    {
        var iniPath = Path.Combine(_tempDir, "config.ini");
        var existingContent = @"# Existing Configuration
StationId=MY-CUSTOM-STATION
DefaultPrinter=Zebra-Barcode-01
ServerUrl=http://old-server:8080
CustomSetting=KeepThisValue
";
        File.WriteAllText(iniPath, existingContent);

        var newConfig = new AgentInstallerConfig
        {
            ServerUrl = "http://10.0.0.88:82"
        };

        AgentInstallerService.MergeConfigFile(iniPath, newConfig);

        var lines = File.ReadAllLines(iniPath);
        Assert.Contains("ServerUrl=http://10.0.0.88:82", lines);
        Assert.DoesNotContain("ServerUrl=http://old-server:8080", lines);
        Assert.Contains("StationId=MY-CUSTOM-STATION", lines);
        Assert.Contains("DefaultPrinter=Zebra-Barcode-01", lines);
        Assert.Contains("CustomSetting=KeepThisValue", lines);
    }

    [Fact]
    public void ResolveConfiguration_parses_command_line_flags()
    {
        var args = new[]
        {
            "--server-url=http://192.168.10.50:9000",
            "--station-id=REG-10",
            "--silent-print=true",
            "--backend=Shell"
        };

        var cfg = AgentInstallerService.ResolveConfiguration(args);
        Assert.Equal("http://192.168.10.50:9000", cfg.ServerUrl);
        Assert.Equal("REG-10", cfg.StationId);
        Assert.True(cfg.SilentPrint);
        Assert.Equal("Shell", cfg.PrintBackend);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }
}
