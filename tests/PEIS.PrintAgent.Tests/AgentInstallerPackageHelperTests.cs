using System.IO;
using System.Text;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class AgentInstallerPackageHelperTests
{
    [Fact]
    public void Encode_and_extract_from_stream_roundtrip()
    {
        var original = new AgentInstallerConfig
        {
            ServerUrl = "http://192.168.0.237:82",
            StationId = "REG-01",
            SilentPrint = true,
            PrintBackend = "Spool",
            DefaultPrinter = "HP LaserJet"
        };

        var overlay = AgentInstallerPackageHelper.EncodeOverlay(original);
        Assert.NotEmpty(overlay);

        // Simulate a fake executable stream followed by the overlay
        using var ms = new MemoryStream();
        var dummyExeHeader = Encoding.ASCII.GetBytes("MZ_DUMMY_EXE_CONTENT_HERE_WITH_SECTIONS");
        ms.Write(dummyExeHeader, 0, dummyExeHeader.Length);
        ms.Write(overlay, 0, overlay.Length);

        var extracted = AgentInstallerPackageHelper.TryExtractConfigFromStream(ms);
        Assert.NotNull(extracted);
        Assert.Equal("http://192.168.0.237:82", extracted.ServerUrl);
        Assert.Equal("REG-01", extracted.StationId);
        Assert.True(extracted.SilentPrint);
        Assert.Equal("Spool", extracted.PrintBackend);
        Assert.Equal("HP LaserJet", extracted.DefaultPrinter);
    }

    [Fact]
    public void Extract_from_filename_works_as_fallback()
    {
        var config = AgentInstallerPackageHelper.TryExtractConfigFromFileName("PEIS-PrintAgent-Setup_192-168-0-237_82.exe");
        Assert.NotNull(config);
        Assert.Equal("http://192.168.0.237:82", config.ServerUrl);

        var configFromPath = AgentInstallerPackageHelper.TryExtractConfigFromFileName(@"C:\Downloads\PEIS-PrintAgent-Setup_10-20-30-40_8080.exe");
        Assert.NotNull(configFromPath);
        Assert.Equal("http://10.20.30.40:8080", configFromPath.ServerUrl);

        var invalid = AgentInstallerPackageHelper.TryExtractConfigFromFileName("PEIS-PrintAgent-Setup.exe");
        Assert.Null(invalid);
    }

    [Fact]
    public void Generate_download_file_name_creates_clean_ip_port_name()
    {
        var name = AgentInstallerPackageHelper.GenerateDownloadFileName("http://192.168.0.237:82");
        Assert.Equal("PEIS-PrintAgent-Setup_192-168-0-237_82.exe", name);

        var defaultName = AgentInstallerPackageHelper.GenerateDownloadFileName("invalid-url");
        Assert.Equal("PEIS-PrintAgent-Setup.exe", defaultName);
    }
}
