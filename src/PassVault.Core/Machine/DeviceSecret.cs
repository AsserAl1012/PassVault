using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PassVault.Core.Machine;

/// <summary>How the device secret is protected. Worth telling the user honestly.</summary>
public enum DeviceProtection
{
    /// <summary>Sealed by the TPM chip, which also checks the PIN. The secret cannot leave this machine.</summary>
    Tpm,

    /// <summary>
    /// Fallback when there is no usable TPM. Tied to this Windows account rather than the
    /// hardware, so someone who can log in as you - or who has your Windows password and
    /// your disk - can get it back, and nothing but the app limits PIN guesses. Weaker, and
    /// the app says so.
    /// </summary>
    Dpapi,

    /// <summary>
    /// The Linux fallback when there is no TPM: a file only your user can read. Anyone who can
    /// read your files - logged in as you, or holding your disk - gets it, and nothing but the
    /// app limits PIN guesses. Weaker, and the app says so.
    /// </summary>
    UserFile,
}

/// <summary>
/// The TPM chip refused the PIN. It counts every refusal itself, so this is the limit on
/// guessing that no one can get round by copying files or skipping the app.
/// </summary>
public sealed class PinRefusedException(bool lockedOut) : Exception(lockedOut
    ? "This PC's chip is not taking PINs just now - it may be resting after too many wrong ones. "
      + "Try again later, or open your vault with your 12 words."
    : "The PIN is wrong.")
{
    /// <summary>
    /// True when the chip would not check the PIN at all just now - resting after too many wrong
    /// ones, or busy. Nothing was learned about the PIN.
    /// </summary>
    public bool LockedOut { get; } = lockedOut;
}

/// <summary>
/// This PC has a chip, but PassVault cannot use it - the TPM tools are missing, or the user may not
/// open it. The message says what to do. Nothing reached the chip, so no PIN try is spent on it,
/// and it is never "the chip was cleared": setting up again would fail the same way.
/// </summary>
public sealed class ChipUnavailableException(string message) : Exception(message);

/// <summary>
/// A 32-byte secret that belongs to this machine and nowhere else. It is the second of the
/// three ingredients in the daily unlock.
///
/// On a TPM machine it is sealed with a key that lives in the chip, and the chip will only use
/// that key when given the PIN. The chip counts wrong PINs itself - 32, then one more every two
/// hours on a normal Windows PC - so someone holding the PC and the stick together cannot just
/// try all the PINs. Before this, the chip opened the secret without asking and only the app
/// counted wrong PINs, so skipping the app made a 4-digit PIN a few hours' work.
///
/// The sealed secret is kept in machine.pvm, with the chip's key inside it, wrapped so only this
/// PC's chip can load it. Windows would otherwise keep the key as a file in AppData, which the
/// Microsoft Store version redirects and deletes on uninstall.
///
/// On Linux the chip is reached through tpm2-tools instead - see <see cref="LinuxChip"/>.
/// </summary>
public static class DeviceSecret
{
    public const int Size = 32;

    /// <summary>Where set-ups from before 1.0.2 keep the sealed secret, until their first unlock moves it into machine.pvm.</summary>
    public const string FileName = "device.bin";

    /// <summary>
    /// Where version 1 kept its key: a named chip key that never asked for the PIN. Still read,
    /// so existing set-ups keep working, and replaced the first time the PIN is proven right.
    /// </summary>
    public const string LegacyTpmKeyName = "PassVault.DeviceKey";

    private const string TpmProviderName = "Microsoft Platform Crypto Provider";
    [SupportedOSPlatform("windows")] private static readonly CngProvider TpmProvider = new(TpmProviderName);
    [SupportedOSPlatform("windows")] private static readonly CngKeyBlobFormat ChipWrapped = new("OpaqueKeyBlob");

    // ---- file format: "PVDS" || kind(1) || payload ----
    private static readonly byte[] Magic = "PVDS"u8.ToArray();
    private const byte LegacyTpm = 0;    // payload: the secret sealed with the named key
    private const byte Dpapi = 1;        // payload: the secret sealed by Windows for this account
    private const byte TpmWithPin = 2;   // payload: keyLength(2) || chip-wrapped key || sealed secret
    // 3 and 4 are Linux's - see LinuxChip.

