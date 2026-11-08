using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Krach.Core;
using Microsoft.Win32;

namespace Krach.Services
{
    /// <summary>
    /// Autostart ohne Administrator: Registry "Run".
    /// Läuft Krach als Administrator, wird stattdessen eine Aufgabe mit höchsten Rechten angelegt
    /// (nötig, falls der Interception-Treiber Adminrechte verlangt).
    /// </summary>
    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Krach Soundboard";
        const string TaskName = "Krach Soundboard";
        public const string Argument = "--autostart";

        public static bool IsAdmin
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

        public static void Set(bool on)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (on && !IsAdmin) key.SetValue(ValueName, $"\"{ExePath}\" {Argument}");
                else key.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                Log.Write("Autostart (Registry): " + ex.Message);
            }

            if (on && IsAdmin) CreateTask();
            else if (!on) RunSchtasks("/Delete", "/TN", TaskName, "/F");
        }

        static void CreateTask()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Author>Erlin Ramaj</Author><Description>Startet Krach Soundboard bei der Anmeldung.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(ExePath)}</Command><Arguments>{Argument}</Arguments></Exec></Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "krach-autostart.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                RunSchtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            }
            catch (Exception ex)
            {
                Log.Write("Autostart (Aufgabe): " + ex.Message);
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        static void RunSchtasks(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                p?.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                Log.Write("schtasks: " + ex.Message);
            }
        }
    }
}
