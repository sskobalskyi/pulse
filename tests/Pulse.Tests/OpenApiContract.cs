using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Pulse.Tests;

/// <summary>
/// Checks real HTTP responses against an OpenAPI 3.1 document: the status code must be one the operation documents,
/// and the body must match the schema documented for it. It also records which operations were exercised, so a test
/// can state exactly which part of the contract is covered.
/// </summary>
public sealed class OpenApiContract
{
    private const string BaseUri = "https://contract.test/openapi";
    private const string SchemaPrefix = "#/components/schemas/";

    private readonly JsonObject _document;
    private readonly BuildOptions _buildOptions;
    private readonly HashSet<string> _covered = [];

    public OpenApiContract(string yamlPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(yamlPath)) yaml.Load(reader);
        _document = (JsonObject)ToJson(yaml.Documents[0].RootNode)!;

        // Register the component schemas as $defs of one document, so '#/components/schemas/X' references resolve.
        _buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var definitions = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BaseUri,
            ["$defs"] = _document["components"]!["schemas"]!.DeepClone(),
        };
        JsonSchema.FromText(PointAtDefinitions(definitions).ToJsonString(), _buildOptions, new Uri(BaseUri));
    }

    /// <summary>Every operation of the contract, as (METHOD, path template, operationId).</summary>
    public IReadOnlyList<(string Method, string Path, string OperationId)> Operations =>
        [.. _document["paths"]!.AsObject().SelectMany(path =>
            path.Value!.AsObject()
                .Where(m => m.Key is "get" or "post" or "put" or "patch" or "delete")
                .Select(m => (m.Key.ToUpperInvariant(), path.Key, m.Value!["operationId"]!.GetValue<string>())))];

    /// <summary>The operation ids that responses have been verified for so far.</summary>
    public IReadOnlySet<string> Covered => _covered;

    /// <summary>The base path of the first server of the contract, joined to <paramref name="path"/>.</summary>
    public string Url(string path) => new Uri(_document["servers"]![0]!["url"]!.GetValue<string>()).AbsolutePath.TrimEnd('/') + path;

    /// <summary>Fails the test unless the response is documented for the operation and fits the documented schema.</summary>
    public async Task Verify(HttpResponseMessage response, string method, string pathTemplate)
    {
        var operation = _document["paths"]![pathTemplate]?[method.ToLowerInvariant()]
            ?? throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} is not in the contract");
        _covered.Add(operation["operationId"]!.GetValue<string>());

        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var documented = operation["responses"]!.AsObject();
        var entry = documented[status] ?? documented["default"];
        if (entry is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"{method} {pathTemplate} returned {status}, the contract documents: {string.Join(", ", documented.Select(r => r.Key))}");
        }

        var content = Resolve(entry.AsObject())["content"]?.AsObject();
        var body = await response.Content.ReadAsStringAsync();
        if (content is null || content.Count == 0)
        {
            Xunit.Assert.True(string.IsNullOrEmpty(body), $"{method} {pathTemplate} {status} should have no body, got: {body}");
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var media = content[mediaType]
            ?? throw new Xunit.Sdk.XunitException(
                $"{method} {pathTemplate} {status} returned '{mediaType}', the contract documents: {string.Join(", ", content.Select(c => c.Key))}");
        if (media["schema"] is not { } schemaNode) return;

        var schema = JsonSchema.FromText(PointAtDefinitions(schemaNode.DeepClone()).ToJsonString(), _buildOptions);
        using var instance = JsonDocument.Parse(body);
        var result = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (result.IsValid) return;

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"  {d.InstanceLocation}: {e.Value}"))
            .Distinct();
        throw new Xunit.Sdk.XunitException(
            $"{method} {pathTemplate} {status} does not match the schema:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}{Environment.NewLine}Body: {body}");
    }

    /// <summary>
    /// Checks a request body against the schema the contract documents for the operation. Returns what is wrong, empty if it fits.
    /// This is how a test can say that a client following the contract would be accepted, or refused, the way the API does.
    /// </summary>
    public IReadOnlyList<string> ValidateRequest(string method, string pathTemplate, object body)
    {
        var operation = _document["paths"]![pathTemplate]?[method.ToLowerInvariant()]
            ?? throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} is not in the contract");
        var schemaNode = operation["requestBody"]?["content"]?["application/json"]?["schema"]
            ?? throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} documents no JSON request body");

        var schema = JsonSchema.FromText(PointAtDefinitions(schemaNode.DeepClone()).ToJsonString(), _buildOptions);
        var instance = JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        return result.IsValid
            ? []
            : [.. (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Distinct()];
    }

    // A response may be a reference to '#/components/responses/X'.
    private JsonObject Resolve(JsonObject node)
    {
        if (node["$ref"] is not JsonValue reference || reference.GetValue<string>() is not { } pointer || !pointer.StartsWith("#/", StringComparison.Ordinal))
            return node;

        JsonNode? current = _document;
        foreach (var part in pointer[2..].Split('/')) current = current![part.Replace("~1", "/").Replace("~0", "~")];
        return current!.AsObject();
    }

    // Point '#/components/schemas/X' references at the registered definitions document.
    private static JsonNode PointAtDefinitions(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Key == "$ref" && property.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                        obj[property.Key] = BaseUri + "#/$defs/" + text[SchemaPrefix.Length..];
                    else if (property.Value is not null) PointAtDefinitions(property.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>()) PointAtDefinitions(item);
                break;
        }

        return node;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, ToJson(c.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => ToScalar(scalar),
        _ => null,
    };

    // YAML plain scalars carry their type (true, 3, 1.5, null); quoted ones are always strings.
    private static JsonNode? ToScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);

        return value switch
        {
            null or "" or "null" or "~" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(value),
        };
    }
}
