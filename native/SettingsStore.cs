using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static partial class LocalSmtpHost {
    sealed class SettingsData {
        public string Raw;
        public string Revision;
        public string MailSendPath;
        public List<KeyValuePair<string, Dictionary<string, string>>> Accounts;
        public List<string> Order;
    }
    sealed class IniBlock {
        public string Name;
        public List<string> Lines = new List<string>();
    }
    static string Digest(byte[] bytes) {
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
    }
    static SettingsData LoadSettingsCore() {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Root, "smtp.ini"));
        string raw = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var accounts = ReadConfigs(raw);
        string orderValue = null, mailSendValue = null;
        foreach (string rawLine in Regex.Split(raw, "\\r?\\n")) {
            string line = rawLine.Trim();
            if (line.StartsWith("[")) break;
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            int p = line.IndexOf('=');
            if (p > 0 && String.Equals(line.Substring(0,p).Trim(), "order", StringComparison.OrdinalIgnoreCase)) {
                if (orderValue != null) throw new InvalidOperationException("order の指定が重複しています。");
                orderValue = line.Substring(p+1).Trim();
            }
            if (p > 0 && String.Equals(line.Substring(0,p).Trim(), "mailsend_path", StringComparison.OrdinalIgnoreCase)) {
                if (mailSendValue != null) throw new InvalidOperationException("mailsend_path の指定が重複しています。");
                mailSendValue = line.Substring(p+1).Trim();
            }
        }
        var ids = accounts.Select(a => a.Key).ToList();
        List<string> order;
        if (orderValue == null) {
            order = new List<string>(ids);
            order.Insert(Math.Min(1, order.Count), "gmail");
        } else {
            order = orderValue.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToList();
            CheckOrder(order, ids);
        }
        if (String.IsNullOrEmpty(mailSendValue)) mailSendValue = "mailsend-go.exe";
        if (!Path.IsPathRooted(mailSendValue)) {
            if (!String.Equals(mailSendValue,"mailsend-go.exe",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("mailsend_path のパスを確認してください。");
            mailSendValue = Path.Combine(Root,mailSendValue);
        }
        string fullPath = Path.GetFullPath(mailSendValue);
        if (!String.Equals(Path.GetExtension(fullPath),".exe",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("送信プログラムにexeファイルを指定してください。");
        return new SettingsData {Raw=raw, Revision=Digest(bytes), Accounts=accounts, Order=order, MailSendPath=fullPath};
    }
    static SettingsData LoadSettings() {
        var settings = LoadSettingsCore();
        if (!HasPasswordFields(settings.Raw)) return settings;
        using (var mutex = new Mutex(false, @"Local\LocalSmtpGmailSettings")) {
            bool entered = false, committed = false;
            var staged = new List<string>();
            try {
                try { entered = mutex.WaitOne(10000); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new InvalidOperationException("別の画面で設定を保存中です。");
                settings = LoadSettingsCore();
                var ids = settings.Accounts.ToDictionary(a => a.Key, a => a.Value["credential_id"], StringComparer.OrdinalIgnoreCase);
                foreach (var account in settings.Accounts.Where(a => a.Value["password"].Length > 0)) {
                    string password = account.Value["password"];
                    string id = CredentialStore.Create(password); staged.Add(id);
                    if (CredentialStore.Read(id) != password) throw new InvalidOperationException("資格情報の保存を確認できませんでした。");
                    ids[account.Key] = id;
                }
                if (HasPasswordFields(settings.Raw)) {
                    WriteIniAtomically(settings, MigrateIni(settings.Raw, ids));
                    committed = true;
                    return LoadSettingsCore();
                }
                return settings;
            } finally {
                if (!committed) foreach (string id in staged) { try { CredentialStore.Delete(id); } catch { } }
                if (entered) mutex.ReleaseMutex();
            }
        }
    }
    static bool HasPasswordFields(string raw) {
        bool smtp = false;
        foreach (string line in Regex.Split(raw,"\\r?\\n")) {
            var section = Regex.Match(line,@"^\s*\[([^\]]+)\]\s*$");
            if (section.Success) smtp = Regex.IsMatch(section.Groups[1].Value.Trim(),@"^smtp(?:[1-9]|10)$",RegexOptions.IgnoreCase);
            else if (smtp && Regex.IsMatch(line,@"^\s*password\s*=",RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }
    static void RemovePasswordField(List<string> lines) {
        lines.RemoveAll(line => Regex.IsMatch(line,@"^\s*password\s*=",RegexOptions.IgnoreCase)
            || line.Trim() == "; SMTP認証のパスワード。平文で保存されるため、このファイルを他人に公開しない。"
            || line.Trim() == "; SMTP認証パスワードはWindowsの資格情報マネージャーに保存。ここは空欄。");
    }
    static void WriteIniAtomically(SettingsData old, string output) {
        string destination = Path.Combine(Root,"smtp.ini"), temporary = Path.Combine(Root,"smtp.ini." + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            File.WriteAllText(temporary, output, new UTF8Encoding(true));
            if (Digest(File.ReadAllBytes(destination)) != old.Revision) throw new InvalidOperationException("設定が別の場所で変更されました。画面を開き直してください。");
            File.Replace(temporary, destination, null);
        } finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
    static string MigrateIni(string raw, Dictionary<string,string> ids) {
        var output = new List<string>(); var block = new List<string>(); string name = null;
        Action flush = () => {
            string id;
            if (name != null && ids.TryGetValue(name,out id)) {
                RemovePasswordField(block);
                if (id.Length > 0) SetIniValue(block,"credential_id",id);
            }
            output.AddRange(block); block.Clear();
        };
        foreach (string line in Regex.Split(raw.TrimStart('\uFEFF'),"\\r?\\n")) {
            var match = Regex.Match(line,@"^\s*\[([^\]]+)\]\s*$");
            if (match.Success) { flush(); name = match.Groups[1].Value.Trim(); }
            block.Add(line);
        }
        flush();
        return String.Join("\r\n",output);
    }
    static void CheckOrder(List<string> order, List<string> ids) {
        var expected = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase); expected.Add("gmail");
        if (order.Count != expected.Count || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count || order.Any(x => !expected.Contains(x)))
            throw new InvalidOperationException("送信元の表示順が設定と一致しません。");
    }
    static Dictionary<string, object> PublicSettings(SettingsData settings) {
        return new Dictionary<string, object> {
            {"ok",true}, {"revision",settings.Revision}, {"order",settings.Order}, {"mailsend_path",settings.MailSendPath},
            {"accounts",settings.Accounts.Select(a => new Dictionary<string, object> {
                {"id",a.Key},{"from",a.Value["from"]},{"host",a.Value["host"]},{"port",a.Value["port"]},
                {"security",a.Value["security"].ToLowerInvariant()=="tls"?"ssl":a.Value["security"].ToLowerInvariant()},
                {"verify_certificate",String.Equals(a.Value["verify_certificate"],"true",StringComparison.OrdinalIgnoreCase)},
                {"username",a.Value["username"]},{"hasPassword",a.Value["credential_id"].Length>0 && CredentialStore.Exists(a.Value["credential_id"])},
                {"display_name",a.Value["display_name"]},{"bcc",a.Value["bcc"]}
            }).ToArray()}
        };
    }
    static string ConfigValue(Dictionary<string, object> obj, string key, int max) {
        string value = Text(obj,key,max);
        if (value.IndexOfAny(new[]{'\r','\n','\0'}) >= 0 || value != value.Trim()) throw new InvalidOperationException(key + " の前後の空白・改行を確認してください。");
        return value;
    }
    static Dictionary<string, object> SaveSettings(Dictionary<string, object> request) {
        using (var mutex = new Mutex(false, @"Local\LocalSmtpGmailSettings")) {
            bool entered = false;
            try {
                try { entered = mutex.WaitOne(10000); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new InvalidOperationException("別の画面で設定を保存中です。");
                var old = LoadSettings();
                if (!String.Equals(Text(request,"revision",100), old.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("設定が別の場所で変更されました。画面を開き直してください。");
                string programPath = ConfigValue(request,"mailsend_path",32760);
                if (!Path.IsPathRooted(programPath) || !String.Equals(Path.GetExtension(programPath),".exe",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("送信プログラムにexeファイルを含む絶対パスを指定してください。");
                programPath = Path.GetFullPath(programPath);
                if (!File.Exists(programPath)) throw new InvalidOperationException("指定された送信プログラムが見つかりません。");
                object itemsValue, orderValue;
                request.TryGetValue("accounts",out itemsValue); request.TryGetValue("order",out orderValue);
                var items = itemsValue as object[]; var orderItems = orderValue as object[];
                if (items == null || items.Length > 10 || orderItems == null) throw new InvalidOperationException("SMTPアカウントは最大10件です。");
                var next = new Dictionary<string, Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items) {
                    var row = item as Dictionary<string,object>; if (row == null) throw new InvalidOperationException("アカウント設定が不正です。");
                    string id = ConfigValue(row,"id",20).ToLowerInvariant();
                    if (!Regex.IsMatch(id, @"^smtp(?:[1-9]|10)$") || next.ContainsKey(id)) throw new InvalidOperationException("アカウントの識別子が重複または不正です。");
                    var previous = old.Accounts.FirstOrDefault(a => a.Key == id).Value;
                    var values = previous == null ? new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string,string>(previous,StringComparer.OrdinalIgnoreCase);
                    foreach (string key in new[]{"host","port","security","username","from","display_name","bcc"}) values[key] = ConfigValue(row,key,key=="display_name"?200:1024);
                    object verify; if (!row.TryGetValue("verify_certificate",out verify) || !(verify is bool)) throw new InvalidOperationException("証明書の検証設定が不正です。");
                    values["verify_certificate"] = (bool)verify ? "true" : "false";
                    string password = ConfigValue(row,"password",1024);
                    if (values["username"].Length == 0) {
                        if (password.Length > 0) throw new InvalidOperationException("パスワードを指定する場合はユーザー名も入力してください。");
                        values["password"] = ""; values["credential_id"] = "";
                    } else if (password.Length > 0) {
                        values["password"] = password; values["credential_id"] = "";
                    } else if (previous == null || previous["credential_id"].Length == 0 || !CredentialStore.Exists(previous["credential_id"]))
                        throw new InvalidOperationException("アカウントのパスワードを入力してください。");
                    else { values["password"] = ""; values["credential_id"] = previous["credential_id"]; }
                    ValidateConfig(values); next.Add(id,values);
                }
                var order = new List<string>();
                foreach (var item in orderItems) {
                    var id = item as string; if (id == null) throw new InvalidOperationException("表示順が不正です。"); order.Add(id.ToLowerInvariant());
                }
                CheckOrder(order,next.Keys.ToList());
                string localProgram = Path.GetFullPath(Path.Combine(Root,"mailsend-go.exe"));
                string storedProgram = String.Equals(programPath,localProgram,StringComparison.OrdinalIgnoreCase) ? "mailsend-go.exe" : programPath;
                var staged = new List<string>(); bool committed = false;
                try {
                    foreach (var values in next.Values.Where(v => v["password"].Length > 0)) {
                        string password = values["password"];
                        string id = CredentialStore.Create(password); staged.Add(id);
                        if (CredentialStore.Read(id) != password) throw new InvalidOperationException("資格情報の保存を確認できませんでした。");
                        values["password"] = ""; values["credential_id"] = id;
                    }
                    WriteIniAtomically(old, UpdateIni(old.Raw, next, order, storedProgram));
                    committed = true;
                    var retained = new HashSet<string>(next.Values.Select(v => v["credential_id"]).Where(id => id.Length > 0),StringComparer.OrdinalIgnoreCase);
                    bool cleanupFailed = false;
                    foreach (string id in old.Accounts.Select(a => a.Value["credential_id"]).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                        if (!retained.Contains(id)) try { CredentialStore.Delete(id); } catch { cleanupFailed = true; }
                    var result = PublicSettings(LoadSettingsCore());
                    if (cleanupFailed) result["warning"] = "古いWindows資格情報を削除できませんでした。";
                    return result;
                } finally { if (!committed) foreach (string id in staged) { try { CredentialStore.Delete(id); } catch { } } }
            } finally { if (entered) mutex.ReleaseMutex(); }
        }
    }
    static void SetIniValue(List<string> lines, string key, string value) {
        var re = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=",RegexOptions.IgnoreCase);
        int index = lines.FindIndex(line => re.IsMatch(line));
        if (index >= 0) lines[index] = key + "=" + value;
        else lines.Add(key + "=" + value);
    }
    static string UpdateIni(string raw, Dictionary<string,Dictionary<string,string>> values, List<string> order, string programPath) {
        var prefix = new List<string>(); var blocks = new List<IniBlock>(); IniBlock current = null;
        foreach (string line in Regex.Split(raw.TrimStart('\uFEFF'),"\\r?\\n")) {
            var match = Regex.Match(line,@"^\s*\[([^\]]+)\]\s*$");
            if (match.Success) { current = new IniBlock {Name=match.Groups[1].Value.Trim().ToLowerInvariant()}; current.Lines.Add(line); blocks.Add(current); }
            else if (current == null) prefix.Add(line); else current.Lines.Add(line);
        }
        SetIniValue(prefix,"account",values.Count.ToString());
        if (!prefix.Any(line=>Regex.IsMatch(line,@"^\s*order\s*=",RegexOptions.IgnoreCase))) prefix.Add("; 送信元の表示順。先頭がデフォルト。gmailはGmail通常送信。");
        SetIniValue(prefix,"order",String.Join(",",order));
        if (!prefix.Any(line=>Regex.IsMatch(line,@"^\s*mailsend_path\s*=",RegexOptions.IgnoreCase))) prefix.Add("; mailsend-go.exe の保存場所。プログラムと同じフォルダーの場合はファイル名だけでも指定可能。");
        SetIniValue(prefix,"mailsend_path",programPath);
        var output = new List<string>(prefix); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new[]{"host","security","port","verify_certificate","username","credential_id","from","display_name","bcc"};
        foreach (var block in blocks) {
            if (!Regex.IsMatch(block.Name,@"^smtp(?:[1-9]|10)$",RegexOptions.IgnoreCase)) { output.AddRange(block.Lines); continue; }
            Dictionary<string,string> cfg;
            if (!values.TryGetValue(block.Name,out cfg)) continue;
            RemovePasswordField(block.Lines);
            foreach (string key in keys) {
                if (!block.Lines.Any(line=>Regex.IsMatch(line,@"^\s*" + Regex.Escape(key) + @"\s*=",RegexOptions.IgnoreCase))) block.Lines.Add("; " + SettingDescription(key));
                SetIniValue(block.Lines,key,cfg[key]);
            }
            output.AddRange(block.Lines); used.Add(block.Name);
        }
        foreach (var item in values.Where(v=>!used.Contains(v.Key)).OrderBy(v=>Int32.Parse(v.Key.Substring(4)))) {
            output.Add(""); output.Add("; " + item.Key + " の送信設定。"); output.Add("[" + item.Key + "]");
            foreach (string key in keys) { output.Add("; " + SettingDescription(key)); output.Add(key + "=" + item.Value[key]); }
        }
        return String.Join("\r\n", output);
    }
    static string SettingDescription(string key) {
        switch (key) {
            case "host": return "SMTP送信サーバーのホスト名。";
            case "security": return "暗号化方式。starttls、ssl、none（暗号化なし）のいずれか。";
            case "port": return "SMTP送信ポート。例: STARTTLSは587、SSL/TLSは465、暗号化なしは25。";
            case "verify_certificate": return "サーバー証明書の検証。暗号化なしでは使用しない。";
            case "username": return "SMTP認証のユーザー名。認証不要ならパスワードと両方空欄。";
            case "credential_id": return "Windowsの資格情報を参照するための識別子。";
            case "from": return "送信元メールアドレス。";
            case "display_name": return "送信元として表示する名前。空欄でもよい。";
            case "bcc": return "送信履歴のBcc。不要なら空欄。複数はカンマまたはセミコロンで区切る。";
            default: return "SMTP設定。";
        }
    }
}
