using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace CarbonControl
{
    public static class HardwareLoadingTests
    {
        private static int count;
        private static void Assert(bool condition, string label)
        { count++; if (!condition) throw new Exception("Hardware loading: " + label); }

        // Separate domains reproduce the CLR's failed-bind cache without poisoning
        // the application domain used to check the corrected pre-install loader.
        public sealed class IsolatedProbe : MarshalByRefObject
        {
            public string Load(string path)
            {
                try
                {
                    Assembly library = Assembly.LoadFrom(path);
                    return (string)library.GetType("PortableHardwareFixture", true).GetMethod("Ping").Invoke(null, null);
                }
                catch (FileNotFoundException) { return "not-found"; }
            }
            public override object InitializeLifetimeService() { return null; }
        }

        private static AppDomain NewDomain()
        {
            return AppDomain.CreateDomain("hardware-loader-" + Guid.NewGuid().ToString("N"), null,
                new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) });
        }
        private static IsolatedProbe Probe(AppDomain domain)
        {
            return (IsolatedProbe)domain.CreateInstanceAndUnwrap(Assembly.GetExecutingAssembly().FullName,
                typeof(IsolatedProbe).FullName);
        }

        public static int Run(string directory)
        {
            count = 0;
            string fixture = Path.Combine(Path.GetFullPath(directory), "release", "loader-tests-" + Guid.NewGuid().ToString("N"));
            string application = Path.Combine(fixture, "安装目录 with spaces", "CarbonControl");
            string installedLibrary = Path.Combine(application, "hardware", "LibreHardwareMonitorLib.dll");
            Directory.CreateDirectory(application);
            bool missing = false;
            try { HardwareComponents.LoadLibrary(application); }
            catch (FileNotFoundException ex)
            {
                missing = true;
                Assert(ex.FileName == Path.GetFullPath(installedLibrary), "missing component reports the full filesystem path");
                Assert(ex.Message.Contains("未安装"), "missing component has a concise status");
            }
            Assert(missing, "pre-install call is rejected before a CLR bind");
            Assert(!Directory.Exists(Path.GetDirectoryName(installedLibrary)), "checking components does not create installation folders");

            string payloadDirectory = Path.Combine(fixture, "payload");
            Directory.CreateDirectory(payloadDirectory);
            string source = Path.Combine(payloadDirectory, "Fixture.cs");
            string payload = Path.Combine(payloadDirectory, "LibreHardwareMonitorLib.dll");
            int revision = (int)(DateTime.UtcNow.Ticks % 65000);
            File.WriteAllText(source,
                "using System.Reflection; [assembly:AssemblyVersion(\"0.0.1." + revision + "\")] " +
                "public class PortableHardwareFixture { public static string Ping() { return \"ready\"; } }", Encoding.UTF8);
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            var start = new ProcessStartInfo(compiler,
                "/nologo /target:library /codepage:65001 /out:\"" + payload + "\" \"" + source + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(start))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("Fixture compiler timed out"); }
                Assert(process.ExitCode == 0, "fixture compiler: " + stdout + stderr);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installedLibrary));
            File.Copy(payload, installedLibrary);
            Assembly loaded = HardwareComponents.LoadLibrary(application);
            Assert(loaded.GetName().Name == "LibreHardwareMonitorLib", "component installed after the first check loads in the same process");
            Assert((string)loaded.GetType("PortableHardwareFixture", true).GetMethod("Ping").Invoke(null, null) == "ready",
                "portable component can execute after installation");
            Assert(string.Equals(loaded.Location, Path.GetFullPath(installedLibrary), StringComparison.OrdinalIgnoreCase),
                "spaces and Chinese characters preserve the assembly location");
            Assert(object.ReferenceEquals(loaded, HardwareComponents.LoadLibrary(application)), "repeated component loads share the assembly");

            string negativePath = Path.Combine(fixture, "old-loader", "hardware", "LibreHardwareMonitorLib.dll");
            AppDomain oldDomain = NewDomain();
            try
            {
                IsolatedProbe old = Probe(oldDomain);
                Assert(old.Load(negativePath) == "not-found", "original unguarded loader fails before installation");
                Directory.CreateDirectory(Path.GetDirectoryName(negativePath)); File.Copy(payload, negativePath);
                Assert(File.Exists(negativePath) && old.Load(negativePath) == "not-found", "original failure remains cached even after the DLL exists");
            }
            finally { AppDomain.Unload(oldDomain); }
            AppDomain freshDomain = NewDomain();
            try { Assert(Probe(freshDomain).Load(negativePath) == "ready", "fresh application domain clears the pre-install binding failure"); }
            finally { AppDomain.Unload(freshDomain); }
            return count;
        }
    }
}
