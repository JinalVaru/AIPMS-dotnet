using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Aipms.Net;

public sealed class AipmsStore : IDisposable
{
    private readonly object _gate = new();
    private readonly IntPtr _database;
    private readonly ILogger<AipmsStore> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DatabaseState _state;
    private string _stateJson;

    public string DatabasePath { get; }

    public AipmsStore(IWebHostEnvironment environment, ILogger<AipmsStore> log)
    {
        _log = log;
        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "aipms.db");
        var isNewFile = !File.Exists(DatabasePath);
        _database = NativeSqlite.Open(DatabasePath);
        NativeSqlite.Execute(_database, "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; CREATE TABLE IF NOT EXISTS app_state (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL, updated_at TEXT NOT NULL);" + SqliteSchema.Definition);

        var saved = NativeSqlite.Scalar(_database, "SELECT json FROM app_state WHERE id = 1;");
        if (string.IsNullOrWhiteSpace(saved) && isNewFile)
        {
            // Earlier builds passed the path to SQLite as UTF-16, so SQLite opened a file named after the
            // drive letter (e.g. "D") in the project folder instead of App_Data/aipms.db. Import that data once.
            saved = LegacyDatabaseImporter.TryReadState(environment.ContentRootPath, _log);
        }
        if (string.IsNullOrWhiteSpace(saved)) _state = Seed.Create();
        else _state = JsonSerializer.Deserialize<DatabaseState>(saved, Json) ?? Seed.Create();
        _stateJson = JsonSerializer.Serialize(_state, Json);

        // Verify the stored state can actually be written to the relational schema. A database poisoned by the
        // old non-atomic Persist() (for example two students with the same enrollment number) is repaired here
        // instead of failing every later request.
        try { Commit(_state, _stateJson); }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Stored data violates the database constraints; attempting automatic repair.");
            DataRepair.Apply(_state, _log);
            _stateJson = JsonSerializer.Serialize(_state, Json);
            Commit(_state, _stateJson);
        }
    }

    /// <summary>Read-only access. Stored state is immutable once published (writes work on a copy), so results are safe to enumerate after the call returns.</summary>
    public T Read<T>(Func<DatabaseState, T> action) { lock (_gate) return action(_state); }

    /// <summary>
    /// Atomic write. The action runs on a private copy of the state. Only if it completes without throwing, and
    /// does not return an HTTP error result, is the copy committed to SQLite (single transaction) and published.
    /// Otherwise nothing changes in memory or on disk.
    /// </summary>
    public T Write<T>(Func<DatabaseState, T> action)
    {
        lock (_gate)
        {
            var working = JsonSerializer.Deserialize<DatabaseState>(_stateJson, Json)!;
            var result = action(working);
            if (result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: >= 400 }) return result;
            var json = JsonSerializer.Serialize(working, Json);
            if (json == _stateJson) return result;
            Commit(working, json);
            _state = working; _stateJson = json;
            return result;
        }
    }

    public static int Next(DatabaseState state) => state.NextId++;

    private void Commit(DatabaseState state, string json)
    {
        NativeSqlite.Execute(_database, "BEGIN IMMEDIATE;");
        try
        {
            NativeSqlite.Execute(_database, $"INSERT INTO app_state (id, json, updated_at) VALUES (1, '{json.Replace("'", "''")}', datetime('now')) ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at;");
            SqliteSchema.Sync(_database, state);
            NativeSqlite.Execute(_database, "COMMIT;");
        }
        catch
        {
            try { NativeSqlite.Execute(_database, "ROLLBACK;"); } catch { /* connection already rolled back */ }
            throw;
        }
    }

    public void Dispose() => NativeSqlite.Close(_database);
}

