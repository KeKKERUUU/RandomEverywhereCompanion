// HPG Mission Companion - downloads (Piper engine, Piper voices), the Piper voice catalog and the start with Microsoft Flight Simulator.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace HPGMissionCompanion
{
    internal static class Net
    {
        static Net()
        {
            // TLS 1.2 (3072) is not on by default in .NET Framework 4.0 to 4.5
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
        }

        static HttpWebRequest NewRequest(string url)
        {
            if (!new Uri(url).Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloads require HTTPS.");
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HPGMissionCompanion/0.2";
            req.AllowAutoRedirect = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            return req;
        }

        static string RelativeToStorage(string path)
        {
            var root = Path.GetFullPath(Ctx.BaseDir).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Download destination is outside companion storage.");
            return full.Substring(root.Length);
        }

        public static string GetString(string url)
        {
            using (WebResponse resp = NewRequest(url).GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        // Downloads to dest (through dest.part), reports progress, checks the md5 when given.
        public static void Download(string url, string dest, Action<long, long> progress, string md5, long expected)
        {
            dest = RuntimeFileSafety.ManagedPath(Ctx.BaseDir, RelativeToStorage(dest));
            string part = dest + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (WebResponse resp = NewRequest(url).GetResponse())
                {
                    long total = resp.ContentLength > 0 ? resp.ContentLength : expected;
                    using (Stream src = resp.GetResponseStream())
                    using (FileStream dst = new FileStream(part, FileMode.Create, FileAccess.Write))
                    using (MD5 hash = MD5.Create())
                    {
                        byte[] buf = new byte[81920];
                        long done = 0;
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            hash.TransformBlock(buf, 0, n, null, 0);
                            done += n;
                            if (done > 2L * 1024 * 1024 * 1024 || (expected > 0 && done > expected)) throw new IOException("Download exceeds its declared size.");
                            if (progress != null) progress(done, total);
                        }
                        if (expected > 0 && done != expected) throw new IOException("Download size differs from its catalog entry.");
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (!string.IsNullOrEmpty(md5))
                        {
                            string got = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                            if (got != md5.ToLowerInvariant())
                            {
                                dst.Close();
                                File.Delete(part);
                                throw new IOException("download is corrupted (md5 differs), try again");
                            }
                        }
                    }
                }
                ReplaceVerifiedDownload(part, dest);
            }
            finally { if (File.Exists(part)) File.Delete(part); }
        }

        static void ReplaceVerifiedDownload(string part, string destination)
        {
            var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
            if (File.Exists(destination)) File.Move(destination, previous);
            try { File.Move(part, destination); }
            catch { if (File.Exists(previous) && !File.Exists(destination)) File.Move(previous, destination); throw; }
            if (File.Exists(previous)) File.Delete(previous);
        }
    }

    internal sealed class CatalogFile
    {
        public string Path;
        public long Size;
        public string Md5;
    }

    internal sealed class CatalogVoice
    {
        public string Key;
        public string Name;
        public string LangCode;
        public string Family;
        public string Quality;
        public int Speakers;
        public List<CatalogFile> Files = new List<CatalogFile>();
        public long OnnxSize;
        public string BaseUrl;          // where the files are, for a voice that is not in the Piper repository (null: the Piper repository)
        public string Source = "";      // community voices: who made it and where it is
        public string License = "";     // community voices: the licence, shown before the download and written to the model card file
        public string Note = "";        // community voices: what the page of the voice says (gender, how it was made); not tested by the author of the program
    }

    internal static class PiperInstaller
    {
        public const string EngineUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
        public const long EngineSize = 22477236;
        public const string CatalogUrl = "https://huggingface.co/rhasspy/piper-voices/raw/main/voices.json";
        public const string VoiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

        public static void InstallEngine(Action<long, long> progress)
        {
            var relative = ".staging/piper-" + Guid.NewGuid().ToString("N");
            var tmp = RuntimeFileSafety.ManagedPath(Ctx.BaseDir, relative);
            Directory.CreateDirectory(tmp);
            var backup = RuntimeFileSafety.ManagedPath(Ctx.BaseDir, relative + "-previous");
            var target = RuntimeFileSafety.ManagedPath(Ctx.BaseDir, "piper");
            var committed = false;
            try
            {
                var zip = Path.Combine(tmp, "piper.zip");
                Net.Download(EngineUrl, zip, progress, null, EngineSize);
                var extracted = Path.Combine(tmp, "extracted");
                Directory.CreateDirectory(extracted);
                // Validate every archive path and byte budget before writing any entry.
                using (var archive = ZipFile.OpenRead(zip))
                {
                    long bytes = 0;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries)
                    {
                        var path = RuntimeFileSafety.ManagedPath(extracted, entry.FullName.Replace('/', '\\'));
                        if (!seen.Add(path)) throw new IOException("Duplicate Piper archive entry.");
                        bytes = checked(bytes + entry.Length);
                        if (bytes > 1024L * 1024 * 1024) throw new IOException("Piper archive exceeds the extraction budget.");
                    }
                    foreach (var entry in archive.Entries)
                    {
                        var path = RuntimeFileSafety.ManagedPath(extracted, entry.FullName.Replace('/', '\\'));
                        if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(path); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        {
                            var buffer = new byte[81920]; long copied = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                copied += count;
                                if (copied > entry.Length) throw new IOException("Piper archive entry exceeds its declared size.");
                                output.Write(buffer, 0, count);
                            }
                            if (copied != entry.Length) throw new IOException("Incomplete Piper archive entry.");
                        }
                    }
                }
                var source = Path.Combine(extracted, "piper");
                if (!File.Exists(Path.Combine(source, "piper.exe"))) source = extracted;
                if (!File.Exists(Path.Combine(source, "piper.exe"))) throw new IOException("piper.exe was not found in the download.");
                PiperEngine.StopAll();
                if (Directory.Exists(target)) Directory.Move(target, backup);
                try { Directory.Move(source, target); committed = true; }
                catch { if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
            }
            finally
            {
                // Only randomly named, managed staging folders can be removed here.
                try { if (!RuntimeFileSafety.HasReparsePoint(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
                if (committed) try { if (!RuntimeFileSafety.HasReparsePoint(backup) && Directory.Exists(backup)) Directory.Delete(backup, true); } catch (IOException) { }
            }
        }

        public static List<CatalogVoice> LoadCatalog(string json)
        {
            List<CatalogVoice> list = new List<CatalogVoice>();
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return list;
            foreach (KeyValuePair<string, object> kv in root)
            {
                Dictionary<string, object> v = kv.Value as Dictionary<string, object>;
                if (v == null) continue;
                try
                {
                    CatalogVoice c = new CatalogVoice();
                    c.Key = Convert.ToString(v["key"]);
                    c.Name = Convert.ToString(v["name"]);
                    Dictionary<string, object> lang = (Dictionary<string, object>)v["language"];
                    c.LangCode = Convert.ToString(lang["code"]);
                    c.Family = Convert.ToString(lang["family"]).ToLowerInvariant();
                    c.Quality = Convert.ToString(v["quality"]);
                    c.Speakers = Convert.ToInt32(v["num_speakers"], CultureInfo.InvariantCulture);
                    foreach (KeyValuePair<string, object> f in (Dictionary<string, object>)v["files"])
                    {
                        Dictionary<string, object> fm = (Dictionary<string, object>)f.Value;
                        CatalogFile cf = new CatalogFile();
                        cf.Path = f.Key;
                        cf.Size = Convert.ToInt64(fm["size_bytes"], CultureInfo.InvariantCulture);
                        cf.Md5 = Convert.ToString(fm["md5_digest"]);
                        c.Files.Add(cf);
                        if (cf.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) c.OnnxSize = cf.Size;
                    }
                    if (c.Speakers <= 40) list.Add(c);
                }
                catch (Exception) { }
            }
            return list;
        }

        // Voices that are not in the official Piper list: community voices on Hugging Face, with their licence. They are listed beside the official ones
        // (Piper voices tab) and downloaded from their own repository when you press Install; they are not part of this program. Italian has few voices,
        // and only one official male one (riccardo, x_low). The sizes are the ones the server reports (no md5 is published), the gender is what the page says.
        public static readonly List<CatalogVoice> Community = MakeCommunity();

        static CatalogVoice Community1(string key, string name, string baseUrl, string onnx, string config, long size, string source, string license, string note)
        {
            CatalogVoice c = new CatalogVoice();
            c.Key = key; c.Name = name; c.LangCode = "it_IT"; c.Family = "it"; c.Quality = "medium"; c.Speakers = 1;
            c.BaseUrl = baseUrl; c.Source = source; c.License = license; c.Note = note;
            CatalogFile f = new CatalogFile(); f.Path = onnx; f.Size = size; c.Files.Add(f);
            CatalogFile j = new CatalogFile(); j.Path = config; j.Size = 0; c.Files.Add(j);
            c.OnnxSize = size;
            return c;
        }

        static List<CatalogVoice> MakeCommunity()
        {
            List<CatalogVoice> list = new List<CatalogVoice>();
            string kirys = "https://huggingface.co/kirys79/piper_italiano/resolve/main/";
            list.Add(Community1("it_IT-leonardo-medium", "leonardo", kirys, "Leonardo/leonardo-epoch=2024-step=996300.onnx", "Leonardo/leonardo-epoch=2024-step=996300.json", 63511038,
                "kirys79/piper_italiano on Hugging Face, trained from scratch on Multilingual LibriSpeech", "CC BY 4.0 (attribution)",
                "Male, probably the voice of riccardo with a higher quality."));
            list.Add(Community1("it_IT-giorgio-medium", "giorgio", kirys, "Giorgio/giorgio-epoch=5028-step=1098436.onnx", "Giorgio/giorgio-epoch=5028-step=1098436.json", 63511038,
                "kirys79/piper_italiano on Hugging Face, an experimental fine-tune of leonardo", "CC BY 4.0 (attribution)",
                "Gender not stated, no quality rating: listen first."));
            list.Add(Community1("it_IT-miro-medium", "miro", "https://huggingface.co/OpenVoiceOS/pipertts_it-IT_miro/resolve/main/", "miro_it-IT.onnx", "miro_it-IT.onnx.json", 63511038,
                "OpenVoiceOS/pipertts_it-IT_miro on Hugging Face, synthetic training data", "CC BY-NC-ND 4.0, non-commercial use only, attribution to TigreGotico Lda",
                "Male, says its page."));
            return list;
        }

        public static string CommunityCard(CatalogVoice c)
        {
            return "Community voice " + c.Name + " (" + c.Key + ")\r\nSource: " + c.Source + "\r\nLicence: " + c.License + "\r\n" + c.Note + "\r\nNot tested by the author of the HPG Mission Companion.\r\n\r\nDownloaded by the HPG Mission Companion from the source above; read the page of the voice for the full terms before using it outside your own PC.\r\n";
        }

        public static bool IsInstalled(CatalogVoice c)
        {
            return File.Exists(Path.Combine(Ctx.PiperVoicesDir, c.Key + ".onnx")) && File.Exists(Path.Combine(Ctx.PiperVoicesDir, c.Key + ".onnx.json"));
        }

        public static void InstallVoice(CatalogVoice c, Action<long, long> progress)
        {
            RuntimeFileSafety.ManagedPath(Ctx.PiperVoicesDir, c.Key + ".onnx");
            Directory.CreateDirectory(Ctx.PiperVoicesDir);
            foreach (CatalogFile f in c.Files)
            {
                string dest;
                if (f.Path.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase)) dest = Path.Combine(Ctx.PiperVoicesDir, c.Key + ".onnx.json");
                else if (f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) dest = Path.Combine(Ctx.PiperVoicesDir, c.Key + ".onnx");
                else if (c.BaseUrl != null && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) dest = Path.Combine(Ctx.PiperVoicesDir, c.Key + ".onnx.json");    // a config named model.json: Piper wants model.onnx.json
                else if (f.Path.EndsWith("MODEL_CARD", StringComparison.OrdinalIgnoreCase)) dest = Path.Combine(Ctx.PiperVoicesDir, c.Key + ".MODEL_CARD.txt");
                else continue;
                try { Net.Download((c.BaseUrl ?? VoiceBase) + f.Path, dest, progress, f.Md5, f.Size); }
                catch (Exception)
                {
                    if (!dest.EndsWith(".MODEL_CARD.txt")) throw;
                }
            }
            if (c.BaseUrl != null) File.WriteAllText(Path.Combine(Ctx.PiperVoicesDir, c.Key + ".MODEL_CARD.txt"), CommunityCard(c), new UTF8Encoding(false));
        }

    }
}
