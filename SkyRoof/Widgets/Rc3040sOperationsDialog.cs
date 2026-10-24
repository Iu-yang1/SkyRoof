using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SkyRoof.NativeRotator;

namespace SkyRoof;

/// <summary>
/// RC-3040S LAN operations. The two TCP ports have independent lifetimes:
/// motor commands stay exclusively in NativeRotatorControlEngine (60020);
/// this dialog opens bounded, on-demand WebSocket/60030 sessions.
/// Dragging either slider does not send AT commands or write EEPROM.
/// </summary>
internal sealed class Rc3040sOperationsDialog : Form
{
  private readonly RotatorWidget rotor;
  private readonly RotatorSettings settings;
  private readonly Label status=new();
  private readonly Label firmware=new();
  private readonly Label actual=new();
  private readonly TrackBar az=new(),el=new();
  private readonly Label azValue=new(),elValue=new();
  private readonly Button read=new(){Text="Read device"},reset=new(){Text="Revert sliders"};
  private readonly Button saveAz=new(){Text="Save AZ..."},saveEl=new(){Text="Save EL..."};
  private readonly Button reboot=new(){Text="Restart..."};
  private readonly Button motion=new(){Text="Motion Settings..."};
  private readonly System.Windows.Forms.Timer refresh=new(){Interval=500};
  private readonly CancellationTokenSource cancel=new();
  private Rc3040sManagementService.Snapshot? snapshot;
  private bool busy,dirtyAz,dirtyEl,updating;

  public Rc3040sOperationsDialog(RotatorWidget widget,RotatorSettings options)
  {
    rotor=widget;settings=options;
    Text="RC-3040S LAN | Motion & AT Management";
    ClientSize=new Size(475,430);
    AutoScaleMode=AutoScaleMode.Font;
    FormBorderStyle=FormBorderStyle.FixedDialog;
    StartPosition=FormStartPosition.CenterParent;
    MaximizeBox=false;
    MinimizeBox=false;
    Controls.Add(Label("GS-232B motor TCP/60020  |  WebSocket management TCP/60030",12,12,451,23));
    firmware.SetBounds(12,42,451,19);firmware.Text="Firmware: not read";Controls.Add(firmware);
    actual.SetBounds(12,66,451,18);actual.Text="Rotation speed: not read";Controls.Add(actual);
    read.SetBounds(12,91,112,29);read.Click+=async(_,_)=>await ReadDeviceAsync();
    reset.SetBounds(135,91,116,29);reset.Click+=(_,_)=>RestoreSliders();
    Controls.Add(read);Controls.Add(reset);

    Controls.Add(Label("AZ rotation speed (°/s)",12,137,265,19));
    az.SetBounds(12,160,330,35);az.Minimum=1;az.Maximum=Rc3040sManagementService.SpeedUnits.SliderMaximum(true,10000);
    az.TickFrequency=50;az.SmallChange=5;az.LargeChange=25;
    az.Enabled=false;Controls.Add(az);
    azValue.SetBounds(349,158,114,31);Controls.Add(azValue);
    az.Scroll+=(_,_)=>{if(!updating)dirtyAz=true;ShowValues();};

    Controls.Add(Label("EL rotation speed (°/s)",12,203,265,19));
    el.SetBounds(12,227,330,35);el.Minimum=1;el.Maximum=Rc3040sManagementService.SpeedUnits.SliderMaximum(false,10000);
    el.TickFrequency=50;el.SmallChange=5;el.LargeChange=25;
    el.Enabled=false;Controls.Add(el);
    elValue.SetBounds(349,225,114,31);Controls.Add(elValue);
    el.Scroll+=(_,_)=>{if(!updating)dirtyEl=true;ShowValues();};
    saveAz.SetBounds(12,273,111,29);saveEl.SetBounds(133,273,111,29);
    saveAz.Click+=async(_,_)=>await SaveSpeedAsync(true);
    saveEl.Click+=async(_,_)=>await SaveSpeedAsync(false);
    Controls.Add(saveAz);Controls.Add(saveEl);
    Controls.Add(Label("Drag to adjust · Save each axis separately",12,308,451,24));

    Controls.Add(Label("Device actions",12,334,260,19));
    reboot.SetBounds(343,356,118,29);motion.SetBounds(12,356,165,29);
    reboot.Click+=async(_,_)=>await RestartAsync();
    motion.Click+=(_,_)=>{
      using var dialog=new Rc3040sMotionSettingsDialog(rotor.ctx.Settings.Rotator,rotor.ctx.Settings.SaveToFile);
      dialog.ShowDialog(this);
    };
    Controls.Add(reboot);Controls.Add(motion);
    status.SetBounds(12,388,451,36);status.ForeColor=SystemColors.GrayText;Controls.Add(status);
    refresh.Tick+=(_,_)=>RefreshButtons();refresh.Start();
    RefreshButtons();ShowValues();
    Shown+=async(_,_)=>await ReadDeviceAsync();
  }

