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
        return false;
      await stopTask.ConfigureAwait(false);
      return true;
    }
  }
}