internal static class LegacyDatabaseImporter
{
    /// <summary>Finds the newest app_state in legacy files named like a drive letter ("C", "D"...). Works on temp copies; never modifies or deletes the originals.</summary>
    public static string? TryReadState(string contentRoot, ILogger log)
    {
        var folders = new[] { contentRoot, Directory.GetCurrentDirectory() }.Distinct(StringComparer.OrdinalIgnoreCase);
        string? best = null, bestStamp = null, bestName = null;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetFileName(f).Length == 1 && char.IsLetter(Path.GetFileName(f)[0])))
            {
                var temp = Path.Combine(Path.GetTempPath(), "aipms-legacy-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(temp);
                    var copy = Path.Combine(temp, "legacy.db");
                    File.Copy(file, copy);
                    foreach (var suffix in new[] { "-wal", "-shm" }) if (File.Exists(file + suffix)) File.Copy(file + suffix, copy + suffix);
                    var db = NativeSqlite.Open(copy);
                    try
                    {
                        var json = NativeSqlite.Scalar(db, "SELECT json FROM app_state WHERE id = 1;");
                        var stamp = NativeSqlite.Scalar(db, "SELECT updated_at FROM app_state WHERE id = 1;");
                        if (!string.IsNullOrWhiteSpace(json) && string.CompareOrdinal(stamp, bestStamp) > 0) { best = json; bestStamp = stamp; bestName = file; }
                    }
                    finally { NativeSqlite.Close(db); }
                }
                catch (Exception ex) { log.LogDebug(ex, "Skipping {File}: not a readable AIPMS legacy database.", file); }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
        if (best is not null) log.LogWarning("Imported existing data from legacy database file '{File}' into App_Data/aipms.db. The legacy file was left untouched.", bestName);
        return best;
    }
}

internal static class DataRepair
{
    /// <summary>Repairs known inconsistencies that would violate the relational schema. Each fix is logged.</summary>
    public static void Apply(DatabaseState s, ILogger log)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in s.Students.OrderBy(x => x.Id))
        {
            if (string.IsNullOrWhiteSpace(st.EnrollmentNo)) { st.EnrollmentNo = null; continue; }
            if (!seen.Add(st.EnrollmentNo)) { log.LogWarning("Repair: student {Id} had a duplicate enrollment number; it was cleared.", st.Id); st.EnrollmentNo = null; }
        }
        var departments = s.Departments.Select(x => x.Id).ToHashSet();
        foreach (var st in s.Students.Where(x => x.DepartmentId is int d && !departments.Contains(d))) { log.LogWarning("Repair: student {Id} referenced a missing department; cleared.", st.Id); st.DepartmentId = null; }
        var students = s.Students.Select(x => x.Id).ToHashSet(); var skills = s.Skills.Select(x => x.Id).ToHashSet();
        s.StudentSkills.RemoveAll(x => !students.Contains(x.StudentId) || !skills.Contains(x.SkillId));
        s.StudentSkills = s.StudentSkills.GroupBy(x => (x.StudentId, x.SkillId)).Select(g => g.First()).ToList();
        var users = s.Users.Select(x => x.Id).ToHashSet();
        s.Notifications.RemoveAll(x => !users.Contains(x.UserId));
        foreach (var a in s.AuditLogs.Where(x => x.UserId is int u && !users.Contains(u))) a.UserId = null;
    }
}