  private static Label Label(string text,int x,int y,int w,int h)=>
    new(){Text=text,Location=new Point(x,y),Size=new Size(w,h),
      TextAlign=ContentAlignment.MiddleLeft};

  private bool CanEditHardware =>
    settings.Enabled && settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan &&
    rotor.IsRunning() && !rotor.IsTracking && !rotor.IsParking &&
    !rotor.PositionSafetyTripped;

  private void RefreshButtons()
  {
    if(IsDisposed)return;
    bool safe=CanEditHardware && !busy;
    read.Enabled=!busy && settings.Rc3040sAtEnabled;
    reset.Enabled=!busy && snapshot!=null;
    az.Enabled=el.Enabled=safe && snapshot?.AzRaw is >=15 and <=10000 &&
      snapshot?.ElRaw is >=15 and <=10000;
    saveAz.Enabled=safe && dirtyAz && az.Enabled && snapshot?.AzRaw is int currentAz &&
      Rc3040sManagementService.SpeedUnits.SliderChanged(true,az.Value,currentAz) &&
      currentAz!=SelectedRaw(true,az);
    saveEl.Enabled=safe && dirtyEl && el.Enabled && snapshot?.ElRaw is int currentEl &&
      Rc3040sManagementService.SpeedUnits.SliderChanged(false,el.Value,currentEl) &&
      currentEl!=SelectedRaw(false,el);
    reboot.Enabled=safe && snapshot?.Firmware!=null &&
      snapshot.DeviceIdSuffix!=null;
    motion.Enabled=!busy;
    // Status is updated by explicit actions. Do not erase network errors and
    // readback/uncertain-write warnings every 500 ms during the UI refresh.
    if(!busy && string.IsNullOrEmpty(status.Text))
      status.Text=safe ? "Ready · no AT is sent while dragging" :
        "AT writes locked: stop TRACK / PARK first.";
  }

  private static int SelectedRaw(bool azimuth,TrackBar slider) =>
    Rc3040sManagementService.SpeedUnits.RawFromSlider(azimuth,slider.Value,15,10000);

  private void ShowValues()
  {
    azValue.Text=snapshot==null?"Unknown":$"{az.Value/100.0:0.00} °/s";
    elValue.Text=snapshot==null?"Unknown":$"{el.Value/100.0:0.00} °/s";
    if(snapshot is not null)
      actual.Text=$"Saved: AZ {SpeedLabel(true,snapshot.AzRaw)}   EL {SpeedLabel(false,snapshot.ElRaw)}";
    RefreshButtons();
  }
  private static string SpeedLabel(bool azimuth,int? raw) =>
    raw.HasValue?Rc3040sManagementService.SpeedUnits.Display(azimuth,raw.Value):"Unknown";

  private void RestoreSliders()
  {
    if(snapshot?.AzRaw is not int a || snapshot.ElRaw is not int e)return;
    if(a is <15 or >10000 || e is <15 or >10000)
    {
      status.Text="Speed outside slider range; read-only.";
      return;
    }
    updating=true;
    try
    {
      az.Value=Math.Clamp(Rc3040sManagementService.SpeedUnits.SliderTick(true,a),az.Minimum,az.Maximum);
      el.Value=Math.Clamp(Rc3040sManagementService.SpeedUnits.SliderTick(false,e),el.Minimum,el.Maximum);
      dirtyAz=dirtyEl=false;
    }
    finally {updating=false;}
    ShowValues();
  }

  private async Task ReadDeviceAsync()
  {
    if(busy)return;
    busy=true;status.Text="Reading management WebSocket (C2 / diagnostic read-only echo)...";
    RefreshButtons();
    try
    {
      // Detailed read is a confirmed AT+TEST echo, not a speed-setting command.
      var current=await Rc3040sManagementService.ReadAsync(settings.Host,true,cancel.Token);
      if(IsDisposed)return;
      snapshot=current;
      firmware.Text="Firmware: "+(current.Firmware??"unknown");
      RestoreSliders();
      status.Text=current.AzRaw is null || current.ElRaw is null
        ? "Speed fields not received; writing disabled. No automatic retry."
        : "Speeds loaded.";
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text="Management read failed: "+ex.Message;}
    finally {busy=false;RefreshButtons();}
  }

