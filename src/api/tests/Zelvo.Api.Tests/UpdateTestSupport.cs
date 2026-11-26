using System.IO.Compression;
using System.Security.Cryptography;
using Zelvo.Licensing;
using Zelvo.Updater;

namespace Zelvo.Api.Tests;

/// <summary>Update packages made at run time (task F4): stand-in image archives with random bytes, signed with the test key.</summary>
internal sealed class UpdateTestSupport : IDisposable
{
    public UpdateTestSupport()
    {
        Folder = Directory.CreateTempSubdirectory("zelvo-updates-").FullName;
        Directory.CreateDirectory(Options.PackagesDirectory);
        Directory.CreateDirectory(Options.InboxDirectory);
    }

    public string Folder { get; }

    public static IReadOnlyList<string> Images { get; } = ["zelvo/api", "zelvo/web"];

    public UpdateOptions Options => new() { Directory = Path.Combine(Folder, "updates"), Images = [.. Images] };

    /// <summary>Writes a signed package for <paramref name="version"/>.</summary>
    public async Task<string> PackageAsync(LicensingTestSupport licensing, string version, UpdatePackageType type = UpdatePackageType.Security,
        IReadOnlyList<string>? images = null, string? keyId = null, RSA? key = null, string? requiresVersion = null)
    {
        var content = Directory.CreateTempSubdirectory("zelvo-content-").FullName;
        Directory.CreateDirectory(Path.Combine(content, "images"));
        var list = new List<UpdatePackageImage>();
        foreach (var image in images ?? Images)
        {
            var file = $"images/{image.Split('/')[1]}.tar";
            await File.WriteAllBytesAsync(Path.Combine(content, file), RandomNumberGenerator.GetBytes(2048));
            list.Add(new UpdatePackageImage($"{image}:{version}", file));
        }

        await File.WriteAllTextAsync(Path.Combine(content, "NOTICE.txt"), "Release files");
        var manifest = new UpdatePackageManifest
        {
            Version = version, Type = type, RequiresVersion = requiresVersion, ReleaseNotes = new UpdateReleaseNotes("Fixes", "إصلاحات"), ExpectedMinutes = 20,
            CreatedAt = DateTimeOffset.UtcNow, Images = list,
        };
        var output = Path.Combine(Folder, $"zelvo-{version}-{Guid.NewGuid():N}.zpkg");
        await UpdatePackageWriter.WriteAsync(manifest, content, output, keyId ?? licensing.KeyId, key ?? LicensingTestSupport.Key, TestContext.Current.CancellationToken);
        Directory.Delete(content, recursive: true);
        return output;
    }

    /// <summary>Adds a file the manifest does not list, or replaces one's bytes.</summary>
    public static void Change(string package, string entry, bool add)
    {
        using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
        if (!add)
        {
            zip.GetEntry(entry)!.Delete();
        }

        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(RandomNumberGenerator.GetBytes(2048));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A container host that records what the installer asked for and fails where a test says.</summary>
internal sealed class FakeContainerHost : IContainerHost
{
    public List<string> Calls { get; } = [];

    /// <summary>The call that fails (for example "migrate"); rollback calls are named rollback-… only by the installer's steps.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> LoadImageAsync(string archive, CancellationToken cancellationToken)
    {
        Record("load");
        // A docker save archive of the image named after its file: zelvo/<name>:<version> comes from the manifest in tests.
        return Task.FromResult<IReadOnlyList<string>>(Loaded.TryGetValue(Path.GetFileName(archive), out var reference) ? [reference] : []);
    }

    /// <summary>What each archive holds.</summary>
    public Dictionary<string, string> Loaded { get; } = new(StringComparer.Ordinal);

    public Task StopAsync(IReadOnlyList<string> services, CancellationToken cancellationToken) => Record("stop");

    public Task StartAsync(IReadOnlyList<string> services, CancellationToken cancellationToken) => Record("start");

    public Task MigrateAsync(CancellationToken cancellationToken) => Record("migrate");

    public Task BackupDatabaseAsync(string database, string file, CancellationToken cancellationToken)
    {
        File.WriteAllText(file, $"backup of {database}");
        return Record($"backup:{database}");
    }

    public Task RestoreDatabaseAsync(string database, string file, CancellationToken cancellationToken)
    {
        Assert.Equal($"backup of {database}", File.ReadAllText(file));
        return Record($"restore:{database}");
    }

    public Task RestartSelfAsync(CancellationToken cancellationToken) => Record("restart-self");

    private Task Record(string call)
    {
        Calls.Add(call);
        if (Failing.Contains(call) || (Failing.Contains("start-after-migrate") && call == "start" && Calls.Contains("migrate")))
        {
            throw new HostCommandException($"{call} failed in the test");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Health that is good unless a test says otherwise (only for the first wait).</summary>
internal sealed class FakeHealth : IHealthProbe
{
    public int Waits { get; private set; }

    public bool FirstFails { get; set; }

    public Task WaitHealthyAsync(IReadOnlyList<string> urls, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Waits++;
        return FirstFails && Waits == 1 ? throw new TimeoutException("api did not become healthy within 10 minutes.") : Task.CompletedTask;
    }
}

/// <summary>Progress kept in memory.</summary>
internal sealed class MemoryProgress : IInstallProgress
{
    public List<IReadOnlyList<Zelvo.Data.Installation.UpdateInstallStep>> Reports { get; } = [];

    public Task ReportAsync(Guid installId, IReadOnlyList<Zelvo.Data.Installation.UpdateInstallStep> steps, CancellationToken cancellationToken)
    {
        Reports.Add([.. steps]);
        return Task.CompletedTask;
    }
}
