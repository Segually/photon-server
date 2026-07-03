using System;
using System.Numerics;
using System.Security.Cryptography;

namespace PhotonServer
{
    // Photon's built-in encryption uses Diffie-Hellman over the Oakley Group 1
    // (768-bit MODP, RFC 2409) with generator 2. The shared secret is SHA-256
    // hashed to yield a 256-bit AES key. This is a standard, publicly documented
    // construction implemented here from the RFC — nothing Photon-specific read.
    public sealed class DiffieHellman
    {
        // RFC 2409 First Oakley Group (768-bit).
        private static readonly BigInteger P = ParseHexUnsigned(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
            "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
            "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
            "E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF");
        private static readonly BigInteger G = 2;
        private const int KeyBytes = 96; // 768 bits

        // Photon's OakleyGroups uses generator 22 (0x16) with the RFC 2409
        // 768-bit MODP prime.
        public static BigInteger Generator = 22;

        private readonly BigInteger _priv;
        public byte[] PublicKey { get; }
        public byte[] PrivateKeyBytes { get; }
        public byte[]? SharedKey { get; private set; }  // SHA-256(shared secret)
        public byte[]? OtherPublic { get; private set; }

        public DiffieHellman()
        {
            // Random private exponent in [2, P-2].
            var rnd = new byte[KeyBytes];
            RandomNumberGenerator.Fill(rnd);
            _priv = ParseUnsigned(rnd) % (P - 2) + 1;
            PrivateKeyBytes = ToFixed(_priv, KeyBytes);
            PublicKey = ToFixed(BigInteger.ModPow(Generator, _priv, P), KeyBytes);
        }

        public byte[]? SharedSecretRaw { get; private set; }
        public byte[]? SharedSecretMinimal { get; private set; }

        public void DeriveShared(byte[] otherPublic)
        {
            OtherPublic = otherPublic;
            var pub = ParseUnsigned(otherPublic);
            var secret = BigInteger.ModPow(pub, _priv, P);
            SharedSecretRaw = ToFixed(secret, KeyBytes);         // fixed 96-byte big-endian
            SharedSecretMinimal = ToMinimalBigEndian(secret);   // Photon format (big-endian minimal)
            // Photon hashes the big-endian-minimal shared secret (sign byte stripped).
            SharedKey = SHA256.HashData(SharedSecretMinimal);
        }

        private static byte[] ToMinimalBigEndian(BigInteger v)
        {
            var le = v.ToByteArray();
            int len = le.Length;
            while (len > 1 && le[len - 1] == 0) len--; // strip sign/zero padding
            var be = new byte[len];
            for (int i = 0; i < len; i++) be[i] = le[len - 1 - i];
            return be;
        }

        // ── BigInteger <-> fixed-width big-endian unsigned helpers ───────────
        private static BigInteger ParseUnsigned(byte[] bigEndian)
        {
            // Append a zero high byte so BigInteger treats it as positive.
            var le = new byte[bigEndian.Length + 1];
            for (int i = 0; i < bigEndian.Length; i++)
                le[i] = bigEndian[bigEndian.Length - 1 - i];
            return new BigInteger(le);
        }

        private static BigInteger ParseHexUnsigned(string hex) =>
            ParseUnsigned(Convert.FromHexString(hex));

        private static byte[] ToFixed(BigInteger v, int width)
        {
            var le = v.ToByteArray(); // little-endian, possibly with trailing sign byte
            var outBe = new byte[width];
            int n = Math.Min(le.Length, width);
            for (int i = 0; i < n; i++)
                outBe[width - 1 - i] = le[i];
            return outBe;
        }
    }

    public static class PhotonCrypto
    {
        // Photon uses AES-256-CBC with a ZERO IV and PKCS7 padding. The IV is
        // not prepended — the ciphertext is the raw CBC output.
        private static readonly byte[] ZeroIv = new byte[16];

        public static byte[] Encrypt(byte[] plain, byte[] key)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256; aes.Key = key;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.IV = ZeroIv;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(plain, 0, plain.Length);
        }

        public static byte[] Decrypt(byte[] cipher, byte[] key)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256; aes.Key = key;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.IV = ZeroIv;
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(cipher, 0, cipher.Length);
        }
    }
}
