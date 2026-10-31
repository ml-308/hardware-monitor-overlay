using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace HwMonPublish
{
    internal static class Program
    {
        // ---------- Windows 凭据管理器 ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr buffer);

        static string _token;
        static string _owner;
        static string _repo = "hardware-monitor-overlay";
        static bool _insecure;
        static bool _codeOnly;

        static int Main(string[] args)
        {
            bool inspect = false;
            string tag = "v1.0.0";
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--inspect") inspect = true;
                else if (a == "--token" && i + 1 < args.Length) _token = args[++i];
                else if (a == "--owner" && i + 1 < args.Length) _owner = args[++i];
                else if (a == "--repo" && i + 1 < args.Length) _repo = args[++i];
                else if (a == "--tag" && i + 1 < args.Length) tag = args[++i];
                else if (a == "--insecure") _insecure = true;
                else if (a == "--code-only") _codeOnly = true;
            }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (_insecure) ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

            if (_token == null) _token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            string credUser = null;
            if (_token == null) _token = ReadStoredToken(out credUser);

            Console.WriteLine("凭据来源: " + (_token == null ? "未找到" : ("已获取 (" + Mask(_token) + ")" + (credUser != null ? " 账号=" + credUser : ""))));
            if (_token == null) { Console.WriteLine("没有可用的 GitHub 凭据，请设置 GITHUB_TOKEN"); return 1; }

            if (_owner == null)
            {
                int code; string body;
                body = Http("GET", "https://api.github.com/user", _token, null, out code);
                if (code != 200) { Console.WriteLine("GET /user 失败: " + code + " " + Trim(body)); return 1; }
                _owner = JsonStr(body, "login");
                Console.WriteLine("已认证账号: " + _owner);
            }
            if (inspect) return 0;

            // 1) 创建仓库
            int c; string r;
            r = Http("GET", "https://api.github.com/repos/" + _owner + "/" + _repo, _token, null, out c);
            if (c == 404)
            {
                string payload = "{\"name\":\"" + _repo + "\",\"description\":\"轻量透明可拖动可缩放的 Windows 硬件利用率浮窗（CPU/内存/GPU/磁盘/网络）\",\"private\":false,\"has_issues\":true,\"has_wiki\":false,\"auto_init\":false}";
                r = Http("POST", "https://api.github.com/user/repos", _token, payload, out c);
                Console.WriteLine("[1/5] 创建仓库 -> " + c + (c == 201 ? " 已创建" : (" " + Trim(r))));
                if (c != 201) return 1;
            }
            else Console.WriteLine("[1/5] 仓库已存在 (" + c + ")，跳过创建");
            Console.WriteLine("      https://github.com/" + _owner + "/" + _repo);

            // 2) 推送代码
            Console.WriteLine("[2/5] 推送代码与标签 ...");
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(_owner + ":" + _token));
            if (!Git(new[] { "remote", "remove", "origin" }, basic, true)) { }
            if (!Git(new[] { "remote", "add", "origin", "https://github.com/" + _owner + "/" + _repo + ".git" }, basic, true)) { }
            Git(new[] { "branch", "-M", "main" }, basic, true);
            if (!Git(new[] { "push", "-u", "origin", "main", "--force" }, basic, false)) return 1;
            Git(new[] { "tag", "-f", tag }, basic, true);
            Git(new[] { "push", "origin", tag, "--force" }, basic, false);

            if (_codeOnly) { Console.WriteLine("[3/5] --code-only，跳过 Release"); Console.WriteLine("仓库    https://github.com/" + _owner + "/" + _repo); return 0; }

            // 3) 创建 Release（已存在则复用）
            Console.WriteLine("[3/5] 创建 Release " + tag + " ...");
            string notes = File.Exists("RELEASE_NOTES.md") ? File.ReadAllText("RELEASE_NOTES.md", Encoding.UTF8) : "";
            string relJson = "{\"tag_name\":\"" + tag + "\",\"name\":\"硬件监控浮窗 " + tag + "\",\"body\":" + Quote(notes) + ",\"draft\":false,\"prerelease\":false}";
            r = Http("POST", "https://api.github.com/repos/" + _owner + "/" + _repo + "/releases", _token, relJson, out c);
            if (c == 422)
            {
                r = Http("GET", "https://api.github.com/repos/" + _owner + "/" + _repo + "/releases/tags/" + tag, _token, null, out c);
                Console.WriteLine("      Release 已存在，复用现有条目");
            }
            if (c != 201 && c != 200) { Console.WriteLine("      Release 创建失败: " + c + " " + Trim(r)); return 1; }
            string relId = JsonNum(r, "id");
            string relUrl = JsonStr(r, "html_url");
            Console.WriteLine("      " + relUrl);

            // 4) 上传附件
            Console.WriteLine("[4/5] 上传发布产物 ...");
            string[] assets = { "HardwareMonitor.exe", "setup.exe", "release\\HardwareMonitor-1.0.0-portable.zip", "release\\SHA256SUMS.txt" };
            foreach (string f in assets)
            {
                if (!File.Exists(f)) { Console.WriteLine("      跳过(不存在): " + f); continue; }
                string name = Path.GetFileName(f);
                long size = new FileInfo(f).Length;
                string url = "https://uploads.github.com/repos/" + _owner + "/" + _repo + "/releases/" + relId + "/assets?name=" + Uri.EscapeDataString(name);
                string resp = Upload(url, f, _token, out c);
                Console.WriteLine("      " + (c == 201 ? "已上传 " : "上传失败(" + c + ") ") + name + "  " + (size / 1024) + " KB" + (c == 201 ? "" : "  " + Trim(resp)));
            }

            // 5) 收尾：remote 去掉 token（本来就没写 token）
            Console.WriteLine("[5/5] 完成");
            Console.WriteLine("仓库    https://github.com/" + _owner + "/" + _repo);
            Console.WriteLine("Release " + relUrl);
            return 0;
        }

        static bool Git(string[] argv, string basic, bool quiet)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Directory.GetCurrentDirectory();
            psi.EnvironmentVariables["GIT_SSL_NO_VERIFY"] = "1";
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_CONFIG_COUNT"] = "1";
            psi.EnvironmentVariables["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.EnvironmentVariables["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic;
            StringBuilder ab = new StringBuilder();
            foreach (string a in argv)
            {
                if (ab.Length > 0) ab.Append(' ');
                if (a.IndexOf(' ') >= 0 || a.Length == 0) ab.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else ab.Append(a);
            }
            psi.Arguments = ab.ToString();
            Process p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && !quiet)
            {
                Console.WriteLine("      git " + string.Join(" ", argv) + " 失败(" + p.ExitCode + "): " + Trim(e.Length > 0 ? e : o));
                return false;
            }
            return p.ExitCode == 0;
        }

        static string ReadStoredToken(out string user)
        {
            user = null;
            string[] targets = { "git:https://github.com", "git:https://github.com/", "git:https://api.github.com" };
            foreach (string t in targets)
            {
                IntPtr ptr;
                if (!CredRead(t, 1, 0, out ptr)) continue;
                try
                {
                    CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                    user = c.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(c.UserName);
                    if (c.CredentialBlobSize == 0) continue;
                    byte[] blob = new byte[c.CredentialBlobSize];
                    Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
                    string s = (blob.Length >= 2 && blob[1] == 0)
                        ? Encoding.Unicode.GetString(blob)
                        : Encoding.UTF8.GetString(blob);
                    s = s.TrimEnd('\0', ' ', '\r', '\n');
                    if (s.Length == 0) continue;
                    if (s.StartsWith("{"))
                    {
                        string at = JsonStr(s, "accessToken");
                        if (string.IsNullOrEmpty(at)) at = JsonStr(s, "password");
                        if (!string.IsNullOrEmpty(at)) return at;
                        continue;
                    }
                    return s;
                }
                catch { }
                finally { CredFree(ptr); }
            }
            return null;
        }

        static string Mask(string t)
        {
            if (t == null || t.Length < 8) return "??";
            return t.Substring(0, 4) + "…" + t.Substring(t.Length - 4) + " len=" + t.Length;
        }

        static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }

        static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append(' ');
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('\"');
            return sb.ToString();
        }

        static string JsonStr(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i) + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char ch = json[i];
                if (ch == '\\') { sb.Append(json[i + 1]); i += 2; continue; }
                if (ch == '"') break;
                sb.Append(ch); i++;
            }
            return sb.ToString();
        }

        static string JsonNum(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i) + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;
            return json.Substring(start, i - start);
        }

        static string Http(string method, string url, string token, string body, out int code)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "HwMon-Publisher";
            req.Accept = "application/vnd.github+json";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Timeout = 60000;
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            return Read(req, out code);
        }

        static string Upload(string url, string file, string token, out int code)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = "HwMon-Publisher";
            req.Headers["Authorization"] = "Bearer " + token;
            req.ContentType = "application/octet-stream";
            byte[] data = File.ReadAllBytes(file);
            req.ContentLength = data.Length;
            req.Timeout = 300000;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            return Read(req, out code);
        }

        static string Read(HttpWebRequest req, out int code)
        {
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    code = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    using (HttpWebResponse resp = (HttpWebResponse)we.Response)
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        code = (int)resp.StatusCode;
                        return sr.ReadToEnd();
                    }
                }
                code = 0;
                return we.Message;
            }
        }
    }
}
