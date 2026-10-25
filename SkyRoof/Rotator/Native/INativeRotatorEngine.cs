using System;
using System.Threading;
using System.Threading.Tasks;
using SkyRoof.NativeRotator;

namespace SkyRoof;

internal interface INativeRotatorEngine : IRotatorEngine
{
  bool IsReady {get;}
  bool HasLiveConnection {get;}
  bool ManagementReviewRequired {get;}
  DateTime LastParkTargetWriteUtc {get;}
  DateTime LastParkStopWriteUtc {get;}
  Rc3040sFeedbackEstimator.Observation? LatestObservedRate {get;}
  string GetStatusString();
  void SetParkDiagnostics(bool active);
  Task<IDisposable?> AcquireManagementLeaseAsync(
    CancellationToken cancellationToken=default);
  Task<bool> VerifyPostManagementFeedbackAsync(
    CancellationToken cancellationToken=default);
  void LatchAfterManagementChange();
}
