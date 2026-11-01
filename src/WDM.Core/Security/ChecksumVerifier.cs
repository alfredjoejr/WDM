using System.Security.Cryptography;

namespace WDM.Core.Security;

public enum HashAlgorithmType
{
    Sha256,
    Sha512,
    Sha1,
    Md5
}

/// <summary>
/// Fast async checksum computation and validation for downloaded files.
/// </summary>
public static class ChecksumVerifier
{
    public static async Task<string> ComputeHashAsync(
        string filePath, 
        HashAlgorithmType algorithm = HashAlgorithmType.Sha256, 
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for checksum verification", filePath);
        }

        await using var stream = File.OpenRead(filePath);

        using HashAlgorithm hasher = algorithm switch
        {
            HashAlgorithmType.Sha256 => SHA256.Create(),
            HashAlgorithmType.Sha512 => SHA512.Create(),
            HashAlgorithmType.Sha1 => SHA1.Create(),
            HashAlgorithmType.Md5 => MD5.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };

        var hashBytes = await hasher.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static HashAlgorithmType ParseAlgorithm(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return HashAlgorithmType.Sha256;
        return name.Trim().ToLowerInvariant() switch
        {
            "md5" => HashAlgorithmType.Md5,
            "sha1" => HashAlgorithmType.Sha1,
            "sha512" => HashAlgorithmType.Sha512,
            _ => HashAlgorithmType.Sha256
        };
    }

    public static HashAlgorithmType InferAlgorithmFromHash(string hash)
    {
        var clean = hash.Trim();
        return clean.Length switch
        {
            32 => HashAlgorithmType.Md5,
            40 => HashAlgorithmType.Sha1,
            128 => HashAlgorithmType.Sha512,
            _ => HashAlgorithmType.Sha256
        };
    }

    public static async Task<Dictionary<HashAlgorithmType, string>> ComputeAllHashesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for checksum verification", filePath);
        }

        await using var stream = File.OpenRead(filePath);

        using var sha256 = SHA256.Create();
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            md5.TransformBlock(buffer, 0, bytesRead, null, 0);
            sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return new Dictionary<HashAlgorithmType, string>
        {
            [HashAlgorithmType.Sha256] = Convert.ToHexString(sha256.Hash!).ToLowerInvariant(),
            [HashAlgorithmType.Md5] = Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
            [HashAlgorithmType.Sha1] = Convert.ToHexString(sha1.Hash!).ToLowerInvariant()
        };
    }

    public static async Task<bool> VerifyAsync(
        string filePath, 
        string expectedHash, 
        HashAlgorithmType algorithm = HashAlgorithmType.Sha256, 
        CancellationToken cancellationToken = default)
    {
        var actual = await ComputeHashAsync(filePath, algorithm, cancellationToken);
        return string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