  private async Task SaveSpeedAsync(bool azimuth)
  {
    if(busy || !CanEditHardware || snapshot is null || !settings.Rc3040sAtEnabled)return;
    int before=azimuth?(snapshot.AzRaw??-1):(snapshot.ElRaw??-1);
    int proposed=azimuth?SelectedRaw(true,az):SelectedRaw(false,el);
    if(before<15 || before>10000 || proposed==before ||
       !Rc3040sManagementService.SpeedUnits.SliderChanged(
         azimuth,azimuth?az.Value:el.Value,before))return;
    string axis=azimuth?"AZ":"EL",phrase=$"SAVE-{axis}-{proposed:D5}";
    if(!ConfirmTwice(
      $"{axis}: {SpeedLabel(azimuth,before)} → {SpeedLabel(azimuth,proposed)}\nVerify the motor is stopped before saving.",
      phrase))return;
    if(!CanEditHardware)return;
    busy=true;status.Text=$"Writing {axis} once; no automatic retries...";
    RefreshButtons();
    try
    {
      var motor=rotor.EngineForRc3040s;
      if(motor is null)throw new InvalidOperationException("RC-3040S native motor worker missing.");
      using var lease=await motor.AcquireManagementLeaseAsync(cancel.Token);
      if(lease is null)throw new InvalidOperationException("STOP/feedback safety check failed.");
      try
      {
        var result=await Rc3040sManagementService.SaveSpeedAsync(
          settings.Host,azimuth,proposed,cancel.Token);
        if(!IsDisposed)
        {
          if(result.After is not null)snapshot=result.After;
          if(result.Outcome==Rc3040sManagementService.SpeedWriteOutcome.Applied)
          {
            RestoreSliders();
            status.Text=$"{axis}: saved. Reapply rotator settings.";
          }
          else status.Text=result.Outcome switch
          {
            Rc3040sManagementService.SpeedWriteOutcome.Unchanged => $"{axis}: device speed unchanged.",
            Rc3040sManagementService.SpeedWriteOutcome.EchoOnly => $"{axis}: echo received; value unchanged.",
            Rc3040sManagementService.SpeedWriteOutcome.ReadbackUnavailable => $"{axis}: readback unavailable.",
            _ => $"{axis}: save not verified."
          };
        }
      }
      finally {motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text=$"{axis} result UNKNOWN: {ex.Message}; do not retry blindly.";}
    finally{busy=false;RefreshButtons();}
  }

  private async Task RestartAsync()
  {
    if(busy || !CanEditHardware || !settings.Rc3040sAtEnabled ||
      snapshot?.Firmware is null || snapshot.DeviceIdSuffix is null)return;
    if(!ConfirmTwice("Restart may disconnect RC-3040S and reset its control state.\n"
      +"Confirm that the motor is physically stopped and a real emergency stop is available.","RESTART"))
      return;
    if(!CanEditHardware)return;
    busy=true;status.Text="Sending one WebSocket restart; reboot is not acknowledged...";
    RefreshButtons();
    try
    {
      var motor=rotor.EngineForRc3040s;
      if(motor is null)throw new InvalidOperationException("RC-3040S native motor worker missing.");
      using var lease=await motor.AcquireManagementLeaseAsync(cancel.Token);
      if(lease is null)throw new InvalidOperationException("STOP/feedback safety check failed.");
      try
      {
        await Rc3040sManagementService.RestartAsync(
          settings.Host,snapshot.Firmware,snapshot.DeviceIdSuffix,cancel.Token);
        if(!IsDisposed)status.Text="Restart sent, reboot NOT verified. Reapply settings after physical inspection.";
      }
      finally {motor.LatchAfterManagementChange();}
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text="Restart result unknown: "+ex.Message;}
    finally{busy=false;RefreshButtons();}
  }

  private bool ConfirmTwice(string warning,string exactPhrase)
  {
    if(MessageBox.Show(this,warning,"RC-3040S hardware operation",
      MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return false;
    using var confirm=new Form{Text="Type exact confirmation",ClientSize=new Size(380,145),
      FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,
      MaximizeBox=false,MinimizeBox=false};
    var prompt=Label("Type: "+exactPhrase,12,12,352,28);
    var entry=new TextBox{Location=new Point(12,49),Width=352};
    var ok=new Button{Text="Confirm",Location=new Point(190,95),Width=84,DialogResult=DialogResult.OK};
    var cancelButton=new Button{Text="Cancel",Location=new Point(280,95),Width=84,DialogResult=DialogResult.Cancel};
    confirm.Controls.AddRange(new Control[]{prompt,entry,ok,cancelButton});
    confirm.AcceptButton=ok;confirm.CancelButton=cancelButton;
    return confirm.ShowDialog(this)==DialogResult.OK &&
      string.Equals(entry.Text.Trim(),exactPhrase,StringComparison.Ordinal);
  }

  protected override void Dispose(bool disposing)
  {
    if(disposing){cancel.Cancel();refresh.Stop();refresh.Dispose();}
    base.Dispose(disposing);
    if(disposing)cancel.Dispose();
  }
}