internal static class SqliteSchema
{
    public const string Definition = @"
CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, role TEXT NOT NULL, is_active INTEGER NOT NULL, is_approved INTEGER NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS departments (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
CREATE TABLE IF NOT EXISTS courses (id INTEGER PRIMARY KEY, department_id INTEGER NOT NULL REFERENCES departments(id), name TEXT NOT NULL, UNIQUE(department_id,name));
CREATE TABLE IF NOT EXISTS companies (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, website TEXT, industry TEXT, location TEXT, description TEXT, is_verified INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS students (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL UNIQUE REFERENCES users(id), full_name TEXT NOT NULL, enrollment_no TEXT UNIQUE, department_id INTEGER REFERENCES departments(id), cgpa REAL, phone TEXT, passing_year INTEGER, bio TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recruiters (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL UNIQUE REFERENCES users(id), company_id INTEGER REFERENCES companies(id), full_name TEXT NOT NULL, designation TEXT, phone TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS skills (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
CREATE TABLE IF NOT EXISTS student_skills (student_id INTEGER REFERENCES students(id), skill_id INTEGER REFERENCES skills(id), PRIMARY KEY(student_id,skill_id));
CREATE TABLE IF NOT EXISTS resumes (id INTEGER PRIMARY KEY, student_id INTEGER NOT NULL REFERENCES students(id), filename TEXT NOT NULL, filepath TEXT NOT NULL, raw_text TEXT, is_active INTEGER NOT NULL, uploaded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS resume_analysis (id INTEGER PRIMARY KEY, resume_id INTEGER NOT NULL REFERENCES resumes(id), ats_score INTEGER NOT NULL, extracted_skills TEXT, missing_skills TEXT, suggestions TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS jobs (id INTEGER PRIMARY KEY, recruiter_id INTEGER NOT NULL REFERENCES recruiters(id), title TEXT NOT NULL, description TEXT, job_type TEXT, location TEXT, salary_package REAL, min_cgpa REAL, required_skills TEXT, deadline TEXT, posted_at TEXT NOT NULL, is_active INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS applications (id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id), student_id INTEGER NOT NULL REFERENCES students(id), status TEXT NOT NULL, ats_score_at_apply INTEGER, applied_at TEXT NOT NULL, UNIQUE(job_id,student_id));
CREATE TABLE IF NOT EXISTS interviews (id INTEGER PRIMARY KEY, application_id INTEGER NOT NULL UNIQUE REFERENCES applications(id), scheduled_at TEXT, mode TEXT, status TEXT NOT NULL, feedback TEXT);
CREATE TABLE IF NOT EXISTS offer_letters (id INTEGER PRIMARY KEY, interview_id INTEGER NOT NULL UNIQUE REFERENCES interviews(id), package REAL, joining_date TEXT, response TEXT NOT NULL, response_message TEXT, responded_at TEXT, issued_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS placement_drives (id INTEGER PRIMARY KEY, title TEXT NOT NULL, company_id INTEGER REFERENCES companies(id), drive_date TEXT, description TEXT, status TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS notifications (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), message TEXT NOT NULL, type TEXT NOT NULL, detail TEXT, reference_id INTEGER, is_read INTEGER NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS student_queries (id INTEGER PRIMARY KEY, student_id INTEGER NOT NULL REFERENCES students(id), subject TEXT, message TEXT NOT NULL, status TEXT NOT NULL, reply TEXT, replied_at TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS personal_messages (id INTEGER PRIMARY KEY, sender_user_id INTEGER REFERENCES users(id), subject TEXT, message TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS personal_message_recipients (message_id INTEGER REFERENCES personal_messages(id), student_id INTEGER REFERENCES students(id), PRIMARY KEY(message_id,student_id));
CREATE TABLE IF NOT EXISTS audit_logs (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), action TEXT NOT NULL, created_at TEXT NOT NULL);";
    private static string Q(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";
    private static string D(DateTime value) => Q(value.ToString("O"));
    private static string D(DateTime? value) => value is null ? "NULL" : D(value.Value);
    public static void Sync(IntPtr db, DatabaseState s)
    {
        NativeSqlite.Execute(db, "DELETE FROM personal_message_recipients;DELETE FROM personal_messages;DELETE FROM student_queries;DELETE FROM notifications;DELETE FROM offer_letters;DELETE FROM interviews;DELETE FROM applications;DELETE FROM jobs;DELETE FROM resume_analysis;DELETE FROM resumes;DELETE FROM student_skills;DELETE FROM skills;DELETE FROM recruiters;DELETE FROM students;DELETE FROM placement_drives;DELETE FROM courses;DELETE FROM companies;DELETE FROM departments;DELETE FROM audit_logs;DELETE FROM users;");
        foreach (var x in s.Users) NativeSqlite.Execute(db, $"INSERT INTO users VALUES({x.Id},{Q(x.Email)},{Q(x.PasswordHash)},{Q(x.Role)},{(x.IsActive?1:0)},{(x.IsApproved?1:0)},{D(x.CreatedAt)});");
        foreach (var x in s.Departments) NativeSqlite.Execute(db, $"INSERT INTO departments VALUES({x.Id},{Q(x.Name)});"); foreach (var x in s.Courses) NativeSqlite.Execute(db, $"INSERT INTO courses VALUES({x.Id},{x.DepartmentId},{Q(x.Name)});");
        foreach (var x in s.Companies) NativeSqlite.Execute(db, $"INSERT INTO companies VALUES({x.Id},{Q(x.Name)},{Q(x.Website)},{Q(x.Industry)},{Q(x.Location)},{Q(x.Description)},{(x.IsVerified?1:0)});");
        foreach (var x in s.Students) NativeSqlite.Execute(db, $"INSERT INTO students VALUES({x.Id},{x.UserId},{Q(x.FullName)},{Q(x.EnrollmentNo)},{(x.DepartmentId?.ToString()??"NULL")},{(x.Cgpa?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"NULL")},{Q(x.Phone)},{(x.PassingYear?.ToString()??"NULL")},{Q(x.Bio)},{D(x.CreatedAt)});");
        foreach (var x in s.Recruiters) NativeSqlite.Execute(db, $"INSERT INTO recruiters VALUES({x.Id},{x.UserId},{(x.CompanyId?.ToString()??"NULL")},{Q(x.FullName)},{Q(x.Designation)},{Q(x.Phone)},{D(x.CreatedAt)});");
        foreach (var x in s.Skills) NativeSqlite.Execute(db, $"INSERT INTO skills VALUES({x.Id},{Q(x.Name)});"); foreach (var x in s.StudentSkills) NativeSqlite.Execute(db, $"INSERT INTO student_skills VALUES({x.StudentId},{x.SkillId});");
        foreach (var x in s.Resumes) NativeSqlite.Execute(db, $"INSERT INTO resumes VALUES({x.Id},{x.StudentId},{Q(x.Filename)},{Q(x.Filepath)},{Q(x.RawText)},{(x.IsActive?1:0)},{D(x.UploadedAt)});");
        foreach (var x in s.ResumeAnalyses) NativeSqlite.Execute(db, $"INSERT INTO resume_analysis VALUES({x.Id},{x.ResumeId},{x.AtsScore},{Q(JsonSerializer.Serialize(x.ExtractedSkills))},{Q(JsonSerializer.Serialize(x.MissingSkills))},{Q(JsonSerializer.Serialize(x.Suggestions))},{D(x.CreatedAt)});");
        foreach (var x in s.Jobs) NativeSqlite.Execute(db, $"INSERT INTO jobs VALUES({x.Id},{x.RecruiterId},{Q(x.Title)},{Q(x.Description)},{Q(x.JobType)},{Q(x.Location)},{(x.SalaryPackage?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"NULL")},{x.MinCgpa.ToString(System.Globalization.CultureInfo.InvariantCulture)},{Q(string.Join(", ",x.RequiredSkills))},{D(x.Deadline)},{D(x.PostedAt)},{(x.IsActive?1:0)});");
        foreach (var x in s.Applications) NativeSqlite.Execute(db, $"INSERT INTO applications VALUES({x.Id},{x.JobId},{x.StudentId},{Q(x.Status)},{(x.AtsScoreAtApply?.ToString()??"NULL")},{D(x.AppliedAt)});");
        foreach (var x in s.Interviews) NativeSqlite.Execute(db, $"INSERT INTO interviews VALUES({x.Id},{x.ApplicationId},{D(x.ScheduledAt)},{Q(x.Mode)},{Q(x.Status)},{Q(x.Feedback)});");
        foreach (var x in s.OfferLetters) NativeSqlite.Execute(db, $"INSERT INTO offer_letters VALUES({x.Id},{x.InterviewId},{(x.Package?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"NULL")},{D(x.JoiningDate)},{Q(x.Response)},{Q(x.ResponseMessage)},{D(x.RespondedAt)},{D(x.IssuedAt)});");
        foreach (var x in s.PlacementDrives) NativeSqlite.Execute(db, $"INSERT INTO placement_drives VALUES({x.Id},{Q(x.Title)},{(x.CompanyId?.ToString()??"NULL")},{D(x.DriveDate)},{Q(x.Description)},{Q(x.Status)},{D(x.CreatedAt)});");
        foreach (var x in s.Notifications) NativeSqlite.Execute(db, $"INSERT INTO notifications VALUES({x.Id},{x.UserId},{Q(x.Message)},{Q(x.Type)},{Q(x.Detail)},{(x.ReferenceId?.ToString()??"NULL")},{(x.IsRead?1:0)},{D(x.CreatedAt)});");
        foreach (var x in s.StudentQueries) NativeSqlite.Execute(db, $"INSERT INTO student_queries VALUES({x.Id},{x.StudentId},{Q(x.Subject)},{Q(x.Message)},{Q(x.Status)},{Q(x.Reply)},{D(x.RepliedAt)},{D(x.CreatedAt)});");
        foreach (var x in s.PersonalMessages) NativeSqlite.Execute(db, $"INSERT INTO personal_messages VALUES({x.Id},{x.SenderUserId},{Q(x.Subject)},{Q(x.Message)},{D(x.CreatedAt)});");
        foreach (var x in s.PersonalMessageRecipients) NativeSqlite.Execute(db, $"INSERT INTO personal_message_recipients VALUES({x.MessageId},{x.StudentId});");
        foreach (var x in s.AuditLogs) NativeSqlite.Execute(db, $"INSERT INTO audit_logs VALUES({x.Id},{(x.UserId?.ToString()??"NULL")},{Q(x.Action)},{D(x.CreatedAt)});");
    }
}

internal static class NativeSqlite
{
    private const int Ok = 0, Row = 100, OpenReadWriteCreate = 0x00000006;

    static NativeSqlite()
    {
        // Windows ships winsqlite3.dll. On Linux/macOS map the same C API to the system SQLite library so the app can run and be tested there.
        if (!OperatingSystem.IsWindows())
            NativeLibrary.SetDllImportResolver(typeof(NativeSqlite).Assembly, (name, assembly, path) =>
            {
                if (name != "winsqlite3.dll") return IntPtr.Zero;
                foreach (var candidate in new[] { "libsqlite3.so.0", "libsqlite3.so", "libsqlite3.dylib" })
                    if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
                return IntPtr.Zero;
            });
    }

    // SQLite expects UTF-8 for file names and SQL text. (CharSet.Unicode sent UTF-16, which truncated the path to its first character.)
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);

    public static IntPtr Open(string path)
    {
        var result = sqlite3_open_v2(path, out var db, OpenReadWriteCreate, IntPtr.Zero);
        if (result != Ok) { var message = db == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)); Close(db); throw new InvalidOperationException($"SQLite could not open the database (code {result}) {message}"); }
        return db;
    }
    public static void Close(IntPtr db) { if (db != IntPtr.Zero) sqlite3_close_v2(db); }
    public static void Execute(IntPtr db, string sql)
    {
        var result = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result == Ok) return;
        var message = error == IntPtr.Zero ? Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) : Marshal.PtrToStringUTF8(error);
        if (error != IntPtr.Zero) sqlite3_free(error);
        throw new InvalidOperationException($"SQLite error: {message}");
    }
    public static string? Scalar(IntPtr db, string sql)
    {
        var result = sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero);
        if (result != Ok) throw new InvalidOperationException(Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
        try { return sqlite3_step(statement) == Row ? Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) : null; }
        finally { sqlite3_finalize(statement); }
    }
}

public sealed class PasswordService
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[3]));
        }
        catch (FormatException) { return false; }
    }
}

