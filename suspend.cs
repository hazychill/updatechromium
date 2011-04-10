using System;
using System.Reflection;
using System.IO;
using Hazychill.Setting;

public class Program {
  public static void Main() {
    // Suppress warning for never used variable `dummy'.
#pragma warning disable 0168
    // We share settings.txt with updatechromium.
    // Thus, absense of reference for the type `Uri' results error
    // when loading settings.txt.
    Uri dummy;
#pragma warning restore 0168

    try {
      ISettingsManager smng = GetSettings();
      string suspendFileName;
      if (smng.TryGetItem("suspendFileName", out suspendFileName)) {
        string execDir = GetExecDir();
        string suspendFilePath = Path.Combine(execDir, suspendFileName);
        if (!File.Exists(suspendFilePath)) {
          using (Stream emptyFile = File.Open(suspendFilePath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
            // Do nothing, just create a file.
          }
          OutputMessage("Suspended.");
        }
      }
    }
    catch (Exception e) {
      OutputError(e);
    }
    finally {
      OutputMessage("Press enter to exit");
      Console.ReadLine();
    }
  }

  private static ISettingsManager GetSettings() {
    string execDir = GetExecDir();
    string settingsFilePath = Path.Combine(execDir, "settings.txt");
    SettingsManager smng = new SettingsManager();
    smng.Load(settingsFilePath);
    return smng;
  }

  private static string GetExecDir() {
    Assembly myAssembly = Assembly.GetEntryAssembly();
    string path = myAssembly.Location;
    return Path.GetDirectoryName(path);
  }

  private static void OutputMessage(object message) {
    DateTime now = DateTime.Now;
    Console.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }

  private static void OutputError(object message) {
    DateTime now = DateTime.Now;
    Console.Error.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }
}
