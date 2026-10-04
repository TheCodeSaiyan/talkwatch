using System.Security.Cryptography;
using System.Text;

namespace TalkWatch.Capture;

/// <summary>
/// The one secret a capture needs. Separate keys for encryption and pseudonymisation are derived from it,
/// so keeping one value in 1Password covers both, and neither derived key reveals the other.
/// </summary>
public sealed class CaptureKey
{
    public const string EnvironmentVariable = "TALKWATCH_CAPTURE_KEY";
    private const int KeyLength = 32;

    private readonly byte[] _master;

    private CaptureKey(byte[] master) => _master = master;

    public byte[] EncryptionKey => Derive("talkwatch/capture-encryption/v1");

    public byte[] PseudonymKey => Derive("talkwatch/pseudonym/v1");

    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyLength));

    public static CaptureKey Parse(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is not base64. Generate one with 'keygen'.");
        }

        return bytes.Length == KeyLength
            ? new CaptureKey(bytes)
            : throw new InvalidOperationException($"{EnvironmentVariable} must decode to {KeyLength} bytes, not {bytes.Length}.");
    }

    public static CaptureKey FromEnvironment() =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } value
            ? Parse(value)
            : throw new InvalidOperationException($"Set {EnvironmentVariable}, for example from 1Password with op read.");

    private byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, KeyLength, salt: [], info: Encoding.UTF8.GetBytes(purpose));
}
