using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

return await LegacyApiProbeProgram.RunAsync(args);

internal static class LegacyApiProbeProgram
{
    private const int DefaultMaxResponseBytes = 64 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ProbeOptions.Parse(args);
            var requestBytes = await File.ReadAllBytesAsync(options.RequestFilePath);
            var requestSha256 = Convert.ToHexString(SHA256.HashData(requestBytes));
            var outputPath = ResolveOutputPath(options.OutputPath);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var cancellation = new CancellationTokenSource(options.Timeout);

            var oldResult = options.OldUrl is null
                ? null
                : await SendAsync(client, options.OldUrl, options.Method, requestBytes, options.MaxResponseBytes, cancellation.Token);
            var newResult = options.NewUrl is null
                ? null
                : await SendAsync(client, options.NewUrl, options.Method, requestBytes, options.MaxResponseBytes, cancellation.Token);
            var singleResult = options.Url is null
                ? null
                : await SendAsync(client, options.Url, options.Method, requestBytes, options.MaxResponseBytes, cancellation.Token);

            var result = new ProbeRun(
                CollectedAtUtc: DateTimeOffset.UtcNow,
                Request: new RequestEvidence(options.Method.Method, Path.GetFileName(options.RequestFilePath), requestBytes.LongLength, requestSha256),
                Single: singleResult,
                Old: oldResult,
                New: newResult,
                Comparison: oldResult is null || newResult is null ? null : Compare(oldResult, newResult));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
            PrintSummary(result, outputPath);
            return 0;
        }
        catch (ProbeUsageException exception)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            Console.Error.WriteLine("Use --help for supported probe commands.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Request timed out before a complete response fingerprint could be collected.");
            return 3;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"HTTP request failed: {exception.Message}");
            return 4;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"File or response stream failed: {exception.Message}");
            return 5;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Probe failed: {exception.Message}");
            return 6;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task<HttpResponseEvidence> SendAsync(HttpClient client, Uri url, HttpMethod method, byte[] requestBytes, int maxResponseBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new ByteArrayContent(requestBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var fingerprint = await FingerprintResponseAsync(response, maxResponseBytes, cancellationToken);
        stopwatch.Stop();

        return new HttpResponseEvidence(
            Endpoint: DisplayUrl(url),
            StatusCode: (int)response.StatusCode,
            ReasonPhrase: response.ReasonPhrase,
            ContentType: response.Content.Headers.ContentType?.MediaType,
            DeclaredContentLength: response.Content.Headers.ContentLength,
            ReceivedBytes: fingerprint.ReceivedBytes,
            Sha256: fingerprint.Sha256,
            StartsWithPdfSignature: fingerprint.StartsWithPdfSignature,
            ElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
            Location: response.Headers.Location is null ? null : DisplayUrl(response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(url, response.Headers.Location)));
    }

    private static async Task<ResponseFingerprint> FingerprintResponseAsync(HttpResponseMessage response, int maxResponseBytes, CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > 0 && declaredLength > maxResponseBytes)
        {
            throw new IOException($"Response declares {declaredLength} bytes, exceeding the configured maximum of {maxResponseBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var prefix = new byte[5];
        var prefixCount = 0;
        long received = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            received += read;
            if (received > maxResponseBytes)
            {
                throw new IOException($"Response exceeded the configured maximum of {maxResponseBytes} bytes.");
            }

            hash.AppendData(buffer, 0, read);
            var prefixRemaining = prefix.Length - prefixCount;
            if (prefixRemaining > 0)
            {
                var copyLength = Math.Min(prefixRemaining, read);
                Buffer.BlockCopy(buffer, 0, prefix, prefixCount, copyLength);
                prefixCount += copyLength;
            }
        }

        var startsWithPdf = prefixCount == prefix.Length && prefix.AsSpan().SequenceEqual("%PDF-"u8);
        return new ResponseFingerprint(received, Convert.ToHexString(hash.GetHashAndReset()), startsWithPdf);
    }

    private static ComparisonEvidence Compare(HttpResponseEvidence oldResponse, HttpResponseEvidence newResponse)
    {
        var statusMatches = oldResponse.StatusCode == newResponse.StatusCode;
        var contentTypeMatches = string.Equals(oldResponse.ContentType, newResponse.ContentType, StringComparison.OrdinalIgnoreCase);
        var signatureMatches = oldResponse.StartsWithPdfSignature == newResponse.StartsWithPdfSignature;
        var byteLengthMatches = oldResponse.ReceivedBytes == newResponse.ReceivedBytes;
        var contentHashMatches = string.Equals(oldResponse.Sha256, newResponse.Sha256, StringComparison.OrdinalIgnoreCase);
        return new ComparisonEvidence(statusMatches, contentTypeMatches, signatureMatches, byteLengthMatches, contentHashMatches,
            statusMatches && contentTypeMatches && signatureMatches && byteLengthMatches && contentHashMatches);
    }

    private static string ResolveOutputPath(string configuredPath)
    {
        var fullPath = Path.GetFullPath(configuredPath);
        return Path.HasExtension(fullPath) ? fullPath : Path.Combine(fullPath, "legacy-api-probe.json");
    }

    private static string DisplayUrl(Uri url)
    {
        var builder = new UriBuilder(url) { Query = string.Empty, Fragment = string.Empty, UserName = string.Empty, Password = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static void PrintSummary(ProbeRun result, string outputPath)
    {
        Console.WriteLine($"Probe evidence written to: {outputPath}");
        if (result.Single is not null) PrintResponse("Endpoint", result.Single);
        if (result.Old is not null) PrintResponse("Old", result.Old);
        if (result.New is not null) PrintResponse("New", result.New);
        if (result.Comparison is not null) Console.WriteLine($"Comparison overall match: {result.Comparison.OverallMatch}");
    }

    private static void PrintResponse(string label, HttpResponseEvidence response)
    {
        Console.WriteLine($"{label}: HTTP {response.StatusCode}; bytes={response.ReceivedBytes}; pdfSignature={response.StartsWithPdfSignature}; sha256={response.Sha256}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PEIS.LegacyApiProbe — raw HTTP/PDF compatibility evidence collector

            Single endpoint:
              --url <url> --request <json-file> --output <json-file-or-directory>

            Old/new comparison:
              --old-url <url> --new-url <url> --request <json-file> --output <json-file-or-directory>

            Optional: --method POST|PUT|PATCH, --timeout-seconds <1..600>, --max-response-bytes <1..268435456>

            The request body is relayed without parsing. Output contains only request size/hash and response metadata,
            PDF signature validity, byte count, and SHA-256; it never writes the request body or response body.
            """);
    }
}

internal sealed class ProbeOptions
{
    public Uri? Url { get; init; }
    public Uri? OldUrl { get; init; }
    public Uri? NewUrl { get; init; }
    public required string RequestFilePath { get; init; }
    public required string OutputPath { get; init; }
    public required HttpMethod Method { get; init; }
    public required TimeSpan Timeout { get; init; }
    public int MaxResponseBytes { get; init; }

    public static ProbeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new ProbeUsageException($"Unexpected positional argument '{argument}'.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ProbeUsageException($"Option '{argument}' requires a value.");
            if (!values.TryAdd(argument, args[++index])) throw new ProbeUsageException($"Option '{argument}' was supplied more than once.");
        }

        values.TryGetValue("--url", out var urlText);
        values.TryGetValue("--old-url", out var oldUrlText);
        values.TryGetValue("--new-url", out var newUrlText);
        var hasSingle = !string.IsNullOrWhiteSpace(urlText);
        var hasComparison = !string.IsNullOrWhiteSpace(oldUrlText) || !string.IsNullOrWhiteSpace(newUrlText);
        if (hasSingle == hasComparison) throw new ProbeUsageException("Provide either --url or both --old-url and --new-url.");
        if (hasComparison && (string.IsNullOrWhiteSpace(oldUrlText) || string.IsNullOrWhiteSpace(newUrlText))) throw new ProbeUsageException("Comparison mode requires both --old-url and --new-url.");

        var url = hasSingle ? ParseHttpUri(urlText!, "--url") : null;
        var oldUrl = hasComparison ? ParseHttpUri(oldUrlText!, "--old-url") : null;
        var newUrl = hasComparison ? ParseHttpUri(newUrlText!, "--new-url") : null;
        var request = Require(values, "--request");
        if (!File.Exists(request)) throw new ProbeUsageException($"Request file does not exist: '{request}'.");
        var output = Require(values, "--output");

        values.TryGetValue("--method", out var methodText);
        var method = new HttpMethod(string.IsNullOrWhiteSpace(methodText) ? "POST" : methodText.ToUpperInvariant());
        values.TryGetValue("--timeout-seconds", out var timeoutText);
        var timeoutSeconds = ParseBoundedInt(timeoutText, 90, 1, 600, "--timeout-seconds");
        values.TryGetValue("--max-response-bytes", out var maxBytesText);
        var maxResponseBytes = ParseBoundedInt(maxBytesText, 64 * 1024 * 1024, 1, 256 * 1024 * 1024, "--max-response-bytes");

        var knownOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--url", "--old-url", "--new-url", "--request", "--output", "--method", "--timeout-seconds", "--max-response-bytes" };
        var unknown = values.Keys.FirstOrDefault(option => !knownOptions.Contains(option));
        if (unknown is not null) throw new ProbeUsageException($"Unsupported option '{unknown}'.");

        return new ProbeOptions
        {
            Url = url,
            OldUrl = oldUrl,
            NewUrl = newUrl,
            RequestFilePath = Path.GetFullPath(request),
            OutputPath = output,
            Method = method,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxResponseBytes = maxResponseBytes
        };
    }

    private static Uri ParseHttpUri(string value, string option)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ProbeUsageException($"{option} must be an absolute HTTP or HTTPS URL.");
        return uri;
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string option) => values.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ProbeUsageException($"{option} is required.");

    private static int ParseBoundedInt(string? text, int defaultValue, int minimum, int maximum, string option)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum) throw new ProbeUsageException($"{option} must be an integer from {minimum} to {maximum}.");
        return value;
    }
}

internal sealed class ProbeUsageException(string message) : Exception(message);
internal sealed record RequestEvidence(string Method, string RequestFileName, long RequestBytes, string RequestSha256);
internal sealed record ResponseFingerprint(long ReceivedBytes, string Sha256, bool StartsWithPdfSignature);
internal sealed record HttpResponseEvidence(string Endpoint, int StatusCode, string? ReasonPhrase, string? ContentType, long? DeclaredContentLength, long ReceivedBytes, string Sha256, bool StartsWithPdfSignature, long ElapsedMilliseconds, string? Location);
internal sealed record ComparisonEvidence(bool StatusMatches, bool ContentTypeMatches, bool PdfSignatureMatches, bool ByteLengthMatches, bool ContentHashMatches, bool OverallMatch);
internal sealed record ProbeRun(DateTimeOffset CollectedAtUtc, RequestEvidence Request, HttpResponseEvidence? Single, HttpResponseEvidence? Old, HttpResponseEvidence? New, ComparisonEvidence? Comparison);
