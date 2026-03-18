using System.Security.Cryptography;
using System.Text;

namespace DPCS.Blazor.Security;

/// <summary>
/// Utility class for generating and validating signed job IDs to prevent tampering.
/// Each job ID is a combination of a GUID and a truncated HMAC signature, encoded in a URL-safe Base64 format.
/// TODO: The secret key used for HMAC should be securely stored and managed in a production environment,
/// such as in environment variables or a secrets manager.
/// </summary>
public static class JobIdSecurity
{
    private static readonly byte[] SecretKey = Encoding.UTF8.GetBytes("super-secret-signing-key-change-me");

    public static string GenerateSignedId(Guid id)
    {
        // 16 bytes for Guid + 16 bytes for truncated HMAC = 32 bytes
        Span<byte> combined = stackalloc byte[32];
        
        if (!id.TryWriteBytes(combined[..16]))
            throw new InvalidOperationException("Failed to write Guid bytes");

        using var hmac = new HMACSHA256(SecretKey);
        Span<byte> hashOutput = stackalloc byte[32];
        if (!hmac.TryComputeHash(combined[..16], hashOutput, out _))
            throw new InvalidOperationException("Failed to compute HMAC");
        
        // Copy truncated HMAC (16 bytes) to the end of combined
        hashOutput[..16].CopyTo(combined[16..]);
        
        // Base64 encode (32 bytes -> 44 chars)
        Span<char> charBuffer = stackalloc char[44];
        if (!Convert.TryToBase64Chars(combined, charBuffer, out int charsWritten))
            throw new InvalidOperationException("Failed to encode Base64");

        // URL-safe replacement and padding removal
        for (int i = 0; i < charsWritten; i++)
        {
            if (charBuffer[i] == '+') charBuffer[i] = '-';
            else if (charBuffer[i] == '/') charBuffer[i] = '_';
        }

        // Trim padding '='
        int length = charsWritten;
        while (length > 0 && charBuffer[length - 1] == '=')
        {
            length--;
        }

        return new string(charBuffer[..length]);
    }

    public static bool ValidateSignedId(string signedId)
    {
        if (string.IsNullOrWhiteSpace(signedId)) return false;

        ReadOnlySpan<char> input = signedId.AsSpan();
        if (input.Length > 50) return false; // Sanity check

        Span<char> base64Chars = stackalloc char[50];
        input.CopyTo(base64Chars);
        int length = input.Length;

        // Restore standard Base64 chars
        for (int i = 0; i < length; i++)
        {
            if (base64Chars[i] == '-') base64Chars[i] = '+';
            else if (base64Chars[i] == '_') base64Chars[i] = '/';
        }

        // Add padding
        switch (length % 4)
        {
            case 2: base64Chars[length++] = '='; base64Chars[length++] = '='; break;
            case 3: base64Chars[length++] = '='; break;
        }

        Span<byte> combined = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(base64Chars[..length], combined, out int bytesWritten) || bytesWritten != 32)
        {
            return false;
        }
        
        using var hmac = new HMACSHA256(SecretKey);
        Span<byte> computedHash = stackalloc byte[32];
        if (!hmac.TryComputeHash(combined[..16], computedHash, out _))
        {
            return false;
        }
        
        return CryptographicOperations.FixedTimeEquals(combined[16..], computedHash[..16]);
    }
}