public sealed class TokenService(IConfiguration configuration, AipmsStore store)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(configuration["AIPMS_TOKEN_KEY"] ?? "change-this-development-key-before-production-aipms");
    public string Create(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new JwtPayload(user.Id, user.Email, user.Role, now.ToUnixTimeSeconds(), now.AddDays(7).ToUnixTimeSeconds()))));
        var unsigned = $"{header}.{payload}";
        return $"{unsigned}.{Base64Url(Sign(unsigned))}";
    }
    public UserSession? Validate(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var pieces = authorization[7..].Trim().Split('.');
            if (pieces.Length != 3 || !CryptographicOperations.FixedTimeEquals(Sign($"{pieces[0]}.{pieces[1]}"), FromBase64Url(pieces[2]))) return null;
            using var header = JsonDocument.Parse(Encoding.UTF8.GetString(FromBase64Url(pieces[0])));
            if (header.RootElement.GetProperty("alg").GetString() != "HS256" || header.RootElement.GetProperty("typ").GetString() != "JWT") return null;
            var value = JsonSerializer.Deserialize<JwtPayload>(Encoding.UTF8.GetString(FromBase64Url(pieces[1])));
            if (value is null || value.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            // A token is only as good as the account behind it: deactivated, unapproved or deleted users lose access at once.
            var current = store.Read(s => s.Users.FirstOrDefault(x => x.Id == value.Sub));
            return current is { IsActive: true, IsApproved: true } && current.Role == value.Role ? new UserSession(current.Id, current.Email, current.Role) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return null; }
    }
    private byte[] Sign(string value) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private sealed record JwtPayload(int Sub, string Email, string Role, long Iat, long Exp);
}

