// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

#pragma warning disable OPENAI001

using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Models.Ai;
using OpenAI.Responses;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class OpenAiResponseToolFactory
{
    public static FunctionTool Create(AiToolDefinition definition) =>
        CreateFunctionTool(definition, AiToolJsonSchemaCatalog.GetParametersJson(definition));

    private static FunctionTool CreateFunctionTool(AiToolDefinition definition, string parametersJson) =>
        ResponseTool.CreateFunctionTool(
            functionName: definition.Name,
            functionParameters: BinaryData.FromString(NormalizeParametersJsonForStrictMode(parametersJson)),
            strictModeEnabled: true,
            functionDescription: definition.Description);

    internal static string NormalizeParametersJsonForStrictMode(string json)
    {
        var node = JsonNode.Parse(json)?.DeepClone()
            ?? throw new InvalidOperationException("OpenAI tool parameters JSON could not be parsed.");

        NormalizeSchemaNode(node);
        return node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static void NormalizeSchemaNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties)
                {
                    foreach (var (_, propertySchema) in properties)
                    {
                        if (propertySchema is not null)
                        {
                            NormalizeSchemaNode(propertySchema);
                        }
                    }

                    var required = new JsonArray();
                    foreach (var (propertyName, _) in properties)
                    {
                        required.Add(propertyName);
                    }

                    obj["required"] = required;
                }

                if (obj["items"] is not null)
                {
                    NormalizeSchemaNode(obj["items"]!);
                }

                NormalizeCompositeSchemaArray(obj, "anyOf");
                NormalizeCompositeSchemaArray(obj, "allOf");
                NormalizeCompositeSchemaArray(obj, "oneOf");
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        NormalizeSchemaNode(item);
                    }
                }

                break;
        }
    }

    private static void NormalizeCompositeSchemaArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonArray schemas)
        {
            return;
        }

        foreach (var schema in schemas)
        {
            if (schema is not null)
            {
                NormalizeSchemaNode(schema);
            }
        }
    }
}

#pragma warning restore OPENAI001
