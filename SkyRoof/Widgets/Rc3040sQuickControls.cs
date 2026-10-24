using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SkyRoof.NativeRotator;

namespace SkyRoof;

/// <summary>
/// Inline RC-3040S LAN speed controls inside the existing manual rotator card.
/// TrackBar.Scroll only updates UI state, never sends AT or touches EEPROM.
/// Long operations are async and one-shot; this surface never owns TCP/60020.
/// </summary>
internal sealed class Rc3040sQuickControls : UserControl
{
  private readonly Label title = new(), azText = new(), elText = new(), state = new();
  private readonly TrackBar az = new(), el = new();
  private readonly Button read = new() { Text="Read" };
  private readonly Button saveAz = new() { Text="Save AZ" }, saveEl = new() { Text="Save EL" };
  private readonly Button revert = new() { Text="Revert" }, restart = new() { Text="Restart" };
  private readonly ToolTip tips = new();
  private readonly CancellationTokenSource lifetime = new();
  private Rc3040sManagementService.Snapshot? snapshot;
  private Context? context;
  private RotatorWidget? rotator;
  private bool busy, loaded, dirtyAz, dirtyEl, settingValues;
  private const int RawMin = 15;
  private const int RawMax = 10000; // conservative UI band, NOT verified motor safety

  internal Rc3040sQuickControls()
  {
    Size = new Size(318, 250);
    Margin = Padding.Empty;
    title.SetBounds(12, 3, 294, 19);
    title.Text = "RC-3040S · Rotation speed";
    title.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
    Controls.Add(title);

    azText.SetBounds(12, 25, 294, 16);
    elText.SetBounds(12, 79, 294, 16);
    Controls.Add(azText); Controls.Add(elText);
    ConfigureSlider(az,45,true); ConfigureSlider(el,99,false);
    az.Scroll += (_,_) => {if (!settingValues) dirtyAz=true; Display();};
    el.Scroll += (_,_) => {if (!settingValues) dirtyEl=true; Display();};
    Controls.Add(az); Controls.Add(el);

    read.SetBounds(12, 145, 91, 27);
    revert.SetBounds(111, 145, 91, 27);
    restart.SetBounds(210, 145, 96, 27);
    saveAz.SetBounds(12, 181, 142, 27);
    saveEl.SetBounds(164, 181, 142, 27);
    foreach(var button in new[]{read,saveAz,saveEl,revert,restart}) Controls.Add(button);
    read.Click += async (_,_)=>await ReadAsync();
    saveAz.Click += async (_,_)=>await SaveAsync(true);
    saveEl.Click += async (_,_)=>await SaveAsync(false);
    revert.Click += (_,_)=>RevertSliders();
    restart.Click += async (_,_)=>await RestartAsync();

    state.SetBounds(12, 213, 294, 32);
    state.AutoEllipsis=true;state.ForeColor=SystemColors.GrayText;
    state.Text="Read speed to begin";
    Controls.Add(state);
    tips.SetToolTip(read,"Read 260223_C configuration via WebSocket/60030");
    tips.SetToolTip(revert,"Discard unsaved slider edits");
    tips.SetToolTip(restart,"Restart requires confirmation; motor must be physically stopped");
    tips.SetToolTip(state,"Device status");
    RefreshAccess();
  }

  private static void ConfigureSlider(TrackBar bar,int y,bool azimuth)
  {
    bar.SetBounds(7,y,301,31);
    bar.Minimum=1;bar.Maximum=Rc3040sManagementService.SpeedUnits.SliderMaximum(azimuth,RawMax);
    bar.TickStyle=TickStyle.None;bar.SmallChange=5;bar.LargeChange=25;
    bar.Enabled=false;
  }

  internal void Attach(Context ctx,RotatorWidget widget)
  {
    context=ctx;
    rotator=widget;
    RefreshAccess();
  }

  internal void PopupOpened()
  {
    if(!loaded && !busy && context?.Settings.Rotator.Rc3040sAtEnabled==true)
    {
      loaded=true;
      _=ReadAsync();
    }
  }

  private bool CanSendAt =>
    !busy && !IsDisposed &&
    context?.Settings.Rotator is {Enabled:true,Rc3040sAtEnabled:true,
      NativeProtocol:RotatorProtocolKind.Rc3040sLan} &&
    rotator is not null && rotator.IsRunning() &&
    !rotator.IsManualControlLocked && !rotator.IsTracking &&
    !rotator.IsParking &&
    !rotator.PositionSafetyTripped;

