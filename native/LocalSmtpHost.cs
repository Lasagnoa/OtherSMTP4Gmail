using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Chrome Native Messaging host. The program handles one request per process.
internal static partial class LocalSmtpHost {
    const int MaxInput = 16 * 1024 * 1024;
    const int MaxFiles = 10;
    const int MaxFileBytes = 10 * 1024 * 1024;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxInput, RecursionLimit = 32 };
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

    static void Main() {
        Dictionary<string, object> result;
        try {
            var request = ReadRequest();
            var action = Text(request, "action", 20);
            var settings = LoadSettings();
            var accounts = settings.Accounts;
            if (action == "status") {
                result = new Dictionary<string, object> { {"ok", true}, {"accounts", accounts.Select(a => new Dictionary<string, object> { {"id", a.Key}, {"from", a.Value["from"]}, {"copyConfigured", a.Value["bcc"].Length > 0} }).ToArray()}, {"order", settings.Order} };
            }
            else if (action == "getSettings") result = PublicSettings(settings);
            else if (action == "saveSettings") result = SaveSettings(request);
            else if (action == "send") {
                string id = Text(request, "accountId", 100);
                string expectedFrom = Text(request, "expectedFrom", 254);
                var account = accounts.FirstOrDefault(a => String.Equals(a.Key, id, StringComparison.OrdinalIgnoreCase));
                if (account.Value == null || expectedFrom.Length == 0 || !String.Equals(account.Value["from"], expectedFrom, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("送信元設定が変更されています。Gmailを再読み込みしてください。");
                result = Send(request, account.Value, settings.MailSendPath);
            }
            else throw new InvalidOperationException("不明な要求です。");
        } catch (Exception ex) {
            result = new Dictionary<string, object> { {"ok", false}, {"uncertain", false}, {"error", "設定または入力を確認してください: " + ex.Message} };
        }
        try { WriteResponse(result); } catch { }
    }

    static Dictionary<string, object> ReadRequest() {
        using (var input = Console.OpenStandardInput()) {
            byte[] head = ReadExactly(input, 4);
            int n = BitConverter.ToInt32(head, 0);
            if (n < 2 || n > MaxInput) throw new InvalidOperationException("要求サイズが上限を超えました。");
            return Json.DeserializeObject(Encoding.UTF8.GetString(ReadExactly(input, n))) as Dictionary<string, object> ?? throwError();
        }
    }
    static Dictionary<string, object> throwError() { throw new InvalidOperationException("要求形式が正しくありません。"); }
    static byte[] ReadExactly(Stream s, int n) {
        byte[] b = new byte[n]; int p = 0;
        while (p < n) { int count = s.Read(b, p, n-p); if (count == 0) throw new EndOfStreamException(); p += count; }
        return b;
    }
    static void WriteResponse(Dictionary<string, object> obj) {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(obj));
        using (var output = Console.OpenStandardOutput()) { var size = BitConverter.GetBytes(bytes.Length); output.Write(size,0,4); output.Write(bytes,0,bytes.Length); output.Flush(); }
    }
    static string Text(Dictionary<string, object> obj, string key, int max) {
        object v; var value = obj.TryGetValue(key, out v) && v != null ? v as string : "";
        if (value == null || value.Length > max || value.IndexOf('\0') >= 0) throw new InvalidOperationException(key + " が不正です。");
        return value;
    }
    static List<KeyValuePair<string, Dictionary<string, string>>> ReadConfigs(string rawText) {
        const int maxAccounts = 10;
        var accounts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> cfg = null;
        string countValue = null;
        bool seenSection = false;
        foreach (string raw in Regex.Split(rawText.TrimStart('\uFEFF'), "\\r?\\n")) {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith("]")) {
                seenSection = true;
                string section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                cfg = null;
                if (Regex.IsMatch(section, @"^smtp[0-9]*$")) {
                    if (!Regex.IsMatch(section, @"^smtp(?:[1-9]|10)$")) throw new InvalidOperationException("SMTPセクション名は [smtp1] から [smtp10] までです。");
                    if (accounts.ContainsKey(section)) throw new InvalidOperationException("SMTPセクション名が重複しています: [" + section + "]");
                    cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    accounts.Add(section, cfg);
                }
                continue;
            }
            int p = line.IndexOf('=');
            if (p <= 0) continue;
            string key = line.Substring(0,p).Trim(), value = line.Substring(p+1).Trim();
            if (!seenSection && String.Equals(key, "account", StringComparison.OrdinalIgnoreCase)) {
                if (countValue != null) throw new InvalidOperationException("account の指定が重複しています。");
                countValue = value;
            } else if (cfg != null) {
                if (cfg.ContainsKey(key)) throw new InvalidOperationException("SMTP設定項目が重複しています: " + key);
                cfg.Add(key, value);
            }
        }
        int count;
        if (countValue == null || !Regex.IsMatch(countValue, @"^(?:[0-9]|10)$") || !Int32.TryParse(countValue, out count))
            throw new InvalidOperationException("先頭の account には、登録アカウント数を0から10で指定してください。");
        if (accounts.Count != count) throw new InvalidOperationException("account の登録数とSMTPセクションの数が一致しません。");
        var ordered = new List<KeyValuePair<string, Dictionary<string, string>>>();
        for (int i = 1; i <= maxAccounts; i++) {
            string name = "smtp" + i;
            Dictionary<string, string> account;
            if (!accounts.TryGetValue(name, out account)) continue;
            try { ValidateConfig(account); }
            catch (Exception ex) { throw new InvalidOperationException("[" + name + "] " + ex.Message); }
            ordered.Add(new KeyValuePair<string, Dictionary<string, string>>(name, account));
        }
        return ordered;
    }
    static void ValidateConfig(Dictionary<string, string> cfg) {
        foreach (var key in new[] {"host", "port", "security", "verify_certificate", "from"})
            if (!cfg.ContainsKey(key) || cfg[key].Length == 0) throw new InvalidOperationException("smtp.ini の " + key + " がありません。");
        if (!cfg.ContainsKey("bcc")) cfg["bcc"] = "";
        if (!cfg.ContainsKey("username")) cfg["username"] = "";
        if (!cfg.ContainsKey("password")) cfg["password"] = "";
        if (!cfg.ContainsKey("credential_id")) cfg["credential_id"] = "";
        if (!cfg.ContainsKey("display_name")) cfg["display_name"] = "";
        bool plaintext = cfg["password"].Length > 0, stored = cfg["credential_id"].Length > 0;
        if (plaintext && stored) throw new InvalidOperationException("パスワードと資格情報の両方が設定されています。");
        Guid credentialGuid;
        if (stored && (cfg["credential_id"].Length != 32 || !Guid.TryParseExact(cfg["credential_id"],"N",out credentialGuid))) throw new InvalidOperationException("資格情報の識別子が不正です。");
        if ((cfg["username"].Length == 0) == (plaintext || stored)) throw new InvalidOperationException("ユーザー名とパスワードは両方入力するか、両方空欄にしてください。");
        int port; if (!Int32.TryParse(cfg["port"], out port) || port < 1 || port > 65535) throw new InvalidOperationException("SMTPポートが不正です。");
        if (!new[]{"starttls","ssl","tls","none"}.Contains(cfg["security"].ToLowerInvariant())) throw new InvalidOperationException("暗号化方式が不正です。");
        if (!new[]{"true","false"}.Contains(cfg["verify_certificate"].ToLowerInvariant())) throw new InvalidOperationException("証明書の検証設定が不正です。");
        if (cfg["display_name"].Length > 200 || cfg["display_name"].IndexOfAny(new[]{'\r','\n','\0'}) >= 0) throw new InvalidOperationException("送信元表示名が不正です。");
        if (!Regex.IsMatch(cfg["host"], @"^[A-Za-z0-9.-]{1,253}$")) throw new InvalidOperationException("SMTPホストが不正です。");
        Address(cfg["from"]); AddressList(cfg["bcc"]);
    }
    static string Address(string input) {
        string s = input.Trim();
        if (s.Length > 254 || !Regex.IsMatch(s, @"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")) throw new InvalidOperationException("メールアドレスの形式を確認してください。");
        try { var m = new MailAddress(s); if (!String.Equals(m.Address, s, StringComparison.OrdinalIgnoreCase)) throw new Exception(); } catch { throw new InvalidOperationException("メールアドレスの形式を確認してください。"); }
        return s;
    }
    static List<string> AddressList(string value) {
        return value.Split(new[] {',',';'}, StringSplitOptions.RemoveEmptyEntries).Select(Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    static List<string> Addresses(Dictionary<string, object> req, string key) {
        object val; if (!req.TryGetValue(key, out val) || val == null) return new List<string>();
        var arr = val as object[]; if (arr == null || arr.Length > 100) throw new InvalidOperationException(key + " が不正です。");
        var result = new List<string>();
        foreach (object v in arr) { if (!(v is string)) throw new InvalidOperationException("宛先が不正です。"); result.Add(Address((string)v)); }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    static string Header(string value) {
        if (value.Length == 0) return value;
        if (value.Length > 4000 || value.IndexOfAny(new[] {'\r','\n','\0'}) >= 0 || !Regex.IsMatch(value, @"^(<[^<>\s@]+@[^<>\s@]+>)(\s+<[^<>\s@]+@[^<>\s@]+>)*$")) throw new InvalidOperationException("返信ヘッダーが不正です。");
        return value;
    }
    static Dictionary<string, object> Send(Dictionary<string, object> req, Dictionary<string, string> cfg, string smtp) {
        cfg = new Dictionary<string,string>(cfg, StringComparer.OrdinalIgnoreCase);
        if (cfg["username"].Length > 0) cfg["password"] = CredentialStore.Read(cfg["credential_id"]);
        var to = Addresses(req, "to"); var cc = Addresses(req, "cc"); var bcc = Addresses(req, "bcc");
        foreach (var a in AddressList(cfg["bcc"])) if (!to.Concat(cc).Concat(bcc).Contains(a, StringComparer.OrdinalIgnoreCase)) bcc.Add(a);
        if (to.Count == 0) throw new InvalidOperationException("Toに宛先を指定してください。");
        var subject = Text(req,"subject",998); if (subject.IndexOfAny(new[]{'\r','\n'}) >= 0) throw new InvalidOperationException("件名に改行は使えません。");
        var body = Text(req,"body",1000000);
        if (body.Length == 0) throw new InvalidOperationException("本文に1文字以上入力してください。");
        if (body.Contains("\\n")) throw new InvalidOperationException("本文の文字列 \\n がmailsend-goで改行に変換されるため、送信を中止しました。");
        var reply = Header(Text(req,"inReplyTo",998)); var refs = Header(Text(req,"references",4000));
        object filesObject; req.TryGetValue("attachments", out filesObject);
        var files = filesObject == null ? new object[0] : filesObject as object[];
        if (files == null || files.Length > MaxFiles) throw new InvalidOperationException("添付ファイルは最大10個です。");
        if (!File.Exists(smtp)) throw new InvalidOperationException("送信プログラムが見つかりません。設定画面でパスを確認してください。");
        using (var mutex = new Mutex(false, @"Local\LocalSmtpGmailSend")) {
            bool entered = false;
            try {
                try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new InvalidOperationException("別のメールを送信中です。");
                string dir = Path.Combine(Path.GetTempPath(), "local-smtp-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try {
                    string bodyPath = Path.Combine(dir, "body.txt"); File.WriteAllText(bodyPath, body, new UTF8Encoding(false));
                    var args = new List<string> {"-smtp",cfg["host"],"-port",cfg["port"],"-f",cfg["from"],"-to",String.Join(",",to),"-sub",subject,"-cs","utf-8"};
                    string mode = cfg["security"].ToLowerInvariant();
                    if (String.Equals(cfg["verify_certificate"], "true", StringComparison.OrdinalIgnoreCase)) args.Add("-verifyCert");
                    if (mode == "ssl" || mode == "tls") args.Add("-ssl");
                    if (cfg["display_name"].Length > 0) args.AddRange(new[]{"-fname",cfg["display_name"]});
                    if (cc.Count > 0) { args.Add("-cc"); args.Add(String.Join(",",cc)); }
                    if (bcc.Count > 0) { args.Add("-bcc"); args.Add(String.Join(",",bcc)); }
                    if (cfg["username"].Length > 0) args.AddRange(new[]{"auth","-user",cfg["username"]});
                    args.AddRange(new[]{"body","-file",bodyPath,"-mime-type","text/plain"});
                    int total = 0;
                    var preparedFiles = new List<KeyValuePair<string,string>>();
                    for (int i=0; i<files.Length; i++) {
                        var f = files[i] as Dictionary<string, object>; if (f == null) throw new InvalidOperationException("添付データが不正です。");
                        string name = Text(f,"name",255); string base64 = Text(f,"content",MaxInput);
                        if (name.Length == 0 || name.IndexOfAny(new[]{'\r','\n','\0','/','\\','"'}) >= 0) throw new InvalidOperationException("添付ファイル名が不正です。");
                        byte[] bytes; try { bytes = Convert.FromBase64String(base64); } catch { throw new InvalidOperationException("添付データを読み取れません。"); }
                        total += bytes.Length; if (total > MaxFileBytes) throw new InvalidOperationException("添付は合計10 MiBまでです。");
                        string ext = Path.GetExtension(name); if (!Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{1,10}$")) ext = ".bin";
                        string path = Path.Combine(dir, "attachment_" + i + ext); File.WriteAllBytes(path, bytes);
                        args.AddRange(new[]{"attach","-file",path,"-name",name});
                        preparedFiles.Add(new KeyValuePair<string,string>(path,name));
                    }
                    if (reply.Length > 0) args.AddRange(new[]{"header","-name","In-Reply-To","-value",reply});
                    if (refs.Length > 0) args.AddRange(new[]{"header","-name","References","-value",refs});
                    // mailsend-go enforces STARTTLS for authenticated SMTP, but
                    // can downgrade unauthenticated STARTTLS. Use .NET for these
                    // two explicitly configured cases; never silently downgrade.
                    if (mode == "none" || (mode == "starttls" && cfg["username"].Length == 0))
                        return SendWithSystemSmtp(cfg, to, cc, bcc, subject, body, reply, refs, preparedFiles);
                    var start = new ProcessStartInfo { FileName = smtp, Arguments = String.Join(" ", args.Select(Quote)), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.EnvironmentVariables["SMTP_USER_PASS"] = cfg["password"];
                    using (var proc = new Process { StartInfo = start }) {
                        try { if (!proc.Start()) throw new Exception(); } catch { return Error("送信プログラムを起動できませんでした。", false); }
                        var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(120000)) { try { proc.Kill(); } catch {} return Error("送信結果を確認できません。再送前に履歴を確認してください。", true); }
                        if (proc.ExitCode != 0) return Error("送信に失敗したか、結果を確認できません。再送前に履歴を確認してください。", true);
                        return new Dictionary<string, object> {{"ok",true}, {"copyConfigured", cfg["bcc"].Length > 0}};
                    }
                } finally { try { Directory.Delete(dir, true); } catch {} }
            } finally { if (entered) mutex.ReleaseMutex(); }
        }
    }
    static Dictionary<string, object> SendWithSystemSmtp(Dictionary<string, string> cfg, List<string> to, List<string> cc, List<string> bcc, string subject, string body, string reply, string refs, List<KeyValuePair<string,string>> files) {
        try {
            using (var message = new MailMessage())
            using (var client = new SmtpClient(cfg["host"], Int32.Parse(cfg["port"]))) {
                message.From = new MailAddress(cfg["from"], cfg["display_name"], Encoding.UTF8);
                foreach (var address in to) message.To.Add(address);
                foreach (var address in cc) message.CC.Add(address);
                foreach (var address in bcc) message.Bcc.Add(address);
                message.Subject = subject; message.SubjectEncoding = Encoding.UTF8;
                message.Body = body; message.BodyEncoding = Encoding.UTF8; message.IsBodyHtml = false;
                if (reply.Length > 0) message.Headers.Add("In-Reply-To", reply);
                if (refs.Length > 0) message.Headers.Add("References", refs);
                foreach (var file in files) { var attachment = new Attachment(file.Key); attachment.Name = file.Value; attachment.NameEncoding = Encoding.UTF8; message.Attachments.Add(attachment); }
                client.Timeout = 120000; client.EnableSsl = cfg["security"].ToLowerInvariant() == "starttls";
                client.UseDefaultCredentials = false;
                if (cfg["username"].Length > 0) client.Credentials = new NetworkCredential(cfg["username"], cfg["password"]);
                if (client.EnableSsl && String.Equals(cfg["verify_certificate"], "false", StringComparison.OrdinalIgnoreCase))
                    ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
                client.Send(message);
                return new Dictionary<string, object> {{"ok",true}, {"copyConfigured", cfg["bcc"].Length > 0}};
            }
        } catch { return Error("送信に失敗したか、結果を確認できません。再送前に履歴を確認してください。", true); }
    }
    static Dictionary<string, object> Error(string message, bool uncertain) { return new Dictionary<string, object> {{"ok",false},{"uncertain",uncertain},{"error",message}}; }
    // Windows CreateProcess quoting, with no cmd.exe or shell expansion.
    static string Quote(string value) {
        var b = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value) {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); }
            else { b.Append('\\', slashes); b.Append(ch); }
            slashes = 0;
        }
        b.Append('\\', slashes * 2); return b.Append('"').ToString();
    }
}
