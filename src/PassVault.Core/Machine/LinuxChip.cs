using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PassVault.Core.Machine;

/// <summary>
/// This PC's TPM chip on Linux, through tpm2-tools - the standard command-line tools every
/// distribution packages. Same job as on Windows: the secret is sealed so the chip only gives
/// it back for the right PIN, and the chip counts wrong PINs itself.
///
/// The sealed object never names a key kept anywhere on disk. Its parent is made again from
/// the chip's own seed on every use (the same template always gives the same key), so all
/// there is to keep is what lands in machine.pvm. Clearing the chip changes the seed, and the
/// secret is gone - which is the point.
///
/// The tools send commands wherever TPM2TOOLS_TCTI says - /dev/tpmrm0 by default. Tests point
/// it at swtpm, a software TPM, which can be given wrong PINs freely.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public static class LinuxChip
{
    public const byte Kind = 3;       // payload: pubLength(2) || public part || private part
    public const byte FileKind = 4;   // payload: the secret itself, kept in a file only you can read

    /// <summary>False when the user turned the chip off in the settings (chip.use = "off").</summary>
    public static bool Allowed { get; set; } = true;

    /// <summary>A chip to use: a TPM device, or a software TPM the tools have been pointed at.</summary>
    public static bool HasChip => Allowed &&
        (Environment.GetEnvironmentVariable("TPM2TOOLS_TCTI") is { Length: > 0 } || File.Exists("/dev/tpmrm0"));

    /// <summary>
    /// Seal with the chip when there is one, proving the round trip before trusting it. Like
    /// Windows, a PC with a chip never falls back to something weaker: a failure is shown.
    /// </summary>
    public static DeviceSecret.Made Seal(byte[] secret, string pin)
    {
        if (!HasChip) return new(secret, DeviceProtection.UserFile, DeviceSecret.Pack(FileKind, secret));

        byte[] payload;
        var dir = PrivateFolder();
        try
        {
            MakeParent(dir);
            File.WriteAllText(Path.Combine(dir, "pin"), pin);
            Tool(dir, secret, "tpm2_create", "-C", "parent.ctx", "-g", "sha256",
                 "-u", "seal.pub", "-r", "seal.priv", "-i", "-", "-p", "file:pin");

            var pub = File.ReadAllBytes(Path.Combine(dir, "seal.pub"));
            var priv = File.ReadAllBytes(Path.Combine(dir, "seal.priv"));
            payload = new byte[2 + pub.Length + priv.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)pub.Length);
            pub.CopyTo(payload, 2);
            priv.CopyTo(payload, 2 + pub.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        if (!Open(Kind, payload, pin).Secret.AsSpan().SequenceEqual(secret))
            throw new CryptographicException("The chip did not give the secret back.");

        return new(secret, DeviceProtection.Tpm, DeviceSecret.Pack(Kind, payload));
    }

    public static (byte[] Secret, DeviceProtection Protection) Open(byte kind, byte[] payload, string pin)
    {
        if (kind == FileKind) return (payload, DeviceProtection.UserFile);
        if (kind != Kind)
            throw new InvalidDataException("This machine's own secret was made on another kind of PC. Set this machine up again with your 12 words.");

        int pubLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (payload.Length < 2 + pubLength)
            throw new InvalidDataException("This machine's own secret is damaged.");

        var dir = PrivateFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "seal.pub"), payload[2..(2 + pubLength)]);
            File.WriteAllBytes(Path.Combine(dir, "seal.priv"), payload[(2 + pubLength)..]);
            MakeParent(dir);

            try
            {
                Tool(dir, null, "tpm2_load", "-C", "parent.ctx", "-u", "seal.pub", "-r", "seal.priv", "-c", "seal.ctx");
            }
            catch (ChipSaidNo no) when (no.NotNow)
            {
                throw new PinRefusedException(lockedOut: true);
            }
            catch (ChipSaidNo no)
            {
                // Only the chip that sealed it can load it: this one was cleared, or it is another PC.
                throw new CryptographicException(
                    "This PC's chip no longer knows this secret - it was cleared, or this is another PC.", no);
            }

            try
            {
                // The PIN goes in through a pipe: never on a command line, where other users can read it.
                return (Tool(dir, Encoding.ASCII.GetBytes(pin), "tpm2_unseal", "-c", "seal.ctx", "-p", "file:-"),
                        DeviceProtection.Tpm);
            }
            catch (ChipSaidNo no)
            {
                // The object loaded, so the chip is the right one. A refusal is a wrong PIN - counted by
                // the chip - or "not now": resting after too many, or busy. Never "set this PC up again".
                throw new PinRefusedException(lockedOut: !no.WrongPassword);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The parent key, made again from the chip's seed. A fast elliptic-curve key: an RSA one takes
    /// seconds on a real chip.
    ///
    /// Outside the chip's lockout ("noda"). It has no password, so lockout guards nothing on it -
    /// and when the chip rested, it refused to use the parent at all, which read as "the chip was
    /// cleared, set this PC up again". The secret under it, which needs the PIN, stays inside it.
    /// </summary>
    private static void MakeParent(string dir)
        => Tool(dir, null, "tpm2_createprimary", "-C", "o", "-G", "ecc", "-g", "sha256", "-c", "parent.ctx",
                "-a", "restricted|decrypt|fixedtpm|fixedparent|sensitivedataorigin|userwithauth|noda");

    /// <summary>
    /// For the tools' working files. XDG_RUNTIME_DIR is memory only and readable by you alone,
    /// so the PIN never touches the disk. Deleted as soon as the tools are done.
    /// </summary>
    private static string PrivateFolder()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = Directory.Exists(runtime) ? runtime : Path.GetTempPath();
        var dir = Path.Combine(root, "passvault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    private static byte[] Tool(string dir, byte[]? input, string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "PassVault needs tpm2-tools to use this PC's TPM chip. Install it - for example: sudo apt install tpm2-tools");
        }

        using (process)
        {
            var output = new MemoryStream();
            var copying = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = process.StandardError.ReadToEndAsync();
            if (input is not null) process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
            process.WaitForExit();
            copying.Wait();

            if (process.ExitCode == 0) return output.ToArray();

            var said = errors.Result;
            if (said.Contains("Permission denied"))
                throw new InvalidOperationException(
                    "PassVault cannot reach this PC's TPM chip. Add yourself to the tss group (sudo usermod -aG tss $USER), then log out and back in.");
            throw new ChipSaidNo(tool, said);
        }
    }

    /// <summary>The chip, or the tools on its behalf, refused. Carries the TPM's own response code.</summary>
    private sealed class ChipSaidNo(string tool, string said) : Exception($"{tool}: {said.Trim()}")
    {
        /// <summary>The TPM's response code, the last one the tools printed. -1 when there is none.</summary>
        private int Code
        {
            get
            {
                var codes = Regex.Matches(said, @"\(0x([0-9A-Fa-f]+)\)");
                return codes.Count == 0 ? -1 : Convert.ToInt32(codes[^1].Groups[1].Value, 16);
            }
        }

        /// <summary>
        /// TPM_RC_AUTH_FAIL (counted towards lockout) or TPM_RC_BAD_AUTH. Both are format-one codes,
        /// so the low byte holds the error whichever session it was reported against.
        /// </summary>
        public bool WrongPassword => Code is >= 0 and <= 0xFFFF && (Code & 0x80) != 0 && (Code & 0xFF) is 0x8E or 0xA2;

        /// <summary>A TPM warning - lockout (0x921), busy, retry: the chip is fine and will answer later.</summary>
        public bool NotNow => Code is >= 0 and <= 0xFFFF && (Code & 0x80) == 0 && (Code & 0xF00) == 0x900;
    }
}
