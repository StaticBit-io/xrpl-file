using System.Reflection;
using System.Text.Json;

using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// What an entry's closure is for: recording that the throwaway account it names was deleted and
/// where the remainder went, without losing the seed itself — the key outlives the account, and a
/// deleted address can be funded back into existence.
/// </summary>
public sealed class AccountClosureTests : VaultTestHarness
{
    [Fact]
    public async Task An_entry_stores_and_reads_back_its_closure()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("throwaway"), TestContext.Current.CancellationToken);

        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 28_800_000, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(closure, info.Closure);
    }

    [Fact]
    public async Task An_entry_with_no_closure_reads_back_as_open()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("open"), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Null(info.Closure);
    }

    [Fact]
    public async Task An_edit_that_names_no_closure_leaves_an_existing_one_alone()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("throwaway"), TestContext.Current.CancellationToken);

        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 1, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);
        await vault.UpdateAsync(reference, new VaultEntryEdit(Label: "renamed"), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("renamed", info.Label);
        Assert.Equal(closure, info.Closure);
    }

    /// <summary>
    /// The test that matters most: an entry written <b>before</b> the closure field existed must
    /// still open. That cannot be proven with a round trip through <see cref="KeyVault.StoreAsync"/>
    /// and <see cref="KeyVault.ListAsync"/> — the writer under test always writes the property now,
    /// so a round trip can only ever exercise the "present" path, never the "absent" one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decoder this must reach is <c>KeyVault.EntryCodec.Read</c>, which is <c>private</c> —
    /// nested inside <see cref="KeyVault"/> itself, not merely <c>internal</c>. <c>InternalsVisibleTo</c>
    /// (which <c>XrplFile.Keys.csproj</c> does grant to this test assembly) only unlocks
    /// <c>internal</c> members across the assembly boundary; it has no effect on a <c>private</c>
    /// member, which is accessible only from within the declaring type's own source. So there is no
    /// route to <c>EntryCodec.Read</c> through ordinary C# member access from this project.
    /// </para>
    /// <para>
    /// Reflection is not blocked by that rule — <c>BindingFlags.NonPublic</c> reaches a <c>private</c>
    /// member regardless of assembly or <c>InternalsVisibleTo</c>, precisely because it bypasses
    /// compile-time accessibility rather than depending on a grant of it. That makes it possible to
    /// hand the decoder plaintext JSON built by hand, with no <c>"closure"</c> property in it at
    /// all — exactly the shape of every entry any build before this task ever wrote — and assert
    /// directly on what came back, rather than resting on inspection of the source.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_decoder_reads_an_entry_with_no_closure_property_at_all()
    {
        byte[] plaintext = PlaintextWithNoClosureProperty();

        VaultEntry entry = InvokeEntryCodecRead("pre-existing-reference", plaintext);

        Assert.Null(entry.Info.Closure);
        Assert.Equal("pre-existing-reference", entry.Info.Reference);
        Assert.Equal("legacy", entry.Info.Label);
        Assert.Equal("sSecretOfLegacy", entry.Secret);
    }

    /// <summary>
    /// The exact shape <c>EntryCodec.Write</c> produced before this task added the closure block —
    /// every field it has ever written, and nothing this task added.
    /// </summary>
    private static byte[] PlaintextWithNoClosureProperty()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", nameof(VaultEntryKind.Generated));
            writer.WriteString("label", "legacy");
            writer.WriteString("network", "standalone");
            writer.WriteString("address", "rAddressOfLegacy");
            writer.WriteString("algorithm", (string?)null);
            writer.WriteString("createdAt", DateTimeOffset.UnixEpoch);
            writer.WriteString("secret", "sSecretOfLegacy");
            writer.WriteNull("lastKnownSequence");
            writer.WriteNull("deletableFromLedger");
            writer.WriteBoolean("exported", false);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static VaultEntry InvokeEntryCodecRead(string reference, byte[] plaintext)
    {
        Type entryCodec = typeof(KeyVault).GetNestedType("EntryCodec", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("KeyVault no longer has a nested EntryCodec type.");
        MethodInfo read = entryCodec.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("EntryCodec no longer has a static Read method.");

        return (VaultEntry)read.Invoke(obj: null, [reference, plaintext])!;
    }
}
