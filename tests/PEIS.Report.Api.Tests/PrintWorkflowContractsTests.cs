using System.Text.Json;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class PrintWorkflowContractsTests
{
    [Fact]
    public void Registration_print_expands_to_guide_and_barcode_logical_roles()
    {
        var options = Options.Create(new PrintRoutingOptions
        {
            Scenarios = new Dictionary<string, PrintScenarioOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["REGISTRATION_PRINT"] = new()
                {
                    Documents =
                    [
                        new PrintScenarioDocumentOptions { Key = "guide", ReportId = "GUIDE_A4", PrinterRole = "A4_GUIDE", Profile = "print-a4" },
                        new PrintScenarioDocumentOptions { Key = "barcode", ReportId = "REG_BARCODE", PrinterRole = "BARCODE", Profile = "label" }
                    ]
                }
            }
        });

        var scenario = new PrintScenarioCatalog(options).GetRequired("registration_print");

        Assert.Collection(scenario.Documents,
            guide =>
            {
                Assert.Equal("GUIDE_A4", guide.ReportId);
                Assert.Equal("A4_GUIDE", guide.PrinterRole);
            },
            barcode =>
            {
                Assert.Equal("REG_BARCODE", barcode.ReportId);
                Assert.Equal("BARCODE", barcode.PrinterRole);
            });
    }

    [Fact]
    public async Task Same_business_idempotency_key_creates_one_job()
    {
        var store = new PrintRequestIdempotencyStore();
        var calls = 0;
        Task<CreatePrintJobResponse> CreateAsync()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new CreatePrintJobResponse(Guid.NewGuid(), 2, 2, DateTimeOffset.UtcNow));
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.GetOrCreateAsync("REGISTRATION_PRINT", "REG-01", "registration-123", CreateAsync)));

        Assert.Equal(1, calls);
        Assert.Single(responses.Select(response => response.JobId).Distinct());
    }

    [Fact]
    public void Business_request_has_no_physical_printer_name()
    {
        using var json = JsonDocument.Parse("{\"tjh\":\"TJ-001\"}");
        var request = new BusinessPrintRequest(
            "REGISTRATION_PRINT",
            "REG-01",
            new Dictionary<string, JsonElement> { ["tjh"] = json.RootElement.GetProperty("tjh").Clone() },
            IdempotencyKey: "registration-123");

        Assert.Equal("REGISTRATION_PRINT", request.ActionCode);
        Assert.Equal("REG-01", request.StationId);
        Assert.Equal("registration-123", request.IdempotencyKey);
        Assert.False(JsonSerializer.Serialize(request).Contains("printer", StringComparison.OrdinalIgnoreCase));
    }
}
