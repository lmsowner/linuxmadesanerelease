// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
        AiToolNames.SearchWeb =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "query": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 500,
                  "description": "A focused search query containing the application, version, observed error, or behaviour that local evidence does not explain."
                },
                "domains": {
                  "type": "array",
                  "maxItems": 8,
                  "items": { "type": "string" },
                  "description": "Optional primary-source domains to prefer or constrain, such as the vendor documentation host or github.com."
                },
                "maxResults": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 8,
                  "description": "Maximum number of search results to return."
                }
              },
              "required": [ "query" ]
            }
            """,
        AiToolNames.FetchWebPage =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "url": {
                  "type": "string",
                  "description": "The public HTTP(S) URL returned by search_web or supplied as a relevant primary source."
                },
                "maxCharacters": {
                  "type": "integer",
                  "minimum": 1000,
                  "maximum": 20000,
                  "description": "Maximum documentation text to return."
                }
              },
              "required": [ "url" ]
            }
            """,
        AiToolNames.InspectHomeLab =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {}
            }
            """,
        AiToolNames.InspectHomeLabApplicationConfig =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "installationId": { "type": "string", "format": "uuid", "description": "The exact LMS Home Lab installation ID returned by inspect_home_lab." },
                "relativePath": { "type": "string", "maxLength": 500, "description": "Optional path returned by an earlier config inspection, such as config/config.xml. Omit to list the app's top-level supported configuration files." }
              },
              "required": [ "installationId" ]
            }
            """,
        AiToolNames.RepairHomeLabApplicationConfig =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "installationId": { "type": "string", "format": "uuid" },
                "relativePath": { "type": "string", "minLength": 1, "maxLength": 500, "description": "The exact relative path returned by inspect_home_lab_app_config." },
                "expectedSha256": { "type": "string", "pattern": "^[A-Fa-f0-9]{64}$", "description": "The exact hash returned by the inspection, used to reject stale edits." },
                "expectedText": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "A non-secret exact text fragment that occurs once in the inspected file." },
                "replacementText": { "type": "string", "maxLength": 4096, "description": "The reviewed replacement for that exact fragment." }
              },
              "required": [ "installationId", "relativePath", "expectedSha256", "expectedText", "replacementText" ]
            }
            """,
        AiToolNames.RepairHomeLabInstallation =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "installationId": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The exact LMS Home Lab installation ID returned by inspect_home_lab."
                },
                "restoreContainerSettings": {
                  "type": "boolean",
                  "description": "Set true only when manual image, environment, or volume edits caused the fault and the user wants the saved pre-edit settings restored. Otherwise repair reapplies the app catalog and LMS-managed runtime state, including shared-network host resolution, environment, routes, ports, volumes, and health checks."
                }
              },
              "required": [ "installationId" ]
            }
            """,
        AiToolNames.ApplyHomeLabPromptRecipe =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "promptRecipeId": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 80,
                  "description": "The exact deployable LMS HomeLab Recipe ID returned by inspect_home_lab. Recipes marked as requiring planning cannot be applied until LMS supports the requested components."
                },
                "listenAddress": {
                  "type": "string",
                  "minLength": 7,
                  "maxLength": 45,
                  "description": "The exact current LMS host IPv4 address selected by the user for inbound client connections. Never substitute 0.0.0.0."
                },
                "outboundRoute": {
                  "type": "string",
                  "enum": [ "direct", "vpn" ],
                  "description": "The outbound network selected by the user: direct for the regular server route, or vpn for the selected VPN Gateway."
                },
                "vpnGatewayInstallationId": {
                  "type": [ "string", "null" ],
                  "format": "uuid",
                  "description": "The existing LMS VPN Gateway installation to reuse. Required when the HomeLab Recipe needs VPN and more than one usable gateway exists."
                }
              },
              "required": [ "promptRecipeId", "listenAddress", "outboundRoute" ]
            }
            """,
        AiToolNames.DesktopSetKeyboardLayout =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "layout": {
                  "type": "string",
                  "description": "The XKB keyboard layout code to apply to the signed-in desktop session, for example gb or us."
                }
              },
              "required": [ "layout" ]
            }
            """,
        AiToolNames.DesktopInstallAptPackages =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "packageNames": {
                  "type": "array",
                  "description": "The apt package names to install on the local LMS desktop machine.",
                  "items": {
                    "type": "string"
                  },
                  "minItems": 1
                }
              },
              "required": [ "packageNames" ]
            }
            """,
        _ => throw new InvalidOperationException($"Tool metadata is not defined for tool {definition.Name}.")
    };

    public static JsonNode ParseParametersSchema(AiToolDefinition definition) =>
        JsonNode.Parse(GetParametersJson(definition))
        ?? throw new InvalidOperationException($"Tool schema could not be parsed for {definition.Name}.");
}
