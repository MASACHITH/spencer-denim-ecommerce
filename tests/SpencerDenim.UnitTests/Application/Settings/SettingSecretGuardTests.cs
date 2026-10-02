using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The secret guard (ADM-22, doc 08 §3.8), built while there is nothing to lose by getting it wrong.
/// <para>
/// <b>Why these tests exist with no secret seeded.</b> The task that adds the first one is a task about
/// PayPal or SMTP, under its own deadline, and "can the value get out" is not the question it will be
/// thinking about. Three independent floors are asserted here: the admin DTO cannot carry a secret's value,
/// the storefront contract has no field one could occupy, and the protector uses the purpose doc 08 names.
/// </para>
/// </summary>
public sealed class SettingSecretGuardTests
{
    // ---- floor 1: the admin read model ------------------------------------------------------------

    /// <summary>
    /// <b>Stronger than doc 08 requires, deliberately.</b> The document's rule is that a secret is guarded by
    /// <c>Settings.Security</c>; this returns the value to <i>nobody</i>. The weaker rule needs the permission
    /// check to be right on every future read path and gets one of them wrong eventually; this needs nothing
    /// to be right, because there is no path. Nothing is lost — ciphertext is unreadable to a person, and
    /// plaintext on a settings screen is a credential in a browser's memory, its cache and any screenshot.
    /// </summary>
    [Fact]
    public void A_secrets_value_is_never_in_the_admin_read_model()
    {
        var row = new Setting
        {
            Key = "Email.Smtp.Password",
            GroupKey = "Email.Smtp",
            ValueType = SettingValueType.Secret,
            IsSecret = true,
            Value = "CfDJ8-this-is-ciphertext",
            RequiredPermission = "Settings.Security",
        };

        var dto = AdminSettingDto.From(row);

        Assert.Null(dto.Value);
        Assert.True(dto.IsSecret);

        // …but the screen can still say "a password is set", which is the whole of what it needs.
        Assert.True(dto.HasValue);
    }

    [Fact]
    public void A_secret_with_no_value_reports_that_it_is_unset()
    {
        var dto = AdminSettingDto.From(new Setting
        {
            Key = "Email.Smtp.Password",
            ValueType = SettingValueType.Secret,
            IsSecret = true,
            Value = null,
            RequiredPermission = "Settings.Security",
        });

        Assert.Null(dto.Value);
        Assert.False(dto.HasValue);
    }

    [Fact]
    public void A_non_secret_value_is_returned_as_it_is()
    {
        var dto = AdminSettingDto.From(new Setting
        {
            Key = SettingKeys.Social.InstagramUrl,
            ValueType = SettingValueType.String,
            IsSecret = false,
            Value = "https://www.instagram.com/shop",
            RequiredPermission = "Settings.Edit",
        });

        Assert.Equal("https://www.instagram.com/shop", dto.Value);
        Assert.False(dto.IsSecret);
        Assert.True(dto.HasValue);
    }

    /// <summary>
    /// A blank value counts as unset, so a row holding <c>""</c> — which nothing writes, but a database edit
    /// could — does not make the screen claim a password is configured.
    /// </summary>
    [Fact]
    public void An_empty_string_value_counts_as_unset()
    {
        Assert.False(AdminSettingDto.From(new Setting { Key = "K", Value = string.Empty }).HasValue);
    }

    // ---- floor 2: the storefront contract ---------------------------------------------------------

    /// <summary>
    /// <b>The public payload is an allow-list by shape, and this test is what keeps it one.</b>
    /// <c>GET /api/site/settings</c> is anonymous. The dangerous design is "every non-secret setting as a
    /// dictionary", where one wrong <c>IsSecret</c> flag stands between a bank account number and the
    /// internet. <see cref="SiteSettingsDto"/> instead has three named members and no general-purpose
    /// value, so there is nowhere for a secret to appear.
    /// <para>
    /// The test asserts the member list rather than the behaviour, because the behaviour it is protecting is
    /// the absence of something. A future change that adds <c>IReadOnlyDictionary&lt;string, string&gt;
    /// Values</c> to this record fails here, with the reason attached.
    /// </para>
    /// </summary>
    [Fact]
    public void The_storefront_contract_has_no_field_a_secret_could_occupy()
    {
        var members = typeof(SiteSettingsDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Logo", "Social", "WhatsAppNumber"], members);

        // And each is a type that cannot hold arbitrary key/value pairs.
        Assert.Equal(typeof(SiteLogoDto), typeof(SiteSettingsDto).GetProperty("Logo")!.PropertyType);
        Assert.Equal(typeof(string), typeof(SiteSettingsDto).GetProperty("WhatsAppNumber")!.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<SiteSocialLinkDto>),
            typeof(SiteSettingsDto).GetProperty("Social")!.PropertyType);
    }

