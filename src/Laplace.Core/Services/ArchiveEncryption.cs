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

    public static byte[] DeriveKey(PasswordContext password, byte[] salt, int iterations)
    {
        if (salt.Length < MinimumSaltSizeBytes)
        {
            throw new InvalidDataException("Encrypted archive salt is invalid.");
        }

        if (iterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
            iterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
        {
            throw new InvalidDataException("Encrypted archive key derivation settings are invalid.");
        }

        var secretMaterial = BuildSecretMaterial(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secretMaterial, salt, iterations, HashAlgorithmName.SHA256, KeySizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretMaterial);
        }
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
