// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public static class AiToolJsonSchemaCatalog
{
    public static string GetParametersJson(AiToolDefinition definition) => definition.Name switch
    {
        AiToolNames.ListServers =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "includeUnattachedServers": {
                  "type": "boolean",
                  "description": "When true, include every managed server instead of only the servers attached to this chat."
                }
              }
            }
            """,
        AiToolNames.GetServerSummary =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                }
              },
              "required": [ "serverId" ]
            }
            """,
        AiToolNames.GetServerHealth =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                }
              },
              "required": [ "serverId" ]
            }
            """,
        AiToolNames.ListServices =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "filter": {
                  "type": "string",
                  "description": "Optional case-insensitive filter for matching service names or descriptions."
                }
              },
              "required": [ "serverId" ]
            }
            """,
        AiToolNames.RestartService =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "serviceName": {
                  "type": "string",
                  "description": "The exact systemd unit name to restart."
                }
              },
              "required": [ "serverId", "serviceName" ]
            }
            """,
        AiToolNames.BrowseDirectory =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "path": {
                  "type": "string",
                  "description": "The remote directory path to browse."
                }
              },
              "required": [ "serverId", "path" ]
            }
            """,
        AiToolNames.ReadFile =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "path": {
                  "type": "string",
                  "description": "The remote file path to read."
                },
                "maxBytes": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 262144,
                  "description": "Optional byte limit for large files."
                }
              },
              "required": [ "serverId", "path" ]
            }
            """,
        AiToolNames.RunCommand =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "commandText": {
                  "type": "string",
                  "description": "The shell command to execute exactly as written."
                },
                "workingDirectory": {
                  "type": "string",
                  "description": "Optional working directory for the command."
                }
              },
              "required": [ "serverId", "commandText" ]
            }
            """,
        AiToolNames.WriteFileWithConfirmation =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "path": {
                  "type": "string",
                  "description": "The remote file path to write."
                },
                "content": {
                  "type": "string",
                  "description": "The full text content to write."
                },
                "createDirectories": {
                  "type": "boolean",
                  "description": "When true, create missing parent directories."
                }
              },
              "required": [ "serverId", "path", "content" ]
            }
            """,
        AiToolNames.InstallPackageWithConfirmation =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "serverId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The Linux Made Sane server identifier."
                },
                "packageNames": {
                  "type": "array",
                  "description": "The package names to install with apt-get.",
                  "items": {
                    "type": "string"
                  },
                  "minItems": 1
                }
              },
              "required": [ "serverId", "packageNames" ]
            }
            """,
        _ => throw new InvalidOperationException($"Tool metadata is not defined for tool {definition.Name}.")
    };

    public static JsonNode ParseParametersSchema(AiToolDefinition definition) =>
        JsonNode.Parse(GetParametersJson(definition))
        ?? throw new InvalidOperationException($"Tool schema could not be parsed for {definition.Name}.");
}