  internal void RefreshAccess()
  {
    if(IsDisposed)return;
    bool active=CanSendAt;
    bool valuesKnown = snapshot?.AzRaw is >=RawMin and <=RawMax &&
                       snapshot?.ElRaw is >=RawMin and <=RawMax;
    read.Enabled=!busy && context?.Settings.Rotator.Rc3040sAtEnabled==true;
    az.Enabled=el.Enabled=active && valuesKnown;
    saveAz.Enabled=az.Enabled && dirtyAz &&
      Rc3040sManagementService.SpeedUnits.SliderChanged(true,az.Value,snapshot!.AzRaw!.Value) &&
      snapshot.AzRaw!=ProposedRaw(true,az);
    saveEl.Enabled=el.Enabled && dirtyEl &&
      Rc3040sManagementService.SpeedUnits.SliderChanged(false,el.Value,snapshot!.ElRaw!.Value) &&
      snapshot.ElRaw!=ProposedRaw(false,el);
    revert.Enabled=!busy && valuesKnown;
    restart.Enabled=active && snapshot?.Firmware!=null &&
      snapshot.DeviceIdSuffix!=null;
  }

  private static int ProposedRaw(bool azimuth,TrackBar slider) =>
    Rc3040sManagementService.SpeedUnits.RawFromSlider(azimuth,slider.Value,RawMin,RawMax);

  private void Display()
  {
    azText.Text=$"AZ  {az.Value/100.0:0.00} °/s" +
      (snapshot?.AzRaw is int azOriginal ?
        $"    Saved {Rc3040sManagementService.SpeedUnits.ToDegreesPerSecond(true,azOriginal):0.00}" : "");
    elText.Text=$"EL  {el.Value/100.0:0.00} °/s" +
      (snapshot?.ElRaw is int elOriginal ?
        $"    Saved {Rc3040sManagementService.SpeedUnits.ToDegreesPerSecond(false,elOriginal):0.00}" : "");
    tips.SetToolTip(azText,snapshot?.AzRaw is int a ?
      $"Raw: {a:D5} · Proposed: {ProposedRaw(true,az):D5}" : "Not read");
    tips.SetToolTip(elText,snapshot?.ElRaw is int e ?
      $"Raw: {e:D5} · Proposed: {ProposedRaw(false,el):D5}" : "Not read");
    RefreshAccess();
  }

  private void SetState(string message)
  {
    if(IsDisposed)return;
    state.Text=message;tips.SetToolTip(state,message);
  }
  private void RevertSliders()
  {
    if(snapshot?.AzRaw is not int azRaw || snapshot.ElRaw is not int elRaw ||
       azRaw is <RawMin or >RawMax || elRaw is <RawMin or >RawMax)return;
    settingValues=true;
    try
    {
      az.Value=Math.Clamp(Rc3040sManagementService.SpeedUnits.SliderTick(true,azRaw),az.Minimum,az.Maximum);
      el.Value=Math.Clamp(Rc3040sManagementService.SpeedUnits.SliderTick(false,elRaw),el.Minimum,el.Maximum);
      dirtyAz=dirtyEl=false;
    }
    finally {settingValues=false;}
    SetState("Unsaved changes discarded.");
    Display();
  }

  private async Task ReadAsync()
  {
    if(busy || context is null)return;
    busy=true;RefreshAccess();SetState("Reading WebSocket/60030...");
    try
    {
      var result=await Rc3040sManagementService.ReadAsync(
        context.Settings.Rotator.Host,true,lifetime.Token);
      if(IsDisposed)return;
      snapshot=result;
      if(result.AzRaw is >=RawMin and <=RawMax && result.ElRaw is >=RawMin and <=RawMax)
      {
        RevertSliders();
        SetState("Speeds loaded.");
      }
      else SetState("Speed readback unavailable.");
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex)
    {
      snapshot=null;dirtyAz=dirtyEl=false;
      SetState("Read failed: "+ex.Message);
    }
    finally {busy=false;if(!IsDisposed){RefreshAccess();Display();}}
  }

