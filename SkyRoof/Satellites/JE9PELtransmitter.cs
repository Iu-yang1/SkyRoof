
namespace SkyRoof
{
  public class JE9PELtransmitter
  {
    public string Name;
    public int NoradId;
    public string Uplink;
    public string Downlink;
    public string Beacon;
    public string Mode;
    public string Call;
    public string Status;

    public JE9PELtransmitter() { }

    public JE9PELtransmitter(string csv)
    {
      // JE9PEL is a semi-structured feed: optional trailing fields can be
      // absent, and some records contain only a name/status. The previous
      // fixed [0..7] indexing threw IndexOutOfRangeException and spammed
      // one ERROR per satellite. Never invent columns or NORAD numbers.
      var cols = (csv ?? string.Empty).Split(';');
      string Field(int n) => n < cols.Length ? cols[n].Trim() : string.Empty;

      Name = Field(0);
      int.TryParse(Field(1), out NoradId);
      Uplink = Field(2);
      Downlink = Field(3);
      Beacon = Field(4);
      Mode = Field(5);
      Call = Field(6);
      Status = Field(7);
    }

    public static bool TryParse(string? csv, out JE9PELtransmitter? transmitter)
    {
      transmitter = null;
      if (string.IsNullOrWhiteSpace(csv)) return false;
      var row = new JE9PELtransmitter(csv);
      if (row.NoradId <= 0 || string.IsNullOrWhiteSpace(row.Name))
        return false;
      transmitter = row;
      return true;
    }

    internal string GetTooltipText()
    {
      string tooltip = "";
      if (!string.IsNullOrEmpty(Uplink)) tooltip += $"Uplink: {Uplink}\n";
      if (!string.IsNullOrEmpty(Downlink)) tooltip += $"Downlink: {Downlink}\n";
      if (!string.IsNullOrEmpty(Beacon)) tooltip += $"Beacon: {Beacon}\n";
      if (!string.IsNullOrEmpty(Mode)) tooltip += $"Mode: {Mode}\n";
      if (!string.IsNullOrEmpty(Call)) tooltip += $"Call: {Call}\n";
      if (!string.IsNullOrEmpty(Status)) tooltip += $"Status: {Status}\n";

      return tooltip.Trim();
    }
  }
}
