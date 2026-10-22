using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Pulse.Tests;

/// <summary>
/// Checks the payload and headers of an integration event against an AsyncAPI 3 document, so a test can say that what
/// Pulse publishes is what <c>contracts/integration-events.yaml</c> promises, and what it consumes is what that file describes.
/// </summary>
public sealed class AsyncApiContract
{
    private const string BaseUri = "https://contract.test/asyncapi";

    private readonly JsonObject _document;
    private readonly BuildOptions _buildOptions;

    public AsyncApiContract(string yamlPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(yamlPath)) yaml.Load(reader);
        _document = (JsonObject)OpenApiContract.ToJson(yaml.Documents[0].RootNode)!;

        _buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var definitions = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BaseUri,
            ["$defs"] = _document["components"]!["schemas"]!.DeepClone(),
        };
        JsonSchema.FromText(OpenApiContract.PointAtDefinitions(definitions, BaseUri).ToJsonString(), _buildOptions, new Uri(BaseUri));
    }

    /// <summary>The names of the events the document defines, such as <c>alert.raised.v1</c>.</summary>
    public IReadOnlyList<string> EventNames =>
        [.. _document["components"]!["messages"]!.AsObject().Select(m => m.Value!["name"]!.GetValue<string>())];

    /// <summary>What is wrong with the payload for this event, or nothing if it fits.</summary>
    public IReadOnlyList<string> ValidatePayload(string eventName, JsonElement payload) =>
        Validate(Message(eventName)["payload"] ?? throw new Xunit.Sdk.XunitException($"{eventName} has no payload schema"), payload);

    /// <summary>What is wrong with the headers for this event, or nothing if they fit.</summary>
    public IReadOnlyList<string> ValidateHeaders(string eventName, IReadOnlyDictionary<string, object?> headers) =>
        Validate(
            Message(eventName)["headers"] ?? throw new Xunit.Sdk.XunitException($"{eventName} has no headers schema"),
            JsonSerializer.SerializeToElement(headers));

    private JsonObject Message(string eventName) =>
        _document["components"]!["messages"]!.AsObject().Select(m => m.Value!.AsObject()).FirstOrDefault(m => m["name"]!.GetValue<string>() == eventName)
        ?? throw new Xunit.Sdk.XunitException($"{eventName} is not in the contract. It defines: {string.Join(", ", EventNames)}");

    private IReadOnlyList<string> Validate(JsonNode schemaNode, JsonElement instance)
    {
        var schema = JsonSchema.FromText(OpenApiContract.PointAtDefinitions(schemaNode.DeepClone(), BaseUri).ToJsonString(), _buildOptions);
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        return result.IsValid
            ? []
            : [.. (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Distinct()];
    }
}
