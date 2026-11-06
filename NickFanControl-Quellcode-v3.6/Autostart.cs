using System.IO;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace NickFanControl;

/// <summary>
/// Autostart über die Windows-Aufgabenplanung: startet beim Anmelden mit Admin-Rechten,
/// ohne UAC-Abfrage, mit dem Parameter --tray (nur Symbol in der Taskleiste).
/// </summary>
public static class Autostart
{
    private const string TaskName = "NickFanControl";

    public static bool Enable(out string error)
    {
        error = "";
        try
        {
            var exe = Environment.ProcessPath!;
            var user = WindowsIdentity.GetCurrent().Name;

            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Startet Nick Fan Control beim Anmelden im Infobereich der Taskleiste.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>--tray</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

            var tmp = Path.Combine(Path.GetTempPath(), "NickFanControl_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            try { File.Delete(tmp); } catch { }

            if (code != 0)
            {
                error = output.Trim();
                Log.Write("Autostart anlegen fehlgeschlagen: " + error);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Write("Autostart-Fehler: " + ex);
            return false;
        }
    }

    public static void Disable()
    {
        try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); }
        catch (Exception ex) { Log.Write("Autostart entfernen fehlgeschlagen: " + ex.Message); }
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, stdout + stderr);
    }
}
