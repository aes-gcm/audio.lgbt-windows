using AudioLgbt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class UpdateChecks
{
    internal static async Task GitHub()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(AppUpdate.Feed);
        var release = JsonSerializer.Deserialize<UpdateRelease>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var updater = new AppUpdate();
        var progress = new Progress();
        string result = await updater.Download(release, Path.GetFullPath("dist/github-download-check"), progress, default);
        if (progress.Received != release.Size || new FileInfo(result).Length != release.Size) throw new Exception("GitHub download incomplete");
        Console.WriteLine($"PASS real GitHub feed, release CDN redirects, progress, size and SHA256: {release.Version}");
    }
    internal sealed class Handler(byte[] bytes, UpdateRelease release, bool slow = false) : HttpMessageHandler
    {
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (slow) await Task.Delay(700, token);
            return new(Status) { Content = request.RequestUri!.AbsolutePath.EndsWith(".json")
                ? new StringContent(JsonSerializer.Serialize(release), Encoding.UTF8, "application/json")
                : slow ? new StreamContent(new SlowStream(bytes)) : new ByteArrayContent(bytes) };
        }
    }
    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(120, token); return await base.ReadAsync(buffer[..Math.Min(16384, buffer.Length)], token); }
    }
    private sealed class RedirectHandler(byte[] bytes, Uri destination, bool loop = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!loop && request.RequestUri!.Host == "release-assets.githubusercontent.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = destination;
            return Task.FromResult(response);
        }
    }
    private sealed class Progress : IProgress<UpdateProgress>
    { internal long Received; public void Report(UpdateProgress p) { if (p.Received < Received || p.Received > p.Total) throw new Exception("Invalid progress"); Received = p.Received; } }
    internal static int Preview()
    {
        Application.EnableVisualStyles();
        byte[] bytes = new byte[8 * 1024 * 1024];
        var release = new UpdateRelease("0.3.1", "x64", "https://github.com/aes-gcm/audio.lgbt-windows/releases/download/v0.3.1/audio.lgbt-0.3.1-x64.exe", bytes.Length, new string('0',64));
        // Deliberately invalid hash: a UI fixture must never install arbitrary bytes.
        using var ui = new UpdateWindow([], new AppUpdate(new Handler(bytes, release, true)));
        ui.ShowDialog(); return 0;
    }
    internal static async Task Run()
    {
        static void Assert(bool condition) { if (!condition) throw new Exception("Update assertion failed"); }
        Assert(AppUpdate.IsNewer("0.3.0-preview.4", "0.3.0-preview.3"));
        Assert(AppUpdate.IsNewer("0.3.0", "0.3.0-preview.4"));
        Assert(!AppUpdate.IsNewer("0.3.0-preview.4", "0.3.0"));
        Assert(!AppUpdate.IsNewer("0.3.0-preview.4", "0.3.0-preview.4"));
        var bytes = new byte[512000]; Random.Shared.NextBytes(bytes);
        var release = new UpdateRelease("0.3.1", "x64", "https://github.com/aes-gcm/audio.lgbt-windows/releases/download/v0.3.1/audio.lgbt-0.3.1-x64.exe", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        string root = Path.GetFullPath("dist/update-checks"); Directory.CreateDirectory(root);
        using var updater = new AppUpdate(new Handler(bytes,release));
        Assert(await updater.Check(default) == release);
        var progress = new Progress(); string result = await updater.Download(release,Path.Combine(root,"valid"),progress,default);
        Assert(File.ReadAllBytes(result).SequenceEqual(bytes) && progress.Received==bytes.Length);
        Assert(AppUpdate.Feed.Host == "raw.githubusercontent.com");
        using (var redirected = new AppUpdate(new RedirectHandler(bytes, new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/test?signature=fixture"))))
            Assert(File.ReadAllBytes(await redirected.Download(release, Path.Combine(root,"github"), new Progress(), default)).SequenceEqual(bytes));
        foreach (var destination in new[] { "https://evil.example/update.exe", "http://release-assets.githubusercontent.com/test", "https://github.com/other/repository/update.exe" })
        {
            using var rejected = new AppUpdate(new RedirectHandler(bytes, new Uri(destination)));
            try { await rejected.Download(release, Path.Combine(root,"redirect-bad"), new Progress(), default); throw new Exception("Untrusted redirect accepted"); } catch (InvalidDataException) { }
        }
        using (var loop = new AppUpdate(new RedirectHandler(bytes, new Uri("https://release-assets.githubusercontent.com/loop"), true)))
        { try { await loop.Download(release, Path.Combine(root,"loop"), new Progress(), default); throw new Exception("Redirect loop accepted"); } catch (InvalidDataException) { } }
        foreach (var invalid in new[] { release with { Url="http://audio.lgbt/downloads/audio.lgbt-0.3.1-x64.exe" }, release with { Url="https://evil.example/update.exe" }, release with { Architecture="arm64" }, release with { Sha256="bad" }, release with { Version="../../bad" } })
        { try { AppUpdate.Validate(invalid); throw new Exception("Unsafe manifest accepted"); } catch (InvalidDataException) { } }
        var bad = release with { Sha256=new string('0',64) };
        try { await updater.Download(bad,Path.Combine(root,"bad"),new Progress(),default); throw new Exception("Invalid hash accepted"); } catch (InvalidDataException) { }
        Assert(!File.Exists(Path.Combine(root,"bad","Setup.exe")) && !File.Exists(Path.Combine(root,"bad","Setup.exe.partial")));
        var shortBytes=bytes[..100]; using var truncated=new AppUpdate(new Handler(shortBytes,release));
        try { await truncated.Download(release,Path.Combine(root,"short"),new Progress(),default); throw new Exception("Truncated installer accepted"); } catch (InvalidDataException) { }
        using var cancelled=new AppUpdate(new Handler(bytes,release,true)); using var cancel=new CancellationTokenSource(100);
        try { await cancelled.Download(release,Path.Combine(root,"cancel"),new Progress(),cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        using var failed=new AppUpdate(new Handler(bytes,release) { Status=HttpStatusCode.ServiceUnavailable });
        try { await failed.Check(default); throw new Exception("HTTP failure ignored"); } catch (HttpRequestException) { }
        Console.WriteLine("Update checks passed: versions, manifest boundary, download/progress, checksum, truncated response, cancellation, HTTP failure.");
    }
}

