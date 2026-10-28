using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof;

/// <summary>Motion profile preferences. Applying settings does not move hardware.</summary>
internal sealed class Rc3040sMotionSettingsDialog : Form
{
  private readonly RotatorSettings settings;
  private readonly Action persist;
  private readonly ComboBox profile=new();
  private readonly Label modeDescription=new(), availability=new();
  private readonly CheckBox enabled=new(), tested=new();
  private readonly NumericUpDown az=new(),el=new(),acceleration=new(),jerk=new();
  private readonly Button defaults=new(){Text="Defaults"};
  private readonly Button save=new(){Text="Save settings"};
  private readonly Button cancel=new(){Text="Cancel"};
  private static readonly (Rc3040sMotionProfile Mode,string Title,string Details)[] Modes =
  {
    (Rc3040sMotionProfile.Direct,"Direct","Send the requested position directly."),
    (Rc3040sMotionProfile.PrecisionStop,"Precision stop","Approach the target with shorter steps."),
    (Rc3040sMotionProfile.Trapezoidal,"Trapezoidal","Plan acceleration, cruise and deceleration."),
    (Rc3040sMotionProfile.SCurve,"S-curve","Smooth changes in acceleration.")
  };

  internal Rc3040sMotionSettingsDialog(RotatorSettings options,Action saveSettings)
  {
    settings=options;persist=saveSettings;
    Text="RC-3040S · Motion Settings";
    ClientSize=new Size(490,468);
    AutoScaleMode=AutoScaleMode.Font;
    FormBorderStyle=FormBorderStyle.FixedDialog;
    StartPosition=FormStartPosition.CenterParent;
    MaximizeBox=MinimizeBox=false;

    var modeGroup=new GroupBox{Text="Motion mode",Left=14,Top=12,Width=462,Height=104};
    profile.SetBounds(16,26,428,27);
    profile.DropDownStyle=ComboBoxStyle.DropDownList;
    foreach(var item in Modes)profile.Items.Add(item.Title);
    int index=Array.FindIndex(Modes,x=>x.Mode==settings.Rc3040sMotionMode);
    profile.SelectedIndex=Math.Max(index,0);
    profile.SelectedIndexChanged+=(_,_)=>UpdateUi();
    modeDescription.SetBounds(16,63,428,26);
    modeDescription.ForeColor=SystemColors.GrayText;
    modeGroup.Controls.AddRange(new Control[]{profile,modeDescription});
    Controls.Add(modeGroup);

    var enableGroup=new GroupBox{Text="Activation",Left=14,Top=127,Width=462,Height=91};
    enabled.SetBounds(16,23,428,24);
    enabled.Text="Enable motion planning";enabled.Checked=settings.Rc3040sPlannerEnabled;
    tested.SetBounds(16,51,428,25);
    tested.Text="Actual shaft AZ/EL independently match C2; STOP tested";
    tested.Checked=settings.Rc3040sMotionHardwareValidated &&
      settings.Rc3040sIndependentAngleVerified;
    enabled.CheckedChanged+=(_,_)=>UpdateUi();
    tested.CheckedChanged+=(_,_)=>UpdateUi();
    enableGroup.Controls.AddRange(new Control[]{enabled,tested});
    Controls.Add(enableGroup);

    var parameters=new GroupBox{Text="Motion limits",Left=14,Top=228,Width=462,Height=154};
    SetupNumber(az,settings.Rc3040sPlanAzSpeed,0.1m,20m,16,25,parameters,"AZ max speed", "°/s");
    SetupNumber(el,settings.Rc3040sPlanElSpeed,0.1m,20m,16,58,parameters,"EL max speed","°/s");
    SetupNumber(acceleration,settings.Rc3040sPlanAcceleration,0.05m,8m,16,91,parameters,"Max acceleration","°/s²");
    SetupNumber(jerk,settings.Rc3040sPlanJerk,0.05m,30m,16,124,parameters,"Max jerk","°/s³");
    Controls.Add(parameters);

    availability.SetBounds(17,390,458,25);
    availability.ForeColor=SystemColors.GrayText;Controls.Add(availability);
    defaults.SetBounds(202,426,80,28);
    save.SetBounds(290,426,91,28);
    cancel.SetBounds(389,426,88,28);
    defaults.Click+=(_,_)=>RestoreDefaults();
    save.Click+=(_,_)=>Commit();
    cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
    Controls.AddRange(new Control[]{defaults,save,cancel});
    AcceptButton=save;CancelButton=cancel;
    UpdateUi();
  }

  private static void SetupNumber(NumericUpDown field,double value,decimal minimum,
    decimal maximum,int x,int y,Control parent,string title,string unit)
  {
    parent.Controls.Add(new Label{Text=title,Left=x,Top=y+4,Width=181,Height=22});
    field.SetBounds(x+194,y,122,26);
    field.DecimalPlaces=2;field.Minimum=minimum;field.Maximum=maximum;
    field.Increment=0.05m;
    field.Value=(decimal)Math.Clamp(double.IsFinite(value)?value:(double)minimum,
      (double)minimum,(double)maximum);
    parent.Controls.Add(field);
    parent.Controls.Add(new Label{Text=unit,Left=x+328,Top=y+3,Width=95,Height=23});
  }

  private Rc3040sMotionProfile SelectedMode=>Modes[profile.SelectedIndex].Mode;

  private void UpdateUi()
  {
    modeDescription.Text=Modes[profile.SelectedIndex].Details;
    bool direct=SelectedMode==Rc3040sMotionProfile.Direct;
    bool needsVerification=enabled.Checked && !direct && !tested.Checked;
    foreach(var field in new[]{az,el,acceleration,jerk})
      field.Enabled=!direct;
    tested.Enabled=!direct && enabled.Checked;
    availability.Text=direct?"Direct positioning":
      !enabled.Checked?"Planner disabled":
      needsVerification?"Verify real AZ/EL angles independently, then STOP":"Planner ready after reapply";
    availability.ForeColor=needsVerification?Color.DarkOrange:SystemColors.GrayText;
    save.Enabled=!needsVerification;
  }

  private void RestoreDefaults()
  {
    profile.SelectedIndex=0;enabled.Checked=false;tested.Checked=false;
    az.Value=2m;el.Value=2m;acceleration.Value=0.8m;jerk.Value=1.5m;
    UpdateUi();
  }

  private void Commit()
  {
    if(!save.Enabled)return;
    settings.Rc3040sMotionMode=SelectedMode;
    settings.Rc3040sPlannerEnabled=enabled.Checked && SelectedMode!=Rc3040sMotionProfile.Direct;
    settings.Rc3040sMotionHardwareValidated=tested.Checked;
    settings.Rc3040sIndependentAngleVerified=tested.Checked;
    settings.Rc3040sPlanAzSpeed=(double)az.Value;
    settings.Rc3040sPlanElSpeed=(double)el.Value;
    settings.Rc3040sPlanAcceleration=(double)acceleration.Value;
    settings.Rc3040sPlanJerk=(double)jerk.Value;
    persist();
    DialogResult=DialogResult.OK;
    Close();
  }
}
