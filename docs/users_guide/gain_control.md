# Gain Control

The toolbar has **RF GAIN** and **AF GAIN** controls. They change different
targets depending on the active receive/control backend.

## Local SDR

- **RF GAIN** adjusts the gain of the selected SoapySDR receiver, when that
  device supports gain control.
- **AF GAIN** adjusts SkyRoof's own local playback soundcard gain, using
  the existing -50 to 0 dB slider. This does not change other applications'
  audio, and is saved independently of RS-BA1 audio.

## IC-9700 with SkyCAT + RS-BA1 Remote Utility

- **RF GAIN** writes the IC-9700's hardware RF gain over the existing SkyCAT
  CAT connection using CI-V `14 02` (`U RF_GAIN <0..255>`).
  The slider displays 0–100%, maps that to the rig's 0–255 range, and
  periodically reads back `U RF_GAIN_READ`. This requires a SkyCAT
  version that supports those two commands. Merely receiving the spectrum
  from RS-BA1/WinDivert does not imply that the CAT link is connected.
- **AF GAIN** changes **only the Windows Volume Mixer audio session** used by
  the RS-BA1 Remote Utility playback process. It does *not* use CI-V
  `14 01`, alter the IC-9700's hardware AF level, or change Windows
  endpoint/system master volume.
- RS-BA1 Remote Utility usually owns the remote receive-audio stream.
  If its session is not identified automatically, **right-click the
  AF GAIN heading or readout** while Remote Utility is playing audio,
  then select the RS-BA1 playback application and output device. The
  selection persists in SkyRoof audio settings.
- When the selected session is missing or ambiguous, AF GAIN is disabled;
  no other application audio is changed. If the session is muted in Windows
  Volume Mixer, unmute it there first.
- If RF GAIN is disabled, verify that SkyRoof's RX (or TX) CAT settings
  connect to SkyCAT for model `IC-9700`, not to a generic rigctld endpoint.
  Radio gain readback and write rejections appear in SkyRoof/SkyCAT logs.

These controls work independently of the Icom LAN Spectrum window's
RS-BA1 passive-capture or SkyCAT waveform data source.
