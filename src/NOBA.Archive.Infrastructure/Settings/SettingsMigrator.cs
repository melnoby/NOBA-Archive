using System.Text.Json;
using System.Text.Json.Nodes;

namespace NOBA.Archive.Infrastructure.Settings;

/// <summary>
/// Ordered migration chain, applied one version at a time. Migrations are pure JSON-to-JSON so they
/// stay testable and never depend on the current shape of <see cref="NobaSettings"/>.
/// </summary>
public static class SettingsMigrator
{
    private static readonly Dictionary<int, Func<JsonObject, JsonObject>> Steps = new()
    {
        // v1 -> v2: link handling became an explicit policy instead of an "allowSymlinks" boolean.
        // The old permissive value is deliberately not carried over: a stored 'true' from an older
        // build must not silently re-enable link creation.
        [1] = root =>
        {
            root.Remove("allowSymlinks");
            root["linkPolicy"] = nameof(Core.Formats.LinkPolicy.Never);
            root["illegalNamePolicy"] = nameof(Core.Formats.IllegalNamePolicy.Sanitize);
            root["schemaVersion"] = 2;
            return root;
        },
    };

    public static int HighestKnownVersion => NobaSettings.CurrentSchemaVersion;

    public static JsonObject Migrate(JsonObject root, out int appliedSteps)
    {
        ArgumentNullException.ThrowIfNull(root);

        appliedSteps = 0;
        var version = ReadVersion(root);

        while (version < NobaSettings.CurrentSchemaVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
            {
                throw new InvalidOperationException(
                    $"No migration step is defined from settings schema version {version}.");
            }

            root = step(root);
            appliedSteps++;
            var next = ReadVersion(root);
            if (next <= version)
            {
                throw new InvalidOperationException(
                    $"Migration step from version {version} did not advance the schema version.");
            }

            version = next;
        }

        return root;
    }

    public static int ReadVersion(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.TryGetPropertyValue("schemaVersion", out var node) &&
            node is JsonValue value &&
            value.GetValueKind() == JsonValueKind.Number)
        {
            return value.GetValue<int>();
        }

        // A file with no version predates versioning; treat it as v1.
        return 1;
    }
}
