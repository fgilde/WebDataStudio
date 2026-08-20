using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What a column name says about its content. A false positive costs one click to reveal; a false
/// negative puts a password on a shared screen and into an export.
public class SensitiveColumnsTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("user_password")]
    [InlineData("userPassword")]
    [InlineData("PASSWORD-HASH")]
    [InlineData("password_hash")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("secret")]
    [InlineData("access_token")]
    [InlineData("iban")]
    [InlineData("card_number")]
    [InlineData("cvv")]
    public void A_name_that_says_secret_is_treated_as_one(string column) =>
        Assert.True(SensitiveColumns.IsSensitive(column), column);

    [Theory]
    // Facts about a secret, not the secret: a timestamp, a counter, a policy.
    [InlineData("password_changed_at")]
    [InlineData("passwordUpdatedOn")]
    [InlineData("password_expires")]
    [InlineData("password_attempts")]
    [InlineData("password_reset_required")]
    [InlineData("token_expiry")]
    // And names that merely contain a substring.
    [InlineData("passport_number")]
    [InlineData("name")]
    [InlineData("city")]
    [InlineData("description")]
    public void A_name_that_only_talks_about_one_is_not(string column) =>
        Assert.False(SensitiveColumns.IsSensitive(column), column);

    [Fact]
    public void An_explicit_entry_beats_the_heuristic_in_both_directions()
    {
        var policy = new MaskPolicy(
            MaskByDefault: true,
            Extra: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "salary" },
            Never: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password" });

        Assert.True(SensitiveColumns.ShouldMask("salary", policy));
        Assert.False(SensitiveColumns.ShouldMask("password", policy));
    }

    [Fact]
    public void Masking_can_be_switched_off_for_a_connection()
    {
        var policy = MaskPolicy.Default with { MaskByDefault = false };

        Assert.False(SensitiveColumns.ShouldMask("password", policy));
    }

    [Fact]
    public void The_mask_does_not_leak_the_length_of_what_it_hides()
    {
        // Two secrets of different lengths have to look identical.
        Assert.Equal(SensitiveColumns.Mask, SensitiveColumns.Mask);
        Assert.DoesNotContain(SensitiveColumns.Mask, "abc");
    }
}
