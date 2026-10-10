namespace SkyRoof
{
  /// <summary>
  /// Radio telemetry is authoritative, but must not mutate a WinForms
  /// selector while the operator is navigating its open list or editing
  /// it with keyboard focus. Only committed edits issue CI-V writes.
  /// </summary>
  internal static class IcomScopeUiSelectionPolicy
  {
    internal static bool CanApplyRemoteSelection(
      bool droppedDown,
      bool containsFocus) => !droppedDown && !containsFocus;

    internal static bool ShouldWriteRemoteSelection(
      bool droppedDown,
      bool containsFocus,
      int existingIndex,
      int incomingIndex) =>
      CanApplyRemoteSelection(droppedDown, containsFocus) &&
      existingIndex != incomingIndex;
  }
}
