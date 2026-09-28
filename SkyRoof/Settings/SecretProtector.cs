using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SkyRoof
{
  internal static class SecretProtector
  {
    private const string Prefix = "dpapi:";

    internal static string Protect(string? plaintext)
    {
      if (string.IsNullOrEmpty(plaintext)) return string.Empty;

      byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
      byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
      return Prefix + Convert.ToBase64String(encrypted);
    }

    internal static string Unprotect(string? stored)
    {
      if (string.IsNullOrEmpty(stored)) return string.Empty;

      // Companion *Protected properties are normally DPAPI values. Accepting
      // plaintext here makes a manually edited file recoverable without ever
      // writing that plaintext back out.
      if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        return stored;

      try
      {
        byte[] encrypted = Convert.FromBase64String(stored[Prefix.Length..]);
        byte[] plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintext);
      }
      catch (Exception ex) when (ex is CryptographicException or FormatException)
      {
        Log.Warning(ex, "Unable to decrypt a saved SkyRoof secret for the current Windows user.");
        return string.Empty;
      }
    }

    internal static void SanitizeLegacySecretsFile(string fileName)
    {
      if (!File.Exists(fileName)) return;

      try
      {
        JToken root = JToken.Parse(File.ReadAllText(fileName));
        IEnumerable<JToken> tokens = new[] { root }.Concat(root.Descendants());
        foreach (JProperty property in tokens
          .OfType<JProperty>()
          .Where(p => p.Name is "DirectLanPassword" or "ApiToken")
          .ToArray())
        {
          if (property.Parent is not JObject parent) continue;

          string plaintext = property.Value.Type == JTokenType.String
            ? property.Value.Value<string>() ?? string.Empty
            : string.Empty;
          string protectedName = property.Name == "DirectLanPassword"
            ? "DirectLanPasswordProtected"
            : "ApiTokenProtected";

          parent[protectedName] = Protect(plaintext);
          property.Remove();
        }

        File.WriteAllText(fileName, root.ToString(Formatting.Indented));
      }
      catch (Exception ex)
      {
        Log.Warning(ex, $"Unable to sanitize legacy secrets in {fileName}.");
      }
    }

    internal static string RedactLegacySecrets(string text)
    {
      return Regex.Replace(
        text,
        "(\"(?:DirectLanPassword|ApiToken)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"",
        "$1\"<redacted>\"",
        RegexOptions.CultureInvariant);
    }
  }
}