    // Checked on a real chip: a wrong PIN comes back as NTE_PERM, and the chip counts it.
    private const int NtePerm = unchecked((int)0x80090010);

    /// <summary>A freshly made secret, and the same secret sealed - which set-up keeps in machine.pvm.</summary>
    public sealed record Made(byte[] Secret, DeviceProtection Protection, byte[] Sealed);

    /// <summary>A new secret for setting this machine up, sealed so the chip will only open it with this PIN.</summary>
    public static Made Create(string pin) => Seal(RandomNumberGenerator.GetBytes(Size), pin);

    /// <summary>This PC's sealed secret: in machine.pvm, or in device.bin for a set-up from before 1.0.2.</summary>
    public static byte[] SealedIn(string folder)
    {
        if (MachineStore.IsEnrolled(folder) && MachineStore.Read(folder).Device is { } sealedSecret)
            return sealedSecret;

        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "This machine's own secret is missing. Set this machine up again with your 12 words.");
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Open this machine's secret. On a TPM machine the chip checks the PIN and throws
    /// <see cref="PinRefusedException"/> if it is wrong or if it will not check just now. Anything
    /// else thrown means the secret cannot be opened on this PC any more: the chip was cleared,
    /// the board replaced, or the file damaged.
    /// </summary>
    public static (byte[] Secret, DeviceProtection Protection) Open(string folder, string pin)
        => Open(SealedIn(folder), pin);

