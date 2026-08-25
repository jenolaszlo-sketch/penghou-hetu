using Penghou.Hetu.Testing;
using System.Runtime.InteropServices;

namespace Penghou.Hetu.Ladybug.Tests;

public sealed class LadybugCodeGraphStoreTests
{
    [Fact]
    public async Task Store_PassesProviderConformanceSuite()
    {
        var path = TemporaryDatabasePath();
        if (!NativeRuntimeIsAvailable(path))
            return;
        var fixture = new Fixture(path);
        try
        {
            var report = await CodeGraphStoreConformanceSuite.VerifyAsync(fixture);

            Assert.Contains("bounded-deterministic-traversal", report.PassedChecks);
            Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
            Assert.True(fixture.Store!.CheckHealth().IsHealthy);
        }
        finally
        {
            fixture.Store?.Dispose();
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ReopensDurableState()
    {
        var path = TemporaryDatabasePath();
        if (!NativeRuntimeIsAvailable(path))
            return;
        var repositoryId = new CodeRepositoryId("repo:durable");
        try
        {
            using (var first = new LadybugCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(
                    repositoryId,
                    "Durable repository",
                    "repo://durable"));
            }

            using var reopened = new LadybugCodeGraphStore(path);
            var repository = await reopened.GetRepositoryAsync(repositoryId);

            Assert.NotNull(repository);
            Assert.Equal("Durable repository", repository.DisplayName);
            Assert.True(reopened.CheckHealth().IsHealthy);
            Assert.Equal(1, reopened.CheckHealth().PersistedCommandCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hetu-ladybug-{Guid.NewGuid():N}");

    private static bool NativeRuntimeIsAvailable(string path)
    {
        LoadWindowsOpenSslDependency("libcrypto-3-x64.dll");
        LoadWindowsOpenSslDependency("libssl-3-x64.dll");
        try
        {
            using var store = new LadybugCodeGraphStore(path);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void LoadWindowsOpenSslDependency(string fileName)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "mingw64",
            "bin",
            fileName);
        if (File.Exists(candidate))
            NativeLibrary.Load(candidate);
    }

    private static void DeleteDatabase(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class Fixture(string path) : ICodeGraphStoreFixture
    {
        public LadybugCodeGraphStore? Store { get; private set; }

        public ICodeGraphStore CreateStore() => Store = new(path);
    }
}
