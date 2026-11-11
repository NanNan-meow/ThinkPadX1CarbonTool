using System;
using System.IO;
using System.Reflection;

namespace CarbonControl
{
    public static class HardwareComponents
    {
        public static string DirectoryPath
        { get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hardware")); } }
        public static string LibraryPath
        { get { return Path.Combine(DirectoryPath, "LibreHardwareMonitorLib.dll"); } }
        public static bool IsInstalled { get { return File.Exists(LibraryPath); } }
        public static Assembly LoadLibrary() { return LoadLibrary(AppDomain.CurrentDomain.BaseDirectory); }
        internal static Assembly LoadLibrary(string applicationDirectory)
        {
            string path = Path.GetFullPath(Path.Combine(applicationDirectory, "hardware", "LibreHardwareMonitorLib.dll"));
            // Missing LoadFrom requests can remain cached by the CLR for this process.
            // Check the file first, including on the fan-control path before installation.
            if (!File.Exists(path)) throw new FileNotFoundException("硬件组件未安装", path);
            return Assembly.LoadFrom(path);
        }
    }
}
