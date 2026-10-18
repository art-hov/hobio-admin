using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace HobioAdmin.Web.Configuration;

/// <summary>
/// Loads configuration values from AWS Secrets Manager.
///
/// Settings (section "AwsSecrets", usually set as Elastic Beanstalk environment variables):
///   AwsSecrets__Enabled   = true
///   AwsSecrets__Region    = ca-central-1
///   AwsSecrets__SecretIds = "hobio-admin/dev,tushpa-dev"   (comma separated names or ARNs)
///
/// If SecretIds is empty, every secret in the account/region is loaded (same behavior as the Tushpa API).
/// An explicit list is recommended so the instance role only needs access to those secrets.
///
/// A JSON secret is flattened: each top-level property becomes a key, with "__" mapped to ":"
/// (e.g. "HobioAdmin__ReadOnlyConnectionString" -> "HobioAdmin:ReadOnlyConnectionString").
/// A plain-text secret is exposed under its secret name.
/// </summary>
public static class AwsSecretsConfiguration
{
    public static IConfigurationBuilder AddHobioAwsSecrets(this IConfigurationManager configuration)
    {
        var section = configuration.GetSection("AwsSecrets");
        if (!section.GetValue<bool>("Enabled"))
        {
            return configuration;
        }

        var region = section["Region"] ?? "ca-central-1";
        var secretIds = ParseList(section["SecretIds"]);

        try
        {
            using var client = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));

            if (secretIds.Count == 0)
            {
                secretIds = ListAllSecretIds(client);
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var secretId in secretIds)
            {
                var response = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId })
                    .GetAwaiter().GetResult();

                if (response.SecretString is null)
                {
                    continue;
                }

                foreach (var pair in Flatten(response.Name, response.SecretString))
                {
                    values[pair.Key] = pair.Value;
                }
            }

            configuration.AddInMemoryCollection(values);
            Console.WriteLine($"[HobioAdmin] Loaded {values.Count} configuration values from {secretIds.Count} AWS secret(s).");
        }
        catch (Exception ex)
        {
            // Never print secret values. Only the error type and message.
            Console.WriteLine($"[HobioAdmin] AWS Secrets Manager error ({region}): {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        return configuration;
    }

    internal static List<string> ParseList(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static IEnumerable<KeyValuePair<string, string?>> Flatten(string secretName, string secretString)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(secretString);
        }
        catch (JsonException)
        {
            return [new(secretName, secretString)];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [new(secretName, secretString)];
            }

            var result = new List<KeyValuePair<string, string?>>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name.Replace("__", ":", StringComparison.Ordinal);
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
                result.Add(new(key, value));
            }

            return result;
        }
    }

    private static List<string> ListAllSecretIds(AmazonSecretsManagerClient client)
    {
        var ids = new List<string>();
        var request = new ListSecretsRequest();
        ListSecretsResponse response;
        do
        {
            response = client.ListSecretsAsync(request).GetAwaiter().GetResult();
            ids.AddRange((response.SecretList ?? []).Select(s => s.ARN));
            request.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(response.NextToken));

        return ids;
    }
}
