using System.Security.Cryptography;

namespace PassVault.Core.Usb;

/// <summary>
/// The small file that lives on the stick: "PVKF" || stickId(16) || usbSecret(32).
///
/// It is NOT encrypted, on purpose. We assume anyone who holds the stick for a minute has
/// read it. It is one of three ingredients and useless on its own - the other two are a
/// secret sealed in your PC's chip and the PIN in your head.
/// </summary>
public sealed class Keyfile
{
    public const int StickIdSize = 16;
    public const int SecretSize = 32;
    public const int Size = 4 + StickIdSize + SecretSize;

    private static readonly byte[] Magic = "PVKF"u8.ToArray();

    public byte[] StickId { get; }
    public byte[] UsbSecret { get; }

    public Keyfile(byte[] stickId, byte[] usbSecret)
    {
        if (stickId.Length != StickIdSize)
            throw new ArgumentException($"Stick id must be {StickIdSize} bytes.", nameof(stickId));
        if (usbSecret.Length != SecretSize)
            throw new ArgumentException($"USB secret must be {SecretSize} bytes.", nameof(usbSecret));

        StickId = stickId;
        UsbSecret = usbSecret;
    }

    public static Keyfile Create()
        => new(RandomNumberGenerator.GetBytes(StickIdSize), RandomNumberGenerator.GetBytes(SecretSize));

    /// <summary>Short readable form, only ever used in messages like "that is not your stick".</summary>
    public string ShortId => Convert.ToHexString(StickId)[..8].ToLowerInvariant();

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        Magic.CopyTo(bytes, 0);
        StickId.CopyTo(bytes, 4);
        UsbSecret.CopyTo(bytes, 4 + StickIdSize);
        return bytes;
    }

    public static Keyfile FromBytes(byte[] bytes)
    {
        if (bytes.Length != Size || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("That is not a PassVault keyfile.");

        return new Keyfile(
            bytes[4..(4 + StickIdSize)],
            bytes[(4 + StickIdSize)..]);
    }

    public bool IsSameStick(Keyfile other) => CryptographicOperations.FixedTimeEquals(StickId, other.StickId);
}
