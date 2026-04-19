using System.Security.Cryptography;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates cryptographically secure random passwords and tokens.
    /// </summary>
    public static class PasswordGenerator
    {
        // Excluded ambiguous characters (0/O, 1/l/I) for easier reading.
        private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

        public static string Generate(int length = 10)
        {
            var result = new char[length];
            var bytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            for (int i = 0; i < length; i++)
            {
                result[i] = Chars[bytes[i] % Chars.Length];
            }
            return new string(result);
        }

        /// <summary>
        /// Generates a URL-safe random token (base64 without padding/special chars).
        /// </summary>
        public static string GenerateToken(int byteLength = 48)
        {
            var bytes = new byte[byteLength];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