public sealed class ResumeAnalyzer
{
    public static readonly string[] MasterSkills = ["javascript","typescript","python","java","c++","c#","c","php","ruby","go","rust","kotlin","swift","html","css","react","angular","vue","node.js","node","express","django","flask","spring","spring boot",".net","asp.net","bootstrap","tailwind","jquery","sql","mysql","postgresql","sql server","mongodb","sqlite","redis","firebase","oracle","machine learning","deep learning","data science","nlp","computer vision","tensorflow","pytorch","pandas","numpy","scikit-learn","power bi","tableau","excel","git","github","docker","kubernetes","aws","azure","gcp","linux","ci/cd","jenkins","rest api","graphql","microservices","agile","scrum","testing","unit testing","data structures","algorithms","oop","dbms","operating systems","computer networks","communication","teamwork","leadership","problem solving","time management"];
    private static readonly string[] ActionVerbs = ["developed","designed","implemented","built","created","led","managed","optimized","improved","achieved","reduced","increased","automated","launched","deployed","collaborated","analyzed","engineered","architected","delivered"];
    private static readonly Dictionary<string, string[]> Sections = new() { ["contact"] = ["email", "phone", "linkedin", "@"], ["education"] = ["education", "b.tech", "b.e", "bachelor", "university", "college", "cgpa", "gpa"], ["experience"] = ["experience", "internship", "worked", "employment"], ["projects"] = ["project", "projects"], ["skills"] = ["skill", "skills", "technologies", "tools"], ["certifications"] = ["certification", "certificate", "certified"] };
    public List<string> ExtractSkills(string text) => MasterSkills.Where(skill => Regex.IsMatch(text ?? "", $@"(^|[^a-z0-9]){Regex.Escape(skill)}([^a-z0-9]|$)", RegexOptions.IgnoreCase)).ToList();
    public (int Score, List<string> Notes, List<string> Skills) Score(string text)
    {
        var normalized = Regex.Replace((text ?? "").ToLowerInvariant(), @"\s+", " ");
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var notes = new List<string>(); var sectionsFound = 0;
        foreach (var (section, wordsForSection) in Sections) { if (wordsForSection.Any(normalized.Contains)) sectionsFound++; else notes.Add($"Add a clear \"{section}\" section — it wasn't detected."); }
        var skills = ExtractSkills(text ?? ""); var score = (int)Math.Round((double)sectionsFound / Sections.Count * 40) + Math.Min(25, (int)Math.Round(skills.Count * 2.5));
        if (skills.Count < 5) notes.Add("List more relevant technical skills — fewer than 5 were recognized.");
        var numeric = Regex.Matches(normalized, @"\d+%|\d+\+|\b\d{2,}\b").Count; score += Math.Min(15, numeric * 3);
        if (numeric == 0) notes.Add("Quantify your impact with numbers (e.g. \"improved performance by 30%\").");
        var verbs = ActionVerbs.Count(normalized.Contains); score += Math.Min(10, verbs * 2);
        if (verbs < 3) notes.Add("Use more strong action verbs (developed, led, optimized, automated...).");
        if (words.Length < 120) notes.Add("Resume looks too short — add more detail about projects/experience."); else if (words.Length > 1200) notes.Add("Resume looks very long — consider trimming to 1-2 pages."); else score += 10;
        return (Math.Clamp(score, 0, 100), notes, skills);
    }
    public (List<string> Required, List<string> Matched, List<string> Missing, int MatchPct) SkillGap(IEnumerable<string> studentSkills, IEnumerable<string> requiredSkills)
    {
        var required = requiredSkills.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).ToList(); var have = studentSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = required.Where(have.Contains).ToList(); var missing = required.Where(x => !have.Contains(x)).ToList();
        return (required, matched, missing, required.Count == 0 ? 100 : (int)Math.Round((double)matched.Count / required.Count * 100));
    }
}

