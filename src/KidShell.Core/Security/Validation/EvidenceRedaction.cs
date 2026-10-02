using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KidShell.Core.Security.Validation;

/// <summary>
/// Strips secrets out of evidence before it is written to disk.
///
/// WHY REDACTION IS AUTOMATIC AND NOT A HABIT
/// ------------------------------------------
/// A validation run produces files somebody will attach to a bug report, copy
/// onto a USB stick, or paste into a chat. The useful thing to record about
/// protected state is that it CHANGED, or did not, in the direction expected -
/// and a digest says that perfectly. The contents say it too, and also say
/// what the parent's PIN hashes to.
///
/// So the redaction runs over every evidence document on the way out, keyed on
/// the field name, and replaces a secret with the SHA-256 of its value. The
/// digest is still evidence: two runs can be compared, a value can be shown
/// not to have changed across a reboot, and nothing has been copied.
/// </summary>
public static class EvidenceRedaction
{
    /// <summary>
    /// Field names whose values never leave the machine.
    ///
    /// Matched case-insensitively as a substring, so "parentPinHash",
    /// "pin_salt" and "ProtectedPayload" are all caught without needing to be
    /// listed. Over-matching is the right direction to err in: a redacted
    /// field that did not need redacting costs a reader nothing.
    /// </summary>
    public static readonly string[] SecretFieldMarkers =
    [
        "pin", "hash", "salt", "password", "secret", "credential",
        "token", "capability", "payload", "privatekey", "thumbprint"
    ];

    /// <summary>Fields that are safe despite matching a marker.</summary>
    private static readonly string[] Allowed =
    [
        // A digest IS the redacted form, and re-redacting one would make two
        // runs incomparable.
        "sha256", "imagesha256", "digest", "stagedDigest",

        // These name a thing rather than carry it.
        "pinThrottleStatus", "pinThrottleStage", "capabilityLabel",
        "hashesIdentical", "payloadBytes", "payloadAccepted"
    ];

    /// <summary>
    /// Redacts a JSON document in place.
    ///
    /// Returns the redacted text, or the original when it does not parse -
    /// which is itself reported rather than silently written, because an
    /// evidence file nobody could redact is one nobody should publish.
    /// </summary>
    public static RedactionResult Redact(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RedactionResult(string.Empty, 0, Parsed: false);
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new RedactionResult(json, 0, Parsed: false);
        }

        if (root is null)
        {
            return new RedactionResult(json, 0, Parsed: false);
        }

        var redacted = 0;
        Walk(root, ref redacted);

        return new RedactionResult(
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            redacted,
            Parsed: true);
    }

    private static void Walk(JsonNode node, ref int redacted)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var name in obj.Select(p => p.Key).ToArray())
                {
                    var child = obj[name];

                    if (IsSecret(name))
                    {
                        obj[name] = Mask(child);
                        redacted++;
                        continue;
                    }

                    if (child is not null)
                    {
                        Walk(child, ref redacted);
                    }
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array.Where(i => i is not null))
                {
                    Walk(item!, ref redacted);
                }

                break;
            }
        }
    }

    /// <summary>Whether a field name means its value must not be written.</summary>
    public static bool IsSecret(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        if (Allowed.Any(a => string.Equals(a, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return SecretFieldMarkers.Any(
            m => fieldName.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What replaces a secret: its digest, and the fact that it was there.
    ///
    /// Null stays null. "There was no PIN configured" and "there was one and
    /// we are not telling you" are different facts, and flattening them would
    /// make the evidence lie in the more flattering direction.
    /// </summary>
    private static JsonNode? Mask(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : value.ToJsonString();

        return JsonValue.Create("sha256:" + Digest(text ?? string.Empty));
    }

    /// <summary>Lowercase hexadecimal SHA-256 of the UTF-8 bytes.</summary>
    public static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}

/// <summary>What redaction did.</summary>
/// <param name="Json">The text safe to write.</param>
/// <param name="RedactedFields">How many values were replaced.</param>
/// <param name="Parsed">
/// False when the document could not be parsed, and therefore could not be
/// redacted. The caller must refuse to publish it rather than hope.
/// </param>
public sealed record RedactionResult(string Json, int RedactedFields, bool Parsed);
