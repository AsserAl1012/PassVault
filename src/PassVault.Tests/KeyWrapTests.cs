using System.Security.Cryptography;
using System.Text;
using PassVault.Core.Crypto;

namespace PassVault.Tests;

public class KeyWrapTests
{
    // Cheap settings so the test run stays quick. The real ones are checked in VectorTests.
    private static readonly KdfSettings Fast = new("argon2id", 1, 8192, 1, 32);

    private static readonly byte[] MasterKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] UsbSecret = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] DeviceSecret = RandomNumberGenerator.GetBytes(32);
    private const string VaultId = "dmF1bHQtaWQtMTIzNDU2Nzg=";
    private const string MachineId = "desktop-01";
    private const string Pin = "4821";

    // ---- recovery phrase ----

    [Fact]
    public void Phrase_locks_and_unlocks_the_master_key()
    {
        var phrase = Mnemonic.Generate();
        var slot = KeyWrap.WrapWithPhrase(MasterKey, phrase, VaultId, Fast);

        Assert.Equal(MasterKey, KeyWrap.UnwrapWithPhrase(slot, phrase, VaultId, Fast));
    }

    [Fact]
    public void A_different_phrase_does_not_unlock_it()
    {
        var slot = KeyWrap.WrapWithPhrase(MasterKey, Mnemonic.Generate(), VaultId, Fast);

        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPhrase(slot, Mnemonic.Generate(), VaultId, Fast));
    }

    [Fact]
    public void Messy_typing_still_unlocks()
    {
        var phrase = Mnemonic.Generate();
        var slot = KeyWrap.WrapWithPhrase(MasterKey, phrase, VaultId, Fast);
        var messy = "  " + phrase.ToUpperInvariant().Replace(" ", "  ") + " ";

        Assert.Equal(MasterKey, KeyWrap.UnwrapWithPhrase(slot, messy, VaultId, Fast));
    }

    [Fact]
    public void A_slot_from_another_vault_does_not_open()
    {
        // The vault id is authenticated, so a slot cannot be lifted into a different vault.
        var phrase = Mnemonic.Generate();
        var slot = KeyWrap.WrapWithPhrase(MasterKey, phrase, VaultId, Fast);

        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPhrase(slot, phrase, "b3RoZXItdmF1bHQtaWQxMg==", Fast));
    }

    // ---- USB + chip + PIN ----

    [Fact]
    public void All_three_ingredients_together_unlock_the_master_key()
    {
        var slot = KeyWrap.WrapWithPin(MasterKey, UsbSecret, DeviceSecret, Pin, VaultId, MachineId, Fast);

        Assert.Equal(MasterKey,
            KeyWrap.UnwrapWithPin(slot, UsbSecret, DeviceSecret, Pin, VaultId, MachineId, Fast));
    }

    [Fact]
    public void Missing_any_one_ingredient_fails()
    {
        var slot = KeyWrap.WrapWithPin(MasterKey, UsbSecret, DeviceSecret, Pin, VaultId, MachineId, Fast);
        var other = RandomNumberGenerator.GetBytes(32);

        // wrong stick
        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPin(slot, other, DeviceSecret, Pin, VaultId, MachineId, Fast));
        // wrong machine's chip
        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPin(slot, UsbSecret, other, Pin, VaultId, MachineId, Fast));
        // wrong PIN
        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPin(slot, UsbSecret, DeviceSecret, "0000", VaultId, MachineId, Fast));
        // right ingredients, wrong machine name
        Assert.ThrowsAny<CryptographicException>(
            () => KeyWrap.UnwrapWithPin(slot, UsbSecret, DeviceSecret, Pin, VaultId, "laptop-02", Fast));
    }

    [Fact]
    public void Pin_input_is_the_three_parts_in_order()
    {
        var input = KeyWrap.PinInput(UsbSecret, DeviceSecret, Pin);

        Assert.Equal(68, input.Length);
        Assert.Equal(UsbSecret, input[..32]);
        Assert.Equal(DeviceSecret, input[32..64]);
        Assert.Equal(Encoding.ASCII.GetBytes(Pin), input[64..]);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    [InlineData("")]
    public void A_bad_pin_is_refused_up_front(string pin)
    {
        Assert.Throws<ArgumentException>(() => KeyWrap.PinInput(UsbSecret, DeviceSecret, pin));
    }

    [Fact]
    public void A_slot_is_only_opened_by_the_path_it_belongs_to()
    {
        var pinSlot = KeyWrap.WrapWithPin(MasterKey, UsbSecret, DeviceSecret, Pin, VaultId, MachineId, Fast);

        Assert.Throws<InvalidOperationException>(
            () => KeyWrap.UnwrapWithPhrase(pinSlot, Mnemonic.Generate(), VaultId, Fast));
    }
}
