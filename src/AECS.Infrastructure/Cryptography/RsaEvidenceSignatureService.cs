using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Interfaces;

namespace AECS.Infrastructure.Cryptography;

public sealed class RsaEvidenceSignatureService : IEvidenceSignatureService
{
    public const string AlgorithmName = "RSASSA-PSS-SHA256";
    public const string CurrentPrivateKeyFileName = "current-private.pem";

    private const int KeySizeBits = 3072;
    private readonly object _cryptoGate = new();
    private readonly RSA _signingKey;
    private readonly IReadOnlyDictionary<string, RSA> _trustedKeys;

    private RsaEvidenceSignatureService(
        RSA signingKey,
        IReadOnlyDictionary<string, RSA> trustedKeys)
    {
        _signingKey = signingKey;
        _trustedKeys = trustedKeys;
        KeyId = ComputeKeyId(signingKey);
    }

    public string Algorithm => AlgorithmName;
    public string KeyId { get; }

    public static RsaEvidenceSignatureService LoadOrCreate(string keyDirectory)
    {
        var resolvedDirectory = Path.GetFullPath(keyDirectory);
        Directory.CreateDirectory(resolvedDirectory);
        var privateKeyPath = Path.Combine(resolvedDirectory, CurrentPrivateKeyFileName);

        if (!File.Exists(privateKeyPath))
            CreatePrivateKeyIfMissing(privateKeyPath);

        var signingKey = LoadPrivateKey(privateKeyPath);
        EnsurePublicKey(resolvedDirectory, signingKey);
        var trustedKeys = LoadTrustedKeys(resolvedDirectory, signingKey);
        return new RsaEvidenceSignatureService(signingKey, trustedKeys);
    }

    public static string RotateKey(string keyDirectory)
    {
        var resolvedDirectory = Path.GetFullPath(keyDirectory);
        Directory.CreateDirectory(resolvedDirectory);
        var privateKeyPath = Path.Combine(resolvedDirectory, CurrentPrivateKeyFileName);
        var temporaryPath = privateKeyPath + $".{Guid.NewGuid():N}.tmp";

        if (File.Exists(privateKeyPath))
        {
            using var currentKey = LoadPrivateKey(privateKeyPath);
            EnsurePublicKey(resolvedDirectory, currentKey);
        }

        using var replacement = RSA.Create(KeySizeBits);
        try
        {
            File.WriteAllText(temporaryPath, replacement.ExportRSAPrivateKeyPem());
            RestrictPrivateKeyPermissions(temporaryPath);
            File.Move(temporaryPath, privateKeyPath, overwrite: true);
            RestrictPrivateKeyPermissions(privateKeyPath);
            EnsurePublicKey(resolvedDirectory, replacement);
            return ComputeKeyId(replacement);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        lock (_cryptoGate)
        {
            return _signingKey.SignData(
                payload,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }
    }

    public bool Verify(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        if (!string.Equals(algorithm, AlgorithmName, StringComparison.Ordinal) ||
            !_trustedKeys.TryGetValue(keyId, out var key))
        {
            return false;
        }

        lock (_cryptoGate)
        {
            try
            {
                return key.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static void CreatePrivateKeyIfMissing(string privateKeyPath)
    {
        var temporaryPath = privateKeyPath + $".{Guid.NewGuid():N}.tmp";
        using var key = RSA.Create(KeySizeBits);
        try
        {
            File.WriteAllText(temporaryPath, key.ExportRSAPrivateKeyPem());
            RestrictPrivateKeyPermissions(temporaryPath);
            try
            {
                File.Move(temporaryPath, privateKeyPath, overwrite: false);
                RestrictPrivateKeyPermissions(privateKeyPath);
            }
            catch (IOException) when (File.Exists(privateKeyPath))
            {
                // Another process completed first. Its atomically published key is authoritative.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static RSA LoadPrivateKey(string path)
    {
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            if (key.KeySize < 2048)
                throw new CryptographicException("Evidence signing keys must be at least 2048 bits.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static IReadOnlyDictionary<string, RSA> LoadTrustedKeys(
        string keyDirectory,
        RSA signingKey)
    {
        var trusted = new Dictionary<string, RSA>(StringComparer.Ordinal);
        foreach (var publicKeyPath in Directory.EnumerateFiles(
                     keyDirectory,
                     "*.public.pem",
                     SearchOption.TopDirectoryOnly))
        {
            var key = RSA.Create();
            try
            {
                key.ImportFromPem(File.ReadAllText(publicKeyPath));
                if (key.KeySize < 2048)
                {
                    throw new CryptographicException(
                        "Trusted evidence verification keys must be at least 2048 bits.");
                }

                trusted[ComputeKeyId(key)] = key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        var signingKeyId = ComputeKeyId(signingKey);
        if (!trusted.ContainsKey(signingKeyId))
        {
            var publicCopy = RSA.Create();
            publicCopy.ImportSubjectPublicKeyInfo(
                signingKey.ExportSubjectPublicKeyInfo(),
                out _);
            trusted[signingKeyId] = publicCopy;
        }

        return trusted;
    }

    private static void EnsurePublicKey(string keyDirectory, RSA key)
    {
        var keyId = ComputeKeyId(key);
        var publicKeyPath = Path.Combine(
            keyDirectory,
            $"{keyId[7..]}.public.pem");
        if (File.Exists(publicKeyPath))
            return;

        var temporaryPath = publicKeyPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, key.ExportSubjectPublicKeyInfoPem());
            try
            {
                File.Move(temporaryPath, publicKeyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(publicKeyPath))
            {
                // Another process published the same public key first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ComputeKeyId(RSA key)
    {
        var digest = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static void RestrictPrivateKeyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
