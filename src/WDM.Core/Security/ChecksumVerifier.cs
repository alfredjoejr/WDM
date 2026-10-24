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
