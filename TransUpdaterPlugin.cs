// CU Trans Updater —— 汉化文件自动更新（BepInEx 5 插件）
// 启动时检查 GitHub 上的 latest.json，有新版本就下载并写进游戏的 Lang 目录。
// 界面只在主菜单（场景 PreGen）出现一行状态文字，游戏内不显示任何东西。
// 网络失败不影响游戏启动，只记日志 + 主菜单提示。

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CU_TransUpdater
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class TransUpdaterPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.tigerlin.cu.transupdater";
        public const string NAME = "CU Trans Updater";
        public const string VERSION = "0.3.1";

        // 主菜单场景名（取自 QoL.Unknown 的 UpdateChecker 实现）
        private const string MAIN_MENU_SCENE = "PreGen";

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgManifestUrl;
        internal static ConfigEntry<string> CfgChannel;
        internal static ConfigEntry<string> CfgTargetFileName;
        internal static ConfigEntry<int> CfgCheckIntervalHours;
        internal static ConfigEntry<int> CfgManifestTimeout;
        internal static ConfigEntry<int> CfgDownloadTimeout;
        internal static ConfigEntry<bool> CfgShowStatus;
        internal static ConfigEntry<bool> CfgClickToOpen;
        internal static ConfigEntry<string> CfgClickUrl;
        internal static ConfigEntry<int> CfgLanguage;
        internal static ConfigEntry<string> CfgStatusCorner;
        internal static ConfigEntry<int> CfgStatusOffsetX;
        internal static ConfigEntry<int> CfgStatusOffsetY;

        private static string LastCheckPath;
        private static TransUpdaterPlugin _instance;

        // 主菜单那一行文字的状态
        private static string _statusText = string.Empty;
        private static Color _statusColor = new Color(0.72f, 0.76f, 0.74f, 1f);
        private GameObject _uiObj;
        private TextMeshProUGUI _badgeTitle;
        private TextMeshProUGUI _badgeVersion;
        private GameObject _panel;
        private TextMeshProUGUI _panelBody;
        private bool _panelOpen;
        private bool _running;

        private enum Stage
        {
            Idle,
            Checking,
            Downloading,
            UpToDate,
            Updated,
            Failed,
            NoLangDir
        }
        private static Stage _stage = Stage.Idle;
        private static string _remoteVersion = string.Empty;
        private bool _updateStarted;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            _instance = this;

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "开启自动更新。关闭后不做任何网络请求。");
            CfgManifestUrl = Config.Bind("General", "ManifestUrl",
                "https://raw.githubusercontent.com/tigerLin0127/Casualties-Unknown-tigerLin_Custom_Trans/main/latest.json",
                "最新版指针文件（latest.json）的完整地址。");
            CfgChannel = Config.Bind("General", "Channel", "main",
                "更新通道。main = 主线版本；pausequotes = PauseQuotes 特别版。");
            CfgTargetFileName = Config.Bind("General", "TargetFileName", "tigerLin CustomTrans.json",
                "写进游戏 Lang 目录时使用的文件名（固定名，不含版本号）。");
            CfgCheckIntervalHours = Config.Bind("General", "CheckIntervalHours", 0,
                "两次检查之间的最小间隔（小时）。0 = 每次启动都检查。"
                + "注意 latest.json 只有 257 字节，真正贵的是 360KB 的汉化文件，"
                + "而文件下载本来就会被版本号挡住，所以默认不限制。");
            CfgManifestTimeout = Config.Bind("General", "ManifestTimeoutSeconds", 15,
                "拉取 latest.json 的超时（秒）。断网时靠它快速放弃；不要设太小，冷启动握手可能要几秒。");
            CfgDownloadTimeout = Config.Bind("General", "DownloadTimeoutSeconds", 60,
                "下载汉化文件的超时（秒）。文件约 360KB，首次冷连接可能要十几秒。");

            CfgShowStatus = Config.Bind("Interface", "ShowMainMenuStatus", true,
                "在主菜单显示一行汉化更新状态。游戏内不会显示任何东西。");
            CfgClickToOpen = Config.Bind("Interface", "ClickToOpenPage", true,
                "点击主菜单那行文字时打开页面。");
            CfgClickUrl = Config.Bind("Interface", "ClickUrl",
                "https://github.com/tigerLin0127/Casualties-Unknown-tigerLin_Custom_Trans",
                "点击时要打开的地址。");
            CfgLanguage = Config.Bind("Interface", "Language", 0,
                "界面语言：0 = English，1 = 中文。");
            CfgStatusCorner = Config.Bind("Interface", "StatusCorner", "BottomLeft",
                "状态文字贴在屏幕哪个角：TopLeft / TopRight / BottomLeft / BottomRight。");
            CfgStatusOffsetX = Config.Bind("Interface", "StatusOffsetX", 28,
                "状态文字距该角的水平偏移（像素），以屏幕中心为原点方向。");
            CfgStatusOffsetY = Config.Bind("Interface", "StatusOffsetY", 120,
                "状态文字距该角的垂直偏移（像素）。默认 120 是为了避开 QoL 自己在左下角的提示。");

            LastCheckPath = Path.Combine(Paths.PluginPath, "CU_TransUpdater.lastcheck");

            Logger.LogInfo(NAME + " v" + VERSION + " 已加载");
            Logger.LogInfo("游戏根目录: " + Paths.GameRootPath);

            SceneManager.sceneLoaded += OnSceneLoaded;

            if (!CfgEnabled.Value)
            {
                Logger.LogInfo("自动更新已关闭（Enabled = false）");
                _stage = Stage.Idle;
                SetStatus(T("Auto-update disabled", "自动更新已关闭"), _statusColor);
                return;
            }

            // 注意：不在这里发请求。Awake 阶段场景正在加载、主线程被占满，
            // 协程拿不到调度，而超时计时器照样在走 —— 会产生假超时。
            // 真正的启动放在第一个场景加载完成之后（见 OnSceneLoaded）。
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // ------------------------------------------------------------------ 主菜单界面

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 第一个场景加载完成后再启动网络流程：此时主线程空闲，协程能正常调度
            if (!_updateStarted && CfgEnabled.Value)
            {
                _updateStarted = true;
                StartCoroutine(RunUpdateWrapped(false));
            }

            DestroyUi();

            if (!CfgShowStatus.Value) return;
            if (scene.name != MAIN_MENU_SCENE) return;

            StartCoroutine(BuildUiDelayed());
        }

        private void DestroyUi()
        {
            if (_uiObj != null)
            {
                UnityEngine.Object.Destroy(_uiObj);
                _uiObj = null;
                _badgeTitle = null;
                _badgeVersion = null;
                _panel = null;
                _panelBody = null;
                _panelOpen = false;
            }
        }

        private IEnumerator BuildUiDelayed()
        {
            // 主菜单的 Canvas 不一定和场景同时就绪，重试若干帧
            Canvas canvas = null;
            for (int i = 0; i < 120 && canvas == null; i++)
            {
                if (SceneManager.GetActiveScene().name != MAIN_MENU_SCENE) yield break;
                canvas = FindMainMenuCanvas();
                if (canvas == null) yield return null;
            }
            if (canvas == null)
            {
                Logger.LogWarning("主菜单里找不到 Canvas，跳过状态显示");
                yield break;
            }

            // 等状态确定下来再建 UI（最多等 10 秒）
            float waitStart = Time.realtimeSinceStartup;
            while (_stage == Stage.Idle || _stage == Stage.Checking || _stage == Stage.Downloading)
            {
                if (Time.realtimeSinceStartup - waitStart > 10f) break;
                yield return null;
            }

            BuildUi(canvas);
        }

        private Canvas FindMainMenuCanvas()
        {
            GameObject go = GameObject.Find("Canvas");
            if (go != null)
            {
                Canvas c = go.GetComponent<Canvas>();
                if (c != null) return c;
            }
            Canvas[] all = Resources.FindObjectsOfTypeAll<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.activeInHierarchy) return all[i];
            }
            return null;
        }

        private void BuildUi(Canvas canvas)
        {
            TMP_FontAsset font = FindGameFont();

            _uiObj = new GameObject("CUTransUpdaterUI", typeof(RectTransform));
            _uiObj.transform.SetParent(canvas.transform, false);
            RectTransform rootRect = _uiObj.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 0f);
            rootRect.anchorMax = new Vector2(1f, 1f);
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            float ox = CfgStatusOffsetX.Value;
            float oy = CfgStatusOffsetY.Value;

            const float badgeW = 132f;
            const float badgeH = 58f;

            // ---- 徽标 ----
            GameObject badge = MakeBox(_uiObj.transform, "Badge",
                new Vector2(ox, oy), new Vector2(badgeW, badgeH),
                new Color(0.055f, 0.055f, 0.055f, 1f), new Color(0.42f, 0.42f, 0.42f, 1f));

            _badgeTitle = MakeText(badge.transform, "Title",
                new Vector2(11f, -7f), new Vector2(110f, 26f), 20f,
                TextAlignmentOptions.TopLeft, new Color(0.9f, 0.22f, 0.22f, 1f), font);
            _badgeTitle.text = T("TRANS", "汉化");
            _badgeTitle.color = _statusColor;   // 检查可能在 UI 建好前就结束了，这里补一次

            _badgeVersion = MakeText(badge.transform, "Version",
                new Vector2(11f, -32f), new Vector2(110f, 20f), 14f,
                TextAlignmentOptions.TopLeft, new Color(0.82f, 0.82f, 0.82f, 1f), font);
            _badgeVersion.text = "v?";

            Button badgeBtn = badge.GetComponent<Button>();
            if (badgeBtn == null) badgeBtn = badge.AddComponent<Button>();
            badgeBtn.transition = Selectable.Transition.None;
            badgeBtn.onClick.AddListener(TogglePanel);

            // ---- 展开面板 ----
            GameObject panel = MakeBox(_uiObj.transform, "Panel",
                new Vector2(ox, oy + badgeH + 8f), new Vector2(400f, 212f),
                new Color(0.055f, 0.055f, 0.055f, 1f), new Color(0.42f, 0.42f, 0.42f, 1f));
            _panel = panel;

            MakeText(panel.transform, "PanelTitle",
                new Vector2(14f, -9f), new Vector2(340f, 26f), 18f,
                TextAlignmentOptions.TopLeft, new Color(0.9f, 0.9f, 0.9f, 1f), font).text = "CU Trans Updater";

            _panelBody = MakeText(panel.transform, "PanelBody",
                new Vector2(14f, -40f), new Vector2(372f, 100f), 14f,
                TextAlignmentOptions.TopLeft, new Color(0.78f, 0.78f, 0.78f, 1f), font);

            MakeButton(panel.transform, T("Check now", "立即检查"),
                new Vector2(14f, -168f), new Vector2(120f, 32f), OnManualCheck, font);
            MakeButton(panel.transform, T("Open page", "打开页面"),
                new Vector2(142f, -168f), new Vector2(120f, 32f), OnStatusClicked, font);
            MakeButton(panel.transform, T("Close", "关闭"),
                new Vector2(270f, -168f), new Vector2(116f, 32f), ClosePanel, font);

            panel.SetActive(false);
            _panelOpen = false;

            RefreshPanel();
            Logger.LogInfo("主菜单界面已创建（徽标 + 面板）");
        }

        // ---------------- UI 构件 ----------------

        private GameObject MakeBox(Transform parent, string name, Vector2 pos, Vector2 size, Color fill, Color border)
        {
            GameObject outer = new GameObject(name, typeof(RectTransform), typeof(Image));
            outer.transform.SetParent(parent, false);

            RectTransform r = outer.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 0f);
            r.anchorMax = new Vector2(0f, 0f);
            r.pivot = new Vector2(0f, 0f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;

            Image borderImg = outer.GetComponent<Image>();
            borderImg.color = border;
            borderImg.raycastTarget = true;

            GameObject inner = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            inner.transform.SetParent(outer.transform, false);
            RectTransform ir = inner.GetComponent<RectTransform>();
            ir.anchorMin = Vector2.zero;
            ir.anchorMax = Vector2.one;
            ir.offsetMin = new Vector2(2f, 2f);
            ir.offsetMax = new Vector2(-2f, -2f);
            Image fillImg = inner.GetComponent<Image>();
            fillImg.color = fill;
            fillImg.raycastTarget = false;

            return outer;
        }

        private TextMeshProUGUI MakeText(Transform parent, string name, Vector2 pos, Vector2 size,
            float fontSize, TextAlignmentOptions align, Color color, TMP_FontAsset font)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;

            TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            return t;
        }

        private void MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick, TMP_FontAsset font)
        {
            GameObject go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.16f, 0.16f, 0.95f);

            TextMeshProUGUI t = MakeText(go.transform, "Label", Vector2.zero, size, 15f,
                TextAlignmentOptions.Center, new Color(0.92f, 0.92f, 0.92f, 1f), font);
            RectTransform tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.pivot = new Vector2(0.5f, 0.5f);
            tr.anchoredPosition = Vector2.zero;
            tr.sizeDelta = Vector2.zero;
            t.text = label;

            Button b = go.GetComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.targetGraphic = img;
            b.onClick.AddListener(delegate { onClick(); });
        }

        // ---------------- 面板交互 ----------------

        private void TogglePanel()
        {
            if (_panel == null) return;
            _panelOpen = !_panelOpen;
            _panel.SetActive(_panelOpen);
            if (_panelOpen) RefreshPanel();
        }

        private void ClosePanel()
        {
            _panelOpen = false;
            if (_panel != null) _panel.SetActive(false);
        }

        private void RefreshPanel()
        {
            if (_badgeVersion != null)
                _badgeVersion.text = "v" + ReadInstalledVersion();

            if (_panelBody == null || !_panelOpen) return;

            string local = ReadInstalledVersion();
            string remote = _remoteVersion.Length > 0 ? ("v" + _remoteVersion) : "—";
            string last = ReadLastCheckText();

            StringBuilder sb = new StringBuilder();
            sb.Append(T("Installed", "当前版本")).Append("   v").Append(local).Append('\n');
            sb.Append(T("Latest", "远端版本")).Append("      ").Append(remote).Append('\n');
            sb.Append(T("Last check", "上次检查")).Append("   ").Append(last).Append('\n');
            sb.Append(T("Status", "状态")).Append("        ").Append(_statusText);

            _panelBody.text = sb.ToString();
        }

        private void OnManualCheck()
        {
            Logger.LogInfo("用户点击「立即检查」");
            if (_running)
            {
                Logger.LogInfo("已有检查在进行中，忽略");
                return;
            }
            StartCoroutine(RunUpdateWrapped(true));
        }

        private static string ReadLastCheckText()
        {
            string raw = ReadText(LastCheckPath).Trim();
            long ticks;
            if (raw.Length == 0 || !long.TryParse(raw, out ticks)) return "?";
            try
            {
                DateTime t = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                return t.ToString("MM-dd HH:mm");
            }
            catch { return "?"; }
        }

        private TMP_FontAsset FindGameFont()
        {
            TMP_FontAsset fallback = null;
            TextMeshProUGUI[] all = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].font == null) continue;
                if (all[i].font.name != null && all[i].font.name.Contains("Retro GamingPix"))
                    return all[i].font;
                if (fallback == null) fallback = all[i].font;
            }
            if (fallback != null) return fallback;
            return TMP_Settings.defaultFontAsset;
        }

        private void OnStatusClicked()
        {
            string url = CfgClickUrl.Value;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Application.OpenURL(url);
            }
            catch (Exception e)
            {
                Logger.LogWarning("打开链接失败: " + e.Message);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _statusText = text;
            _statusColor = color;

            if (_badgeTitle != null) _badgeTitle.color = color;
            if (_badgeVersion != null) _badgeVersion.text = "v" + ReadInstalledVersion();

            RefreshPanel();
        }

        /// <summary>包一层，保证 _running 一定被复位（RunUpdate 里有多个 yield break）。</summary>
        private IEnumerator RunUpdateWrapped(bool force)
        {
            _running = true;
            yield return RunUpdate(force);
            _running = false;
        }

        // ------------------------------------------------------------------ 更新流程

        private IEnumerator RunUpdate(bool force)
        {
            float t0 = Time.realtimeSinceStartup;

            if (!force && !ShouldCheckNow())
            {
                Logger.LogInfo("距上次检查不足 " + CfgCheckIntervalHours.Value + " 小时，本次跳过");
                string local = ReadInstalledVersion();
                _stage = Stage.UpToDate;
                SetStatus(T("Translation v" + local + " (checked recently)", "汉化 v" + local + "（近期已检查）"),
                    _statusColor);
                yield break;
            }

            string langDir = FindLangDir(Paths.GameRootPath);
            if (langDir == null)
            {
                Logger.LogWarning("找不到游戏的 Lang 目录，放弃自动更新");
                _stage = Stage.NoLangDir;
                SetStatus(T("Translation: Lang folder not found", "汉化：找不到 Lang 目录"),
                    new Color(1f, 0.65f, 0.35f, 1f));
                yield break;
            }
            Logger.LogInfo("Lang 目录: " + langDir);

            string targetPath = Path.Combine(langDir, CfgTargetFileName.Value);
            string versionPath = targetPath + ".version";
            string localVer = ReadText(versionPath).Trim();
            Logger.LogInfo("本地已装版本: " + (localVer.Length == 0 ? "(未知)" : localVer));

            _stage = Stage.Checking;
            SetStatus(T("Checking translation update...", "正在检查汉化更新…"), _statusColor);

            byte[] manifestBytes = null;
            yield return Fetch(CfgManifestUrl.Value, CfgManifestTimeout.Value,
                delegate (bool ok, byte[] bytes) { if (ok) manifestBytes = bytes; });

            if (manifestBytes == null)
            {
                Logger.LogWarning("拉取 latest.json 失败，本次不更新");
                _stage = Stage.Failed;
                SetStatus(T("Translation update check failed (offline?)", "汉化检查失败（离线？）"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            JObject manifest = null;
            try { manifest = JObject.Parse(Encoding.UTF8.GetString(manifestBytes)); }
            catch (Exception e)
            {
                Logger.LogError("latest.json 解析失败: " + e.Message);
                _stage = Stage.Failed;
                SetStatus(T("Translation: bad manifest", "汉化：manifest 格式错误"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            JObject entry = SelectChannel(manifest, CfgChannel.Value);
            if (entry == null)
            {
                Logger.LogWarning("latest.json 里找不到通道 '" + CfgChannel.Value + "'");
                _stage = Stage.Failed;
                SetStatus(T("Translation: channel not found", "汉化：找不到更新通道"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            string remoteVer = Str(entry, "version");
            string remoteFile = Str(entry, "file");
            string remoteSha = Str(entry, "sha256");
            _remoteVersion = remoteVer;

            Logger.LogInfo("远端版本: " + remoteVer + "  文件: " + remoteFile);

            if (remoteVer.Length == 0 || remoteFile.Length == 0)
            {
                Logger.LogWarning("latest.json 缺少 version 或 file 字段");
                _stage = Stage.Failed;
                SetStatus(T("Translation: bad manifest", "汉化：manifest 格式错误"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            if (CompareVersion(remoteVer, localVer) <= 0)
            {
                Logger.LogInfo("已是最新，无需更新");
                TouchLastCheck();
                _stage = Stage.UpToDate;
                SetStatus(T("Translation v" + remoteVer + " is up to date", "汉化 v" + remoteVer + " 已是最新"),
                    new Color(0.62f, 0.78f, 0.68f, 1f));
                yield break;
            }

            _stage = Stage.Downloading;
            SetStatus(T("Downloading translation v" + remoteVer + "...", "正在下载汉化 v" + remoteVer + "…"),
                _statusColor);

            byte[] data = null;
            yield return Fetch(BuildAssetUrl(CfgManifestUrl.Value, remoteFile), CfgDownloadTimeout.Value,
                delegate (bool ok, byte[] d) { if (ok) data = d; });

            if (data == null || data.Length < 100)
            {
                Logger.LogWarning("下载失败或内容过短");
                _stage = Stage.Failed;
                SetStatus(T("Translation download failed", "汉化下载失败"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            if (remoteSha.Length > 0)
            {
                string actual = Sha256Hex(data);
                if (!actual.Equals(remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("SHA256 不匹配，放弃写入。期望 " + remoteSha + " 实际 " + actual);
                    _stage = Stage.Failed;
                    SetStatus(T("Translation: checksum mismatch", "汉化：校验失败"),
                        new Color(1f, 0.55f, 0.45f, 1f));
                    yield break;
                }
            }

            try { JToken.Parse(Encoding.UTF8.GetString(data)); }
            catch (Exception e)
            {
                Logger.LogError("下载内容不是合法 JSON，放弃写入: " + e.Message);
                _stage = Stage.Failed;
                SetStatus(T("Translation: downloaded file is not valid JSON", "汉化：下载内容不是合法 JSON"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            try
            {
                WriteAtomic(targetPath, data, versionPath, remoteVer);
            }
            catch (Exception e)
            {
                Logger.LogError("写入失败: " + e.ToString());
                _stage = Stage.Failed;
                SetStatus(T("Translation: write failed", "汉化：写入失败"),
                    new Color(1f, 0.55f, 0.45f, 1f));
                yield break;
            }

            TouchLastCheck();
            _stage = Stage.Updated;
            Logger.LogInfo("已更新到 v" + remoteVer + " -> " + targetPath);
            SetStatus(T("Translation updated to v" + remoteVer + " (restart to apply)",
                "汉化已更新到 v" + remoteVer + "（重启生效）"),
                new Color(0.55f, 0.92f, 0.66f, 1f));

            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Logger.LogInfo("自动更新流程结束，耗时 " + ((int)ms) + " ms");
        }

        // ------------------------------------------------------------------ 网络（协程 + 手动超时）

        /// <summary>
        /// 先走 UnityWebRequest；不成功再退回 .NET HttpWebRequest。
        /// 两条走的是完全不同的 TLS 栈，一条走不通不代表另一条也不行。
        /// </summary>
        private IEnumerator Fetch(string url, int timeoutSeconds, Action<bool, byte[]> done)
        {
            bool ok = false;
            byte[] bytes = null;

            yield return FetchUnity(url, timeoutSeconds,
                delegate (bool o, byte[] b, string err) { ok = o; bytes = b; });

            if (ok)
            {
                done(true, bytes);
                yield break;
            }

            Logger.LogWarning("UnityWebRequest 未成功，改用 .NET HttpWebRequest 重试: " + url);
            yield return FetchDotNet(url, timeoutSeconds,
                delegate (bool o, byte[] b, string err) { ok = o; bytes = b; });

            done(ok, bytes);
        }

        private IEnumerator FetchUnity(string url, int timeoutSeconds, Action<bool, byte[], string> done)
        {
            UnityWebRequest www = null;
            try
            {
                www = UnityWebRequest.Get(url);
            }
            catch (Exception e)
            {
                done(false, null, "构造失败: " + e.Message);
                yield break;
            }

            using (www)
            {
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("User-Agent", "CU-TransUpdater/" + VERSION);

                UnityWebRequestAsyncOperation op = null;
                try { op = www.SendWebRequest(); }
                catch (Exception e)
                {
                    done(false, null, "发送失败: " + e.Message);
                    yield break;
                }

                float start = Time.realtimeSinceStartup;
                while (!op.isDone)
                {
                    if (Time.realtimeSinceStartup - start > timeoutSeconds)
                    {
                        int ms = (int)((Time.realtimeSinceStartup - start) * 1000f);
                        string err = string.Empty;
                        try { err = www.error; } catch { }
                        try { www.Abort(); } catch { }
                        done(false, null, "超时 " + ms + "ms，www.error=" + (string.IsNullOrEmpty(err) ? "(空)" : err));
                        yield break;
                    }
                    yield return null;
                }

                if (!string.IsNullOrEmpty(www.error))
                {
                    done(false, null, "error=" + www.error);
                    yield break;
                }

                byte[] data = www.downloadHandler == null ? null : www.downloadHandler.data;
                Logger.LogInfo("UnityWebRequest 成功: " + url + "  "
                    + (int)((Time.realtimeSinceStartup - start) * 1000f) + "ms  "
                    + (data == null ? 0 : data.Length) + " 字节");
                done(data != null, data, null);
            }
        }

        private class NetResult
        {
            public bool Done;
            public bool Ok;
            public byte[] Data;
            public string Error;
        }

        private IEnumerator FetchDotNet(string url, int timeoutSeconds, Action<bool, byte[], string> done)
        {
            NetResult res = new NetResult();

            Thread worker = new Thread(delegate ()
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = timeoutSeconds * 1000;
                    req.ReadWriteTimeout = timeoutSeconds * 1000;
                    req.UserAgent = "CU-TransUpdater/" + VERSION;
                    req.AllowAutoRedirect = true;
                    try { req.ServicePoint.Expect100Continue = false; } catch { }

                    using (WebResponse resp = req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        res.Data = ms.ToArray();
                    }
                    res.Ok = true;
                }
                catch (Exception e)
                {
                    res.Error = e.Message;
                }
                finally
                {
                    res.Done = true;
                }
            });
            worker.IsBackground = true;
            worker.Start();

            float start = Time.realtimeSinceStartup;
            while (!res.Done)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds + 5f)
                {
                    done(false, null, ".NET 通道超时");
                    yield break;
                }
                yield return null;
            }

            if (res.Ok && res.Data != null)
            {
                Logger.LogInfo(".NET 通道成功: " + url + "  " + res.Data.Length + " 字节");
                done(true, res.Data, null);
            }
            else
            {
                done(false, null, ".NET 通道失败: " + res.Error);
            }
        }

        // ------------------------------------------------------------------ 文件与版本

        private bool ShouldCheckNow()
        {
            int hours = CfgCheckIntervalHours.Value;
            if (hours <= 0) return true;

            string raw = ReadText(LastCheckPath).Trim();
            if (raw.Length == 0) return true;

            long ticks;
            if (!long.TryParse(raw, out ticks)) return true;
            DateTime last = new DateTime(ticks, DateTimeKind.Utc);
            return (DateTime.UtcNow - last).TotalHours >= hours;
        }

        private static void TouchLastCheck()
        {
            try { File.WriteAllText(LastCheckPath, DateTime.UtcNow.Ticks.ToString(), Encoding.UTF8); }
            catch { }
        }

        private static string ReadInstalledVersion()
        {
            try
            {
                string langDir = FindLangDir(Paths.GameRootPath);
                if (langDir == null) return "?";
                string v = ReadText(Path.Combine(langDir, CfgTargetFileName.Value + ".version")).Trim();
                return v.Length == 0 ? "?" : v;
            }
            catch { return "?"; }
        }

        /// <summary>从游戏根目录往下找 *\*_Data\Lang，找不到再退化为任意 *\Lang。</summary>
        internal static string FindLangDir(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return null;

            string[] top;
            try { top = Directory.GetDirectories(gameRoot); }
            catch { return null; }

            for (int i = 0; i < top.Length; i++)
            {
                string name = Path.GetFileName(top[i]);
                if (!name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = Path.Combine(top[i], "Lang");
                if (Directory.Exists(candidate)) return candidate;
            }
            for (int i = 0; i < top.Length; i++)
            {
                string candidate = Path.Combine(top[i], "Lang");
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static JObject SelectChannel(JObject manifest, string channel)
        {
            JObject channels = manifest["channels"] as JObject;
            if (channels == null) return manifest;

            JObject picked = channels[channel] as JObject;
            if (picked != null) return picked;

            string[] names = { "main", "pausequotes" };
            for (int i = 0; i < names.Length; i++)
            {
                JObject fallback = channels[names[i]] as JObject;
                if (fallback != null) return fallback;
            }
            return null;
        }

        private static string Str(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return string.Empty;
            return t.ToString().Trim();
        }

        internal static string BuildAssetUrl(string manifestUrl, string fileName)
        {
            int cut = manifestUrl.LastIndexOf('/');
            if (cut < 0) return manifestUrl;
            return manifestUrl.Substring(0, cut + 1) + Uri.EscapeDataString(fileName);
        }

        private static void WriteAtomic(string targetPath, byte[] data, string versionPath, string version)
        {
            string tmp = targetPath + ".tmp";
            File.WriteAllBytes(tmp, data);

            if (File.Exists(targetPath))
            {
                string bak = targetPath + ".bak";
                try
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Copy(targetPath, bak, true);
                }
                catch { }
                File.Delete(targetPath);
            }

            File.Move(tmp, targetPath);
            File.WriteAllText(versionPath, version, new UTF8Encoding(false));
        }

        internal static int CompareVersion(string a, string b)
        {
            int[] x = ParseVersion(a);
            int[] y = ParseVersion(b);
            int len = Math.Max(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < x.Length ? x[i] : 0;
                int vb = i < y.Length ? y[i] : 0;
                if (va < vb) return -1;
                if (va > vb) return 1;
            }
            return 0;
        }

        private static int[] ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return new int[0];
            s = s.Trim().TrimStart('v', 'V');
            string[] parts = s.Split('.');
            int[] result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n = 0;
                int.TryParse(parts[i], out n);
                result[i] = n;
            }
            return result;
        }

        internal static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { return string.Empty; }
        }

        /// <summary>0 = English，1 = 中文。</summary>
        private static string T(string en, string cn)
        {
            if (CfgLanguage != null && CfgLanguage.Value == 1) return cn;
            return en;
        }
    }
}
