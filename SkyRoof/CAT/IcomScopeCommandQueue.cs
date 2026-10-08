namespace SkyRoof
{
  /// <summary>
  /// Small bounded/coalescing queue for IC-9700 scope-control commands.
  /// Commands with the same semantic target replace the older pending value and
  /// move to the tail, preserving the temporal order of the operator's latest
  /// actions while keeping MAIN/SUB targets independently coalesced.
  /// </summary>
  internal sealed class IcomScopeCommandQueue
  {
    private sealed class Entry
    {
      internal string Key = "";
      internal string Command = "";
    }

    private readonly int Capacity;
    private readonly object Sync = new();
    private readonly LinkedList<Entry> Queue = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> ByKey =
      new(StringComparer.Ordinal);

    internal IcomScopeCommandQueue(int capacity = 24)
    {
      if (capacity < 1)
        throw new ArgumentOutOfRangeException(
          nameof(capacity));

      Capacity = capacity;
    }

    internal int Count
    {
      get
      {
        lock (Sync)
          return Queue.Count;
      }
    }

    internal long DroppedCount { get; private set; }

    internal void Enqueue(string command)
    {
      if (string.IsNullOrWhiteSpace(command))
        throw new ArgumentException(
          "Scope command must not be empty.",
          nameof(command));

      string key =
        GetCommandKey(command);

      lock (Sync)
      {
        if (ByKey.TryGetValue(
              key,
              out LinkedListNode<Entry>? existing))
        {
          // A replacement is a newer operator action. Move it to the tail so
          // dependencies across command kinds preserve the most recent event
          // order (for example MODE FIXED followed by EDGE 2).
          Queue.Remove(
            existing);
          ByKey.Remove(
            key);
        }

        if (Queue.Count >=
            Capacity)
        {
          LinkedListNode<Entry>? oldest =
            Queue.First;

          if (oldest != null)
          {
            Queue.RemoveFirst();
            ByKey.Remove(
              oldest.Value.Key);
            DroppedCount++;
          }
        }

        var entry =
          new Entry
          {
            Key = key,
            Command = command
          };

        LinkedListNode<Entry> node =
          Queue.AddLast(
            entry);

        ByKey[key] =
          node;
      }
    }

    internal bool TryDequeue(
      out string? command)
    {
      lock (Sync)
      {
        LinkedListNode<Entry>? first =
          Queue.First;

        if (first == null)
        {
          command = null;
          return false;
        }

        Queue.RemoveFirst();
        ByKey.Remove(
          first.Value.Key);

        command =
          first.Value.Command;

        return true;
      }
    }

    internal void Clear()
    {
      lock (Sync)
      {
        Queue.Clear();
        ByKey.Clear();
      }
    }

    internal static string GetCommandKey(
      string command)
    {
      string[] parts =
        command.Split(
          ' ',
          StringSplitOptions.RemoveEmptyEntries |
          StringSplitOptions.TrimEntries);

      if (parts.Length < 2 ||
          parts[0] != "U")
        return command;

      string verb =
        parts[1];

      return verb switch
      {
        "SCOPE_SELECT" or
        "SCOPE_TX" or
        "SCOPE_CENTER_TYPE" or
        "SCOPE_MARKER" =>
          verb,

        "SCOPE_MODE" or
        "SCOPE_SPAN" or
        "SCOPE_EDGE" or
        "SCOPE_REF" or
        "SCOPE_SPEED" or
        "SCOPE_VBW"
          when parts.Length >= 3 =>
            $"{verb} {parts[2]}",

        "SCOPE_FIXED_EDGE"
          when parts.Length >= 4 =>
            $"{verb} {parts[2]} {parts[3]}",

        _ =>
          command
      };
    }
  }
}