    public static (byte[] Secret, DeviceProtection Protection) Open(byte[] sealedSecret, string pin)
    {
        var (kind, payload) = Unpack(sealedSecret);
        if (!OperatingSystem.IsWindows()) return LinuxChip.Open(kind, payload, pin);

        return kind switch
        {
            TpmWithPin => (OpenWithPin(payload, pin), DeviceProtection.Tpm),
            LegacyTpm => (OpenLegacy(payload), DeviceProtection.Tpm),
            Dpapi => (ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser), DeviceProtection.Dpapi),
            _ => throw new InvalidDataException("This machine's own secret uses a protection we do not know."),
        };
    }

    /// <summary>
    /// Bring a set-up from before 1.0.2 up to date: its secret moves into machine.pvm and, where
    /// this PC has a chip, is sealed again so the chip checks the PIN. That covers a secret the
    /// chip opened without asking, and one Windows sealed because 1.0.1 failed to use the chip.
    /// The secret stays the same, so nothing else has to change.
    ///
    /// Only call this after the PIN has been proven right by opening the vault: sealing a mistyped
    /// PIN in would lock the owner out. It never stops the vault opening - whatever fails here is
    /// tried again on the next unlock.
    /// </summary>
    public static void UpgradeIfOld(string folder, string provenPin)
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux set-ups start out in today's format

        try
        {
            var machine = MachineStore.Read(folder);

            if (machine.Device is null)
            {
                var sealedSecret = SealedIn(folder);

                // Never weaker: with no chip to use, the old protection just moves across as it is.
                if (Unpack(sealedSecret).Kind != TpmWithPin && TpmIsUsable())
                {
                    var made = Seal(Open(sealedSecret, provenPin).Secret, provenPin);
                    (sealedSecret, machine.Protection) = (made.Sealed, made.Protection);
                }

                machine.Device = sealedSecret;
                MachineStore.Write(folder, machine);
            }

            // Only now that machine.pvm holds it: until then device.bin was the only copy.
            var oldFile = Path.Combine(folder, FileName);
            if (File.Exists(oldFile)) File.Delete(oldFile);

            // While the key that opened without a PIN exists, any old device.bin still opens without
            // one - left in the old AppData folder, or in a backup - and PINs can be tried against its
            // machine.pvm with no chip counting. Once the chip checks the PIN here, nothing needs it.
            if (Unpack(machine.Device).Kind == TpmWithPin) ForgetLegacyKey();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or PinRefusedException or InvalidOperationException or InvalidDataException)
        {
            // Tried again on the next unlock.
        }
    }

    /// <summary>
    /// Throw away a set-up's device.bin from before 1.0.2 and its chip key. Since 1.0.2 the secret
    /// is in machine.pvm and goes with it.
    /// </summary>
    public static void Forget(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path)) File.Delete(path);
        if (OperatingSystem.IsWindows()) ForgetLegacyKey();
    }

    /// <summary>
    /// Whether this PC has a TPM 2.0 chip, asked of Windows' own TPM service. Instant - it makes
    /// no key. It used to make and throw away a key to find out, which took seconds on a firmware
    /// TPM and, when that broke, reported "no chip" on a PC that had one.
    /// </summary>
    public static bool TpmIsUsable()
    {
        if (!OperatingSystem.IsWindows()) return LinuxChip.HasChip;

        try
        {
            return Tbsi_GetDeviceInfo(Marshal.SizeOf<TpmDeviceInfo>(), out var info) == 0 && info.TpmVersion == TpmVersion20;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // ---- sealing ----

    /// <summary>
    /// Seal with the chip when this PC has one, proving the round trip with the PIN before
    /// trusting it. Windows' own protection is only for a PC with no usable chip at all.
    ///
    /// A PC that has a chip never falls back. It used to, on any error - so a failure while
    /// upgrading quietly swapped the chip-checked PIN for Windows' own protection, the weakest
    /// thing on offer, and said nothing. A failure here is an error to show, not a reason to
    /// seal with something weaker.
    /// </summary>
    private static Made Seal(byte[] secret, string pin)
        => OperatingSystem.IsWindows() ? SealOnWindows(secret, pin) : LinuxChip.Seal(secret, pin);

    [SupportedOSPlatform("windows")]
    private static Made SealOnWindows(byte[] secret, string pin)
    {
        if (!TpmIsUsable())
            return new Made(secret, DeviceProtection.Dpapi,
                            Pack(Dpapi, ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser)));

        var (sealedSecret, wrappedKey) = WithTemporaryTpmKey(pin, key =>
        {
            using var rsa = new RSACng(key);
            return (rsa.Encrypt(secret, RSAEncryptionPadding.OaepSHA256), key.Export(ChipWrapped));
        });

        var payload = new byte[2 + wrappedKey.Length + sealedSecret.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)wrappedKey.Length);
        wrappedKey.CopyTo(payload, 2);
        sealedSecret.CopyTo(payload, 2 + wrappedKey.Length);

        if (!OpenWithPin(payload, pin).AsSpan().SequenceEqual(secret))
            throw new CryptographicException("The chip did not give the secret back.");

        return new Made(secret, DeviceProtection.Tpm, Pack(TpmWithPin, payload));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] OpenWithPin(byte[] payload, string pin)
    {
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (payload.Length < 2 + keyLength)
            throw new InvalidDataException("This machine's own secret is damaged.");

        using var key = LoadWrappedKey(payload[2..(2 + keyLength)]);
        key.SetProperty(PinProperty(pin));
        return AskChip(key, payload[(2 + keyLength)..]);
    }

    /// <summary>
    /// Decrypt with a key that has already loaded - so the chip is there, and it is the one that
    /// sealed this. A refusal is then a wrong PIN, or "not now": resting after too many wrong PINs,
    /// or busy. The lockout codes were deliberately never provoked on a real chip - locking it locks
    /// Windows Hello's PIN too - so every refusal but a wrong PIN counts as "not now". Never "set
    /// this PC up again": a resting chip would refuse that as well.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[] AskChip(CngKey key, byte[] sealedSecret)
    {
        using var rsa = new RSACng(key);
        try
        {
            return rsa.Decrypt(sealedSecret, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException ex)
        {
            throw new PinRefusedException(lockedOut: ex.HResult != NtePerm);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] OpenLegacy(byte[] sealedSecret)
    {
        if (!CngKey.Exists(LegacyTpmKeyName, TpmProvider))
            throw new InvalidOperationException(
                "This machine's TPM key is gone - the chip was cleared, or Windows was reinstalled. " +
                "Set this machine up again with your 12 words.");

        using var key = CngKey.Open(LegacyTpmKeyName, TpmProvider);
        return AskChip(key, sealedSecret);
    }

    [SupportedOSPlatform("windows")]
    private static void ForgetLegacyKey()
    {
        try
        {
            if (CngKey.Exists(LegacyTpmKeyName, TpmProvider)) CngKey.Open(LegacyTpmKeyName, TpmProvider).Delete();
        }
        catch (CryptographicException)
        {
            // No TPM provider on this PC, so there was never a legacy key to forget.
        }
    }

    /// <summary>
    /// A chip key under a throwaway name, deleted again before this returns - what is kept is
    /// the chip-wrapped copy, inside machine.pvm, not Windows' key file in AppData.
    ///
    /// It would be simpler unnamed, but .NET 9 cannot make an unnamed key on this provider: it
    /// fails with "Invalid flags specified" every time. That made the chip look absent, and
    /// every set-up quietly fell back to Windows' own protection. The name exists only for as
    /// long as it takes to make the key and export its wrapped copy.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static T WithTemporaryTpmKey<T>(string? pin, Func<CngKey, T> use)
    {
        var key = CngKey.Create(CngAlgorithm.Rsa, "PassVault.Making." + Guid.NewGuid().ToString("N"), TpmKeyParameters(pin));
        try
        {
            return use(key);
        }
        finally
        {
            key.Delete();   // also closes it
        }
    }

    /// <summary>The private half never leaves the chip; what is exported is wrapped by this chip and useless anywhere else.</summary>
    [SupportedOSPlatform("windows")]
    private static CngKeyCreationParameters TpmKeyParameters(string? pin)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = TpmProvider,
            ExportPolicy = CngExportPolicies.None,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        if (pin is not null) parameters.Parameters.Add(PinProperty(pin));
        return parameters;
    }

    [SupportedOSPlatform("windows")]
    private static CngProperty PinProperty(string pin)
        => new("SmartCardPin", Encoding.Unicode.GetBytes(pin + '\0'), CngPropertyOptions.None);

    /// <summary>.NET's own CngKey.Import refuses this blob type ("Invalid flags"), so call Windows directly.</summary>
    [SupportedOSPlatform("windows")]
    private static CngKey LoadWrappedKey(byte[] wrappedKey)
    {
        int status = NCryptOpenStorageProvider(out var provider, TpmProviderName, 0);
        if (status != 0) throw new CryptographicException(status);

        using (provider)
        {
            status = NCryptImportKey(provider, IntPtr.Zero, "OpaqueKeyBlob", IntPtr.Zero, out var key,
                                     wrappedKey, wrappedKey.Length, NCryptSilentFlag);
            if (status != 0) throw new CryptographicException(status);
            return CngKey.Open(key, CngKeyHandleOpenOptions.None);
        }
    }

    internal static byte[] Pack(byte kind, byte[] payload)
    {
        var file = new byte[Magic.Length + 1 + payload.Length];
        Magic.CopyTo(file, 0);
        file[Magic.Length] = kind;
        payload.CopyTo(file, Magic.Length + 1);
        return file;
    }

    internal static (byte Kind, byte[] Payload) Unpack(byte[] file)
    {
        if (file.Length < Magic.Length + 1 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("This machine's own secret is damaged.");
        return (file[Magic.Length], file[(Magic.Length + 1)..]);
    }

    private const int NCryptSilentFlag = 0x40;
    private const int TpmVersion20 = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo { public int StructVersion, TpmVersion, TpmInterfaceType, TpmImpRevision; }

    [DllImport("tbs.dll")]
    private static extern int Tbsi_GetDeviceInfo(int size, out TpmDeviceInfo info);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string providerName, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptImportKey(SafeNCryptProviderHandle provider, IntPtr importKey, string blobType,
                                              IntPtr parameterList, out SafeNCryptKeyHandle key, byte[] data,
                                              int dataLength, int flags);
}
