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
  private bool? physicalStopConfirmed;
  private const int RawMin = 15;
  private const int RawMax = 10000; // conservative UI band, NOT verified motor safety

  internal Rc3040sQuickControls()
  {
    Size = new Size(318, 216);
    Margin = Padding.Empty;
    title.SetBounds(12, 3, 294, 19);
    title.Text = "RC-3040S  |  WebSocket AT speed";
    title.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
    Controls.Add(title);

    azText.SetBounds(12, 25, 294, 16);
    elText.SetBounds(12, 79, 294, 16);
    Controls.Add(azText); Controls.Add(elText);
    ConfigureSlider(az, 45); ConfigureSlider(el, 99);
    az.Scroll += (_,_) => {if (!settingValues) dirtyAz=true; Display();};
    el.Scroll += (_,_) => {if (!settingValues) dirtyEl=true; Display();};
    Controls.Add(az); Controls.Add(el);

    read.SetBounds(12, 149, 49, 25);
    saveAz.SetBounds(66, 149, 57, 25);
    saveEl.SetBounds(128, 149, 57, 25);
    revert.SetBounds(190, 149, 56, 25);
    restart.SetBounds(251, 149, 55, 25);
    foreach(var button in new[]{read,saveAz,saveEl,revert,restart}) Controls.Add(button);
    read.Click += async (_,_)=>await ReadAsync();
    saveAz.Click += async (_,_)=>await SaveAsync(true);
    saveEl.Click += async (_,_)=>await SaveAsync(false);
    revert.Click += (_,_)=>RevertSliders();
    restart.Click += async (_,_)=>await RestartAsync();

    state.SetBounds(12, 179, 294, 32);
    state.AutoEllipsis=true;state.ForeColor=SystemColors.GrayText;
    state.Text="Read first · no AT write on drag";
    Controls.Add(state);
    tips.SetToolTip(read,"Read 260223_C configuration via WebSocket/60030");
    tips.SetToolTip(revert,"Undo unsaved slider changes; does NOT reset firmware or EEPROM");
    tips.SetToolTip(restart,"Send AT+RESET only with explicit dual confirmation; motor must be physically stopped");
    tips.SetToolTip(state,"A completed WebSocket send is not proof of a motor/EEPROM operation.");
    RefreshAccess();
  }

  private static void ConfigureSlider(TrackBar bar,int y)
  {
    bar.SetBounds(7,y,301,31);
    bar.Minimum=RawMin;bar.Maximum=RawMax;
    bar.TickStyle=TickStyle.None;
    bar.SmallChange=50;bar.LargeChange=250;
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
    !rotator.IsParking && !rotator.IsRc3040sDemoRunning &&
    !rotator.PositionSafetyTripped;

  internal void RefreshAccess()
  {
    if(IsDisposed)return;
    bool active=CanSendAt;
    bool valuesKnown = snapshot?.AzRaw is >=RawMin and <=RawMax &&
                       snapshot?.ElRaw is >=RawMin and <=RawMax;
    read.Enabled=!busy && context?.Settings.Rotator.Rc3040sAtEnabled==true;
    az.Enabled=el.Enabled=active && valuesKnown;
    saveAz.Enabled=az.Enabled && dirtyAz && snapshot!.AzRaw!=az.Value;
    saveEl.Enabled=el.Enabled && dirtyEl && snapshot!.ElRaw!=el.Value;
    revert.Enabled=!busy && valuesKnown;
    restart.Enabled=active && snapshot?.Firmware!=null &&
      snapshot.DeviceIdSuffix!=null;
  }

  private void Display()
  {
    azText.Text=$"AZ raw  {az.Value:D5}" +
      (snapshot?.AzRaw is int originalAz ? $"  (device {originalAz:D5})" : "  (not read)");
    elText.Text=$"EL raw  {el.Value:D5}" +
      (snapshot?.ElRaw is int originalEl ? $"  (device {originalEl:D5})" : "  (not read)");
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
    try {az.Value=azRaw;el.Value=elRaw;dirtyAz=dirtyEl=false;}
    finally {settingValues=false;}
    SetState("Sliders restored from last read (no AT write).");
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
        SetState("Device speed values read; drag is local only.");
      }
      else SetState("Speed fields not returned or outside 15–10000; writes disabled.");
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex) { SetState("Read failed: "+ex.Message); }
    finally {busy=false;if(!IsDisposed){RefreshAccess();Display();}}
  }

  private async Task SaveAsync(bool azimuth)
  {
    if(!CanSendAt || snapshot is null || context is null)return;
    int before=azimuth ? snapshot.AzRaw??-1 : snapshot.ElRaw??-1;
    int desired=azimuth ? az.Value : el.Value;
    if(before is <RawMin or >RawMax || desired==before)return;
    string axis=azimuth?"AZ":"EL";
    if(!Confirm($"Set {axis} raw speed {before:D5} → {desired:D5}?\n" +
       "May write EEPROM. You must physically stop/isolate the motor.\n" +
       "Moving hardware/firmware effects are not verified.",
       $"SAVE-{axis}-{desired:D5}"))return;
    if(!CanSendAt)return;
    busy=true;RefreshAccess();SetState($"Sending {axis} once; no retry...");
    try
    {
      var motor=rotator?.EngineForRc3040s;
      if(motor is null)throw new InvalidOperationException("No RC-3040S motor worker.");
      using var lease=await motor.AcquireManagementLeaseAsync(lifetime.Token);
      if(lease is null)throw new InvalidOperationException(
        "Motor is not ready or is safety locked; AT write blocked.");
      try
      {
        var result=await Rc3040sManagementService.SaveSpeedAsync(
          context.Settings.Rotator.Host,azimuth,desired,lifetime.Token);
        if(!IsDisposed)
        {
          snapshot=result.After;
          RevertSliders();
          SetState($"{axis} readback matched; reapply rotator settings after physical check.");
        }
      }
      finally {motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex) { SetState($"Write state UNKNOWN: {ex.Message}. Do not retry."); }
    finally {busy=false;RefreshAccess();Display();}
  }

  private async Task RestartAsync()
  {
    if(!CanSendAt || context is null || rotator is null ||
       snapshot?.Firmware is null || snapshot.DeviceIdSuffix is null)return;
    if(!Confirm("Restart RC-3040S via WebSocket/60030?\n" +
      "STOP is not a physical stop acknowledgement.\n" +
      "Check the actual motor is stationary and emergency STOP is available.",
      "RESTART"))return;
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
        SetState("Restart command sent; reboot unverified. Reapply settings.");
      }
      finally {motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    catch(Exception ex) { SetState("Restart result UNKNOWN: "+ex.Message); }
    finally {busy=false;RefreshAccess();}
  }

  private bool Confirm(string question,string phrase)
  {
    if(MessageBox.Show(FindForm(),question,"RC-3040S hardware safety",
      MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return false;
    using var dialog=new Form{Text="Confirm hardware operation",ClientSize=new Size(355,135),
      StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
      MaximizeBox=false,MinimizeBox=false};
    dialog.Controls.Add(new Label{Text=$"Type {phrase} to authorize one command:",
      Left=12,Top=12,Width=325,Height=23});
    var field=new TextBox{Left=12,Top=42,Width=325};
    var ok=new Button{Text="Confirm",Left=160,Top=90,Width=83,DialogResult=DialogResult.OK};
    var cancel=new Button{Text="Cancel",Left=253,Top=90,Width=84,DialogResult=DialogResult.Cancel};
    dialog.Controls.AddRange(new Control[]{field,ok,cancel});
    dialog.AcceptButton=ok;dialog.CancelButton=cancel;
    return dialog.ShowDialog(FindForm())==DialogResult.OK &&
      string.Equals(field.Text.Trim(),phrase,StringComparison.Ordinal);
  }

  protected override void Dispose(bool disposing)
  {
    if(disposing) {lifetime.Cancel();tips.Dispose();}
    base.Dispose(disposing);
    if(disposing)lifetime.Dispose();
  }
}