public static class ResumeTextExtractor
{
    /// <summary>Extracts text from a PDF or DOCX. Throws <see cref="InvalidDataException"/> when the file is not a readable document.</summary>
    public static async Task<string> ExtractAsync(IFormFile file)
    {
        await using var buffer = new MemoryStream(); await file.CopyToAsync(buffer); var bytes = buffer.ToArray();
        var docx = Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        if (docx) return ExtractDocx(bytes);
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw new InvalidDataException("The file is not a valid PDF.");
        return ExtractPdf(bytes);
    }
    private static string ExtractDocx(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes)); var entry = archive.GetEntry("word/document.xml");
            if (entry is null) throw new InvalidDataException("The file is not a valid DOCX document.");
            using var reader = new StreamReader(entry.Open());
            var xml = Regex.Replace(reader.ReadToEnd(), "</w:p>", "\n");
            return System.Net.WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]+>", " "));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("The file is not a valid DOCX document.", ex); }
    }
    private static string ExtractPdf(byte[] bytes)
    {
        var all = Encoding.Latin1.GetString(bytes); var source = new StringBuilder(all);
        // Most PDFs store page content in Flate-compressed streams; inflate them so the text operators become visible.
        for (var i = all.IndexOf("stream", StringComparison.Ordinal); i >= 0 && i < all.Length;)
        {
            var start = i + 6; if (start < all.Length && all[start] == '\r') start++; if (start < all.Length && all[start] == '\n') start++;
            var end = all.IndexOf("endstream", start, StringComparison.Ordinal); if (end < 0) break;
            if (end - start > 2 && (i == 0 || all[i - 1] != 'd'))
            {
                try
                {
                    using var inflater = new ZLibStream(new MemoryStream(bytes, start, end - start), CompressionMode.Decompress); using var output = new MemoryStream();
                    inflater.CopyTo(output); if (output.Length < 20 * 1024 * 1024) source.Append('\n').Append(Encoding.Latin1.GetString(output.ToArray()));
                }
                catch (Exception) { /* not a Flate stream (image, font or plain content) - skip */ }
            }
            i = all.IndexOf("stream", end + 9, StringComparison.Ordinal);
        }
        var text = source.ToString(); var pieces = new StringBuilder();
        foreach (Match m in Regex.Matches(text, @"\((?<text>(?:\\.|[^\\)])*)\)\s*(?:Tj|')", RegexOptions.Singleline)) pieces.Append(' ').Append(UnescapePdf(m.Groups["text"].Value));
        foreach (Match m in Regex.Matches(text, @"\[(?<text>.*?)\]\s*TJ", RegexOptions.Singleline)) foreach (Match t in Regex.Matches(m.Groups["text"].Value, @"\((?<text>(?:\\.|[^\\)])*)\)")) pieces.Append(' ').Append(UnescapePdf(t.Groups["text"].Value));
        return Regex.Replace(pieces.ToString(), @"\s+", " ").Trim();
    }
    private static string UnescapePdf(string value) => Regex.Replace(value, @"\\([nrtbf()\\])", m => m.Groups[1].Value switch { "n" => "\n", "r" => "\r", "t" => "\t", "b" => "\b", "f" => "\f", _ => m.Groups[1].Value });
}

