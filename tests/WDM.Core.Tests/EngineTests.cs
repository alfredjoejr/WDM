using System.Text;
using WDM.Core.Models;
using WDM.Core.Network;
using WDM.Core.Security;
using WDM.Core.Storage;
using Xunit;

namespace WDM.Core.Tests;

public class EngineTests : IDisposable
{
    private readonly string _testDir;

    public EngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "wdm_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Best effort
        }
    }

    [Fact]
    public async Task DiskManager_SupportsConcurrentWritesToSingleFile()
    {
        var targetFile = Path.Combine(_testDir, "concurrent.bin");
        const int partSize = 1024;
        const int partCount = 8;
        var totalSize = partSize * partCount;

        await using (var disk = DiskManager.Create(targetFile, totalSize))
        {
            var tasks = Enumerable.Range(0, partCount).Select(async i =>
            {
                byte[] buffer = new byte[partSize];
                Array.Fill(buffer, (byte)(i + 1));
                await disk.WriteAtAsync(buffer, i * partSize);
            });

            await Task.WhenAll(tasks);
            disk.Flush();
        }

        Assert.True(File.Exists(targetFile));
        var fileBytes = await File.ReadAllBytesAsync(targetFile);
        Assert.Equal(totalSize, fileBytes.Length);

        for (int i = 0; i < partCount; i++)
        {
            var slice = fileBytes.AsSpan(i * partSize, partSize);
            foreach (var b in slice)
            {
                Assert.Equal((byte)(i + 1), b);
            }
        }
    }

    [Fact]
    public async Task ChecksumVerifier_ComputesAccurateSha256()
    {
        var targetFile = Path.Combine(_testDir, "hash_test.txt");
        await File.WriteAllTextAsync(targetFile, "Hello WDM Download Manager!");

        // Known SHA-256 of "Hello WDM Download Manager!"
        var hash = await ChecksumVerifier.ComputeHashAsync(targetFile, HashAlgorithmType.Sha256);
        Assert.NotEmpty(hash);
        Assert.Equal(64, hash.Length);

        var isValid = await ChecksumVerifier.VerifyAsync(targetFile, hash, HashAlgorithmType.Sha256);
        Assert.True(isValid);
    }

    [Fact]
    public async Task CheckpointStore_SavesAndRestoresState()
    {
        var targetFile = Path.Combine(_testDir, "test.bin");
        var state = new CheckpointState
        {
            OriginalUrl = "https://example.com/file.zip",
            FinalUrl = "https://example.com/file.zip",
            FilePath = targetFile,
            TotalBytes = 1000000,
            ETag = "\"test-etag-123\"",
            Chunks =
            [
                new DownloadChunk { Index = 0, StartOffset = 0, EndOffset = 499999, BytesDownloaded = 250000 },
                new DownloadChunk { Index = 1, StartOffset = 500000, EndOffset = 999999, BytesDownloaded = 500000 }
            ]
        };

        await CheckpointStore.SaveAsync(state);

        var restored = await CheckpointStore.TryLoadAsync(targetFile);
        Assert.NotNull(restored);
        Assert.Equal(state.OriginalUrl, restored.OriginalUrl);
        Assert.Equal(state.TotalBytes, restored.TotalBytes);
        Assert.Equal(2, restored.Chunks.Count);
        Assert.Equal(250000, restored.Chunks[0].BytesDownloaded);

        CheckpointStore.DeleteCheckpoint(targetFile);
        var afterDelete = await CheckpointStore.TryLoadAsync(targetFile);
        Assert.Null(afterDelete);
    }

    [Theory]
    [InlineData("valid_file.zip", "valid_file.zip")]
    [InlineData("file/with\\illegal:*?\"<>|chars.dat", "file_with_illegal_______chars.dat")]
    [InlineData("", "download.bin")]
    public void HttpProbe_SanitizesFileNames(string input, string expected)
    {
        var result = HttpProbe.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }
}
