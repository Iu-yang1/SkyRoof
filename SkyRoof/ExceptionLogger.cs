using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.WebSockets;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpGL;

namespace VE3NEA
{
  // Serilog timestamps log events with DateTimeOffset.Now and the file sink renders them as
  // written, so a UTC stamp has to be attached as a property of its own
  public class UtcTimeEnricher : ILogEventEnricher
  {
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
      logEvent.AddPropertyIfAbsent(factory.CreateProperty("Utc", logEvent.Timestamp.UtcDateTime));
    }
  }


  public class ExceptionLogger
  {
    private static NativeSoapySdr.SoapySDRLogHandlerDelegate LogHandlerDelegate = new(SoapySdrLogHandler);
    private static int MainThreadId;

    // SoapySDR streaming status indicators (O, U, S, T) are counted and summarized periodically
    private static readonly TimeSpan SsiSummaryInterval = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<char, int> SsiCounts = new();
    private static readonly Stopwatch SsiStopwatch = Stopwatch.StartNew();

    public static void Initialize()
    {
      string appName = AppDomain.CurrentDomain.FriendlyName ?? "log";
      string fileName = Path.Combine(Utils.GetLogArchiveFolder(), $"{appName}_.txt");
      // the system log is timestamped in UTC regardless of the time display mode, so that logs
      // from different users, and from different sides of a DST change, can be compared directly
      Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
          .Enrich.With<UtcTimeEnricher>()
          .WriteTo.File(fileName,
            outputTemplate: "{Utc:yyyy-MM-dd HH:mm:ss.fff}Z [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 3_000_000,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 20,
            shared: true
           ).CreateLogger();


      // handle exceptions in UI thread
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);

      // handle exceptions in non-UI threads
      MainThreadId = Environment.CurrentManagedThreadId;
      AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
      
      // Handle exceptions in async/await code
      TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

      // handle exceptions in SoapySDR
      NativeSoapySdr.SoapySDR_registerLogHandler(LogHandlerDelegate);

      Log.Information($"Starting {typeof(Utils).Assembly.GetName().FullName}");
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
      var exception = e.ExceptionObject as Exception;
      Log.Error(exception, "Worker thread exception occurred:");

      // show dialog if not in the message loop but still on the main UI thread,
      // e.g., in the main form's constructor
      if (Environment.CurrentManagedThreadId == MainThreadId) ShowDialog(exception, false);
    }

    private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
    {
      Log.Error(e.Exception, "UI thread exception occurred:");
      if (ShowDialog(e.Exception) == DialogResult.Abort) Application.Exit();
    }

    private static readonly object WebSocketFaultSync = new();
    private static DateTime WebSocketFaultIntervalUtc = DateTime.MinValue;
    private static int WebSocketFaultsInInterval;

    // Only classify actual WebSocketExceptions. A SocketException(995)
    // elsewhere might be a meaningful CAT/rotator failure; never hide it.
    internal static bool IsExpectedWebSocketClose(Exception ex)
    {
      if (ex is AggregateException group)
        return group.Flatten().InnerExceptions.Count > 0 &&
          group.Flatten().InnerExceptions.All(IsExpectedWebSocketClose);
      if (ex is WebSocketException socket)
        return socket.Message.Contains("close handshake",
          StringComparison.OrdinalIgnoreCase) ||
          socket.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;
      return ex.InnerException != null &&
        IsExpectedWebSocketClose(ex.InnerException);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender,
      UnobservedTaskExceptionEventArgs e)
    {
      try
      {
        if (!IsExpectedWebSocketClose(e.Exception))
        {
          Log.Error(e.Exception,
            "Unobserved async task exception; source task identity unavailable");
          return;
        }

        // The global finalizer cannot identify the initiating WebSocket
        // owner. Do not make up a component name or call this a rotator drop.
        // Emit bounded summaries; owned tasks must instead be observed via
        // Utils.DoNotAwait() or awaited by their component.
        lock (WebSocketFaultSync)
        {
          DateTime utc = DateTime.UtcNow;
          if (utc - WebSocketFaultIntervalUtc >= TimeSpan.FromMinutes(1))
          {
            if (WebSocketFaultsInInterval > 0)
              Log.Warning(
                "Unobserved remote WebSocket closures in prior interval: {Count}; originating component still unknown",
                WebSocketFaultsInInterval);
            WebSocketFaultsInInterval = 0;
            WebSocketFaultIntervalUtc = utc;
          }
          WebSocketFaultsInInterval++;
          if (WebSocketFaultsInInterval == 1)
            Log.Warning(
              "Remote WebSocket closed without close handshake; source task not observed. Full detail: {Detail}",
              e.Exception.GetBaseException().Message);
        }
      }
      finally
      {
        // Observe even unexpected faults after logging; never allow the
        // finalizer handler itself to crash WinForms at shutdown.
        e.SetObserved();
      }
    }

    private static DialogResult ShowDialog(Exception? e, bool allowContinue = true)
    {
      string errorMessage = $"Exception: {e?.Message ?? e?.ToString()}\nModule: {e.Source} \nFunction: {e.TargetSite}";

      if (allowContinue)
        return MessageBox.Show(errorMessage, AppDomain.CurrentDomain.FriendlyName, MessageBoxButtons.AbortRetryIgnore,
          MessageBoxIcon.Stop);

      MessageBox.Show(errorMessage, AppDomain.CurrentDomain.FriendlyName, MessageBoxButtons.OK, MessageBoxIcon.Stop);
      return DialogResult.OK;
    }

    internal static void CheckOpenglError(OpenGL gl, bool log = true)
    {
      uint err;

      while ((err = gl.GetError()) != OpenGL.GL_NO_ERROR)
        if (log)
        {
          string errorName = gl.ErrorString(err);
          if (string.IsNullOrEmpty(errorName)) errorName = $"error {err}";
          string message = $"OpenGL: {errorName}\n{new StackTrace(true)}";
          Log.Error(message);
          Debug.WriteLine(message);
        }
    }

    public static void SoapySdrLogHandler(NativeSoapySdr.SoapySDRLogLevel logLevel, IntPtr messagePtr)
    {
      try
      {
        if (messagePtr == IntPtr.Zero) return;
        string message = Marshal.PtrToStringAnsi(messagePtr)!;

        if (logLevel == NativeSoapySdr.SoapySDRLogLevel.SOAPY_SDR_SSI)
          CountSsiMessage(message);
        else
          Log.Information($"[{logLevel}]: {message}");
      }
      catch { }
    }

    // the handler is invoked only when a message arrives, so the summary of the last
    // batch of indicators is written when the next indicator comes in
    private static void CountSsiMessage(string message)
    {
      lock (SsiCounts)
      {
        foreach (char c in message)
          if (!char.IsWhiteSpace(c)) SsiCounts[c] = SsiCounts.GetValueOrDefault(c) + 1;

        double seconds = SsiStopwatch.Elapsed.TotalSeconds;
        if (seconds < SsiSummaryInterval.TotalSeconds) return;

        string counts = string.Join(" ", SsiCounts.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        Log.Information($"[SOAPY_SDR_SSI]: stream status in last {seconds:F0} s: {counts}");
        SsiCounts.Clear();
        SsiStopwatch.Restart();
      }
    }
  }
}
