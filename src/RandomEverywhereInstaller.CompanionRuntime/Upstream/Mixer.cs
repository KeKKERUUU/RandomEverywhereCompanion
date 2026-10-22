// HPG Mission Companion - volume: the volumes of the mission (L:VOLUME_CREW, L:VOLUME_CHK) and the level of the voices between inside and outside the helicopter.
using System;
using System.Globalization;

namespace HPGMissionCompanion
{
    internal static class Mixer
    {
        public const string VarCrew = "L:VOLUME_CREW";        // 0-100, set by the volume sliders of the mission tablet
        public const string VarChk = "L:VOLUME_CHK";          // 0-100, the same for the checklist voice
        public const string VarCamera = "CAMERA STATE";       // 2 = cockpit (inside the helicopter), other values are views outside

        static readonly object sync = new object();
        static double crew = -1, chk = -1;
        static int camera = -1;

        public static bool IsMixerName(string name)
        {
            return name == VarCrew || name == VarChk || name == VarCamera;
        }

        // A new session must not inherit the last flight's volume or camera values.
        public static void Reset()
        {
            lock (sync) { crew = -1; chk = -1; camera = -1; }
        }

        public static void OnWatch(string name, double value)
        {
            lock (sync)
            {
                if (name == VarCrew) crew = value;
                else if (name == VarChk) chk = value;
                else if (name == VarCamera) camera = (int)Math.Round(value);
            }
        }

        // 0 = a line of the crew, 1 = the checklist voice: the level the mission asks for (1 when the program does not know it or the option is off)
        public static double MissionFactor(int actor)
        {
            if (!Ctx.Settings.UseMissionVolumes) return 1.0;
            lock (sync)
            {
                double v = actor == Actors.Checklist ? chk : crew;
                if (v < 0) return 1.0;
                return Math.Max(0.0, Math.Min(1.0, v / 100.0));
            }
        }

        public static double CrewFactor() { return MissionFactor(1); }

        // The level of a voice (not a radio call) between the cabin and the outside. speakerInside: true aboard, false outside, null unknown.
        // Camera inside and the speaker outside: reduced by AttenCamInside dB; camera outside and the speaker inside: reduced by AttenCamOutside dB.
        public static double SpatialFactor(bool radio, bool? speakerInside)
        {
            if (radio || speakerInside == null) return 1.0;
            int cam;
            lock (sync) { cam = camera; }
            if (cam < 0) return 1.0;
            bool camInside = cam == 2;
            double db = 0;
            if (camInside && !speakerInside.Value) db = Ctx.Settings.AttenCamInside;
            else if (!camInside && speakerInside.Value) db = Ctx.Settings.AttenCamOutside;
            return db <= 0 ? 1.0 : Math.Pow(10.0, -db / 20.0);
        }

        public static string Describe()
        {
            lock (sync)
            {
                string c = crew < 0 ? "?" : crew.ToString("0", CultureInfo.InvariantCulture);
                string k = chk < 0 ? "?" : chk.ToString("0", CultureInfo.InvariantCulture);
                string v = camera < 0 ? "view unknown" : (camera == 2 ? "view inside" : "view outside");
                return "Mission volumes: crew " + c + ", checklist " + k + "; " + v;
            }
        }
    }
}
