using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// What every text of a project report goes through (<see cref="Mask"/>) before it is written or sent. Secrets are
    /// always masked: Orbiters, Git hosting and API tokens, sign-in headers, cookies, passwords and keys in addresses,
    /// settings, JSON and command lines, Discord webhooks, JSON web tokens, private keys and store license keys; the
    /// user folder becomes %USERPROFILE%. With <see cref="Anonymize"/>, what tells who and where the person is goes too:
    /// user and computer names, machine and session ids, serials, emails, Git authors, remote addresses, avatar and
    /// blueprint ids and paths outside the project. Files copied as they are (pictures, the scene export) are not texts:
    /// the report leaves them out when anonymizing. A file read line by line goes through <see cref="Lines"/>, which also
    /// masks what a line only announces (Editor.log prints "-accessToken" and its value on the next line).
    /// </summary>
    internal sealed class ReportPrivacy
    {
        internal const string UserFolder = "%USERPROFILE%";
        internal const string ProjectFolder = "%PROJECT%";
        internal const string UnityFolder = "%UNITY%";
        internal const string UserName = "%USERNAME%";
        internal const string ComputerName = "%COMPUTERNAME%";
        internal const string Masked = "[masked]";
        internal const string HiddenPath = "[path]";
        internal const string HiddenEmail = "[email]";
        internal const string HiddenRemote = "[remote]";

        private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const RegexOptions CaseSensitive = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        // Names that only ever hold a secret: whatever follows them is masked, spaces included.
        private const string SecretNames = @"access[_\-]?token|refresh[_\-]?token|id[_\-]?token|auth[_\-]?token|api[_\-]?key|apikey|client[_\-]?secret|" +
                                           @"secret|private[_\-]?key|password|passwd|passphrase|pwd|two[_\-]?factor[_\-]?auth|authcookie|session[_\-]?id|" +
                                           @"license[_\-]?key|webhook(?:[_\-]?url)?";
        // Names ordinary text uses too ("Unexpected token: 'foreach'", "OAuth: success"): masked after '=', and after ':'
        // only when what follows looks like a key.
        private const string WordNames = @"token|auth";
        // Command-line flags whose value is a secret, and those that tell who the person is (anonymizing).
        private const string SecretFlags = @"access[_\-]?token|refresh[_\-]?token|auth[_\-]?token|token|password|passwd|api[_\-]?key|client[_\-]?secret|secret";
        private const string IdentityFlags = @"hubSessionId|cloudOrganization|cloudEnvironment|licensingIpc|hubIPC|username|userId";
        // A JSON string's value: in plain JSON up to an unescaped quote; in JSON written inside another string (\"key\":\"…\")
        // up to the next \" that is not itself an escaped quote (\\\").
        private const string PlainJsonValue = @"(?:[^""\\]|\\.)*";
        private const string EscapedJsonValue = @"(?:\\\\\\""|\\\\\\\\|\\[^""\\]|[^""\\])*";

        // ---- Always masked ------------------------------------------------------------------------------------------

        private static readonly Regex OrbitersToken = new Regex(@"\borbit-[0-9a-f]{8}-[0-9a-f]{8}-[0-9a-f]{8}-\d{2}-\d{2}-\d{4}\b", Options);
        private static readonly Regex DiscordWebhook = new Regex(@"(discord(?:app)?\.com(?:\\?/)+api(?:\\?/)+(?:v\d+(?:\\?/)+)?webhooks(?:\\?/)+)\d+(?:\\?/)+[\w\-]+", Options);
        // user:password@ in an address (JSON-escaped slashes too). A password may hold '/' and '@': the shortest one
        // followed by a host wins.
        private static readonly Regex UrlCredentials = new Regex(
            @"(\b[a-z][a-z0-9+.\-]*:(?:\\?/){2})(?!git@)(?:[^\s/@\\""'<>:]+:[^\s\\""'<>]*?|[^\s/@\\""'<>]+?)@(?=[^\s@/\\""'<>]+(?:[:/\\""'\s?#]|$))", Options);
        private static readonly Regex AuthorizationHeader = new Regex(
            @"(\b(?:Proxy-)?Authorization\\?[""']?\s*[:=]\s*\\?[""']?\s*(?:(?:Bearer|Basic|Token|Digest|Bot|Negotiate|NTLM)\s+)?)(?!\[masked\])[^\s""'\\,;]+", Options);
        private static readonly Regex Bearer = new Regex(@"(\bBearer\s+)[A-Za-z0-9\-._~+/]{12,}=*", Options);
        private static readonly Regex SchemeToken = new Regex(@"(\b(?:token|Basic)\s+)[A-Za-z0-9\-._~+/]{16,}=*", Options);
        private static readonly Regex KnownTokens = new Regex(
            @"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-(?:proj-|ant-)?[A-Za-z0-9_\-]{32,}|" +
            @"AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36})\b", CaseSensitive);
        private static readonly Regex Jwt = new Regex(@"\beyJ[A-Za-z0-9_\-]{6,}\.eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]*", CaseSensitive);
        private static readonly Regex QuerySecret = new Regex(
            @"([?&](?:access_token|refresh_token|id_token|token|auth|auth_token|authtoken|key|api_key|apikey|api-key|client_secret|secret|password|passwd|pwd|" +
            @"signature|sig|code|session|sessionid|session_id|t|x-amz-signature|x-amz-credential|x-amz-security-token|x-goog-signature|x-goog-credential)=)[^&\s""'#<>\\]+", Options);
        private static readonly Regex JsonSecret = new Regex(
            @"((?<!\\)""[\w.\-]{0,40}?(?:" + SecretNames + "|" + WordNames + @"|cookie|authorization)""\s*:\s*"")" + PlainJsonValue, Options);
        private static readonly Regex EscapedJsonSecret = new Regex(
            @"(\\""[\w.\-]{0,40}?(?:" + SecretNames + "|" + WordNames + @"|cookie|authorization)\\""\s*:\s*\\"")" + EscapedJsonValue, Options);
        // password: my secret phrase · password = "correct horse battery" · api-key: abc
        private static readonly Regex KeyValueSecret = new Regex(
            @"((?:" + SecretNames + @")[ \t]*[:=][ \t]*)(?:([""'])(?:(?!\2)[^\r\n])*\2?|(?![""'\s])(?!\[masked\])[^\r\n]+)", Options);
        private static readonly Regex WordAssignment = new Regex(@"((?:" + WordNames + @")[ \t]*=[ \t]*[""']?)(?!\[masked\])[^\s""',;&<>\\]{3,}", Options);
        private static readonly Regex WordKey = new Regex(@"((?:" + WordNames + @")[ \t]*:[ \t]*[""']?)(?!\[masked\])[A-Za-z0-9\-._~+/]{16,}=*", Options);
        private static readonly Regex CommandLineSecret = new Regex(@"((?<![\w\-])--?(?:" + SecretFlags + @")\s+)(?!-)\S+", Options);
        // A flag alone on its line, its value on the next (Editor.log's "COMMAND LINE ARGUMENTS:").
        private static readonly Regex SecretFlagLine = new Regex(@"^[ \t]*--?(?:" + SecretFlags + @")[ \t]*$", Options);
        private static readonly Regex IdentityFlagLine = new Regex(@"^[ \t]*-(?:" + IdentityFlags + @")[ \t]*$", Options);
        private static readonly Regex SecretFlagValue = new Regex(@"(?m)(^[ \t]*--?(?:" + SecretFlags + @")[ \t]*\r?\n[ \t]*)(?!-)(?!\[masked\])[^\r\n]+", Options);
        private static readonly Regex IdentityFlagValue = new Regex(@"(?m)(^[ \t]*-(?:" + IdentityFlags + @")[ \t]*\r?\n[ \t]*)(?!-)(?!\[masked\])[^\r\n]+", Options);
        // A key's lines up to its end, or to the end of the text when it was cut; a BEGIN line alone is left to LineMasker.
        private static readonly Regex PrivateKeyBlock = new Regex(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----)(?:[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----)|\r?\n[\s\S]*$)", Options);
        private static readonly Regex PrivateKeyBegin = new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", Options);
        private static readonly Regex PrivateKeyEnd = new Regex(@"-----END [A-Z ]*PRIVATE KEY-----", Options);
        private static readonly Regex CookieHeader = new Regex(@"(\b(?:Set-)?Cookie:[ \t]*)(?!\[masked\])[^\r\n""]+", Options);
        private static readonly Regex AuthCookie = new Regex(@"\bauthcookie_[0-9a-f\-]{8,}", Options);
        // Store license keys (Gumroad's four groups of eight).
        private static readonly Regex LicenseKey = new Regex(@"(?<![0-9A-Fa-f\-])[0-9A-F]{8}-[0-9A-F]{8}-[0-9A-F]{8}-[0-9A-F]{8}(?![0-9A-Fa-f\-])", CaseSensitive);
        // Windows' 8.3 short spelling of a user folder (C:\Users\JANEDO~1), escaped in JSON or in an address too.
        private static readonly Regex ShortUserFolder = new Regex(
            @"\b[A-Za-z](?::|%3A)(?:[\\/]{1,4}|%5C|%2F)(?:Users|Documents and Settings|Documents%20and%20Settings)(?:[\\/]{1,4}|%5C|%2F)[^\\/\s""'<>|:*?%]{1,6}~\d{1,3}(?=[\\/""'\s:;,)%]|$)",
            Options);

        // ---- Masked when anonymizing --------------------------------------------------------------------------------

        private static readonly Regex GitTrailers = new Regex(@"(\b(?:Author|Committer|Co-authored-by|Signed-off-by|Reviewed-by|Acked-by|Tested-by|Reported-by|Helped-by):[ \t]*)(?!\[masked\])[^\r\n]+", Options);
        private static readonly Regex RemoteUrl = new Regex(
            @"\b(?:(?:ssh|git|git\+ssh|svn):(?:\\?/){2}[^\s""'<>]+|https?:(?:\\?/){2}[^\s""'<>/\\]*(?:github\.com|gitlab\.com|bitbucket\.org|codeberg\.org|dev\.azure\.com|visualstudio\.com|sourceforge\.net|sr\.ht)(?:\\?/)[^\s""'<>()\[\]]*|" +
            @"https?:(?:\\?/){2}[^\s""'<>]+?\.git\b[^\s""'<>()\[\]]*)", Options);
        // git@github.com:owner/repo.git
        private static readonly Regex RemoteScp = new Regex(@"(?<![\w.\-])[\w.\-]+@[\w\-]+(?:\.[\w\-]+)+:(?!\d+(?:\D|$))[\w.~\-]+(?:/[\w.~\-]+)*", Options);
        // jane@example.com, and jane%40example.com in an address.
        private static readonly Regex Email = new Regex(@"(?<![\w.%+\-])[A-Za-z0-9._%+\-]+?(?:@|%40)[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}(?![\w\-])", Options);
        private static readonly Regex VrchatId = new Regex(@"(?<![A-Za-z0-9])(avtr|wrld|usr|file|grp|prop|inst|not|fvrt|gpos|gmem|grol|mod|cal|unk)_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-f])", Options);
        private static readonly Regex MachineIds = new Regex(
            @"(\b(?:Machine|Session|Correlation|External correlation|Device|Hardware|Install(?:ation)?|Hub session|Organi[sz]ation|Org|User|Account|Entitlement group|Client)[ _\-]?Id\\?[""']?\s*[:=]\s*\\?[""']?)(?!\[masked\])[^\s""',;]+",
            Options);
        private static readonly Regex HubArguments = new Regex(@"((?<![\w\-])-(?:" + IdentityFlags + @")\s+)(?!-)\S+", Options);
        private static readonly Regex Serial = new Regex(@"(\bSerial(?:[ _\-]?(?:number|no|key))?(?: assigned to)?\\?[""']?\s*[:=]\s*\\?[""']?)(?!\[masked\])[^\s""',;]+", Options);
        private static readonly Regex UnitySerial = new Regex(@"(?<![A-Z0-9\-])[A-Z0-9]{2}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-(?:[A-Z0-9]{4}|XXXX)(?![A-Z0-9\-])", CaseSensitive);
        private const string IdentityNames = @"author(?:Name|Email)?|committer(?:Name|Email)?|user(?:Name)?|displayName|email|owner|creator|machineName|computerName|hostName";
        private static readonly Regex JsonIdentity = new Regex(@"((?<!\\)""(?:" + IdentityNames + @")""\s*:\s*"")" + PlainJsonValue, Options);
        private static readonly Regex EscapedJsonIdentity = new Regex(@"(\\""(?:" + IdentityNames + @")\\""\s*:\s*\\"")" + EscapedJsonValue, Options);
        // Absolute paths outside the project, under the user folder too: the folders go, a file name stays (it tells what
        // was read). The last part runs to the end of the line or a closing quote: folder names have spaces.
        private static readonly Regex WindowsPath = new Regex(
            @"(?<![\w%\]~])([A-Za-z]:[\\/]{1,4}|\\\\[\w.$\-]+\\{1,2}|%USERPROFILE%[\\/]{1,4})((?:[^\\/\r\n""<>|*?:]{1,255}?[\\/]{1,4})*)((?:(?![A-Za-z]:[\\/]|%USERPROFILE%)[^\\/\r\n""'<>|*?:])*)",
            Options);
        private static readonly Regex UnixPath = new Regex(
            @"(?<![\w.~%\]\-:/])(/(?:Users|home|Volumes|mnt|media|run/media|private/var/folders|var/folders)/)((?:[^/\r\n""<>|*?:]{1,255}?/)*)((?:(?!\s/)[^/\r\n""'<>|*?:])*)",
            Options);
        // A file name at the start of what follows the last folder: "Unity Hub.exe", "Rexouium.unitypackage" (then the
        // rest of the sentence).
        private static readonly Regex FileName = new Regex(@"^(\S.{0,254}?\.[A-Za-z][A-Za-z0-9]{0,15})(?=$|[\s,;)\]'(])", CaseSensitive);
        private static readonly Regex UserRootFolder = new Regex(@"^(?:Users|Documents and Settings|home)[\\/]{1,4}$", Options);
        private static readonly Regex TrailingSeparator = new Regex(@"[\\/]+$", CaseSensitive);

        private static readonly HashSet<string> CommonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user", "users", "admin", "administrator", "owner", "guest", "default", "unity", "home", "test", "dev", "root", "public", "desktop"
        };

        private readonly List<string> secrets = new List<string>();
        private readonly List<string> userFolders = new List<string>();
        private readonly List<string> projectFolders = new List<string>();
        private readonly List<string> unityFolders = new List<string>();
        private readonly Regex computerName;
        private readonly Regex userName;

        /// <summary>Whether names, ids, emails, remotes and outside paths are masked too.</summary>
        public bool Anonymize { get; }

        /// <summary>Who and where the report is made: the values masked by name.</summary>
        internal sealed class Identity
        {
            public string UserFolder;
            public string UserName;
            public string ComputerName;
            public string ProjectFolder;
            public string UnityFolder;
        }

        /// <param name="userFolder">The user folder to hide ("C:\Users\Name"); every spelling of it is hidden.</param>
        /// <param name="secrets">Exact values to hide (the signed-in token).</param>
        internal ReportPrivacy(string userFolder, params string[] secrets) : this(new Identity { UserFolder = userFolder }, false, secrets)
        {
        }

        internal ReportPrivacy(Identity identity, bool anonymize, params string[] secrets)
        {
            Anonymize = anonymize;
            identity = identity ?? new Identity();
            AddSpellings(userFolders, identity.UserFolder);
            if (anonymize)
            {
                AddSpellings(projectFolders, identity.ProjectFolder);
                AddSpellings(unityFolders, identity.UnityFolder);
                string computer = (identity.ComputerName ?? string.Empty).Trim();
                computerName = computer.Length >= 3 && !CommonNames.Contains(computer) ? WordOf(new[] { Regex.Escape(computer) }) : null;
                userName = WordOf(UserNameSpellings(identity.UserName));
            }

            foreach (string secret in secrets ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
                {
                    this.secrets.Add(secret);
                }
            }
        }

        /// <summary>The privacy of this computer: its user folder, the signed-in Orbiters token and, anonymizing, its names. Main thread.</summary>
        internal static ReportPrivacy ForThisComputer(bool anonymize)
        {
            var identity = new Identity
            {
                UserFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UserName = Environment.UserName,
                ComputerName = Environment.MachineName,
                ProjectFolder = GitCli.ProjectRoot
            };
            try
            {
                identity.UnityFolder = Path.GetDirectoryName(UnityEditor.EditorApplication.applicationPath);
            }
            catch (Exception)
            {
                // Paths of the editor are then masked like any other.
            }

            return new ReportPrivacy(identity, anonymize, OrbitersLink.SafeToken());
        }

        /// <summary>Masks <paramref name="text"/>, then cuts it to <paramref name="max"/> characters (masking first: a secret cut in half no longer matches).</summary>
        internal string MaskAndClip(string text, int max) => LogReport.Clip(Mask(text), max);

        /// <summary>A masker for a file read line by line, which remembers what the previous lines announced.</summary>
        internal LineMasker Lines() => new LineMasker(this);

        /// <summary>Whether <paramref name="line"/> is a command-line flag alone whose value, on the next line, is masked.</summary>
        internal bool AnnouncesSecret(string line) =>
            !string.IsNullOrEmpty(line) && line.IndexOf('-') >= 0 && (SecretFlagLine.IsMatch(line) || Anonymize && IdentityFlagLine.IsMatch(line));

        internal string Mask(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            foreach (string secret in secrets)
            {
                if (text.IndexOf(secret, StringComparison.Ordinal) >= 0)
                {
                    text = text.Replace(secret, Masked);
                }
            }

            // Cheap checks first: most lines hold none of these.
            bool at = text.IndexOf('@') >= 0;
            bool address = text.IndexOf("://", StringComparison.Ordinal) >= 0 || text.IndexOf(":\\/\\/", StringComparison.Ordinal) >= 0;
            bool assigns = text.IndexOf('=') >= 0;
            bool colon = text.IndexOf(':') >= 0;
            bool quote = text.IndexOf('"') >= 0;
            bool dash = text.IndexOf('-') >= 0;
            bool lines = text.IndexOf('\n') >= 0;
            text = dash && lines && Has(text, "PRIVATE KEY-----") ? PrivateKeyBlock.Replace(text, match => match.Groups[1].Value + "\n" + Masked +
                (match.Groups[2].Success ? "\n" + match.Groups[2].Value : string.Empty)) : text;
            text = dash && lines ? SecretFlagValue.Replace(text, "$1" + Masked) : text;
            text = Has(text, "orbit-") ? OrbitersToken.Replace(text, Masked) : text;
            text = Has(text, "webhooks") ? DiscordWebhook.Replace(text, "$1" + Masked) : text;
            text = at && address ? UrlCredentials.Replace(text, "$1" + Masked + "@") : text;
            text = Has(text, "uthorization") ? AuthorizationHeader.Replace(text, "$1" + Masked) : text;
            text = Has(text, "bearer") ? Bearer.Replace(text, "$1" + Masked) : text;
            text = Has(text, "token ") || Has(text, "basic ") ? SchemeToken.Replace(text, "$1" + Masked) : text;
            text = KnownTokens.Replace(text, Masked);
            text = text.IndexOf("eyJ", StringComparison.Ordinal) >= 0 ? Jwt.Replace(text, Masked) : text;
            text = assigns && (text.IndexOf('?') >= 0 || text.IndexOf('&') >= 0) ? QuerySecret.Replace(text, "$1" + Masked) : text;
            if (quote && colon)
            {
                text = JsonSecret.Replace(text, "$1" + Masked);
                text = text.IndexOf("\\\"", StringComparison.Ordinal) >= 0 ? EscapedJsonSecret.Replace(text, "$1" + Masked) : text;
            }

            text = colon || assigns ? KeyValueSecret.Replace(text, QuoteMasked) : text;
            text = assigns ? WordAssignment.Replace(text, "$1" + Masked) : text;
            text = colon ? WordKey.Replace(text, "$1" + Masked) : text;
            text = dash ? CommandLineSecret.Replace(text, "$1" + Masked) : text;
            text = Has(text, "cookie:") ? CookieHeader.Replace(text, "$1" + Masked) : text;
            text = Has(text, "authcookie_") ? AuthCookie.Replace(text, "authcookie_" + Masked) : text;
            text = dash ? LicenseKey.Replace(text, Masked) : text;

            // The project and the editor first: they may sit in the user folder.
            text = Replace(text, projectFolders, ProjectFolder);
            text = Replace(text, unityFolders, UnityFolder);
            text = Replace(text, userFolders, UserFolder);
            text = text.IndexOf('~') >= 0 ? ShortUserFolder.Replace(text, UserFolder) : text;
            return Anonymize ? AnonymizeText(text) : text;
        }

        // A quoted value keeps its quotes around the mask.
        private static string QuoteMasked(Match match)
        {
            string quote = match.Groups[2].Value;
            return match.Groups[1].Value + quote + Masked + quote;
        }

        private string AnonymizeText(string text)
        {
            bool at = text.IndexOf('@') >= 0;
            bool encodedAt = text.IndexOf("%40", StringComparison.Ordinal) >= 0;
            text = text.IndexOf('-') >= 0 && text.IndexOf('\n') >= 0 ? IdentityFlagValue.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf(':') >= 0 ? GitTrailers.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf("://", StringComparison.Ordinal) >= 0 || text.IndexOf(":\\/\\/", StringComparison.Ordinal) >= 0 ? RemoteUrl.Replace(text, HiddenRemote) : text;
            text = at ? RemoteScp.Replace(text, HiddenRemote) : text;
            text = at || encodedAt ? Email.Replace(text, HiddenEmail) : text;
            // Names before the ids and flags that may hold them ("-licensingIpc LicenseClient-Jane Doe").
            text = computerName != null ? computerName.Replace(text, ComputerName) : text;
            text = userName != null ? userName.Replace(text, UserName) : text;
            text = text.IndexOf('_') >= 0 ? VrchatId.Replace(text, "$1_" + Masked) : text;
            text = Has(text, "id") ? MachineIds.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf('-') >= 0 ? HubArguments.Replace(text, "$1" + Masked) : text;
            text = Has(text, "serial") ? Serial.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf('-') >= 0 ? UnitySerial.Replace(text, Masked) : text;
            if (text.IndexOf('"') >= 0)
            {
                text = JsonIdentity.Replace(text, "$1" + Masked);
                text = text.IndexOf("\\\"", StringComparison.Ordinal) >= 0 ? EscapedJsonIdentity.Replace(text, "$1" + Masked) : text;
            }

            if (text.IndexOf(":\\", StringComparison.Ordinal) >= 0 || text.IndexOf(":/", StringComparison.Ordinal) >= 0 || text.IndexOf("\\\\", StringComparison.Ordinal) >= 0 ||
                text.IndexOf(UserFolder, StringComparison.Ordinal) >= 0)
            {
                text = WindowsPath.Replace(text, HidePath);
            }

            if (text.IndexOf('/') >= 0)
            {
                text = UnixPath.Replace(text, HidePath);
            }

            return text;
        }

        // The folders of an absolute path become [path]. A file name right after them stays, with the rest of the line;
        // anything else there is a folder or a name (what follows Users/ or home/) and goes too.
        private static string HidePath(Match match)
        {
            string root = match.Groups[1].Value;
            string folders = match.Groups[2].Value;
            string last = match.Groups[3].Value;
            if (folders.Length == 0 && last.Trim().Length == 0)
            {
                return match.Value;
            }

            string separator = TrailingSeparator.Match(folders.Length > 0 ? folders : root).Value;
            if (last.Length > 0 && char.IsWhiteSpace(last[0]))
            {
                // The path ended with its folder: the line goes on.
                return HiddenPath + separator + last;
            }

            bool userRoot = root.StartsWith("/", StringComparison.Ordinal) && folders.Length == 0 || UserRootFolder.IsMatch(folders);
            var file = userRoot ? Match.Empty : FileName.Match(last);
            return file.Success ? HiddenPath + separator + last : HiddenPath + (last.Length == 0 ? separator : string.Empty);
        }

        /// <summary>One line for the README and the window: what this report's texts hide.</summary>
        internal string Describe() => Anonymize
            ? "Anonymized: user and computer names, machine and session ids, serials, emails, Git authors, remote addresses (" + HiddenRemote + "), avatar and " +
              "blueprint ids and paths outside the project (" + HiddenPath + ") are masked; the project folder is written " + ProjectFolder +
              ". Release pictures and the scene export are never in an anonymized report: they can't be anonymized. Tokens, keys, passwords, sign-in headers, cookies and " +
              "webhooks are " + Masked + "."
            : "The user folder is written " + UserFolder + "; tokens, API and license keys, passwords, sign-in headers, cookies and Discord webhooks are " + Masked +
              ". Not anonymized: Unity's logs hold the Windows user name, computer name, machine id, license serial and project paths, and the Git history its " +
              "authors' names and emails.";

        private static bool Has(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void AddSpellings(List<string> into, string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Trim().Length <= 3)
            {
                return;
            }

            string trimmed = folder.Trim().TrimEnd('\\', '/');
            string back = trimmed.Replace('/', '\\');
            string slash = trimmed.Replace('\\', '/');
            // As written in JSON ("C:\\Users\\Name", "C:\/Users"), in JSON inside a string (C:\\\\Users), with either
            // slash, and in addresses.
            foreach (string spelling in new[]
                     {
                         back.Replace("\\", "\\\\\\\\"), back.Replace("\\", "\\\\"), back, slash, slash.Replace("/", "\\/"), slash.Replace(" ", "%20"),
                         Uri.EscapeDataString(back), Uri.EscapeDataString(slash)
                     })
            {
                if (!into.Contains(spelling, StringComparer.OrdinalIgnoreCase))
                {
                    into.Add(spelling);
                }
            }
        }

        // The user name, its spellings in addresses and as one word (JaneDoe, jane_doe, jane.doe), and its 8.3 short form
        // (JANEDO~1); never a common word.
        private static IEnumerable<string> UserNameSpellings(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length < 3 || CommonNames.Contains(name))
            {
                yield break;
            }

            yield return Regex.Escape(name);
            if (name.IndexOf(' ') >= 0)
            {
                foreach (string joined in new[] { "%20", "+", string.Empty, "_", ".", "-" })
                {
                    yield return Regex.Escape(name.Replace(" ", joined));
                }
            }

            var shortName = new StringBuilder();
            foreach (char c in name.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c) && shortName.Length < 6)
                {
                    shortName.Append(c);
                }
            }

            if (shortName.Length >= 3)
            {
                yield return Regex.Escape(shortName.ToString()) + @"~\d{1,3}";
            }
        }

        // Whole words only, from patterns already escaped; null without any (an empty alternation would match everywhere).
        private static Regex WordOf(IEnumerable<string> patterns)
        {
            var list = patterns.Where(p => !string.IsNullOrEmpty(p)).ToList();
            return list.Count == 0 ? null : new Regex(@"(?<![A-Za-z0-9])(?:" + string.Join("|", list) + @")(?![A-Za-z0-9])", Options);
        }

        private static string Replace(string text, List<string> spellings, string replacement)
        {
            foreach (string spelling in spellings)
            {
                text = ReplaceIgnoringCase(text, spelling, replacement);
            }

            return text;
        }

        // Only whole folders: "C:\Users\Jane" is not replaced inside "C:\Users\Janet", "C:\Users\Jane.old" or "…\Avatar-Backup".
        private static string ReplaceIgnoringCase(string text, string value, string replacement)
        {
            int at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                int end = at + value.Length;
                if (end < text.Length && (char.IsLetterOrDigit(text[end]) ||
                                          (text[end] == '.' || text[end] == '_' || text[end] == '-') && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])))
                {
                    at = text.IndexOf(value, at + 1, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                builder.Append(text, from, at - from).Append(replacement);
                from = end;
                at = text.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
            }

            return builder.Append(text, from, text.Length - from).ToString();
        }

        /// <summary>
        /// Masks a file line by line (Editor.log): each line as <see cref="Mask"/> does, the line after a secret flag
        /// alone (its value), and the lines of a private key block.
        /// </summary>
        internal sealed class LineMasker
        {
            private readonly ReportPrivacy privacy;
            private bool valueNext;
            private bool inKey;

            public LineMasker(ReportPrivacy privacy) => this.privacy = privacy;

            public string Mask(string line)
            {
                line = line ?? string.Empty;
                string trimmed = line.TrimStart();
                string indent = line.Substring(0, line.Length - trimmed.Length);
                if (inKey)
                {
                    inKey = !PrivateKeyEnd.IsMatch(line);
                    return inKey ? (trimmed.Length == 0 ? line : indent + Masked) : privacy.Mask(line);
                }

                if (valueNext)
                {
                    valueNext = false;
                    if (trimmed.Length > 0 && !trimmed.StartsWith("-", StringComparison.Ordinal))
                    {
                        return indent + Masked;
                    }
                }

                valueNext = privacy.AnnouncesSecret(line);
                inKey = line.IndexOf('-') >= 0 && PrivateKeyBegin.IsMatch(line) && !PrivateKeyEnd.IsMatch(line);
                return privacy.Mask(line);
            }
        }
    }
}
