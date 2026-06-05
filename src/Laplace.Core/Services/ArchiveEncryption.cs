using Konscious.Security.Cryptography;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Laplace.Core.Services;

internal static class ArchiveEncryption
{
    public const int MinimumSaltSizeBytes = 16;
    public const int GeneratedSaltSizeBytes = 32;
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(GeneratedSaltSizeBytes);

    public static byte[] DeriveKey(PasswordContext password, ArchiveHeader header)
    {
        if (header.EncryptionSalt.Length < MinimumSaltSizeBytes)
        {
            throw new InvalidDataException("Encrypted archive salt is invalid.");
        }

        var algorithm = header.FormatVersion >= 5
            ? (KeyDerivationAlgorithm)header.KeyDerivationAlgorithmId
            : KeyDerivationAlgorithm.Pbkdf2Sha256;
        var secretMaterial = BuildSecretMaterial(password);
        try
        {
            return algorithm switch
            {
                KeyDerivationAlgorithm.Pbkdf2Sha256 => DerivePbkdf2(secretMaterial, header),
                KeyDerivationAlgorithm.Argon2id => DeriveArgon2id(secretMaterial, header),
                _ => throw new InvalidDataException($"Unsupported LPC key derivation algorithm: {header.KeyDerivationAlgorithmId}.")
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretMaterial);
        }
    }

    private static byte[] DerivePbkdf2(byte[] secretMaterial, ArchiveHeader header)
    {
        if (header.KeyDerivationIterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
            header.KeyDerivationIterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
        {
            throw new InvalidDataException("Encrypted archive PBKDF2 settings are invalid.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(
            secretMaterial,
            header.EncryptionSalt,
            header.KeyDerivationIterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    private static byte[] DeriveArgon2id(byte[] secretMaterial, ArchiveHeader header)
    {
        if (header.KeyDerivationIterations < CreateArchiveOptions.MinimumArgon2Iterations ||
            header.KeyDerivationIterations > CreateArchiveOptions.MaximumArgon2Iterations ||
            header.KeyDerivationMemoryKiB < CreateArchiveOptions.MinimumArgon2MemoryKiB ||
            header.KeyDerivationMemoryKiB > CreateArchiveOptions.MaximumArgon2MemoryKiB ||
            header.KeyDerivationParallelism < 1 ||
            header.KeyDerivationParallelism > CreateArchiveOptions.MaximumArgon2Parallelism)
        {
            throw new InvalidDataException("Encrypted archive Argon2id settings are invalid.");
        }

        using var argon2 = new Argon2id(secretMaterial)
        {
            Salt = header.EncryptionSalt,
            Iterations = header.KeyDerivationIterations,
            MemorySize = header.KeyDerivationMemoryKiB,
            DegreeOfParallelism = header.KeyDerivationParallelism
        };
        return argon2.GetBytes(KeySizeBytes);
    }

    public static byte[] EncryptBlock(byte[] plaintext, byte[] key, BlockEntryRecord block)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var tag = new byte[TagSizeBytes];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAdditionalData(block));

        block.EncryptionNonce = nonce;
        block.EncryptionTag = tag;
        return ciphertext;
    }

    public static byte[] DecryptBlock(byte[] ciphertext, byte[] key, BlockEntryRecord block)
    {
        if (block.EncryptionNonce.Length != NonceSizeBytes || block.EncryptionTag.Length != TagSizeBytes)
        {
            throw new InvalidDataException($"Encrypted block metadata is invalid for block #{block.BlockId}.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(block.EncryptionNonce, ciphertext, block.EncryptionTag, plaintext, BuildAdditionalData(block));
        return plaintext;
    }

    public static EncryptedPayload EncryptMetadata(byte[] plaintext, byte[] key, string tableName, ArchiveHeader header)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var tag = new byte[TagSizeBytes];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildMetadataAdditionalData(tableName, header));
        return new EncryptedPayload(ciphertext, nonce, tag);
    }

    public static byte[] DecryptMetadata(
        byte[] ciphertext,
        byte[] nonce,
        byte[] tag,
        byte[] key,
        string tableName,
        ArchiveHeader header)
    {
        if (nonce.Length != NonceSizeBytes || tag.Length != TagSizeBytes)
        {
            throw new InvalidDataException($"Encrypted {tableName} metadata is invalid.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildMetadataAdditionalData(tableName, header));
        return plaintext;
    }

    private static byte[] BuildAdditionalData(BlockEntryRecord block)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.Write("LPC2"u8);
        writer.Write(block.BlockId);
        writer.Write(block.OwningFileEntryId);
        writer.Write(block.OriginalBlockSize);
        writer.Write(block.CompressedBlockSize);
        writer.Write((byte)block.CompressionMethod);
        writer.Write(block.IsRaw);
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildMetadataAdditionalData(string tableName, ArchiveHeader header)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write("LPC6"u8);
        BinaryCodec.WriteUtf8String(writer, tableName);
        writer.Write(header.FormatVersion);
        writer.Write(header.ArchiveFlags);
        writer.Write(header.FileEntryCount);
        writer.Write(header.BlockEntryCount);
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSecretMaterial(PasswordContext password)
    {
        var passwordBytes = string.IsNullOrEmpty(password.Password)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(password.Password);
        var keyfileHash = password.KeyfileHash ?? Array.Empty<byte>();
        var material = new byte[8 + passwordBytes.Length + keyfileHash.Length];

        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(0, 4), passwordBytes.Length);
        passwordBytes.CopyTo(material.AsSpan(4, passwordBytes.Length));
        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(4 + passwordBytes.Length, 4), keyfileHash.Length);
        keyfileHash.CopyTo(material.AsSpan(8 + passwordBytes.Length, keyfileHash.Length));

        if (passwordBytes.Length > 0)
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        return material;
    }
}

internal sealed record EncryptedPayload(byte[] Ciphertext, byte[] Nonce, byte[] Tag);
