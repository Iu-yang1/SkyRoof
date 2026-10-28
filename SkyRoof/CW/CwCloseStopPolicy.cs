namespace SkyRoof.CW
{
  /// <summary>
  /// Bounded, UI-safe verification wait; timeout does NOT imply a radio STOP
  /// ACK or cancel/abandon the still-running lease cleanup.
  /// </summary>
  public static class CwCloseStopPolicy
  {
    public static async Task<bool> AwaitVerifiedStopAsync(
      Task stopTask, TimeSpan timeout)
    {
      ArgumentNullException.ThrowIfNull(stopTask);
      if(timeout<=TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(timeout));
      if(await Task.WhenAny(stopTask,Task.Delay(timeout))
         .ConfigureAwait(false)!=stopTask)
      {
        // Do not abandon fault observation after the UI reports a timeout:
        // the session still owns STOP and its eventual failure is logged.
        _=stopTask.ContinueWith(task=>
        {
          if(task.Exception!=null)
            Serilog.Log.Error(task.Exception,
              "Late SkyCAT CW STOP failure following close timeout");
        },CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,
          TaskScheduler.Default);
        return false;
      }
      await stopTask.ConfigureAwait(false);
      return true;
    }
  }
}
