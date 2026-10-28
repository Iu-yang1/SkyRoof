using System;
using System.Threading;

namespace SkyRoof;

internal static class NativeRotatorReconnectPolicy
{
  // Use a short ramp for an intermittently reachable TCP-to-serial device.
  // The old path inherited a 5s initial wait and backed off to 30s.
  // Keep the user's ReconnectDelay as the upper bound (default 5s).
  // A UI Retry wakeup must never shorten the minimum reconnect backoff.
  internal static bool WaitForRetryOrShutdown(WaitHandle shutdown,int delayMilliseconds) =>
    shutdown.WaitOne(Math.Clamp(delayMilliseconds,0,30000));

  internal static int GetDelayMilliseconds(int consecutiveFailures,int configuredLimitMs)
  {
    int maxDelay=Math.Clamp(configuredLimitMs,500,10000);
    int exponent=Math.Clamp(consecutiveFailures,0,5);
    return Math.Min(maxDelay,500*(1<<exponent));
  }
}
