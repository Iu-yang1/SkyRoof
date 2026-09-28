using System.ComponentModel;
using Newtonsoft.Json;

namespace SkyRoof
{
  public class SatnogsUploaderSettings
  {
    [Description("Upload decoded telemetry frames to the SatNOGS DB (db.satnogs.org). Requires a SatNOGS-DB API key")]
    [DefaultValue(false)]
    public bool Enabled { get; set; } = false;

    [DisplayName("API Key")]
    [Description("Your SatNOGS DB API key (profile -> Settings -> API Key on the SatNOGS server)")]
    [PasswordPropertyText(true)]
    [JsonIgnore]
    public string ApiToken { get; set; } = "";

    [Browsable(false)]
    [JsonProperty("ApiTokenProtected")]
    public string ApiTokenProtected
    {
      get => SecretProtector.Protect(ApiToken);
      set => ApiToken = SecretProtector.Unprotect(value);
    }

    [Browsable(false)]
    [JsonProperty("ApiToken", NullValueHandling = NullValueHandling.Ignore)]
    private string? LegacyApiToken
    {
      get => null;
      set
      {
        if (value == null) return;
        ApiToken = value;
        SecretMigrationNeeded = true;
      }
    }

    [JsonIgnore]
    [Browsable(false)]
    internal bool SecretMigrationNeeded { get; private set; }


    public override string ToString() { return string.Empty; }
  }
}
