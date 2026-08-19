using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Saydin.CalendarData.Tests;

public sealed class CalendarAcquisitionTests
{
    [Fact]
    public async Task Acquire_ProducesPrivateVerifiedAtomicQuarantine_FromRawBytes()
    {
        using var temp = new TempRoot();
        var manifest = CalendarDataTestRoot.ReadManifest();
        var source = manifest.Sources.Single(item => item.Id == "tcmb-month-202608");
        var raw = CalendarDataTestRoot.Raw(source.Id).Concat("\n"u8.ToArray()).ToArray();
        var refreshedHash = Convert.ToHexStringLower(SHA256.HashData(raw));
        Assert.NotEqual(source.RawSha256, refreshedHash);
        var handler = new DelegateHandler((request, _) => Task.FromResult(Response(
            HttpStatusCode.OK, raw, source.MediaType)));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var plan = new CalendarAcquisitionPlan
        {
            SchemaVersion = 1,
            SnapshotSetId = "cal-test-2026-08-18",
            Calendars = manifest.Calendars,
            Sources = [Request(source)],
        };
        var planPath = Path.Combine(temp.Path, "plan.json");
        await File.WriteAllBytesAsync(planPath,
            JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var staging = Path.Combine(temp.Path, "staging");
        var acquisition = new CalendarAcquisition(client,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 18, 8, 15, 0, TimeSpan.Zero)));

