using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace CarbonControl
{
    public static class CurveSlotTests
    {
        private static int count;
        private static void Assert(bool condition, string label)
        { count++; if (!condition) throw new Exception("Curve slot test failed: " + label); }
        private static void Reject<TException>(Action action, string label) where TException : Exception
        {
            bool rejected = false;
            try { action(); } catch (TException) { rejected = true; }
            Assert(rejected, label);
        }
        private static bool Same(IList<CurvePoint> a, IList<CurvePoint> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Temperature != b[i].Temperature || a[i].Level != b[i].Level) return false;
            return true;
        }
        private static CurvePoint[] NinePoints()
        {
            return new[] { new CurvePoint(30, 0), new CurvePoint(38, 0), new CurvePoint(44, 1),
                new CurvePoint(50, 2), new CurvePoint(58, 3), new CurvePoint(65, 4),
                new CurvePoint(72, 5), new CurvePoint(79, 6), new CurvePoint(85, 7) };
        }
        private static void WithoutSlots(string file)
        {
            var document = new XmlDocument(); document.Load(file);
            XmlNode slots = document.DocumentElement.SelectSingleNode("CurveSlots");
            if (slots != null) document.DocumentElement.RemoveChild(slots);
            XmlNode selected = document.DocumentElement.SelectSingleNode("SelectedCurveSlot");
            if (selected != null) document.DocumentElement.RemoveChild(selected);
            document.Save(file);
        }
        public static int Run(string directory)
        {
            count = 0;
            Directory.CreateDirectory(directory);
            string prefix = "slot-test-" + Guid.NewGuid().ToString("N");
            string file = Path.Combine(directory, prefix + ".xml");
            string legacyFile = Path.Combine(directory, prefix + "-legacy.xml");
            string malformedFile = Path.Combine(directory, prefix + "-malformed.xml");
            var settings = new Settings();
            Assert(settings.CurveSlots.Length == 3 && settings.SelectedCurveSlot == 0, "three default slots");
            for (int i = 0; i < 3; i++)
                Assert(settings.CurveSlots[i].Name == "我的曲线 " + (i + 1) && !settings.CurveSlots[i].HasCurve, "default empty slot " + i);
            Reject<InvalidOperationException>(() => settings.LoadCurveSlot(1), "empty slot load");
            Reject<ArgumentOutOfRangeException>(() => settings.LoadCurveSlot(-1), "negative slot index");
            Reject<ArgumentOutOfRangeException>(() => settings.SaveCurveSlot(3, "x", NinePoints()), "large slot index");
            settings.Save(file);
            var empty = Settings.Read(file);
            Assert(empty.CurveSlots.Length == 3 && !empty.CurveSlots[0].HasCurve, "explicit empty slots stay empty");
            Assert(!File.ReadAllText(file).Contains("HasCurve"), "computed flag is not serialized");

            var source = NinePoints(); var active = settings.Curve.ToArray();
            settings.SaveCurveSlot(0, "  日常办公  ", source);
            Assert(settings.CurveSlots[0].Name == "日常办公" && settings.CurveSlots[0].HasCurve, "slot name trimmed");
            Assert(Same(settings.Curve, active), "saving slot keeps active custom curve");
            source[0].Temperature = 26;
            Assert(settings.LoadCurveSlot(0)[0].Temperature == 30, "save deep copies source");
            var copy = settings.LoadCurveSlot(0); copy[0].Temperature = 25;
            Assert(settings.LoadCurveSlot(0)[0].Temperature == 30, "load returns deep copy");
            settings.SaveCurveSlot(1, "持续散热", FanPolicy.Preset(FanMode.Cool));
            settings.SaveCurveSlot(2, null, FanPolicy.Preset(FanMode.Quiet));
            Assert(settings.CurveSlots[2].Name == "我的曲线 3" && settings.SelectedCurveSlot == 2, "empty name fallback and selected slot");
            settings.Save(file);
            var loaded = Settings.Read(file);
            Assert(loaded.CurveSlots.Length == 3 && loaded.SelectedCurveSlot == 2, "selection persists");
            Assert(loaded.LoadCurveSlot(0).Length == 9 && Same(loaded.LoadCurveSlot(0), NinePoints()), "nine node curve persists");
            Assert(loaded.CurveSlots[0].Name == "日常办公" && loaded.CurveSlots[1].Name == "持续散热", "names persist");
            Assert(Same(loaded.LoadCurveSlot(1), FanPolicy.Preset(FanMode.Cool)) && Same(loaded.LoadCurveSlot(2), FanPolicy.Preset(FanMode.Quiet)), "slots stay independent");
            loaded.SaveCurveSlot(0, "重新命名", FanPolicy.Preset(FanMode.Balanced));
            Assert(loaded.CurveSlots[0].Name == "重新命名" && loaded.LoadCurveSlot(0).Length == 6, "overwrite and rename");
            Assert(Same(loaded.LoadCurveSlot(1), FanPolicy.Preset(FanMode.Cool)), "overwrite keeps other slot");
            loaded.SaveCurveSlot(0, new string('长', 30), NinePoints());
            Assert(loaded.CurveSlots[0].Name.Length == 24, "maximum name length");
            string priorName = loaded.CurveSlots[0].Name;
            Reject<ArgumentException>(() => loaded.SaveCurveSlot(0, "invalid", new[] { new CurvePoint(50, 3) }), "invalid save rejected");
            Assert(loaded.CurveSlots[0].Name == priorName && loaded.LoadCurveSlot(0).Length == 9, "invalid save leaves slot intact");

            var legacy = new Settings { Curve = NinePoints(), Theme = "Dark", ManualLevel = 6 };
            legacy.Save(legacyFile); WithoutSlots(legacyFile);
            var migrated = Settings.Read(legacyFile);
            Assert(migrated.CurveSlots.Length == 3 && migrated.CurveSlots[0].Name == "我的曲线 1", "legacy migration creates slots");
            Assert(Same(migrated.LoadCurveSlot(0), NinePoints()) && Same(migrated.Curve, NinePoints()), "legacy active curve retained and copied");
            Assert(migrated.Theme == "Dark" && migrated.ManualLevel == 6 && !migrated.CurveSlots[1].HasCurve, "legacy other settings retained");
            migrated.Curve[0].Temperature = 25;
            Assert(migrated.LoadCurveSlot(0)[0].Temperature == 30, "migrated slot does not alias active curve");
            migrated.Save(legacyFile); var reread = Settings.Read(legacyFile);
            Assert(reread.Curve[0].Temperature == 25 && reread.LoadCurveSlot(0)[0].Temperature == 30, "migration not repeated once slots exist");

            var malformed = new Settings { Curve = NinePoints(), SelectedCurveSlot = 40 };
            malformed.CurveSlots = new[] { null, new CurveSlot { Name = "   ", Curve = new[] { new CurvePoint(70, 2) } },
                new CurveSlot { Name = " 保留有效曲线 ", Curve = FanPolicy.Preset(FanMode.Cool).ToArray() }, new CurveSlot { Name = "多余" } };
            malformed.Save(malformedFile);
            var repaired = Settings.Read(malformedFile);
            Assert(repaired.CurveSlots.Length == 3 && repaired.SelectedCurveSlot == 2, "normalize slot count and large selection");
            Assert(!repaired.CurveSlots[0].HasCurve && !repaired.CurveSlots[1].HasCurve && repaired.CurveSlots[1].Name == "我的曲线 2", "null and invalid slots normalized");
            Assert(repaired.CurveSlots[2].Name == "保留有效曲线" && Same(repaired.LoadCurveSlot(2), FanPolicy.Preset(FanMode.Cool)), "valid slot survives normalization");
            Assert(Same(repaired.Curve, NinePoints()), "valid active curve survives normalization");
            repaired.CurveSlots = null; repaired.SelectedCurveSlot = -4; repaired.Save(malformedFile);
            repaired = Settings.Read(malformedFile);
            Assert(repaired.CurveSlots.Length == 3 && repaired.SelectedCurveSlot == 0 && !repaired.CurveSlots[0].HasCurve, "null slots and negative selection");
            repaired.CurveSlots = new[] { new CurveSlot { Name = "仅一个", Curve = NinePoints() } }; repaired.Save(malformedFile);
            repaired = Settings.Read(malformedFile);
            Assert(repaired.CurveSlots.Length == 3 && repaired.LoadCurveSlot(0).Length == 9 && !repaired.CurveSlots[2].HasCurve, "short slot array filled");

            // Corrupt a serialized slot after Save so this exercises normalization on Read as well.
            var xml = new XmlDocument(); xml.Load(malformedFile);
            xml.DocumentElement.SelectSingleNode("CurveSlots/CurveSlot/Curve/CurvePoint/Temperature").InnerText = "200";
            xml.DocumentElement.SelectSingleNode("SelectedCurveSlot").InnerText = "-10"; xml.Save(malformedFile);
            repaired = Settings.Read(malformedFile);
            Assert(!repaired.CurveSlots[0].HasCurve && repaired.SelectedCurveSlot == 0 && Same(repaired.Curve, NinePoints()), "read repairs invalid slot and preserves active curve");
            File.Delete(file); File.Delete(legacyFile); File.Delete(malformedFile);
            return count;
        }
    }
}
