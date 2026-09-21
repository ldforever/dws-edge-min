using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>B9 鉴权策略（runtime\config\auth.json，支持热加载）。</summary>
    public sealed class AuthOptions
    {
        /// <summary>总开关。关掉 = 所有人可访问所有接口（只在完全隔离的调试环境用）。</summary>
        public bool enabled { get; set; } = true;

        /// <summary>读接口（包裹/统计/历史/图片/监控）是否也要登录。默认不用 —— 现场大屏与第三方读数据不该被卡住。</summary>
        public bool protectRead { get; set; } = false;

        /// <summary>是否允许用 X-Api-Key 服务令牌访问（脚本、上位机、采集侧集成用）。</summary>
        public bool allowServiceKey { get; set; } = true;

        /// <summary>服务令牌（首次运行自动生成）。</summary>
        public string serviceKey { get; set; } = "";

        /// <summary>服务令牌的角色：admin / operator。</summary>
        public string serviceKeyRole { get; set; } = "admin";

        /// <summary>连续密码错误多少次锁定账号。</summary>
        public int maxFailures { get; set; } = 5;

        /// <summary>锁定时长（分钟）。</summary>
        public int lockMinutes { get; set; } = 15;

        /// <summary>失败计数的统计窗口（分钟）：超过窗口没再失败就自动清零。</summary>
        public int failureWindowMinutes { get; set; } = 10;

        /// <summary>登录会话有效期（分钟）。</summary>
        public int sessionMinutes { get; set; } = 480;
    }

    /// <summary>一个账号（存 runtime\config\users.json；只存 PBKDF2 哈希，不存明文）。</summary>
    public sealed class AuthUser
    {
        public string username { get; set; }
        /// <summary>admin（全部）/ operator（业务与配置）/ viewer（只读）。</summary>
        public string role { get; set; } = "operator";
        public bool enabled { get; set; } = true;
        public string algorithm { get; set; } = "PBKDF2-SHA256";
        public int iterations { get; set; }
        public string salt { get; set; }
        public string hash { get; set; }
        public string createdAt { get; set; }
        public string updatedAt { get; set; }
        /// <summary>初始密码/被管理员重置后为 true：登录后界面会提示尽快改密。</summary>
        public bool mustChangePassword { get; set; }
        public string note { get; set; }

        // ---- 失败限制与最近登录 ----
        public int failedCount { get; set; }
        public long lockedUntilMs { get; set; }
        public string lastLoginAt { get; set; }
        public string lastLoginIp { get; set; }
        public string lastFailureAt { get; set; }
    }

    /// <summary>给界面的账号视图（不含哈希与盐）。</summary>
    public sealed class AuthUserView
    {
        public string username { get; set; }
        public string role { get; set; }
        public bool enabled { get; set; }
        public string createdAt { get; set; }
        public string updatedAt { get; set; }
        public bool mustChangePassword { get; set; }
        public string note { get; set; }
        public bool locked { get; set; }
        public int lockedSeconds { get; set; }
        public int failedCount { get; set; }
        public string lastLoginAt { get; set; }
        public string lastLoginIp { get; set; }
        public string lastFailureAt { get; set; }
    }

    /// <summary>一条登录会话（进程内存；平台重启后需要重新登录）。</summary>
    public sealed class AuthSession
    {
        public string token { get; set; }
        public string username { get; set; }
        public string role { get; set; }
        public string ip { get; set; }
        public string agent { get; set; }
        public long createdAtMs { get; set; }
        public long expiresAtMs { get; set; }
        public long lastSeenMs { get; set; }
    }

    /// <summary>登录/鉴权审计事件（落盘 data\auth-events-yyyyMMdd.jsonl）。</summary>
    public sealed class AuthEventRecord
    {
        public string time { get; set; }
        public long atMs { get; set; }
        /// <summary>login-ok / login-fail / login-locked / logout / password-change / user-add / user-update / user-delete / config-change</summary>
        public string kind { get; set; }
        public string username { get; set; }
        public string ip { get; set; }
        public string detail { get; set; }
    }

    /// <summary>登录结果。</summary>
    public sealed class LoginResult
    {
        public bool ok { get; set; }
        public string code { get; set; }
        public string error { get; set; }
        /// <summary>还能错几次（-1 表示不适用）。</summary>
        public int remainingAttempts { get; set; } = -1;
        public int lockedSeconds { get; set; }
        public AuthUserView user { get; set; }
        public string token { get; set; }
        public long expiresAtMs { get; set; }
        public int sessionMinutes { get; set; }
    }

    /// <summary>访问控制结果。</summary>
    public sealed class AccessCheck
    {
        /// <summary>allowed / unauthenticated / forbidden</summary>
        public string kind { get; set; }
        public string error { get; set; }
        public AuthSession session { get; set; }
    }

    /// <summary>登录请求。</summary>
    public sealed class LoginRequest
    {
        public string username { get; set; }
        public string password { get; set; }
    }

    /// <summary>改密请求（username 留空 = 改自己的密码；填了别人 = 管理员重置）。</summary>
    public sealed class ChangePasswordRequest
    {
        public string username { get; set; }
        public string oldPassword { get; set; }
        public string newPassword { get; set; }
    }

    /// <summary>账号维护请求（新增 / 更新 / 删除 / 重置密码共用）。</summary>
    public sealed class AuthUserRequest
    {
        public string username { get; set; }
        public string password { get; set; }
        public string role { get; set; }
        public bool? enabled { get; set; }
        public string note { get; set; }
    }

    /// <summary>
    /// B9 账号与鉴权。
    ///
    /// 做四件事：
    ///   1. 账号：`runtime\config\users.json`，密码只存 PBKDF2-SHA256 哈希（每个账号独立盐、12 万次迭代）；
    ///   2. 登录限制：连续错 `maxFailures` 次锁定 `lockMinutes` 分钟，窗口内计数，成功即清零；
    ///   3. 会话：登录发随机 token（HttpOnly Cookie 给浏览器、Bearer 给程序、X-Api-Key 给脚本/上位机）；
    ///   4. 审计：登录成功/失败/锁定/改密/账号变更都写 data\auth-events-*.jsonl。
    ///
    /// 首次运行自动创建管理员并生成随机初始密码（写 config\admin-initial-password.txt），
    /// 同时生成服务令牌 serviceKey（给脚本与上位机集成用，可在界面上轮换）。
    /// </summary>
    public sealed class AuthStore
    {
        public const string CookieName = "dws_session";
        /// <summary>HttpContext.Items 里放当前会话的键（中间件写入，接口读取）。</summary>
        public const string SessionItemKey = "dws.session";

        private const int DefaultIterations = 120000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int SessionTokenBytes = 32;
        private const int MaxEvents = 500;

        private readonly object _sync = new object();
        private readonly ILogger<AuthStore> _logger;
        private readonly Dictionary<string, AuthSession> _sessions =
            new Dictionary<string, AuthSession>(StringComparer.Ordinal);
        private readonly List<AuthEventRecord> _events = new List<AuthEventRecord>();

        private AuthOptions _options = new AuthOptions();
        private List<AuthUser> _users = new List<AuthUser>();
        private DateTime _optionsLoadedAtUtc = DateTime.MinValue;
        private DateTime _usersLoadedAtUtc = DateTime.MinValue;
        private DateTime _lastConfigCheckUtc = DateTime.MinValue;
        private long _sessionSeq;

        public AuthStore(IConfiguration config, ILogger<AuthStore> logger)
        {
            _logger = logger;
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config["Runtime:Root"] ?? ".."));
            ConfigPath = Path.Combine(runtimeRoot, @"config\auth.json");
            UsersPath = Path.Combine(runtimeRoot, @"config\users.json");
            InitialPasswordPath = Path.Combine(runtimeRoot, @"config\admin-initial-password.txt");
            DataDirectory = Path.Combine(runtimeRoot, "data");
            Directory.CreateDirectory(DataDirectory);

            EnsureDefaultConfig();
            ReloadConfig();
            EnsureDefaultAdmin();
            ReloadUsers();
            LoadRecentEvents();
        }

        public string ConfigPath { get; private set; }
        public string UsersPath { get; private set; }
        public string InitialPasswordPath { get; private set; }
        public string DataDirectory { get; private set; }

        public AuthOptions Options
        {
            get { EnsureConfigFresh(); lock (_sync) { return _options; } }
        }

        // ---------------------------------------------------------------- 配置文件

        private static readonly JsonSerializerOptions PrettyJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>日志/审计文件必须一条一行（缩进 JSON 会让"按行读"整片解析失败）。</summary>
        private static readonly JsonSerializerOptions LineJson = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private void EnsureDefaultConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(ConfigPath))
                {
                    File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new AuthOptions(), PrettyJson),
                        new UTF8Encoding(false));
                    _logger.LogInformation("已生成鉴权配置模板：{0}", ConfigPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("生成鉴权配置失败：{0}", ex.Message);
            }
        }

        private void ReloadConfig()
        {
            AuthOptions loaded = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<AuthOptions>(text, PrettyJson);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("鉴权配置解析失败，沿用上一份：{0}", ex.Message);
            }

            if (loaded == null)
            {
                loaded = new AuthOptions();
            }

            // 服务令牌是"自动生成、可轮换"的：空着就直接生成一个写回文件，省得现场自己去编
            if (string.IsNullOrEmpty(loaded.serviceKey))
            {
                loaded.serviceKey = NewToken(24);
                try
                {
                    File.WriteAllText(ConfigPath, JsonSerializer.Serialize(loaded, PrettyJson), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("写入服务令牌失败：{0}", ex.Message);
                }
            }

            lock (_sync)
            {
                _options = loaded;
                _optionsLoadedAtUtc = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
            }
        }

        private void EnsureConfigFresh()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastConfigCheckUtc).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastConfigCheckUtc = now;

            try
            {
                bool optionsChanged = File.Exists(ConfigPath) && File.GetLastWriteTimeUtc(ConfigPath) != _optionsLoadedAtUtc;
                bool usersChanged = File.Exists(UsersPath) && File.GetLastWriteTimeUtc(UsersPath) != _usersLoadedAtUtc;

                if (optionsChanged)
                {
                    ReloadConfig();
                    _logger.LogInformation("鉴权配置已变化，已重新加载：{0}", ConfigPath);
                }
                if (usersChanged)
                {
                    ReloadUsers();
                    _logger.LogInformation("账号文件已变化，已重新加载：{0}（{1} 个账号）", UsersPath, _users.Count);
                }
            }
            catch (Exception)
            {
            }
        }

        public List<string> Validate(AuthOptions options)
        {
            List<string> problems = new List<string>();
            if (options == null)
            {
                problems.Add("配置不能为空");
                return problems;
            }
            if (options.maxFailures < 1 || options.maxFailures > 100)
            {
                problems.Add("maxFailures 建议在 1-100 之间");
            }
            if (options.lockMinutes < 0 || options.lockMinutes > 1440)
            {
                problems.Add("lockMinutes 建议在 0-1440 之间（0 = 不锁定，不推荐）");
            }
            if (options.failureWindowMinutes < 1 || options.failureWindowMinutes > 1440)
            {
                problems.Add("failureWindowMinutes 建议在 1-1440 之间");
            }
            if (options.sessionMinutes < 5 || options.sessionMinutes > 43200)
            {
                problems.Add("sessionMinutes 建议在 5-43200 之间");
            }
            if (!string.Equals(options.serviceKeyRole, "admin", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.serviceKeyRole, "operator", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("serviceKeyRole 只能是 admin 或 operator");
            }
            return problems;
        }

        /// <summary>保存策略（自动备份）。返回备份文件路径（原来是新建的则为 null）。</summary>
        public string SaveOptions(AuthOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            List<string> problems = Validate(options);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.ToArray()));
            }

            string backup = null;
            lock (_sync)
            {
                // 令牌不允许通过这个接口清空：空值表示"保持原样"
                if (string.IsNullOrEmpty(options.serviceKey))
                {
                    options.serviceKey = _options.serviceKey;
                }
                if (File.Exists(ConfigPath))
                {
                    backup = ConfigPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(ConfigPath, backup, true);
                }
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(options, PrettyJson), new UTF8Encoding(false));
                _options = options;
                _optionsLoadedAtUtc = File.GetLastWriteTimeUtc(ConfigPath);
            }
            _logger.LogInformation("鉴权策略已保存：{0}", ConfigPath);
            return backup;
        }

        /// <summary>轮换服务令牌（返回新令牌）。</summary>
        public string RotateServiceKey()
        {
            lock (_sync)
            {
                _options.serviceKey = NewToken(24);
                string backup = ConfigPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (File.Exists(ConfigPath))
                {
                    File.Copy(ConfigPath, backup, true);
                }
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_options, PrettyJson), new UTF8Encoding(false));
                _optionsLoadedAtUtc = File.GetLastWriteTimeUtc(ConfigPath);
                AddEventLocked("config-change", "service-key", null, "服务令牌已轮换");
                return _options.serviceKey;
            }
        }

        // ---------------------------------------------------------------- 账号文件

        private void EnsureDefaultAdmin()
        {
            try
            {
                if (File.Exists(UsersPath))
                {
                    return;
                }

                string password = NewPassword();
                AuthUser admin = new AuthUser();
                admin.username = "admin";
                admin.role = "admin";
                admin.enabled = true;
                admin.createdAt = NowText();
                admin.updatedAt = admin.createdAt;
                admin.mustChangePassword = true;
                admin.note = "首次运行自动创建";
                SetPassword(admin, password);
                SaveUsers(new List<AuthUser> { admin });

                // 明文初始密码只写这个文件：不进日志（日志会被拷来拷去），现场登录后请删除它
                StringBuilder text = new StringBuilder();
                text.AppendLine("DWS 物流解码平台 —— 初始管理员账号");
                text.AppendLine("（首次运行自动生成；登录后请在【系统与安全 → 账号与安全】里改密，然后删除本文件）");
                text.AppendLine();
                text.AppendLine("用户名：" + admin.username);
                text.AppendLine("初始密码：" + password);
                File.WriteAllText(InitialPasswordPath, text.ToString(), new UTF8Encoding(true));

                _logger.LogWarning("已创建默认管理员账号 admin（初始密码见 {0}），请登录后立即修改密码", InitialPasswordPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("创建默认管理员失败：{0}", ex.Message);
            }
        }

        private void ReloadUsers()
        {
            List<AuthUser> loaded = null;
            try
            {
                if (File.Exists(UsersPath))
                {
                    string text = File.ReadAllText(UsersPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<List<AuthUser>>(text, PrettyJson);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("账号文件解析失败，沿用上一份：{0}", ex.Message);
            }

            lock (_sync)
            {
                if (loaded != null)
                {
                    _users = loaded;
                }
                _usersLoadedAtUtc = File.Exists(UsersPath) ? File.GetLastWriteTimeUtc(UsersPath) : DateTime.MinValue;
            }
        }

        private void SaveUsers(List<AuthUser> users)
        {
            lock (_sync)
            {
                File.WriteAllText(UsersPath, JsonSerializer.Serialize(users, PrettyJson), new UTF8Encoding(false));
                _users = users;
                _usersLoadedAtUtc = File.GetLastWriteTimeUtc(UsersPath);
            }
        }

        private static AuthUser FindUser(List<AuthUser> users, string username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return null;
            }
            for (int i = 0; i < users.Count; i++)
            {
                if (string.Equals(users[i].username, username, StringComparison.OrdinalIgnoreCase))
                {
                    return users[i];
                }
            }
            return null;
        }

        public List<AuthUserView> Users()
        {
            EnsureConfigFresh();
            List<AuthUserView> list = new List<AuthUserView>();
            lock (_sync)
            {
                for (int i = 0; i < _users.Count; i++)
                {
                    list.Add(ToView(_users[i]));
                }
            }
            return list;
        }

        private static AuthUserView ToView(AuthUser user)
        {
            long now = NowMs();
            AuthUserView view = new AuthUserView();
            view.username = user.username;
            view.role = user.role;
            view.enabled = user.enabled;
            view.createdAt = user.createdAt;
            view.updatedAt = user.updatedAt;
            view.mustChangePassword = user.mustChangePassword;
            view.note = user.note;
            view.locked = user.lockedUntilMs > now;
            view.lockedSeconds = view.locked ? (int)Math.Max(0, (user.lockedUntilMs - now) / 1000) : 0;
            view.failedCount = user.failedCount;
            view.lastLoginAt = user.lastLoginAt;
            view.lastLoginIp = user.lastLoginIp;
            view.lastFailureAt = user.lastFailureAt;
            return view;
        }

        // ---------------------------------------------------------------- 密码

        private static void SetPassword(AuthUser user, string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
            user.algorithm = "PBKDF2-SHA256";
            user.iterations = DefaultIterations;
            user.salt = Convert.ToBase64String(salt);
            user.hash = Convert.ToBase64String(hash);
            user.updatedAt = NowText();
        }

        private static bool VerifyPassword(AuthUser user, string password)
        {
            if (user == null || string.IsNullOrEmpty(user.salt) || string.IsNullOrEmpty(user.hash))
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(user.salt);
                byte[] expected = Convert.FromBase64String(user.hash);
                int iterations = user.iterations > 0 ? user.iterations : DefaultIterations;
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>密码强度（V1 的最低要求：8 位以上，且不是纯数字）。</summary>
        public static string CheckPasswordStrength(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                return "密码至少 8 位";
            }
            bool hasLetter = false;
            bool hasDigit = false;
            for (int i = 0; i < password.Length; i++)
            {
                if (char.IsLetter(password[i])) { hasLetter = true; }
                else if (char.IsDigit(password[i])) { hasDigit = true; }
            }
            if (!hasLetter || !hasDigit)
            {
                return "密码要同时包含字母和数字";
            }
            return null;
        }

        // ---------------------------------------------------------------- 登录 / 会话

        public LoginResult Login(string username, string password, string ip)
        {
            AuthOptions options = Options;
            if (!options.enabled)
            {
                return new LoginResult { ok = false, code = "disabled", error = "平台未启用鉴权" };
            }

            AuthUserView view = null;
            string loginUsername = username ?? "";

            lock (_sync)
            {
                AuthUser user = FindUser(_users, loginUsername);
                long now = NowMs();

                if (user == null || !user.enabled)
                {
                    AddEventLocked("login-fail", loginUsername, ip,
                        user == null ? "账号不存在" : "账号已禁用");
                    return new LoginResult
                    {
                        ok = false,
                        code = "bad-credentials",
                        error = "用户名或密码错误",
                        remainingAttempts = options.maxFailures
                    };
                }

                // 锁定中：即使密码正确也拒绝，并告知还要等多久
                if (user.lockedUntilMs > now)
                {
                    int seconds = (int)Math.Max(1, (user.lockedUntilMs - now) / 1000);
                    AddEventLocked("login-locked", user.username, ip,
                        "账号锁定中，剩余 " + seconds + " 秒");
                    return new LoginResult
                    {
                        ok = false,
                        code = "locked",
                        error = "账号已锁定，请 " + seconds + " 秒后再试",
                        lockedSeconds = seconds,
                        remainingAttempts = 0
                    };
                }

                // 失败计数的滑动窗口：窗口内没再失败就清零，避免"很久以前错两次 + 今天错三次"被锁
                if (user.failedCount > 0 && options.failureWindowMinutes > 0 && !string.IsNullOrEmpty(user.lastFailureAt))
                {
                    DateTime last;
                    if (DateTime.TryParse(user.lastFailureAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out last)
                        && (DateTime.Now - last).TotalMinutes > options.failureWindowMinutes)
                    {
                        user.failedCount = 0;
                    }
                }

                if (!VerifyPassword(user, password ?? ""))
                {
                    user.failedCount++;
                    user.lastFailureAt = NowText();

                    if (user.failedCount >= options.maxFailures)
                    {
                        user.lockedUntilMs = now + options.lockMinutes * 60000L;
                        int lockedSeconds = options.lockMinutes * 60;
                        user.failedCount = 0;
                        SaveUsersLocked();
                        AddEventLocked("login-locked", user.username, ip,
                            "连续 " + options.maxFailures + " 次密码错误，锁定 " + options.lockMinutes + " 分钟");
                        _logger.LogWarning("账号 {0} 连续密码错误达上限，已锁定 {1} 分钟（来源 {2}）",
                            user.username, options.lockMinutes, ip);
                        return new LoginResult
                        {
                            ok = false,
                            code = "locked",
                            error = "密码错误次数过多，账号已锁定 " + options.lockMinutes + " 分钟",
                            lockedSeconds = lockedSeconds,
                            remainingAttempts = 0
                        };
                    }

                    int remaining = Math.Max(0, options.maxFailures - user.failedCount);
                    SaveUsersLocked();
                    AddEventLocked("login-fail", user.username, ip,
                        "密码错误（第 " + user.failedCount + " 次，还剩 " + remaining + " 次）");
                    return new LoginResult
                    {
                        ok = false,
                        code = "bad-credentials",
                        error = "用户名或密码错误（还可以尝试 " + remaining + " 次）",
                        remainingAttempts = remaining
                    };
                }

                // 登录成功
                user.failedCount = 0;
                user.lockedUntilMs = 0;
                user.lastLoginAt = NowText();
                user.lastLoginIp = ip;
                SaveUsersLocked();

                view = ToView(user);
                string token = NewSessionLocked(user, ip);
                AddEventLocked("login-ok", user.username, ip, "登录成功");

                return new LoginResult
                {
                    ok = true,
                    user = view,
                    token = token,
                    expiresAtMs = _sessions[token].expiresAtMs,
                    sessionMinutes = options.sessionMinutes
                };
            }
        }

        private void SaveUsersLocked()
        {
            try
            {
                File.WriteAllText(UsersPath, JsonSerializer.Serialize(_users, PrettyJson), new UTF8Encoding(false));
                _usersLoadedAtUtc = File.GetLastWriteTimeUtc(UsersPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("写入账号文件失败：{0}", ex.Message);
            }
        }

        private string NewSessionLocked(AuthUser user, string ip)
        {
            AuthOptions options = _options;
            AuthSession session = new AuthSession();
            session.token = NewToken(SessionTokenBytes);
            session.username = user.username;
            session.role = user.role;
            session.ip = ip;
            session.createdAtMs = NowMs();
            session.expiresAtMs = session.createdAtMs + Math.Max(5, options.sessionMinutes) * 60000L;
            session.lastSeenMs = session.createdAtMs;
            _sessions[session.token] = session;
            _sessionSeq++;
            return session.token;
        }

        public bool Logout(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            lock (_sync)
            {
                AuthSession session;
                if (!_sessions.TryGetValue(token, out session))
                {
                    return false;
                }
                _sessions.Remove(token);
                AddEventLocked("logout", session.username, session.ip, "退出登录");
                return true;
            }
        }

        /// <summary>把内存会话全部作废（改策略/轮换令牌/管理员要求踢人时用）。</summary>
        public int ClearSessions()
        {
            lock (_sync)
            {
                int count = _sessions.Count;
                _sessions.Clear();
                return count;
            }
        }

        public int ActiveSessionCount
        {
            get { lock (_sync) { return _sessions.Count; } }
        }

        // ---------------------------------------------------------------- 访问控制

        /// <summary>接口白名单：不需要登录的路径。</summary>
        private static bool IsAlwaysPublic(string path)
        {
            return string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/auth/login", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/auth/status", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>管理接口（配置/规则/下游/账号/监控阈值……）：未登录一律 401。</summary>
        private static bool IsManagementPath(string path)
        {
            return StartsWith(path, "/api/config")
                || StartsWith(path, "/api/camera-positions")
                || StartsWith(path, "/api/rules")
                || StartsWith(path, "/api/downstream")
                || StartsWith(path, "/api/monitor/config")
                || StartsWith(path, "/api/stats/shifts")
                || StartsWith(path, "/api/diag")
                // 只保护"写操作"：/api/host/command（触发一次，可能影响生产）。
                // 状态类只读接口（/api/host/status、/api/host/channel）不要求登录 ——
                // 否则未登录时页面每 5 秒轮询一次就拿 401，触发弹登录框、把光标抢回用户名。
                || string.Equals(path, "/api/host/command", StringComparison.OrdinalIgnoreCase)
                || StartsWith(path, "/api/camera-probe")
                || StartsWith(path, "/api/dedup/compact")
                || StartsWith(path, "/api/dispatch/ack");
        }

        /// <summary>只有 admin 能碰的接口（账号管理、鉴权策略、审计）。</summary>
        private static bool IsAdminOnlyPath(string path)
        {
            return StartsWith(path, "/api/auth/users")
                || StartsWith(path, "/api/auth/config")
                || StartsWith(path, "/api/auth/events");
        }

        private static bool StartsWith(string path, string prefix)
        {
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判定这次请求能不能过。规则：
        ///   * 非 /api/ 路径（页面、js、css）永远放行 —— 页面本身要能打开，登录框才显示得出来；
        ///   * /api/health、/api/auth/login、/api/auth/status 永远放行（存活检查与前端判断登录态）；
        ///   * 管理接口要登录（viewer 角色只能看，改不了）；
        ///   * 其余读接口看 protectRead 开关。
        /// </summary>
        public AccessCheck Check(HttpRequest request)
        {
            AccessCheck result = new AccessCheck();
            result.kind = "allowed";

            AuthOptions options = Options;
            if (!options.enabled)
            {
                return result;
            }

            string path = request.Path.Value ?? "";
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || IsAlwaysPublic(path))
            {
                return result;
            }

            bool adminOnly = IsAdminOnlyPath(path);
            bool management = adminOnly || IsManagementPath(path);
            bool authPath = path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);
            if (!management && !authPath && !options.protectRead)
            {
                return result;
            }

            AuthSession session = Resolve(request, options);
            if (session == null)
            {
                result.kind = "unauthenticated";
                result.error = "未登录或登录已过期，请先登录";
                return result;
            }

            result.session = session;

            if (adminOnly && !IsAdmin(session.role))
            {
                result.kind = "forbidden";
                result.error = "需要管理员权限";
                return result;
            }
            if (management && !CanManage(session.role))
            {
                result.kind = "forbidden";
                result.error = "当前账号是只读账号，不能修改配置";
                return result;
            }

            return result;
        }

        /// <summary>解析凭据：Cookie（浏览器）→ Bearer（程序）→ X-Api-Key（脚本/上位机）。</summary>
        public AuthSession Resolve(HttpRequest request, AuthOptions options)
        {
            string token = null;

            if (request.Cookies != null && request.Cookies.TryGetValue(CookieName, out string cookieToken))
            {
                token = cookieToken;
            }

            string header = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(header)
                && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header.Substring(7).Trim();
            }

            if (!string.IsNullOrEmpty(token))
            {
                AuthSession session = ValidateToken(token);
                if (session != null)
                {
                    return session;
                }
            }

            string apiKey = request.Headers["X-Api-Key"];
            if (options.allowServiceKey && !string.IsNullOrEmpty(options.serviceKey)
                && !string.IsNullOrEmpty(apiKey) && FixedTimeEquals(apiKey, options.serviceKey))
            {
                AuthSession service = new AuthSession();
                service.token = null;
                service.username = "service-key";
                service.role = string.IsNullOrEmpty(options.serviceKeyRole) ? "admin" : options.serviceKeyRole;
                service.ip = ClientIp(request);
                service.createdAtMs = NowMs();
                service.expiresAtMs = long.MaxValue;
                service.lastSeenMs = service.createdAtMs;
                return service;
            }

            return null;
        }

        public AuthSession ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            lock (_sync)
            {
                AuthSession session;
                if (!_sessions.TryGetValue(token, out session))
                {
                    return null;
                }

                long now = NowMs();
                if (session.expiresAtMs <= now)
                {
                    _sessions.Remove(token);
                    AddEventLocked("logout", session.username, session.ip, "会话过期");
                    return null;
                }

                session.lastSeenMs = now;
                return session;
            }
        }

        public static string ClientIp(HttpRequest request)
        {
            try
            {
                return request.HttpContext != null && request.HttpContext.Connection.RemoteIpAddress != null
                    ? request.HttpContext.Connection.RemoteIpAddress.ToString()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsAdmin(string role)
        {
            return string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanManage(string role)
        {
            return IsAdmin(role) || string.Equals(role, "operator", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- 账号管理

        public AuthUserView AddUser(string username, string password, string role, string note, string actor)
        {
            string problem = ValidateUsername(username);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            problem = ValidateRole(role);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            problem = CheckPasswordStrength(password);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }

            AuthUserView view;
            lock (_sync)
            {
                if (FindUser(_users, username) != null)
                {
                    throw new InvalidOperationException("账号已存在：" + username);
                }

                AuthUser user = new AuthUser();
                user.username = username.Trim();
                user.role = NormalizeRole(role);
                user.enabled = true;
                user.createdAt = NowText();
                user.updatedAt = user.createdAt;
                user.mustChangePassword = true;
                user.note = note;
                SetPassword(user, password);
                _users.Add(user);
                SaveUsersLocked();
                AddEventLocked("user-add", user.username, null, "新增账号（角色 " + user.role + "，操作人 " + actor + "）");
                view = ToView(user);
            }
            return view;
        }

        /// <summary>改角色 / 启用禁用 / 改备注。username 不允许改名。</summary>
        public AuthUserView UpdateUser(string username, string role, bool? enabled, string note, string actor)
        {
            AuthUserView view;
            lock (_sync)
            {
                AuthUser user = FindUser(_users, username);
                if (user == null)
                {
                    throw new InvalidOperationException("账号不存在：" + username);
                }

                if (!string.IsNullOrEmpty(role))
                {
                    string problem = ValidateRole(role);
                    if (problem != null)
                    {
                        throw new InvalidOperationException(problem);
                    }
                    // 必须留一个能登录的管理员，否则谁都进不来了
                    if (IsAdmin(user.role) && !IsAdmin(role) && CountEnabledAdminsLocked() <= 1)
                    {
                        throw new InvalidOperationException("至少要保留一个启用状态的管理员账号");
                    }
                    user.role = NormalizeRole(role);
                }

                if (enabled.HasValue && enabled.Value != user.enabled)
                {
                    if (!enabled.Value && IsAdmin(user.role) && CountEnabledAdminsLocked() <= 1)
                    {
                        throw new InvalidOperationException("至少要保留一个启用状态的管理员账号");
                    }
                    user.enabled = enabled.Value;
                    if (!enabled.Value)
                    {
                        RemoveSessionsForUserLocked(user.username);
                    }
                }

                if (note != null)
                {
                    user.note = note;
                }

                user.updatedAt = NowText();
                SaveUsersLocked();
                AddEventLocked("user-update", user.username, null,
                    "账号更新（角色 " + user.role + "，启用 " + (user.enabled ? "是" : "否") + "，操作人 " + actor + "）");
                view = ToView(user);
            }
            return view;
        }

        public void DeleteUser(string username, string actor)
        {
            lock (_sync)
            {
                AuthUser user = FindUser(_users, username);
                if (user == null)
                {
                    throw new InvalidOperationException("账号不存在：" + username);
                }
                if (IsAdmin(user.role) && user.enabled && CountEnabledAdminsLocked() <= 1)
                {
                    throw new InvalidOperationException("至少要保留一个启用状态的管理员账号");
                }
                _users.Remove(user);
                RemoveSessionsForUserLocked(user.username);
                SaveUsersLocked();
                AddEventLocked("user-delete", user.username, null, "删除账号（操作人 " + actor + "）");
            }
        }

        /// <summary>
        /// 改密码（byAdmin=true 时不需要旧密码，并顺便解锁）。
        /// keepToken = 本人改密时保留当前会话，别把自己也踢下线（其它会话会失效）。
        /// </summary>
        public void ChangePassword(string username, string oldPassword, string newPassword, bool byAdmin,
            string actor, string keepToken)
        {
            string problem = CheckPasswordStrength(newPassword);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }

            lock (_sync)
            {
                AuthUser user = FindUser(_users, username);
                if (user == null)
                {
                    throw new InvalidOperationException("账号不存在：" + username);
                }
                if (!byAdmin && !VerifyPassword(user, oldPassword ?? ""))
                {
                    AddEventLocked("password-change", user.username, null, "改密失败：原密码错误");
                    throw new InvalidOperationException("原密码不正确");
                }
                if (VerifyPassword(user, newPassword))
                {
                    throw new InvalidOperationException("新密码不能与原密码相同");
                }

                SetPassword(user, newPassword);
                user.mustChangePassword = false;
                user.failedCount = 0;
                user.lockedUntilMs = 0;
                SaveUsersLocked();
                // 改密后把其它会话踢掉（当前这次会话保留，用户不用重新登录）
                RemoveSessionsForUserLocked(user.username, keepToken);
                AddEventLocked("password-change", user.username, null,
                    byAdmin ? ("管理员重置密码（操作人 " + actor + "）") : "用户自助修改密码");
                // 只要"初始密码文件里记的那个账号"改过密码，这个文件就没用了（再留着会误导现场）
                TryClearInitialPasswordFileLocked(user.username);
            }
        }

        /// <summary>改密成功后清掉初始密码提示文件（只清它记的那个账号，避免误删别人写的文件）。</summary>
        private void TryClearInitialPasswordFileLocked(string username)
        {
            try
            {
                if (!File.Exists(InitialPasswordPath))
                {
                    return;
                }
                string text = File.ReadAllText(InitialPasswordPath, Encoding.UTF8);
                if (text.IndexOf("用户名：" + username, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    File.Delete(InitialPasswordPath);
                    _logger.LogInformation("初始密码文件已删除：{0}", InitialPasswordPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("删除初始密码文件失败：{0}", ex.Message);
            }
        }

        private void RemoveSessionsForUserLocked(string username, string keepToken = null)
        {
            List<string> keys = new List<string>();
            foreach (KeyValuePair<string, AuthSession> pair in _sessions)
            {
                if (string.Equals(pair.Value.username, username, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, keepToken, StringComparison.Ordinal))
                {
                    keys.Add(pair.Key);
                }
            }
            for (int i = 0; i < keys.Count; i++)
            {
                _sessions.Remove(keys[i]);
            }
        }

        private int CountEnabledAdminsLocked()
        {
            int count = 0;
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].enabled && IsAdmin(_users[i].role))
                {
                    count++;
                }
            }
            return count;
        }

        private static string ValidateUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return "用户名不能为空";
            }
            string value = username.Trim();
            if (value.Length < 3 || value.Length > 32)
            {
                return "用户名长度 3-32 位";
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '_' || c == '-' || c == '.';
                if (!ok)
                {
                    return "用户名只能用字母、数字、下划线、短横线、点";
                }
            }
            return null;
        }

        private static string ValidateRole(string role)
        {
            if (string.IsNullOrEmpty(role))
            {
                return null;
            }
            if (IsAdmin(role) || string.Equals(role, "operator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "viewer", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return "角色只能是 admin / operator / viewer";
        }

        private static string NormalizeRole(string role)
        {
            if (string.IsNullOrEmpty(role))
            {
                return "operator";
            }
            return role.Trim().ToLowerInvariant();
        }

        // ---------------------------------------------------------------- 审计

        private void AddEventLocked(string kind, string username, string ip, string detail)
        {
            AuthEventRecord record = new AuthEventRecord();
            record.atMs = NowMs();
            record.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            record.kind = kind;
            record.username = username;
            record.ip = ip;
            record.detail = detail;

            _events.Add(record);
            while (_events.Count > MaxEvents)
            {
                _events.RemoveAt(0);
            }

            try
            {
                string path = Path.Combine(DataDirectory, "auth-events-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(JsonSerializer.Serialize(record, LineJson));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("写鉴权审计失败：{0}", ex.Message);
            }
        }

        public void RecordEvent(string kind, string username, string ip, string detail)
        {
            lock (_sync)
            {
                AddEventLocked(kind, username, ip, detail);
            }
        }

        public List<AuthEventRecord> Events(int limit, string kind)
        {
            EnsureConfigFresh();
            List<AuthEventRecord> result = new List<AuthEventRecord>();
            lock (_sync)
            {
                for (int i = _events.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    if (string.IsNullOrEmpty(kind)
                        || string.Equals(_events[i].kind, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(_events[i]);
                    }
                }
            }
            return result;
        }

        private void LoadRecentEvents()
        {
            try
            {
                List<AuthEventRecord> all = new List<AuthEventRecord>();
                for (int i = 0; i < 2; i++)
                {
                    string path = Path.Combine(DataDirectory,
                        "auth-events-" + DateTime.Today.AddDays(-i).ToString("yyyyMMdd") + ".jsonl");
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) { continue; }
                            try
                            {
                                AuthEventRecord item = JsonSerializer.Deserialize<AuthEventRecord>(line, LineJson);
                                if (item != null) { all.Add(item); }
                            }
                            catch (JsonException)
                            {
                            }
                        }
                    }
                }

                if (all.Count == 0)
                {
                    return;
                }

                all.Sort(delegate(AuthEventRecord a, AuthEventRecord b) { return a.atMs.CompareTo(b.atMs); });
                lock (_sync)
                {
                    for (int i = 0; i < all.Count && _events.Count < MaxEvents; i++)
                    {
                        _events.Add(all[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("恢复鉴权审计失败：{0}", ex.Message);
            }
        }

        // ---------------------------------------------------------------- 工具

        private static string NewToken(int bytes)
        {
            byte[] buffer = RandomNumberGenerator.GetBytes(bytes);
            return Convert.ToBase64String(buffer).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        /// <summary>初始密码：避免 0/O、1/l 这种现场念不清的字符。</summary>
        private static string NewPassword()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            byte[] buffer = RandomNumberGenerator.GetBytes(10);
            StringBuilder text = new StringBuilder(10);
            for (int i = 0; i < buffer.Length; i++)
            {
                text.Append(alphabet[buffer[i] % alphabet.Length]);
            }
            return text.ToString();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            byte[] left = Encoding.UTF8.GetBytes(a ?? "");
            byte[] right = Encoding.UTF8.GetBytes(b ?? "");
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }

        private static string NowText()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
