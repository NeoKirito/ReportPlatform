using Microsoft.Extensions.Options;

namespace PEIS.Report.Api.Printing;

public sealed class PrintScenarioCatalog(IOptions<PrintRoutingOptions> options)
{
    private readonly PrintRoutingOptions _options = options.Value;

    public PrintScenarioOptions GetRequired(string actionCode)
    {
        if (!_options.Scenarios.TryGetValue(actionCode, out var scenario))
            throw new KeyNotFoundException($"Unknown print action '{actionCode}'.");
        if (scenario.Documents.Count == 0)
            throw new InvalidOperationException($"Print action '{actionCode}' has no documents configured.");
        return scenario;
    }

    public IReadOnlyDictionary<string, PrintScenarioOptions> Snapshot() => _options.Scenarios;
}
