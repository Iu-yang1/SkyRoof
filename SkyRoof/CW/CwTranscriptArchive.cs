using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SkyRoof.CW
{
  /// <summary>
  /// Immutable archive view. Tracking IDs and window generations are NOT
  /// durable station identities: events never silently merge across them.
  /// </summary>
  public sealed record CwArchivedTranscript(
    long TimelineGeneration, bool UsesAssociationHint, int IdentityId,
    string CommittedText, DateTime LastWindowEndUtc,
    Guid SessionId = default);

  public sealed record CwArchiveEvent(
    DateTime Utc, long TimelineGeneration, bool UsesAssociationHint,
    int IdentityId, string Delta, Guid SessionId = default);

  /// <summary>
  /// Independent receive text lifetime: survives short QSB, slot recycling,
  /// timeline resets and CW Console form recreation. Committed text is
  /// append-only; provisional text is not persisted or treated as verified.
  /// Writes use a bounded independent channel off the real-time RX worker.
  /// </summary>
  public sealed class CwTranscriptArchive : IDisposable
  {
    private sealed class Entry
    {
      public required Guid SessionId;
      public required long Generation;
      public required bool UsesHint;
      public required int Id;
      public DateTime LastUtc;
      public string PreviousWindowCommitted = "";
      public readonly StringBuilder Committed = new();
    }

    private readonly object sync = new();
    private readonly Guid sessionId=Guid.NewGuid();
    private readonly Dictionary<(Guid, long, bool, int), Entry> entries = new();
    private readonly Channel<CwArchiveEvent> pending;
    private readonly Task writer;
    private readonly string folder;
    private readonly int maximumEntries;
    private bool disposed;
    private long droppedEvents;
    private long version;
    public long Version => Interlocked.Read(ref version);
    public long DroppedDiskEvents => Interlocked.Read(ref droppedEvents);
    public string? LastWriteError { get; private set; }
    public string ArchiveFolder => folder;

    public CwTranscriptArchive(string? folder = null, int maximumEntries = 512)
    {
      if(maximumEntries is < 1 or > 10000)
        throw new ArgumentOutOfRangeException(nameof(maximumEntries));
      this.maximumEntries = maximumEntries;
      this.folder = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyRoof", "CW", "History");
      pending = Channel.CreateBounded<CwArchiveEvent>(
        new BoundedChannelOptions(512)
        {
          SingleReader = true,
          SingleWriter = false,
          FullMode = BoundedChannelFullMode.Wait
        });
      LoadRecentEvents();
      writer = Task.Run(PersistAsync);
    }

    public void Append(long generation, IEnumerable<CwTranscriptSnapshot> snapshots)
    {
      ArgumentNullException.ThrowIfNull(snapshots);
      lock(sync)
      {
        if(disposed)return;
        foreach(CwTranscriptSnapshot snapshot in snapshots)
        {
          if(snapshot.LastWindowEndUtc.Kind != DateTimeKind.Utc)
            continue;
          bool hint = snapshot.AssociationHintId != 0;
          int id = hint ? snapshot.AssociationHintId : snapshot.TrackId;
          var key = (sessionId,generation, hint, id);
          if(!entries.TryGetValue(key, out Entry? entry))
          {
            entry = new Entry { SessionId=sessionId,
              Generation=generation, UsesHint=hint, Id=id };
            entries.Add(key,entry);
          }
          if(snapshot.LastWindowEndUtc < entry.LastUtc)continue;
          entry.LastUtc=snapshot.LastWindowEndUtc;
          string incoming=snapshot.CommittedText ?? "";
          string delta;
          if(incoming.StartsWith(entry.PreviousWindowCommitted,
              StringComparison.Ordinal))
            delta=incoming[entry.PreviousWindowCommitted.Length..];
          else if(entry.PreviousWindowCommitted.StartsWith(incoming,
              StringComparison.Ordinal))
            delta=""; // a stale/shorter decoder window never deletes history
          else
            delta="\n[decoder timeline revised]\n"+incoming;
          entry.PreviousWindowCommitted=incoming;
          if(delta.Length==0)continue;
          entry.Committed.Append(delta);
          Interlocked.Increment(ref version);
          if(!pending.Writer.TryWrite(
            new CwArchiveEvent(snapshot.LastWindowEndUtc,generation,hint,id,delta,sessionId)))
            Interlocked.Increment(ref droppedEvents);
        }
        while(entries.Count>maximumEntries)
        {
          var oldest=entries.OrderBy(x=>x.Value.LastUtc).First();
          entries.Remove(oldest.Key);
        }
      }
    }

    public IReadOnlyList<CwArchivedTranscript> GetAll()
    {
      lock(sync)
        return entries.Values
          .OrderByDescending(x=>x.LastUtc)
          .Select(x=>new CwArchivedTranscript(
            x.Generation,x.UsesHint,x.Id,x.Committed.ToString(),x.LastUtc,x.SessionId))
          .ToArray();
    }

    private void LoadRecentEvents()
    {
      try
      {
        if(!Directory.Exists(folder))return;
        foreach(string path in Directory.EnumerateFiles(folder,"cw-*.jsonl")
          .OrderByDescending(x=>x).Take(2).Reverse())
        {
          foreach(string line in File.ReadLines(path).TakeLast(4096))
          {
            CwArchiveEvent? item=JsonSerializer.Deserialize<CwArchiveEvent>(line);
            if(item==null || item.Utc.Kind!=DateTimeKind.Utc)continue;
            var key=(item.SessionId,item.TimelineGeneration,item.UsesAssociationHint,item.IdentityId);
            if(!entries.TryGetValue(key,out Entry? entry))
            {
              entry=new Entry {SessionId=item.SessionId,
                Generation=item.TimelineGeneration,
                UsesHint=item.UsesAssociationHint,Id=item.IdentityId};
              entries.Add(key,entry);
            }
            if(item.Utc>entry.LastUtc)entry.LastUtc=item.Utc;
            entry.Committed.Append(item.Delta);
          }
        }
        foreach(Entry entry in entries.Values)
          entry.PreviousWindowCommitted=entry.Committed.ToString();
      }
      catch(Exception ex)
      {
        LastWriteError="CW history read: "+ex.Message;
      }
    }

    private async Task PersistAsync()
    {
      await foreach(CwArchiveEvent item in pending.Reader.ReadAllAsync())
      {
        try
        {
          Directory.CreateDirectory(folder);
          string file=Path.Combine(folder,$"cw-{item.Utc:yyyy-MM-dd}.jsonl");
          // Per-day size limit prevents a broken decoder flooding disk.
          if(File.Exists(file) && new FileInfo(file).Length>=16*1024*1024)
          {
            Interlocked.Increment(ref droppedEvents);
            continue;
          }
          await File.AppendAllTextAsync(file,
            JsonSerializer.Serialize(item)+"\n").ConfigureAwait(false);
        }
        catch(Exception ex)
        {
          LastWriteError="CW history write: "+ex.Message;
          Interlocked.Increment(ref droppedEvents);
        }
      }
    }

    public void Dispose()
    {
      lock(sync)
      {
        if(disposed)return;
        disposed=true;
        pending.Writer.TryComplete();
      }
      // Never wait on filesystem I/O from WinForms shutdown.
      // The independent writer drains accepted messages asynchronously.
      _=writer.ContinueWith(t=>{_ = t.Exception;},
        TaskContinuationOptions.OnlyOnFaulted);
    }
  }
}
