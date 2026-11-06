using System.Security.Cryptography;

namespace Hive
{
    /// <summary>
    /// Signing keys: ECDSA over P-256 with SHA-256. A private key is a PEM file (PKCS #8) its author keeps; the public key
    /// travels in the registry's index, and its fingerprint (the SHA-256 of the public key, hex) in each manifest and lock.
    /// </summary>
    public static class HiveKeys
    {
        /// <summary>A new key pair: the private key as PEM, the public key as base64, and its fingerprint.</summary>
        public static (string PrivatePem, string PublicKey, string Fingerprint) Generate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] publicKey = key.ExportSubjectPublicKeyInfo();
            return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(publicKey), Fingerprint(publicKey));
        }

        public static string Fingerprint(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey));

        public static string Fingerprint(string publicKey) => Fingerprint(Convert.FromBase64String(publicKey));

        /// <summary>The public key (base64) of a private key file.</summary>
        public static string PublicOf(string privatePem)
        {
            using var key = Load(privatePem);
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }

        public static byte[] Sign(byte[] data, string privatePem)
        {
            using var key = Load(privatePem);
            return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        public static bool Verify(byte[] data, byte[] signature, string publicKey)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                return false;
            }
        }

        private static ECDsa Load(string privatePem)
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(privatePem);
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                key.Dispose();
                throw new HiveException($"not a Hive signing key (make one with `hive key`): {e.Message}");
            }
            return key;
        }
    }
}
