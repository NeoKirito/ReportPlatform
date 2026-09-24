using System.IO;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Controllers;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class AgentInstallControllerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "peis-agent-controller-tests", Guid.NewGuid().ToString("N"));

    public AgentInstallControllerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "PEIS.Report.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Development";
    }

    [Fact]
    public void SequenceStream_reads_all_streams_in_order()
    {
        var s1 = new MemoryStream(Encoding.UTF8.GetBytes("Hello, "));
        var s2 = new MemoryStream(Encoding.UTF8.GetBytes("World!"));
        using var seq = new SequenceStream(s1, s2);

        Assert.Equal(13, seq.Length);
        using var reader = new StreamReader(seq, Encoding.UTF8);
        var result = reader.ReadToEnd();
        Assert.Equal("Hello, World!", result);
    }

    [Fact]
    public void Probe_reports_offline_when_no_agent_registered()
    {
        var registry = new AgentRegistry(Options.Create(new AgentRegistryOptions()));
        var config = new ConfigurationBuilder().Build();
        var env = new TestWebHostEnvironment();

        var controller = new AgentInstallController(registry, config, env)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");

        var result = controller.Probe();
        var ok = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<AgentProbeResponse>(ok.Value);
        Assert.False(val.Online);
        Assert.Equal("192.168.1.50", val.ClientIp);
        Assert.Contains("未检测到", val.Message);
    }

    [Fact]
    public void Probe_reports_online_when_matching_client_ip_exists()
    {
        var registry = new AgentRegistry(Options.Create(new AgentRegistryOptions()));
        registry.TryRegister("conn-1", new AgentRegistration(
            "agent-01",
            "REG-01",
            "DESKTOP-ABC",
            new List<PrinterDescriptor>(),
            new Dictionary<string, string>(),
            "1.0.0"), "192.168.1.50");

        var config = new ConfigurationBuilder().Build();
        var env = new TestWebHostEnvironment();

        var controller = new AgentInstallController(registry, config, env)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");

        var result = controller.Probe();
        var ok = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<AgentProbeResponse>(ok.Value);
        Assert.True(val.Online);
        Assert.Equal("REG-01", val.StationId);
        Assert.Equal("agent-01", val.AgentId);
        Assert.Contains("在线", val.Message);
    }

    [Fact]
    public void DownloadSetup_streams_binary_with_injected_overlay()
    {
        var dummySetupPath = Path.Combine(_tempDir, "PEIS-PrintAgent-Setup.exe");
        File.WriteAllBytes(dummySetupPath, Encoding.ASCII.GetBytes("MZ_DUMMY_EXE_DATA"));

        var registry = new AgentRegistry(Options.Create(new AgentRegistryOptions()));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentInstall:SetupFilePath"] = dummySetupPath,
                ["AgentInstall:ServerUrl"] = "http://192.168.0.88:82"
            })
            .Build();

        var env = new TestWebHostEnvironment();
        var controller = new AgentInstallController(registry, config, env)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = controller.DownloadSetup(stationId: "STATION-VIP");
        var fileResult = Assert.IsType<FileStreamResult>(result);

        Assert.Equal("application/vnd.microsoft.portable-executable", fileResult.ContentType);
        Assert.Equal("PEIS-PrintAgent-Setup_192-168-0-88_82.exe", fileResult.FileDownloadName);

        // Read the streamed content and verify that AgentInstallerPackageHelper extracts the configuration!
        using var memoryStream = new MemoryStream();
        fileResult.FileStream.CopyTo(memoryStream);

        var extracted = AgentInstallerPackageHelper.TryExtractConfigFromStream(memoryStream);
        Assert.NotNull(extracted);
        Assert.Equal("http://192.168.0.88:82", extracted.ServerUrl);
        Assert.Equal("STATION-VIP", extracted.StationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }
}
