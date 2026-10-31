using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwMonSetup
{
    internal sealed class Installer
    {
        public const string AppName = "硬件监控浮窗";
        public const string AppFile = "HardwareMonitor.exe";
        public const string Version = "1.0.0";
        public const string Publisher = "HwMon";
        public const string RunValue = "HwMonOverlay";
        public const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HwMonOverlay";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Payload = "HwMonPayload.exe";

        public static string DefaultDir
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                return Path.Combine(Path.Combine(baseDir, "Programs"), "HardwareMonitor");
            }
        }

        public static string SelfPath
        {
            get { try { return Assembly.GetExecutingAssembly().Location; } catch { return Application.ExecutablePath; } }
        }

        public static bool Install(string dir, bool desktop, bool startup, bool shortcuts, out string error)
        {
            error = null;
            try
            {
                if (dir == null || dir.Trim().Length == 0) throw new Exception("安装路径为空");
                dir = Path.GetFullPath(dir);
                try { Directory.CreateDirectory(dir); }
                catch (Exception de) { throw new Exception("无法创建安装目录 " + dir + " : " + de.Message); }

                // 1) 释放主程序
                Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream(Payload);
                if (src == null) throw new Exception("安装包负载缺失 (" + Payload + ")");
                string appPath = Path.Combine(dir, AppFile);
                using (src)
                using (FileStream fs = new FileStream(appPath, FileMode.Create, FileAccess.Write))
                    src.CopyTo(fs);

                // 2) 复制卸载器（自身副本）
                string uninst = Path.Combine(dir, "uninstall.exe");
                try { File.Copy(SelfPath, uninst, true); } catch { }

                // 3) 快捷方式（受限环境下失败不阻断安装）
                if (shortcuts)
                {
                    try
                    {
                        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "硬件监控浮窗.lnk");
                        CreateShortcut(startMenu, appPath, dir);
                        if (desktop)
                        {
                            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "硬件监控浮窗.lnk");
                            CreateShortcut(desk, appPath, dir);
                        }
                    }
                    catch { }
                }

                // 4) 开机自启（可选）
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (k != null)
                        {
                            if (startup) k.SetValue(RunValue, "\"" + appPath + "\"");
                            else k.DeleteValue(RunValue, false);
                        }
                    }
                }
                catch { }

                // 5) 卸载信息（可选）
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstKey))
                    {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", AppName);
                        k.SetValue("DisplayVersion", Version);
                        k.SetValue("Publisher", Publisher);
                        k.SetValue("InstallLocation", dir);
                        k.SetValue("DisplayIcon", appPath);
                        k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall");
                        k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall --silent");
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        try
                        {
                            FileInfo fi = new FileInfo(appPath);
                            k.SetValue("EstimatedSize", (int)(fi.Length / 1024), RegistryValueKind.DWord);
                        }
                        catch { }
                        }
                    }
                }
                catch { }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Uninstall(string dir, out string error)
        {
            error = null;
            try
            {
                if (dir == null || dir.Trim().Length == 0) dir = DefaultDir;
                dir = Path.GetFullPath(dir);
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "硬件监控浮窗.lnk")); } catch { }
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "硬件监控浮窗.lnk")); } catch { }
                try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(RunValue, false); } } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
                // 自删除：延迟后删除整个目录
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 > nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static void CreateShortcut(string lnkPath, string target, string workDir)
        {
            object shell = null, sc = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { workDir });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { AppName + " - 硬件利用率浮窗" });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
            finally
            {
                if (sc != null) try { Marshal.ReleaseComObject(sc); } catch { }
                if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        public static string InstallDirFromRegistry()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstKey, false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallLocation");
                        if (v != null) return Convert.ToString(v);
                    }
                }
            }
            catch { }
            return DefaultDir;
        }
    }

    internal sealed class SetupForm : Form
    {
        readonly TextBox _path = new TextBox();
        readonly CheckBox _desktop = new CheckBox();
        readonly CheckBox _startup = new CheckBox();
        readonly CheckBox _run = new CheckBox();
        readonly ProgressBar _bar = new ProgressBar();
        readonly Label _status = new Label();
        readonly Button _ok = new Button();
        readonly Button _cancel = new Button();
        bool _installing;
        bool _done;

        public SetupForm()
        {
            Text = Installer.AppName + " 安装程序";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 330);
            BackColor = Color.FromArgb(246, 248, 251);
            Font = new Font("Microsoft YaHei UI", 9f);

            Panel head = new Panel();
            head.Dock = DockStyle.Top; head.Height = 72; head.BackColor = Color.FromArgb(18, 22, 30);
            Label t = new Label();
            t.Text = "硬件监控浮窗"; t.ForeColor = Color.White; t.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            t.AutoSize = true; t.Location = new Point(20, 12);
            Label s = new Label();
            s.Text = "轻量硬件利用率检测器 · 版本 " + Installer.Version + " · 逐像素透明浮窗（CPU / 内存 / GPU / 磁盘 / 网络）";
            s.ForeColor = Color.FromArgb(150, 162, 178); s.AutoSize = true; s.Location = new Point(21, 44);
            head.Controls.Add(t); head.Controls.Add(s);
            Controls.Add(head);

            Label lp = new Label();
            lp.Text = "安装位置"; lp.AutoSize = true; lp.Location = new Point(20, 92);
            lp.ForeColor = Color.FromArgb(60, 68, 82);
            _path.Location = new Point(20, 112); _path.Width = 420; _path.Text = Installer.DefaultDir;
            Button browse = new Button();
            browse.Text = "浏览..."; browse.Location = new Point(448, 111); browse.Width = 54;
            browse.Click += delegate
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.Description = "选择安装位置";
                if (Directory.Exists(_path.Text)) d.SelectedPath = _path.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(d.SelectedPath, "HardwareMonitor");
            };

            _desktop.Text = "创建桌面快捷方式"; _desktop.Location = new Point(20, 152); _desktop.AutoSize = true; _desktop.Checked = true;
            _startup.Text = "开机自动启动（后台浮窗）"; _startup.Location = new Point(20, 176); _startup.AutoSize = true;
            _run.Text = "安装完成后立即运行"; _run.Location = new Point(20, 200); _run.AutoSize = true; _run.Checked = true;

            _bar.Location = new Point(20, 236); _bar.Width = 482; _bar.Height = 14; _bar.Style = ProgressBarStyle.Continuous;
            _status.Location = new Point(20, 256); _status.AutoSize = true; _status.ForeColor = Color.FromArgb(90, 100, 115);
            _status.Text = "准备安装。";

            _ok.Text = "安装"; _ok.Location = new Point(326, 285); _ok.Width = 84; _ok.Height = 30;
            _ok.Click += delegate { if (_done) { Close(); return; } StartInstall(); };
            _cancel.Text = "取消"; _cancel.Location = new Point(418, 285); _cancel.Width = 84; _cancel.Height = 30;
            _cancel.Click += delegate { Close(); };

            Controls.Add(lp); Controls.Add(_path); Controls.Add(browse);
            Controls.Add(_desktop); Controls.Add(_startup); Controls.Add(_run);
            Controls.Add(_bar); Controls.Add(_status); Controls.Add(_ok); Controls.Add(_cancel);
            AcceptButton = _ok; CancelButton = _cancel;
        }

        void StartInstall()
        {
            if (_installing) return;
            _installing = true;
            _ok.Enabled = false; _cancel.Enabled = false; _path.Enabled = false;
            string dir = _path.Text;
            bool desktop = _desktop.Checked, startup = _startup.Checked, run = _run.Checked;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                SetProgress(20, "正在写入程序文件 ...");
                string err;
                bool ok = Installer.Install(dir, desktop, startup, true, out err);
                System.Threading.Thread.Sleep(250);
                SetProgress(80, ok ? "正在创建快捷方式与注册信息 ..." : "安装失败");
                System.Threading.Thread.Sleep(250);
                SetProgress(100, ok ? "安装完成。" : "安装失败：" + err);
                BeginInvoke((MethodInvoker)delegate
                {
                    _installing = false; _done = ok;
                    _ok.Enabled = true; _ok.Text = ok ? "完成" : "重试";
                    _cancel.Text = ok ? "关闭" : "取消";
                    if (ok && run)
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(dir, Installer.AppFile));
                            psi.WorkingDirectory = dir;
                            psi.UseShellExecute = true;
                            Process.Start(psi);
                        }
                        catch (Exception ex) { _status.Text = "已安装，但启动失败：" + ex.Message; return; }
                        Close();
                    }
                });
            });
        }

        void SetProgress(int v, string text)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate { _bar.Value = Math.Max(0, Math.Min(100, v)); _status.Text = text; });
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        static void LogLine(string s)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine;
            string[] cands = new string[]
            {
                Environment.GetEnvironmentVariable("HWMON_SETUP_LOG"),
                Path.Combine(Path.GetDirectoryName(Installer.SelfPath) ?? ".", "setup.log"),
                Path.Combine(Path.GetTempPath(), "HwMonSetup.log")
            };
            for (int i = 0; i < cands.Length; i++)
            {
                if (string.IsNullOrEmpty(cands[i])) continue;
                try { File.AppendAllText(cands[i], line); return; }
                catch { }
            }
        }

        static void Main(string[] args)
        {
            string dir = null;
            bool silent = false, uninstall = false, noShortcuts = false, desktop = true, startup = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--dir" && i + 1 < args.Length) dir = args[++i];
                else if (a == "--silent") silent = true;
                else if (a == "--uninstall") uninstall = true;
                else if (a == "--no-shortcuts") noShortcuts = true;
                else if (a == "--no-desktop") desktop = false;
                else if (a == "--startup") startup = true;
            }
            if (dir == null || dir.Trim().Length == 0) dir = Installer.DefaultDir;
            if (uninstall)
            {
                string uErr;
                string target = Installer.InstallDirFromRegistry();
                bool uOk = Installer.Uninstall(target, out uErr);
                LogLine("uninstall dir=" + target + " -> " + (uOk ? "ok" : uErr));
                Environment.Exit(uOk ? 0 : 1);
                return;
            }
            if (silent)
            {
                string err;
                bool ok = Installer.Install(dir, desktop, startup, !noShortcuts, out err);
                LogLine("install dir=" + dir + " -> " + (ok ? "ok" : ("failed: " + err)));
                Environment.Exit(ok ? 0 : 1);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }
}