  private async Task SaveAsync(bool azimuth)
  {
    if(!CanSendAt || snapshot is null || context is null)return;
    int before=azimuth ? snapshot.AzRaw??-1 : snapshot.ElRaw??-1;
    int desired=azimuth ? ProposedRaw(true,az) : ProposedRaw(false,el);
    if(before is <RawMin or >RawMax || desired==before ||
       !Rc3040sManagementService.SpeedUnits.SliderChanged(
         azimuth,azimuth?az.Value:el.Value,before))return;
    string axis=azimuth?"AZ":"EL";
    if(!Confirm($"{axis}: {Rc3040sManagementService.SpeedUnits.Display(azimuth,before)} → " +
       $"{Rc3040sManagementService.SpeedUnits.Display(azimuth,desired)}?\n" +
       "Verify the motor is physically stopped before saving."))return;
    if(!CanSendAt)return;
    busy=true;RefreshAccess();SetState($"Sending {axis} once; no retry...");
    try
    {
      var motor=rotator?.EngineForRc3040s;
      if(motor is null)throw new InvalidOperationException("No RC-3040S motor worker.");
      using var lease=await motor.AcquireManagementLeaseAsync(lifetime.Token);
      if(lease is null)throw new InvalidOperationException(
        "Motor is not ready or is safety locked; AT write blocked.");
      bool commandAttempted=false,feedbackVerified=false;
      try
      {
        var result=await Rc3040sManagementService.SaveSpeedAsync(
          context.Settings.Rotator.Host,azimuth,desired,lifetime.Token,
          ()=>commandAttempted=true,snapshot.Firmware,snapshot.DeviceIdSuffix);
        if(result.Outcome==Rc3040sManagementService.SpeedWriteOutcome.Applied)
          feedbackVerified=await motor.VerifyPostManagementFeedbackAsync(lifetime.Token);
        if(!IsDisposed)
        {
          // On an ambiguous write, do not enable another speed write based
          // on an old readback. The operator must explicitly read again.
          snapshot=result.Outcome is Rc3040sManagementService.SpeedWriteOutcome.Applied or
            Rc3040sManagementService.SpeedWriteOutcome.Unchanged or
            Rc3040sManagementService.SpeedWriteOutcome.EchoOnly ? result.After : null;
          if(result.Outcome==Rc3040sManagementService.SpeedWriteOutcome.Applied)
          {
            RevertSliders();
            SetState(feedbackVerified
              ? $"{axis}: updated; motor feedback OK. Retention unverified."
              : $"{axis}: updated; motor locked. Reapply settings.");
          }
          else SetState(result.Outcome switch
          {
            Rc3040sManagementService.SpeedWriteOutcome.Unchanged => $"{axis}: device speed unchanged.",
            Rc3040sManagementService.SpeedWriteOutcome.EchoOnly => $"{axis}: echo only; speed unchanged.",
            Rc3040sManagementService.SpeedWriteOutcome.ReadbackUnavailable =>
              $"{axis}: write uncertain; do not retry. Read after recovery.",
            _ => $"{axis}: {result.Detail}"
          });
        }
      }
      finally {if(commandAttempted && !feedbackVerified)motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex)
    {
      snapshot=null;
      SetState($"Speed save blocked: {ex.Message}");
    }
    finally {busy=false;if(!IsDisposed){RefreshAccess();Display();}}
  }

  private async Task RestartAsync()
  {
    if(!CanSendAt || context is null || rotator is null ||
       snapshot?.Firmware is null || snapshot.DeviceIdSuffix is null)return;
    if(!Confirm("Restart RC-3040S via WebSocket/60030?\n" +
      "STOP is not a physical stop acknowledgement.\n" +
      "Check the actual motor is stationary and emergency STOP is available."))return;
    if(!CanSendAt)return;
    busy=true;RefreshAccess();SetState("One restart attempt; no retries...");
    try
    {
      var motor=rotator.EngineForRc3040s;
      if(motor is null)throw new InvalidOperationException("No RC-3040S motor worker.");
      using var lease=await motor.AcquireManagementLeaseAsync(lifetime.Token);
      if(lease is null)throw new InvalidOperationException("STOP/feedback safety check failed.");
      try
      {
        await Rc3040sManagementService.RestartAsync(
          context.Settings.Rotator.Host,snapshot.Firmware,snapshot.DeviceIdSuffix,lifetime.Token);
        snapshot=null;
        SetState("Restart command sent; verify device before further changes.");
      }
      finally {motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex)
    {
      snapshot=null;
      SetState("Restart result UNKNOWN: "+ex.Message);
    }
    finally {busy=false;if(!IsDisposed){RefreshAccess();Display();}}
  }

  private bool Confirm(string question)
  {
    return MessageBox.Show(FindForm(),question,"RC-3040S hardware safety",
      MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
  }

  protected override void Dispose(bool disposing)
  {
    if(disposing) {lifetime.Cancel();tips.Dispose();}
    base.Dispose(disposing);
    if(disposing)lifetime.Dispose();
  }
}