public static class Seed
{
    public static DatabaseState Create()
    {
        var s = new DatabaseState(); int AddId() => AipmsStore.Next(s);
        foreach (var name in new[] { "Computer Science", "Information Technology", "Electronics", "Mechanical Engineering", "Civil Engineering" }) s.Departments.Add(new Department { Id = AddId(), Name = name });
        var courseMap = new Dictionary<string, string[]> { ["Computer Science"] = ["B.Tech CSE", "M.Tech CSE", "B.Tech CSE (AI & ML)"], ["Information Technology"] = ["B.Tech IT", "M.Tech IT"], ["Electronics"] = ["B.Tech ECE", "B.Tech EEE"], ["Mechanical Engineering"] = ["B.Tech Mechanical", "Diploma Mechanical"], ["Civil Engineering"] = ["B.Tech Civil", "Diploma Civil"] };
        foreach (var (department, courses) in courseMap) foreach (var course in courses) s.Courses.Add(new Course { Id = AddId(), DepartmentId = s.Departments.First(x => x.Name == department).Id, Name = course });
        foreach (var skill in ResumeAnalyzer.MasterSkills) s.Skills.Add(new Skill { Id = AddId(), Name = skill });
        var passwords = new PasswordService();
        User User(string email, string password, string role, bool approved = true) { var u = new User { Id = AddId(), Email = email, PasswordHash = passwords.Hash(password), Role = role, IsApproved = approved }; s.Users.Add(u); return u; }
        User("admin@aipms.local", "Admin@123", "admin"); User("tpo@aipms.local", "Tpo@1234", "tpo");
        Company Company(string name, string industry, string location, string description) { var c = new Company { Id = AddId(), Name = name, Industry = industry, Location = location, Description = description, IsVerified = true }; s.Companies.Add(c); return c; }
        var techno = Company("TechNova Solutions", "Software", "Bengaluru, India", "A product company building developer tools and SaaS platforms."); var data = Company("DataSphere Analytics", "Data / AI", "Pune, India", "Data analytics and applied ML consultancy for enterprise clients."); Company("BuildRight Infra", "Civil Engineering", "Ahmedabad, India", "Infrastructure and civil construction projects across Western India.");
        Recruiter Recruiter(string email, string password, string name, Company company) { var r = new Recruiter { Id = AddId(), UserId = User(email, password, "recruiter").Id, CompanyId = company.Id, FullName = name, Designation = "HR Manager" }; s.Recruiters.Add(r); return r; }
        var r1 = Recruiter("hr@technova.com", "Recruit@123", "Priya Sharma", techno); var r2 = Recruiter("hr@datasphere.com", "Recruit@123", "Arjun Mehta", data);
        Student Student(string email, string password, string name, string enrollment, string department, double cgpa, params string[] skills) { var st = new Student { Id = AddId(), UserId = User(email, password, "student").Id, FullName = name, EnrollmentNo = enrollment, DepartmentId = s.Departments.First(x => x.Name == department).Id, Cgpa = cgpa, PassingYear = 2026 }; s.Students.Add(st); foreach (var skill in skills) s.StudentSkills.Add(new StudentSkill { StudentId = st.Id, SkillId = s.Skills.First(x => x.Name == skill).Id }); return st; }
        Student("aditi.rao@student.aipms.local", "Student@123", "Aditi Rao", "22CS001", "Computer Science", 8.7, "javascript", "react", "node.js", "sql", "git"); Student("rahul.verma@student.aipms.local", "Student@123", "Rahul Verma", "22IT014", "Information Technology", 7.4, "python", "machine learning", "pandas", "sql");
        void Job(Recruiter r, string title, string description, string type, string location, double salary, double cgpa, params string[] skills) => s.Jobs.Add(new Job { Id = AddId(), RecruiterId = r.Id, Title = title, Description = description, JobType = type, Location = location, SalaryPackage = salary, MinCgpa = cgpa, RequiredSkills = skills.ToList(), Deadline = DateTime.UtcNow.AddDays(30) });
        Job(r1, "Frontend Developer Intern", "Build and ship UI features using React and modern JavaScript.", "Internship", "Bengaluru", 600000, 7, "javascript", "react", "css", "git"); Job(r1, "Full Stack Developer", "Work across a Node.js/React stack building placement-tech products.", "Full-Time", "Bengaluru", 1200000, 7.5, "javascript", "react", "node.js", "sql", "rest api"); Job(r2, "Data Analyst", "Analyze large datasets and build dashboards for business insights.", "Full-Time", "Pune", 900000, 7, "python", "sql", "pandas", "power bi"); Job(r2, "ML Engineer Intern", "Assist in building and evaluating machine learning models.", "Internship", "Remote", 500000, 7.5, "python", "machine learning", "pandas", "numpy");
        return s;
    }
}