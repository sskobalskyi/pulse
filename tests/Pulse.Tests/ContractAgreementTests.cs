using Pulse.Core;

namespace Pulse.Tests;

/// <summary>
/// The contract and the code both say what a valid registration looks like: the contract in schemas and patterns,
/// the code in <see cref="RegistrationValidator"/>. They are written in two places, so they can drift. These tests
/// feed the same inputs to both and require the same answer.
/// </summary>
public class ContractAgreementTests
{
    private static readonly OpenApiContract Contract = new(ContractPath());
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "pulse-device-api.yaml");
    }

    private static DeviceRegistration Registration(
        string id = "ghx-demo-0001",
        string? claimTokenHash = null,
        string channelId = "soil-1",
        DateTimeOffset? expires = null) => new(
        id,
        "greenhaus-pot-v1",
        new Capabilities([new Channel(channelId, "soil_moisture")], [new ActuatorCapability("pump-1", "pump", new HardLimits(20, 600, 120))]),
        "pbkdf2$salt$hash",
        claimTokenHash ?? ClaimTokens.Hash("token"),
        expires ?? Now.AddDays(30));

    private static bool ContractAccepts(DeviceRegistration registration) =>
        Contract.ValidateRequest("POST", "/devices", registration).Count == 0;

    private static bool CodeAccepts(DeviceRegistration registration) =>
        RegistrationValidator.Validate(registration, Now).Count == 0;

    [Fact]
    public void A_complete_registration_is_valid_for_both()
    {
        var registration = Registration();

        Assert.Empty(Contract.ValidateRequest("POST", "/devices", registration));
        Assert.Empty(RegistrationValidator.Validate(registration, Now));
    }

    [Theory]
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824")]
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b982")] // one short
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b98244")] // one long
    [InlineData("zcf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")] // not hex
    [InlineData("sha256:")]
    [InlineData("sha1:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("a-plain-token")]
    [InlineData("")]
    public void A_claim_token_hash_is_valid_for_the_contract_exactly_when_it_is_valid_for_the_code(string hash)
    {
        var registration = Registration(claimTokenHash: hash);

        Assert.Equal(CodeAccepts(registration), ContractAccepts(registration));
    }

    [Theory]
    [InlineData("ghx-demo-0001", true)]
    [InlineData("A.b_c-9", true)]
    [InlineData("has space", false)]
    [InlineData("slash/id", false)]
    [InlineData("plus+id", false)]
    [InlineData("hash#id", false)]
    [InlineData("ünïcode", false)]
    [InlineData("", false)]
    public void An_id_the_contract_allows_is_one_the_code_allows(string id, bool expected)
    {
        var registration = Registration(id: id, channelId: id.Length == 0 ? "soil-1" : id);

        Assert.Equal(expected, ContractAccepts(registration));
        Assert.Equal(expected, CodeAccepts(registration));
    }

    [Fact]
    public void The_longest_allowed_id_is_64_characters_for_both()
    {
        var longest = new string('a', 64);
        var tooLong = new string('a', 65);

        Assert.True(ContractAccepts(Registration(id: longest)));
        Assert.True(CodeAccepts(Registration(id: longest)));
        Assert.False(ContractAccepts(Registration(id: tooLong)));
        Assert.False(CodeAccepts(Registration(id: tooLong)));
    }

    [Theory]
    [InlineData("soil-1", true)]
    [InlineData("zone.2_b", true)]
    [InlineData("zone 2", false)]
    [InlineData("zone/2", false)]
    public void A_channel_id_the_contract_allows_is_one_the_code_allows(string channelId, bool expected)
    {
        var registration = Registration(channelId: channelId);

        Assert.Equal(expected, ContractAccepts(registration));
        Assert.Equal(expected, CodeAccepts(registration));
    }

    [Fact]
    public void An_actuator_without_hard_limits_is_refused_by_both()
    {
        var body = new
        {
            id = "ghx-1",
            model = "m",
            capabilities = new { channels = Array.Empty<object>(), actuators = new[] { new { id = "pump-1", kind = "pump" } } },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = Now.AddDays(1),
        };

        Assert.NotEmpty(Contract.ValidateRequest("POST", "/devices", body));
    }

    [Fact]
    public void The_hash_the_claim_code_computes_is_one_the_contract_accepts()
    {
        // Whoever prints the token on the device computes this. It must be what the contract says.
        foreach (var token in new[] { "a", "ghx-3f9a-7c21", new string('x', 200), "ünïcode-token" })
            Assert.Empty(Contract.ValidateRequest("POST", "/devices", Registration(claimTokenHash: ClaimTokens.Hash(token))));
    }
}
