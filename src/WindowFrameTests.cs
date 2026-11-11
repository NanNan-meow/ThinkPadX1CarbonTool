using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CarbonControl
{
    public static class WindowFrameTests
    {
        private static int count;
        private static void Assert(bool condition, string name)
        { count++; if (!condition) throw new Exception("Window frame: " + name); }
        public static int Run()
        {
            count = 0;
            Assert(WindowFrame.ColorRef(Color.FromRgb(0x12, 0x34, 0x56)) == 0x563412, "COLORREF uses BGR byte order");
            var resources = new ResourceDictionary();
            var dark = WindowFrame.Colors(resources, false); var light = WindowFrame.Colors(resources, true);
            Assert(dark.Caption == 0x141210 && dark.Border == 0x393430 && dark.Text == 0xF4F3F1, "dark palette fallback");
            Assert(light.Caption == 0xF8F6F5 && light.Border == 0xE8E4E1 && light.Text == 0x2A2420, "light palette fallback");
            resources["Bg"] = new SolidColorBrush(Color.FromRgb(3, 8, 12));
            resources["Line"] = new SolidColorBrush(Color.FromRgb(21, 34, 55));
            resources["Text"] = new SolidColorBrush(Color.FromRgb(220, 230, 240));
            var actual = WindowFrame.Colors(resources, true);
            Assert(actual.Caption == 0x0C0803 && actual.Border == 0x372215 && actual.Text == 0xF0E6DC, "current app resources override fallbacks");
            var attributes = new Dictionary<int, int>();
            WindowFrame.ApplyAttributes((attribute, value) => { attributes[attribute] = value; return 0; }, false, actual);
            Assert(attributes[20] == 1 && !attributes.ContainsKey(19), "modern dark attribute preferred");
            Assert(attributes[34] == actual.Border && attributes[35] == actual.Caption && attributes[36] == actual.Text, "border caption and title colors applied");
            attributes.Clear();
            WindowFrame.ApplyAttributes((attribute, value) => { attributes[attribute] = value; return attribute == 20 ? unchecked((int)0x80070057) : 0; }, true, light);
            Assert(attributes[20] == 0 && attributes[19] == 0, "old Windows dark attribute fallback also restores light mode");
            attributes.Clear();
            WindowFrame.ApplyAttributes((attribute, value) => { attributes[attribute] = value; return attribute >= 34 ? unchecked((int)0x80070057) : 0; }, false, dark);
            Assert(attributes[20] == 1 && attributes.Count == 4, "unsupported Windows 11 colors keep Windows 10 dark preference");
            return count;
        }
    }
}