    [Fact]
    public void A_social_link_carries_only_a_network_code_and_a_url()
    {
        var members = typeof(SiteSocialLinkDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["Network", "Url"], members);
    }

    [Fact]
    public void The_empty_payload_is_genuinely_empty()
    {
        Assert.Null(SiteSettingsDto.Empty.Logo);
        Assert.Null(SiteSettingsDto.Empty.WhatsAppNumber);
        Assert.Empty(SiteSettingsDto.Empty.Social);
    }

    // ---- floor 3: the protector -------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §3.8 names the purpose, and Data Protection derives the key from it — so changing it makes
    /// every stored secret undecryptable. It is part of the contract rather than an implementation detail,
    /// and the constant is asserted against the document's literal rather than against itself.
    /// </summary>
    [Fact]
    public void The_protection_purpose_is_the_one_doc_08_specifies()
    {
        Assert.Equal("SpencerDenim.Settings.v1", ISettingSecretProtector.Purpose);
    }

    /// <summary>
    /// <c>ops.EmailOutbox.TransportOverrideProtected</c> has its own purpose (doc 08 §3.8), which is what
    /// stops a value protected for one column being unprotected through the other. Asserted here because the
    /// containment is easy to lose by copying a constant.
    /// </summary>
    [Fact]
    public void The_purpose_differs_from_the_email_outboxs_own()
    {
        Assert.NotEqual("SpencerDenim.EmailOutbox.SmtpOverride.v1", ISettingSecretProtector.Purpose);
    }

    [Fact]
    public void A_protected_value_round_trips()
    {
        var protector = Protector();

        var ciphertext = protector.Protect("paypal-client-secret");

        Assert.NotEqual("paypal-client-secret", ciphertext);
        Assert.True(protector.TryUnprotect(ciphertext, out var plaintext));
        Assert.Equal("paypal-client-secret", plaintext);
    }

    /// <summary>
    /// The ciphertext must not be derivable by eye from the plaintext, and must differ between two
    /// protections of the same value — otherwise two accounts with the same password would be visibly the
    /// same in the table.
    /// </summary>
    [Fact]
    public void Two_protections_of_one_value_differ()
    {
        var protector = Protector();

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not ciphertext")]
    [InlineData("CfDJ8-truncated")]
    public void An_unreadable_value_answers_false_rather_than_throwing(string? stored)
    {
        // A Data Protection failure message can carry the payload it failed on, and ops.Settings.Value is the
        // one column whose contents must never reach a log. Every caller's correct response is the same —
        // treat the key as not configured — so there is nothing an exception would add.
        Assert.False(Protector().TryUnprotect(stored, out var plaintext));
        Assert.Equal(string.Empty, plaintext);
    }

    /// <summary>
    /// A value protected under a different purpose cannot be read through this one. This is what the purpose
    /// string actually buys, and it is worth proving rather than assuming.
    /// </summary>
    [Fact]
    public void A_value_protected_for_another_purpose_cannot_be_unprotected()
    {
        var provider = DataProtectionProvider.Create(nameof(SettingSecretGuardTests));
        var elsewhere = provider.CreateProtector("SpencerDenim.EmailOutbox.SmtpOverride.v1");

        var ciphertext = elsewhere.Protect("smtp-password");

        var settings = ProtectorFor(provider);
        Assert.False(settings.TryUnprotect(ciphertext, out _));
    }

    private static ISettingSecretProtector Protector() =>
        ProtectorFor(DataProtectionProvider.Create(nameof(SettingSecretGuardTests)));

    /// <summary>
    /// Builds the production type through reflection, because it is <c>internal</c> to Infrastructure and
    /// this suite tests the port's contract rather than the class. Reflection rather than a hand-written
    /// double: a double would prove its own behaviour, and the thing under test is the real protector's
    /// purpose and failure handling.
    /// </summary>
    private static ISettingSecretProtector ProtectorFor(IDataProtectionProvider provider)
    {
        var type = typeof(SpencerDenim.Infrastructure.DependencyInjection).Assembly
            .GetType("SpencerDenim.Infrastructure.Settings.SettingSecretProtector", throwOnError: true)!;

        return (ISettingSecretProtector)Activator.CreateInstance(type, provider)!;
    }
}
