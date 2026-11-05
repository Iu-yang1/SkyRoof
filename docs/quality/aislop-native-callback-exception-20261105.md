# SkyRoof-Plus native callback exception classification (2026-11-05)

## Intentional non-propagating catch

- Location: `SkyRoof/ExceptionLogger.cs`, `SoapySdrLogHandler`.
- Boundary: SoapySDR's unmanaged/native log callback into managed C#.
- Risk: propagating an exception across an unmanaged callback boundary may terminate the host process. Attempting to log again from the failure handler can itself recurse or throw when a logging sink is already failing, creating the same crash risk.
- Deliberate policy: retain the catch as an explicitly reviewed exception, not a claim that the problem has been fixed. Do not rethrow from this callback or insert another potentially-throwing logger solely to improve the aislop score.
- Residual risk: callback diagnostic message can be dropped if native string conversion or the logging sink fails. SoapySDR's own diagnostics and receiver operational checks remain necessary.
- Follow-up before closure: investigate a bounded, allocation-free, reentrancy-safe native/managed diagnostic channel, then add targeted failure-injection tests. This is not covered by standard xUnit or CI in the current release.
- Note: this is an **exception to the empty-catch cleanup objective** only, not a waiver for native RF safety, CAT/PTT or rotator STOP logic.
