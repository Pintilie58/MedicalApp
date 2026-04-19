using System.Security.Cryptography;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates cryptographically secure random passwords.
    /// </summary>
    public static class PasswordGenerator
    {
        // Excluded ambiguous characters (0/O, 1/l/I) for easier reading in emails.
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
    }
}