        var output = await acquisition.RunAsync(new CalendarAcquisitionOptions(
            CalendarDataTestRoot.DataRoot, planPath, staging, "bundle-under-review",
            TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal(Path.Combine(staging, "bundle-under-review"), output);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(staging, ".pending-*"));
        var verified = CalendarDataGenerator.LoadVerified(output);
        verified.EnsureInputsUnchanged();
        var acquired = verified.Manifest.Sources.Single(item => item.Id == source.Id);
        Assert.Equal(refreshedHash, acquired.RawSha256);
        Assert.Equal("2026-08-18T08:15:00Z", acquired.RetrievedAt);
        Assert.False(File.Exists(Path.Combine(output, source.SnapshotPath)));
        var expectedInventory = verified.Manifest.Sources
            .Select(item => item.SnapshotPath)
            .Concat(verified.Manifest.Calendars.Select(item => item.OutputPath))
            .Concat(["source-manifest.json", "expected-output.json", "review-envelope.json"])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualInventory = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedInventory, actualInventory);
        Assert.Equal(1, handler.CallCount);
        var envelope = JsonSerializer.Deserialize<CalendarReviewEnvelope>(
            await File.ReadAllBytesAsync(Path.Combine(output, "review-envelope.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(output, "source-manifest.json")))),
            envelope.SourceManifestSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(output, "expected-output.json")))),
            envelope.ExpectedOutputSha256);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(output);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        }
    }

    [Fact]
    public async Task Acquire_RejectsCoverageRegressionBeforeNetwork()
    {
        using var temp = new TempRoot();
        var manifest = CalendarDataTestRoot.ReadManifest();
        var source = manifest.Sources.Single(item => item.Id == "tcmb-month-202608");
        var regressed = manifest.Calendars.Select(calendar => new CalendarDefinition
        {
            Code = calendar.Code,
            CoverageFrom = calendar.CoverageFrom,
            CoverageThrough = calendar.Code == CalendarDataGenerator.TcmbCode
                ? "2026-08-16" : calendar.CoverageThrough,
            OutputPath = calendar.OutputPath,
        }).ToArray();
        var plan = new CalendarAcquisitionPlan
        {
            SchemaVersion = 1,
            SnapshotSetId = "cal-regression-test",
            Calendars = regressed,
            Sources = [Request(source)],
        };
        var planPath = Path.Combine(temp.Path, "regression-plan.json");
        await File.WriteAllBytesAsync(planPath,
            JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var handler = new DelegateHandler((_, _) => throw new InvalidOperationException("network must not run"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).RunAsync(new CalendarAcquisitionOptions(
                CalendarDataTestRoot.DataRoot, planPath, Path.Combine(temp.Path, "staging"),
                "regressed", TimeSpan.FromSeconds(2)), CancellationToken.None));

        Assert.Equal("calendar_coverage_regression", error.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("https://evil.example/kurlar/202506/Jun_tr.html")]
    [InlineData("http://www.tcmb.gov.tr/kurlar/202506/Jun_tr.html")]
    [InlineData("https://www.tcmb.gov.tr/kurlar/202505/May_tr.html")]
    [InlineData("https://www.tcmb.gov.tr:444/kurlar/202506/Jun_tr.html")]
    public async Task Redirect_RevalidatesSchemeHostPortAndExactPath(string redirect)
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(redirect) },
        }));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).DownloadForTestAsync(Source(), Options(temp.Path), CancellationToken.None));

        Assert.Contains(error.Code, new[] { "source_uri_invalid", "source_uri_not_allowlisted" });
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RedirectLoop_IsBounded()
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = request.RequestUri },
        }));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).DownloadForTestAsync(Source(), Options(temp.Path, redirects: 1), CancellationToken.None));

        Assert.Equal("source_redirect_invalid", error.Code);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task MediaTypeMismatch_FailsClosed()
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, "<html/>"u8.ToArray(), "application/octet-stream")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).DownloadForTestAsync(Source(), Options(temp.Path), CancellationToken.None));

        Assert.Equal("source_media_type_mismatch", error.Code);
    }

    [Fact]
    public async Task DeclaredAndStreamedOversize_AreRejected()
    {
        using var temp = new TempRoot();
        var declared = new DelegateHandler((_, _) =>
        {
            var response = Response(HttpStatusCode.OK, "x"u8.ToArray(), "text/html");
            response.Content.Headers.ContentLength = OfficialSourcePolicy.HtmlMaximumBytes + 1;
            return Task.FromResult(response);
        });
        using var declaredClient = new HttpClient(declared) { Timeout = Timeout.InfiniteTimeSpan };
        var declaredError = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(declaredClient).DownloadForTestAsync(Source(), Options(temp.Path), CancellationToken.None));

        var streamed = new DelegateHandler((_, _) =>
        {
            var content = new UnknownLengthContent(new byte[OfficialSourcePolicy.HtmlMaximumBytes + 1]);
            content.Headers.ContentType = new("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var streamedClient = new HttpClient(streamed) { Timeout = Timeout.InfiniteTimeSpan };
        var streamedError = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(streamedClient).DownloadForTestAsync(Source(), Options(temp.Path), CancellationToken.None));

        Assert.Equal("source_size_invalid", declaredError.Code);
        Assert.Equal("source_size_invalid", streamedError.Code);
    }

    [Fact]
    public async Task NetworkFault_RetriesBoundedly_ThenSucceeds()
    {
        using var temp = new TempRoot();
        var handlerCalls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            if (handlerCalls++ < 2) throw new HttpRequestException("fixture network fault");
            return Task.FromResult(Response(HttpStatusCode.OK, "<html/>"u8.ToArray(), "text/html"));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var delays = 0;
        var acquisition = new CalendarAcquisition(client, TimeProvider.System, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var raw = await acquisition.DownloadForTestAsync(
            Source(), Options(temp.Path, attempts: 3), CancellationToken.None);

        Assert.Equal("<html/>"u8.ToArray(), raw);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Provider5xx_RetriesBoundedly_ThenReturnsStableFailure()
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var delays = 0;
        var acquisition = new CalendarAcquisition(client, TimeProvider.System, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            acquisition.DownloadForTestAsync(
                Source(), Options(temp.Path, attempts: 3), CancellationToken.None));

        Assert.Equal("source_http_retry_exhausted", error.Code);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task ContentEncoding_IsRejectedEvenWhenMediaTypeMatches()
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler((_, _) =>
        {
            var response = Response(HttpStatusCode.OK, "<html/>"u8.ToArray(), "text/html");
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).DownloadForTestAsync(Source(), Options(temp.Path), CancellationToken.None));

        Assert.Equal("source_content_encoding_invalid", error.Code);
    }

    [Fact]
    public async Task RequestTimeout_IsStableFailure()
    {
        using var temp = new TempRoot();
        var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, "never"u8.ToArray(), "text/html");
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var options = Options(temp.Path) with { RequestTimeout = TimeSpan.FromMilliseconds(20) };

        var error = await Assert.ThrowsAsync<CalendarDataException>(() =>
            Downloader(client).DownloadForTestAsync(Source(), options, CancellationToken.None));

        Assert.Equal("source_timeout", error.Code);
    }

    [Fact]
    public void SnapshotSymlink_IsRejectedBeforeRead()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempRoot();
        var raw = "<html/>"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var external = Path.Combine(temp.Path, "external.html");
        File.WriteAllBytes(external, raw);
        var directory = Path.Combine(temp.Path, "bundle", "snapshots", "sha256");
        Directory.CreateDirectory(directory);
        var snapshot = Path.Combine(directory, $"{hash}.html");
        File.CreateSymbolicLink(snapshot, external);
        var source = Source(hash);
        var manifest = new SourceManifest
        {
            SchemaVersion = 1,
            SnapshotSetId = "test",
            Calendars = [new CalendarDefinition
            {
                Code = CalendarDataGenerator.TcmbCode,
                CoverageFrom = "2025-06-01",
                CoverageThrough = "2025-06-30",
                OutputPath = "normalized/test.csv",
            }],
            Sources = [source],
        };

        var error = Assert.Throws<CalendarDataException>(() =>
            new SourceSnapshotStore(Path.Combine(temp.Path, "bundle"), manifest).Read(source));

        Assert.Equal("snapshot_path_unsafe", error.Code);
    }

    private static CalendarAcquisition Downloader(HttpClient client) =>
        new(client, TimeProvider.System, (_, _) => Task.CompletedTask);

    private static CalendarAcquisitionOptions Options(
        string root, int attempts = 1, int redirects = 3) => new(
        CalendarDataTestRoot.DataRoot,
        Path.Combine(root, "unused.json"),
        root,
        "output",
        TimeSpan.FromSeconds(2),
        attempts,
        redirects);

    private static SourceDefinition Source(string? hash = null)
    {
        hash ??= new string('0', 64);
        return new SourceDefinition
        {
            Id = "tcmb-month-202506",
            CalendarCode = CalendarDataGenerator.TcmbCode,
            Kind = "tcmbMonthlyArchive",
            Role = "authority",
            Uri = "https://www.tcmb.gov.tr/kurlar/202506/Jun_tr.html",
            MediaType = "text/html",
            RetrievedAt = "2026-08-18T00:00:00Z",
            RawSha256 = hash,
            SnapshotPath = $"snapshots/sha256/{hash}.html",
            Year = 2025,
            Month = 6,
        };
    }

    private static CalendarAcquisitionSource Request(SourceDefinition source) => new()
    {
        Id = source.Id,
        CalendarCode = source.CalendarCode,
        Kind = source.Kind,
        Role = source.Role,
        Uri = source.Uri,
        MediaType = source.MediaType,
        Year = source.Year,
        Month = source.Month,
    };

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string mediaType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(),
                $".saydin-calendar-acq-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
