using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// AES-CBC encryption/decryption for sensitive CAM data (patient CNP).
    /// The 32-byte key is read from <see cref="CamSettings.CnpEncryptionKeyBase64"/>
    /// which MUST be set via User Secrets / environment variable in production.
    ///
    /// Output format for ciphertexts:
    /// <c>base64( IV[16 bytes] || ciphertext )</c>
    /// — single string, safe to store in a 256-char NVARCHAR column.
    /// </summary>
    public class CamCryptoService
    {
        private readonly byte[]? _key;
        private readonly ILogger<CamCryptoService> _logger;

        public CamCryptoService(IOptions<CamSettings> opts, ILogger<CamCryptoService> logger)
        {
            _logger = logger;
            var b64 = opts.Value.CnpEncryptionKeyBase64;
            if (!string.IsNullOrWhiteSpace(b64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    if (bytes.Length == 32) _key = bytes;
                    else _logger.LogError("CamSettings.CnpEncryptionKeyBase64 must decode to EXACTLY 32 bytes (current: {Len}).", bytes.Length);
                }
                catch (FormatException ex)
                {
                    _logger.LogError(ex, "CamSettings.CnpEncryptionKeyBase64 is not valid base64.");
                }
            }
        }

        /// <summary>True when a valid key is loaded and encryption is active.</summary>
        public bool IsEnabled => _key != null;

        public string Encrypt(string plaintext)
        {
            if (_key == null)
            {
                // Fail-safe: never store plaintext CNP in DB when crypto is mis-configured.
                // Return a marker so the operator notices early.
                return "ENC_KEY_MISSING";
            }
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.GenerateIV();
            using var enc = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipherBytes = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            var combined = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);
            return Convert.ToBase64String(combined);
        }

        public string Decrypt(string ciphertextB64)
        {
            if (_key == null || string.IsNullOrWhiteSpace(ciphertextB64) || ciphertextB64 == "ENC_KEY_MISSING")
                return string.Empty;
            try
            {
                var combined = Convert.FromBase64String(ciphertextB64);
                using var aes = Aes.Create();
                aes.Key = _key;
                var iv = new byte[16];
                Buffer.BlockCopy(combined, 0, iv, 0, 16);
                aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                var plainBytes = dec.TransformFinalBlock(combined, 16, combined.Length - 16);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CamCrypto: failed to decrypt CNP ciphertext (length={Len}).", ciphertextB64.Length);
                return string.Empty;
            }
        }
    }
}
