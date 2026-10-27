using Aipms.Net;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AipmsStore>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ResumeAnalyzer>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();

// Centralized error handling: log the real exception on the server, return a safe JSON body to the client.
// (Without this, the Development exception page returned an HTML page that echoed request headers.)
app.UseExceptionHandler(errors => errors.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aipms.Api");
    ctx.Response.ContentType = "application/json";
    if (ex is BadHttpRequestException)
    {
        log.LogWarning(ex, "Bad request for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "The request data is missing or invalid." });
        return;
    }
    log.LogError(ex, "Unhandled exception for {Method} {Path} (trace {TraceId})", ctx.Request.Method, ctx.Request.Path, ctx.TraceIdentifier);
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong on the server. Your change was not saved. Please try again.", traceId = ctx.TraceIdentifier });
}));

var store = app.Services.GetRequiredService<AipmsStore>(); // Create/open/seed App_Data/aipms.db during startup.
app.Logger.LogInformation("Database: {Path}", store.DatabasePath);
if (string.IsNullOrWhiteSpace(app.Configuration["AIPMS_TOKEN_KEY"]))
    app.Logger.LogWarning("AIPMS_TOKEN_KEY is not set; using the built-in development signing key. Set it before deploying.");

app.UseCors(); app.UseDefaultFiles(); app.UseStaticFiles();
var api = app.MapGroup("/api");
api.MapGet("/health", () => new { status = "ok", time = DateTime.UtcNow });

// ------------------------------------------------------------------ auth
api.MapPost("/auth/register", (RegisterRequest r, AipmsStore db, PasswordService passwords) =>
{
    var email = (r.Email ?? "").Trim().ToLowerInvariant();
    var role = r.Role ?? "";
    if (email.Length == 0 || !email.Contains('@') || string.IsNullOrEmpty(r.Password) || role is not ("student" or "recruiter")) return Api.Fail("email, password and a student/recruiter role are required", 400);
    if (r.Password.Length < 6) return Api.Fail("Password must be at least 6 characters", 400);
    if (role == "recruiter" && string.IsNullOrWhiteSpace(r.CompanyName)) return Api.Fail("Company name is required", 400);
    if (r.Cgpa is < 0 or > 10) return Api.Fail("CGPA must be between 0 and 10", 400);
    var enrollment = string.IsNullOrWhiteSpace(r.EnrollmentNo) ? null : r.EnrollmentNo.Trim();
    var hash = passwords.Hash(r.Password); // slow by design; done before taking the store lock
    return db.Write(s =>
    {
        if (s.Users.Any(x => x.Email == email)) return Api.Fail("Email already registered", 409);
        if (role == "student")
        {
            if (enrollment is not null && s.Students.Any(x => string.Equals(x.EnrollmentNo, enrollment, StringComparison.OrdinalIgnoreCase))) return Api.Fail("This enrollment number is already registered", 409);
            if (r.DepartmentId is int d && s.Departments.All(x => x.Id != d)) return Api.Fail("Selected department does not exist", 400);
        }
        var user = new User { Id = AipmsStore.Next(s), Email = email, PasswordHash = hash, Role = role, IsApproved = role == "student" };
        s.Users.Add(user);
        var fullName = string.IsNullOrWhiteSpace(r.FullName) ? email.Split('@')[0] : r.FullName.Trim();
        if (role == "student") s.Students.Add(new Student { Id = AipmsStore.Next(s), UserId = user.Id, FullName = fullName, EnrollmentNo = enrollment, DepartmentId = r.DepartmentId, Cgpa = r.Cgpa });
        else
        {
            var name = r.CompanyName!.Trim();
            var c = s.Companies.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (c is null) { c = new Company { Id = AipmsStore.Next(s), Name = name, Location = r.CompanyLocation, Industry = r.CompanyIndustry, Website = r.CompanyWebsite, Description = r.CompanyDescription }; s.Companies.Add(c); }
            s.Recruiters.Add(new Recruiter { Id = AipmsStore.Next(s), UserId = user.Id, CompanyId = c.Id, FullName = fullName, Designation = r.Designation, Phone = r.Phone });
        }
        s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = user.Id, Action = "REGISTER" });
        return Results.Json(new { message = role == "recruiter" ? "Registered. Your company and account need TPO approval before you can log in." : "Registered successfully.", user = new { id = user.Id, email, role = user.Role } }, statusCode: 201);
    });
});

api.MapPost("/auth/login", (LoginRequest r, AipmsStore db, PasswordService passwords, TokenService tokens, ILogger<Program> log) =>
{
    var email = (r.Email ?? "").Trim();
    if (email.Length == 0 || string.IsNullOrEmpty(r.Password)) return Api.Fail("Email and password are required", 400);
    var u = db.Read(s => s.Users.FirstOrDefault(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase)));
    if (u is null || !passwords.Verify(r.Password, u.PasswordHash)) return Api.Fail("Invalid credentials", 401);
    if (!string.IsNullOrEmpty(r.Role) && r.Role != u.Role) return Api.Fail($"This account is not registered as {r.Role}. Select the correct role and try again.", 401);
    if (!u.IsActive) return Api.Fail("Account has been deactivated", 403);
    if (!u.IsApproved) return Api.Fail("Account is pending TPO approval", 403);
    // The audit entry is secondary: a failure to record it must not turn a successful login into an error.
    try { db.Write(s => { s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = u.Id, Action = "LOGIN" }); return true; }); }
    catch (Exception ex) { log.LogError(ex, "Login succeeded but the audit log entry could not be saved."); }
    return Results.Json(new { token = tokens.Create(u), user = new { id = u.Id, email = u.Email, role = u.Role } });
});

// ------------------------------------------------------------------ public lookups & notifications
api.MapGet("/public/departments", (AipmsStore db) => db.Read(s => s.Departments.OrderBy(x => x.Name).ToList()));
api.MapGet("/public/companies", (AipmsStore db) => db.Read(s => s.Companies.OrderBy(x => x.Name).ToList()));
api.MapGet("/public/skills", (AipmsStore db) => db.Read(s => s.Skills.OrderBy(x => x.Name).ToList()));
api.MapGet("/notifications", (HttpContext c, AipmsStore db, TokenService tokens) => Api.WithUser(c, tokens, u => db.Read(s => Results.Json(s.Notifications.Where(x => x.UserId == u.Id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(50).Select(Api.Notif).ToList()))));
api.MapPost("/notifications/{id:int}/read", (int id, HttpContext c, AipmsStore db, TokenService tokens) => Api.WithUser(c, tokens, u => db.Write(s =>
{
    var n = s.Notifications.FirstOrDefault(x => x.Id == id && x.UserId == u.Id);
    if (n is null) return Api.Fail("Notification not found", 404);
    n.IsRead = true; return Results.Json(new { message = "ok" });
})));
api.MapPost("/notifications/mark-all-read", (HttpContext c, AipmsStore db, TokenService tokens) => Api.WithUser(c, tokens, u => db.Write(s =>
{
    foreach (var n in s.Notifications.Where(x => x.UserId == u.Id)) n.IsRead = true;
    return Results.Json(new { message = "ok" });
})));

// ------------------------------------------------------------------ student
api.MapGet("/students/me", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Read(s =>
{
    var st = Api.Student(s, u.Id);
    return st is null ? Api.Fail("Student profile not found", 404) : Results.Json(Api.StudentView(s, st));
})));

api.MapPut("/students/me", (StudentUpdateRequest r, HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Write(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    if (r.Cgpa is < 0 or > 10) return Api.Fail("CGPA must be between 0 and 10", 400);
    if (r.PassingYear is < 1990 or > 2100) return Api.Fail("Passing year is not valid", 400);
    if (r.DepartmentId is int d && s.Departments.All(x => x.Id != d)) return Api.Fail("Selected department does not exist", 400);
    if (r.FullName is not null && string.IsNullOrWhiteSpace(r.FullName)) return Api.Fail("Full name cannot be empty", 400);
    st.FullName = r.FullName?.Trim() ?? st.FullName; st.Phone = r.Phone ?? st.Phone; st.Cgpa = r.Cgpa ?? st.Cgpa; st.PassingYear = r.PassingYear ?? st.PassingYear; st.DepartmentId = r.DepartmentId ?? st.DepartmentId; st.Bio = r.Bio ?? st.Bio;
    if (r.SkillNames is not null)
    {
        s.StudentSkills.RemoveAll(x => x.StudentId == st.Id);
        foreach (var raw in r.SkillNames) Api.AddSkill(s, st.Id, raw);
    }
    return Results.Json(new { message = "Profile updated" });
})));

api.MapPost("/students/resume", async (HttpContext c, AipmsStore db, TokenService tokens, ResumeAnalyzer analyzer, IWebHostEnvironment env, ILogger<Program> log) =>
{
    var u = Api.Authorize(c, tokens, "student", out var error); if (error is not null) return error;
    if (!c.Request.HasFormContentType) return Api.Fail("Upload the resume as multipart form data", 400);
    var form = await c.Request.ReadFormAsync(c.RequestAborted);
    var file = form.Files.GetFile("resume");
    if (file is null || file.Length == 0) return Api.Fail("No file uploaded", 400);
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not ".pdf" and not ".docx") return Api.Fail("Only PDF or DOCX files are allowed", 400);
    if (file.Length > 5 * 1024 * 1024) return Api.Fail("File is too large (maximum is 5MB)", 400);
    if (db.Read(s => Api.Student(s, u!.Id)) is null) return Api.Fail("Student profile not found", 404);

    string text;
    try { text = await ResumeTextExtractor.ExtractAsync(file); }
    catch (InvalidDataException ex) { return Api.Fail(ex.Message, 400); }
    var (score, notes, skills) = analyzer.Score(text);
    if (text.Length < 20) notes.Insert(0, "Very little text could be read from this file. If it is a scanned image or uses unusual fonts, upload a text-based PDF or a DOCX.");

    var name = $"{u!.Id}-{Guid.NewGuid():N}{ext}";
    var folder = Path.Combine(env.ContentRootPath, "App_Data", "uploads"); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, name);
    await using (var output = File.Create(path)) await file.CopyToAsync(output, c.RequestAborted);
    try
    {
        return db.Write(s =>
        {
            var st = Api.Student(s, u.Id)!;
            foreach (var old in s.Resumes.Where(x => x.StudentId == st.Id)) old.IsActive = false;
            var resume = new Resume { Id = AipmsStore.Next(s), StudentId = st.Id, Filename = Path.GetFileName(file.FileName), Filepath = name, RawText = text };
            s.Resumes.Add(resume);
            foreach (var skillName in skills) Api.AddSkill(s, st.Id, skillName);
            s.ResumeAnalyses.Add(new ResumeAnalysis { Id = AipmsStore.Next(s), ResumeId = resume.Id, AtsScore = score, ExtractedSkills = skills, Suggestions = notes });
            return Results.Json(new { message = "Resume uploaded and analyzed", resumeId = resume.Id, atsScore = score, extractedSkills = skills, suggestions = notes }, statusCode: 201);
        });
    }
    catch { try { File.Delete(path); } catch (Exception ex) { log.LogWarning(ex, "Could not remove orphaned upload {File}", name); } throw; }
});

api.MapGet("/students/resume/analysis", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Read(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    var r = s.Resumes.Where(x => x.StudentId == st.Id && x.IsActive).OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.Id).FirstOrDefault();
    if (r is null) return Api.Fail("No resume uploaded yet", 404);
    var a = s.ResumeAnalyses.Where(x => x.ResumeId == r.Id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
    return a is null ? Api.Fail("No analysis found", 404) : Results.Json(new { resume = new { id = r.Id, filename = r.Filename, uploadedAt = r.UploadedAt }, atsScore = a.AtsScore, extractedSkills = a.ExtractedSkills, suggestions = a.Suggestions });
})));

api.MapGet("/students/jobs", (HttpContext c, AipmsStore db, TokenService tokens, string? q, string? type, string? location, double? minPackage) => Api.For(c, tokens, "student", _ => db.Read(s =>
{
    // The search form always sends q/type/location (empty when unused), so blank means "no filter".
    q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(); type = string.IsNullOrWhiteSpace(type) ? null : type; location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
    return Results.Json(s.Jobs.Where(j => j.IsActive
            && (q is null || j.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || j.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            && (type is null || j.JobType == type)
            && (location is null || j.Location.Contains(location, StringComparison.OrdinalIgnoreCase))
            && (!minPackage.HasValue || j.SalaryPackage >= minPackage))
        .OrderByDescending(j => j.PostedAt).ThenByDescending(j => j.Id).Select(j => Api.JobView(s, j)).ToList());
})));

api.MapGet("/students/recommendations", (HttpContext c, AipmsStore db, TokenService tokens, ResumeAnalyzer ai) => Api.For(c, tokens, "student", u => db.Read(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    var skills = Api.Skills(s, st.Id);
    var ranked = s.Jobs.Where(x => x.IsActive).Select(j =>
    {
        var gap = ai.SkillGap(skills, j.RequiredSkills);
        return (job: j, gap.MatchPct, missing: gap.Missing, eligible: Api.IsEligible(st, j));
    }).OrderByDescending(x => x.MatchPct).ThenByDescending(x => x.eligible).Take(20)
      .Select(x => Api.Recommendation(Api.JobView(s, x.job), x.MatchPct, x.missing, x.eligible)).ToList();
    return Results.Json(ranked);
})));

api.MapPost("/students/apply/{jobId:int}", (int jobId, HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Write(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    var job = s.Jobs.FirstOrDefault(x => x.Id == jobId && x.IsActive);
    if (job is null) return Api.Fail("Job not found or closed", 404);
    if (job.Deadline is DateTime deadline && DateTime.Now > deadline.Date.AddDays(1)) return Api.Fail("The application deadline for this job has passed", 400);
    if (s.Applications.Any(x => x.JobId == jobId && x.StudentId == st.Id)) return Api.Fail("You already applied to this job", 409);
    if (!Api.IsEligible(st, job)) return Api.Fail($"Your CGPA is below the minimum required for this job ({job.MinCgpa})", 400);
    var resume = s.Resumes.Where(x => x.StudentId == st.Id && x.IsActive).OrderByDescending(x => x.Id).FirstOrDefault();
    if (resume is null) return Api.Fail("Upload a resume before applying", 400);
    var analysis = s.ResumeAnalyses.Where(x => x.ResumeId == resume.Id).OrderByDescending(x => x.Id).FirstOrDefault();
    var a = new Application { Id = AipmsStore.Next(s), JobId = job.Id, StudentId = st.Id, AtsScoreAtApply = analysis?.AtsScore };
    s.Applications.Add(a);
    var recruiter = s.Recruiters.FirstOrDefault(x => x.Id == job.RecruiterId);
    if (recruiter is not null) Api.Notify(s, recruiter.UserId, $"New application received for \"{job.Title}\"", "new_application", $"{st.FullName} applied.", a.Id);
    return Results.Json(new { message = "Applied successfully", applicationId = a.Id }, statusCode: 201);
})));

api.MapGet("/students/applications", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Read(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    return Results.Json(s.Applications.Where(x => x.StudentId == st.Id).OrderByDescending(x => x.AppliedAt).ThenByDescending(x => x.Id).Select(a => Api.ApplicationView(s, a)).ToList());
})));

api.MapGet("/students/queries", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Read(s =>
{
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    return Results.Json(s.StudentQueries.Where(x => x.StudentId == st.Id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
        .Select(q => new { q.Id, q.Subject, q.Message, q.Status, q.Reply, replied_at = q.RepliedAt, created_at = q.CreatedAt }).ToList());
})));

api.MapPost("/students/queries", (QueryRequest r, HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "student", u => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Message)) return Api.Fail("message required", 400);
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    var q = new StudentQuery { Id = AipmsStore.Next(s), StudentId = st.Id, Subject = r.Subject?.Trim(), Message = r.Message.Trim() };
    s.StudentQueries.Add(q);
    foreach (var t in s.Users.Where(x => x.Role == "tpo" && x.IsActive)) Api.Notify(s, t.Id, $"New query from {st.FullName}", "new_query", q.Message, q.Id);
    return Results.Json(new { message = "Query submitted", id = q.Id }, statusCode: 201);
})));

api.MapPost("/students/offers/{id:int}/respond", (int id, OfferResponseRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "student", u => db.Write(s =>
{
    if (r.Response is not ("Accepted" or "Rejected" or "Negotiating")) return Api.Fail("response must be Accepted, Rejected, or Negotiating", 400);
    var st = Api.Student(s, u.Id); if (st is null) return Api.Fail("Student profile not found", 404);
    var o = s.OfferLetters.FirstOrDefault(x => x.Id == id); if (o is null) return Api.Fail("Offer not found", 404);
    var i = s.Interviews.FirstOrDefault(x => x.Id == o.InterviewId); var a = i is null ? null : s.Applications.FirstOrDefault(x => x.Id == i.ApplicationId);
    if (a is null || a.StudentId != st.Id) return Api.Fail("Offer not found", 404);
    if (o.Response is "Accepted" or "Rejected") return Api.Fail($"You have already {o.Response.ToLowerInvariant()} this offer", 409);
    o.Response = r.Response; o.ResponseMessage = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message.Trim(); o.RespondedAt = DateTime.UtcNow;
    if (r.Response == "Rejected") a.Status = "Rejected";
    var job = s.Jobs.FirstOrDefault(x => x.Id == a.JobId); var rec = job is null ? null : s.Recruiters.FirstOrDefault(x => x.Id == job.RecruiterId);
    if (rec is not null) Api.Notify(s, rec.UserId, $"{st.FullName} responded \"{r.Response}\" to the offer for \"{job!.Title}\"", "offer_response", o.ResponseMessage, a.Id);
    return Results.Json(new { message = "Response recorded" });
})));

// ------------------------------------------------------------------ recruiter
api.MapGet("/recruiters/me", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "recruiter", u => db.Read(s =>
{
    var r = Api.Recruiter(s, u.Id);
    return r is null ? Api.Fail("Recruiter profile not found", 404) : Results.Json(new { r.Id, r.UserId, r.CompanyId, full_name = r.FullName, r.Designation, r.Phone, company = r.CompanyId is null ? null : s.Companies.FirstOrDefault(x => x.Id == r.CompanyId) });
})));

api.MapPost("/recruiters/jobs", (JobRequest r, HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "recruiter", u => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Title)) return Api.Fail("title is required", 400);
    if (r.MinCgpa is < 0 or > 10) return Api.Fail("Minimum CGPA must be between 0 and 10", 400);
    if (r.SalaryPackage is < 0) return Api.Fail("Salary cannot be negative", 400);
    var recruiter = Api.Recruiter(s, u.Id); if (recruiter is null) return Api.Fail("Recruiter profile not found", 404);
    var skills = (r.RequiredSkills ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var job = new Job { Id = AipmsStore.Next(s), RecruiterId = recruiter.Id, Title = r.Title.Trim(), Description = r.Description ?? "", JobType = r.JobType ?? "Full-Time", Location = r.Location ?? "", SalaryPackage = r.SalaryPackage, MinCgpa = r.MinCgpa ?? 0, RequiredSkills = skills, Deadline = r.Deadline };
    s.Jobs.Add(job);
    return Results.Json(new { message = "Job posted", jobId = job.Id }, statusCode: 201);
})));

api.MapGet("/recruiters/jobs", (HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "recruiter", u => db.Read(s =>
{
    var r = Api.Recruiter(s, u.Id); if (r is null) return Api.Fail("Recruiter profile not found", 404);
    return Results.Json(s.Jobs.Where(x => x.RecruiterId == r.Id).OrderByDescending(x => x.PostedAt).ThenByDescending(x => x.Id).Select(j => new { j.Id, recruiter_id = j.RecruiterId, j.Title, description = j.Description, job_type = j.JobType, j.Location, salary_package = j.SalaryPackage, min_cgpa = j.MinCgpa, required_skills = string.Join(", ", j.RequiredSkills), deadline = j.Deadline, posted_at = j.PostedAt, is_active = j.IsActive, applicantCount = s.Applications.Count(a => a.JobId == j.Id) }).ToList());
})));

api.MapPut("/recruiters/jobs/{id:int}/close", (int id, HttpContext c, AipmsStore db, TokenService tokens) => Api.For(c, tokens, "recruiter", u => db.Write(s =>
{
    var r = Api.Recruiter(s, u.Id); if (r is null) return Api.Fail("Recruiter profile not found", 404);
    var j = s.Jobs.FirstOrDefault(x => x.Id == id && x.RecruiterId == r.Id);
    if (j is null) return Api.Fail("Job not found", 404);
    j.IsActive = false; return Results.Json(new { message = "Job closed" });
})));

api.MapGet("/recruiters/jobs/{id:int}/applicants", (int id, HttpContext c, AipmsStore db, TokenService tokens, ResumeAnalyzer ai) => Api.For(c, tokens, "recruiter", u => db.Read(s =>
{
    var r = Api.Recruiter(s, u.Id); if (r is null) return Api.Fail("Recruiter profile not found", 404);
    var j = s.Jobs.FirstOrDefault(x => x.Id == id && x.RecruiterId == r.Id); if (j is null) return Api.Fail("Job not found", 404);
    return Results.Json(s.Applications.Where(x => x.JobId == id).Select(a => Api.ApplicantView(s, a, j, ai)).OrderByDescending(x => (x.ats_score_at_apply ?? 0) + x.matchPct).ToList());
})));

api.MapPut("/recruiters/applications/{id:int}/status", (int id, StatusRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "recruiter", u => db.Write(s =>
{
    var valid = new[] { "Applied", "Shortlisted", "Interview Scheduled", "Selected", "Rejected" };
    if (r.Status is null || !valid.Contains(r.Status)) return Api.Fail("Status must be one of: " + string.Join(", ", valid), 400);
    var (a, job, error) = Api.OwnedApplication(s, id, u.Id); if (error is not null) return error;
    if (a!.Status != r.Status)
    {
        a.Status = r.Status;
        var st = s.Students.FirstOrDefault(x => x.Id == a.StudentId);
        if (st is not null) Api.Notify(s, st.UserId, $"Your application for \"{job!.Title}\" is now \"{a.Status}\"", "status_update", null, a.Id);
    }
    return Results.Json(new { message = "Status updated" });
})));

api.MapPost("/recruiters/applications/{id:int}/interview", (int id, InterviewRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "recruiter", u => db.Write(s =>
{
    if (r.ScheduledAt is null) return Api.Fail("Interview date and time are required", 400);
    if (r.Mode is not null && r.Mode is not ("Online" or "Offline")) return Api.Fail("Mode must be Online or Offline", 400);
    var (a, job, error) = Api.OwnedApplication(s, id, u.Id); if (error is not null) return error;
    var i = s.Interviews.FirstOrDefault(x => x.ApplicationId == id);
    if (i is null) { i = new Interview { Id = AipmsStore.Next(s), ApplicationId = id }; s.Interviews.Add(i); }
    i.ScheduledAt = r.ScheduledAt; i.Mode = r.Mode ?? "Online"; i.Status = "Scheduled"; a!.Status = "Interview Scheduled";
    var st = s.Students.FirstOrDefault(x => x.Id == a.StudentId);
    if (st is not null) Api.Notify(s, st.UserId, $"Interview scheduled for \"{job!.Title}\"", "interview", $"Scheduled for {i.ScheduledAt:f} · Mode: {i.Mode}", a.Id);
    return Results.Json(new { message = "Interview scheduled" });
})));

api.MapPost("/recruiters/applications/{id:int}/offer", (int id, OfferRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "recruiter", u => db.Write(s =>
{
    if (r.PackageAmount is null or < 0) return Api.Fail("A valid package amount is required", 400);
    var (a, job, error) = Api.OwnedApplication(s, id, u.Id); if (error is not null) return error;
    var i = s.Interviews.FirstOrDefault(x => x.ApplicationId == id); if (i is null) return Api.Fail("Schedule an interview first", 400);
    var o = s.OfferLetters.FirstOrDefault(x => x.InterviewId == i.Id);
    if (o is not null && o.Response == "Accepted") return Api.Fail("This offer has already been accepted", 409);
    i.Status = "Completed";
    if (o is null) { o = new OfferLetter { Id = AipmsStore.Next(s), InterviewId = i.Id }; s.OfferLetters.Add(o); }
    else { o.Response = "Pending"; o.ResponseMessage = null; o.RespondedAt = null; } // a revised offer needs a fresh response
    o.Package = r.PackageAmount; o.JoiningDate = r.JoiningDate; a!.Status = "Selected";
    var st = s.Students.FirstOrDefault(x => x.Id == a.StudentId);
    if (st is not null) Api.Notify(s, st.UserId, $"Congratulations! You received an offer for \"{job!.Title}\"", "offer", $"Package: ₹{r.PackageAmount:N0} · Joining: {(r.JoiningDate is DateTime jd ? jd.ToString("d MMM yyyy") : "to be announced")}", o.Id);
    return Results.Json(new { message = "Offer issued" });
})));

// ------------------------------------------------------------------ TPO (and admin)
var tpo = new[] { "tpo", "admin" };

api.MapGet("/tpo/pending-recruiters", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.Recruiters.Select(r => (r, user: s.Users.FirstOrDefault(x => x.Id == r.UserId))).Where(x => x.user is { IsApproved: false, IsActive: true }).Select(x =>
{
    var co = x.r.CompanyId is null ? null : s.Companies.FirstOrDefault(c2 => c2.Id == x.r.CompanyId);
    return new { x.r.Id, full_name = x.r.FullName, x.r.Designation, x.r.Phone, email = x.user!.Email, is_approved = false, companyId = co?.Id, companyName = co?.Name, companyLocation = co?.Location, companyIndustry = co?.Industry, companyWebsite = co?.Website, companyDescription = co?.Description };
}).ToList()))));

api.MapPost("/tpo/recruiters/{id:int}/approve", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, actor => db.Write(s =>
{
    var r = s.Recruiters.FirstOrDefault(x => x.Id == id); if (r is null) return Api.Fail("Recruiter not found", 404);
    var user = s.Users.FirstOrDefault(x => x.Id == r.UserId); if (user is null) return Api.Fail("Recruiter account not found", 404);
    user.IsApproved = true;
    if (r.CompanyId is int company) { var co = s.Companies.FirstOrDefault(x => x.Id == company); if (co is not null) co.IsVerified = true; }
    s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"APPROVE_RECRUITER {user.Email}" });
    return Results.Json(new { message = "Recruiter approved" });
})));

api.MapPost("/tpo/recruiters/{id:int}/reject", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, actor => db.Write(s =>
{
    var r = s.Recruiters.FirstOrDefault(x => x.Id == id); if (r is null) return Api.Fail("Recruiter not found", 404);
    var user = s.Users.FirstOrDefault(x => x.Id == r.UserId); if (user is null) return Api.Fail("Recruiter account not found", 404);
    if (user.IsApproved) return Api.Fail("This recruiter is already approved; deactivate the account instead", 409);
    if (s.Jobs.Any(j => j.RecruiterId == r.Id)) return Api.Fail("This recruiter has job postings and cannot be rejected", 409);
    s.Recruiters.Remove(r); s.Notifications.RemoveAll(n => n.UserId == user.Id);
    foreach (var log in s.AuditLogs.Where(l => l.UserId == user.Id)) log.UserId = null;
    s.Users.Remove(user);
    if (r.CompanyId is int company && s.Recruiters.All(x => x.CompanyId != company) && s.Companies.FirstOrDefault(x => x.Id == company) is { IsVerified: false } co) s.Companies.Remove(co);
    s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"REJECT_RECRUITER {user.Email}" });
    return Results.Json(new { message = "Recruiter rejected" });
})));

api.MapGet("/tpo/students", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.Students.OrderBy(x => x.FullName).Select(st =>
{
    var user = s.Users.FirstOrDefault(u => u.Id == st.UserId);
    return new { st.Id, full_name = st.FullName, enrollment_no = st.EnrollmentNo, st.Cgpa, email = user?.Email, is_active = user?.IsActive ?? false, department = st.DepartmentId is null ? null : s.Departments.FirstOrDefault(d => d.Id == st.DepartmentId)?.Name };
}).ToList()))));

api.MapPut("/tpo/students/{id:int}/toggle-active", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, actor => db.Write(s =>
{
    var st = s.Students.FirstOrDefault(x => x.Id == id); if (st is null) return Api.Fail("Student not found", 404);
    var user = s.Users.FirstOrDefault(x => x.Id == st.UserId); if (user is null) return Api.Fail("Student account not found", 404);
    user.IsActive = !user.IsActive;
    s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"{(user.IsActive ? "ACTIVATE" : "DEACTIVATE")}_USER {user.Email}" });
    return Results.Json(new { message = user.IsActive ? "Student activated" : "Student deactivated", is_active = user.IsActive });
})));

api.MapGet("/tpo/reports", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s =>
{
    var selected = s.Applications.Where(x => x.Status == "Selected").ToList();
    var placedIds = selected.Select(x => x.StudentId).Distinct().ToList();
    var placed = placedIds.Count;
    string? CompanyOf(Application a) { var j = s.Jobs.FirstOrDefault(x => x.Id == a.JobId); var r = j is null ? null : s.Recruiters.FirstOrDefault(x => x.Id == j.RecruiterId); return r?.CompanyId is int ci ? s.Companies.FirstOrDefault(x => x.Id == ci)?.Name : null; }
    return Results.Json(new
    {
        totalStudents = s.Students.Count, totalRecruiters = s.Recruiters.Count, totalJobs = s.Jobs.Count, totalApplications = s.Applications.Count, placed,
        placementRate = s.Students.Count == 0 ? 0 : (int)Math.Round(100d * placed / s.Students.Count),
        avgAtsScore = s.ResumeAnalyses.Count == 0 ? null : (int?)Math.Round(s.ResumeAnalyses.Average(x => x.AtsScore)),
        byStatus = s.Applications.GroupBy(x => x.Status).Select(g => new { status = g.Key, c = g.Count() }).ToList(),
        byDepartment = s.Students.Where(st => placedIds.Contains(st.Id)).GroupBy(st => s.Departments.FirstOrDefault(d => d.Id == st.DepartmentId)?.Name).Select(g => new { department = g.Key, placedCount = g.Count() }).OrderByDescending(x => x.placedCount).ToList(),
        topCompanies = selected.GroupBy(CompanyOf).Select(g => new { name = g.Key, hires = g.Count() }).OrderByDescending(x => x.hires).Take(5).ToList(),
    });
})));

api.MapGet("/tpo/companies", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.Companies.OrderBy(x => x.Name).Select(co =>
{
    var recruiterIds = s.Recruiters.Where(r => r.CompanyId == co.Id).Select(r => r.Id).ToHashSet();
    var jobs = s.Jobs.Where(j => recruiterIds.Contains(j.RecruiterId)).OrderByDescending(j => j.PostedAt).Select(j =>
    {
        var appIds = s.Applications.Where(a => a.JobId == j.Id).Select(a => a.Id).ToHashSet();
        var offers = s.OfferLetters.Count(o => s.Interviews.Any(i => i.Id == o.InterviewId && appIds.Contains(i.ApplicationId)));
        return new { j.Id, j.Title, jobType = j.JobType, j.Location, salaryPackage = j.SalaryPackage, applicantCount = appIds.Count, offersIssued = offers, isActive = j.IsActive };
    }).ToList();
    return new { co.Id, co.Name, co.Industry, co.Location, co.Description, co.Website, is_verified = co.IsVerified, jobs };
}).ToList()))));

api.MapGet("/tpo/drives", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.PlacementDrives.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(d => new { d.Id, d.Title, company_id = d.CompanyId, companyName = d.CompanyId is null ? null : s.Companies.FirstOrDefault(x => x.Id == d.CompanyId)?.Name, drive_date = d.DriveDate, d.Description, d.Status, created_at = d.CreatedAt }).ToList()))));

api.MapPost("/tpo/drives", (DriveRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Title)) return Api.Fail("title is required", 400);
    if (r.CompanyId is int ci && s.Companies.All(x => x.Id != ci)) return Api.Fail("Selected company does not exist", 400);
    var d = new PlacementDrive { Id = AipmsStore.Next(s), Title = r.Title.Trim(), CompanyId = r.CompanyId, DriveDate = r.DriveDate, Description = r.Description ?? "" };
    s.PlacementDrives.Add(d);
    return Results.Json(new { message = "Drive created", id = d.Id }, statusCode: 201);
})));

api.MapPut("/tpo/drives/{id:int}/status", (int id, StatusRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    var valid = new[] { "Upcoming", "Ongoing", "Completed", "Cancelled" };
    if (r.Status is null || !valid.Contains(r.Status)) return Api.Fail("Status must be one of: " + string.Join(", ", valid), 400);
    var d = s.PlacementDrives.FirstOrDefault(x => x.Id == id); if (d is null) return Api.Fail("Drive not found", 404);
    d.Status = r.Status; return Results.Json(new { message = "Status updated" });
})));

api.MapGet("/tpo/departments", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.Departments.OrderBy(x => x.Name).Select(d => new { d.Id, d.Name, courses = s.Courses.Where(x => x.DepartmentId == d.Id).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToList() }).ToList()))));

api.MapPost("/tpo/departments", (DepartmentRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    var name = r.Name?.Trim() ?? ""; if (name.Length == 0) return Api.Fail("Department name is required", 400);
    if (s.Departments.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return Api.Fail("A department with this name already exists", 409);
    var d = new Department { Id = AipmsStore.Next(s), Name = name }; s.Departments.Add(d);
    foreach (var course in (r.Courses ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) s.Courses.Add(new Course { Id = AipmsStore.Next(s), DepartmentId = d.Id, Name = course });
    return Results.Json(new { message = "Department added", id = d.Id }, statusCode: 201);
})));

api.MapPost("/tpo/departments/{id:int}/courses", (int id, CourseRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    if (s.Departments.All(x => x.Id != id)) return Api.Fail("Department not found", 404);
    var name = r.Name?.Trim() ?? ""; if (name.Length == 0) return Api.Fail("Course name is required", 400);
    if (s.Courses.Any(x => x.DepartmentId == id && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return Api.Fail("This course already exists in the department", 409);
    var course = new Course { Id = AipmsStore.Next(s), DepartmentId = id, Name = name }; s.Courses.Add(course);
    return Results.Json(new { message = "Course added", id = course.Id }, statusCode: 201);
})));

api.MapDelete("/tpo/courses/{id:int}", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    var course = s.Courses.FirstOrDefault(x => x.Id == id); if (course is null) return Api.Fail("Course not found", 404);
    s.Courses.Remove(course); return Results.Json(new { message = "Course removed" });
})));

api.MapPost("/tpo/announcements", (TextRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Message)) return Api.Fail("message is required", 400);
    var text = r.Message.Trim(); var recipients = 0;
    foreach (var st in s.Students) { var user = s.Users.FirstOrDefault(x => x.Id == st.UserId); if (user is not { IsActive: true }) continue; Api.Notify(s, user.Id, text.Length > 140 ? text[..140] + "…" : text, "announcement", text, null); recipients++; }
    return Results.Json(new { message = "Announcement sent", recipients });
})));

api.MapGet("/tpo/messages", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.PersonalMessages.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(50).Select(m => new
{
    m.Id, m.Subject, m.Message, created_at = m.CreatedAt,
    recipients = s.PersonalMessageRecipients.Where(x => x.MessageId == m.Id).Select(x => s.Students.FirstOrDefault(st => st.Id == x.StudentId)?.FullName ?? "Unknown").ToList(),
}).ToList()))));

api.MapPost("/tpo/messages", (MessageRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, actor => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Message)) return Api.Fail("message is required", 400);
    var ids = (r.StudentIds ?? []).Distinct().ToList(); if (ids.Count == 0) return Api.Fail("Select at least one student", 400);
    var students = s.Students.Where(x => ids.Contains(x.Id)).ToList(); if (students.Count != ids.Count) return Api.Fail("One or more selected students do not exist", 400);
    var subject = string.IsNullOrWhiteSpace(r.Subject) ? null : r.Subject.Trim();
    var m = new PersonalMessage { Id = AipmsStore.Next(s), SenderUserId = actor.Id, Subject = subject, Message = r.Message.Trim() }; s.PersonalMessages.Add(m);
    foreach (var st in students) { s.PersonalMessageRecipients.Add(new PersonalMessageRecipient { MessageId = m.Id, StudentId = st.Id }); Api.Notify(s, st.UserId, $"New message from the placement cell: {subject ?? "Message"}", "personal", m.Message, m.Id); }
    return Results.Json(new { message = "Message sent", recipients = students.Count }, statusCode: 201);
})));

api.MapGet("/tpo/queries", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Read(s => Results.Json(s.StudentQueries.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(q =>
{
    var st = s.Students.FirstOrDefault(x => x.Id == q.StudentId); var user = st is null ? null : s.Users.FirstOrDefault(x => x.Id == st.UserId);
    return new { q.Id, q.Subject, q.Message, q.Status, q.Reply, replied_at = q.RepliedAt, created_at = q.CreatedAt, full_name = st?.FullName ?? "Unknown", enrollment_no = st?.EnrollmentNo, email = user?.Email };
}).ToList()))));

api.MapPost("/tpo/queries/{id:int}/reply", (int id, ReplyRequest r, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, tpo, _ => db.Write(s =>
{
    if (string.IsNullOrWhiteSpace(r.Reply)) return Api.Fail("reply is required", 400);
    var q = s.StudentQueries.FirstOrDefault(x => x.Id == id); if (q is null) return Api.Fail("Query not found", 404);
    if (q.Reply is not null) return Api.Fail("This query has already been answered", 409);
    q.Reply = r.Reply.Trim(); q.RepliedAt = DateTime.UtcNow; q.Status = "Resolved";
    var st = s.Students.FirstOrDefault(x => x.Id == q.StudentId);
    if (st is not null) Api.Notify(s, st.UserId, $"The placement cell replied to your query{(string.IsNullOrWhiteSpace(q.Subject) ? "" : $" \"{q.Subject}\"")}", "query_reply", q.Reply, q.Id);
    return Results.Json(new { message = "Reply sent" });
})));

// ------------------------------------------------------------------ admin
api.MapGet("/admin/users", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "admin", _ => db.Read(s => Results.Json(s.Users.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(u => new { u.Id, u.Email, u.Role, is_active = u.IsActive, is_approved = u.IsApproved, created_at = u.CreatedAt }).ToList()))));

api.MapPost("/admin/users", (AdminUserRequest r, HttpContext c, AipmsStore db, TokenService t, PasswordService passwords) => Api.For(c, t, "admin", actor =>
{
    var email = (r.Email ?? "").Trim().ToLowerInvariant();
    if (email.Length == 0 || !email.Contains('@')) return Api.Fail("A valid email is required", 400);
    if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 6) return Api.Fail("Password must be at least 6 characters", 400);
    if (r.Role is not ("tpo" or "admin")) return Api.Fail("Role must be tpo or admin", 400);
    var hash = passwords.Hash(r.Password);
    return db.Write(s =>
    {
        if (s.Users.Any(x => x.Email == email)) return Api.Fail("Email already registered", 409);
        var user = new User { Id = AipmsStore.Next(s), Email = email, PasswordHash = hash, Role = r.Role!, IsApproved = true }; s.Users.Add(user);
        s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"CREATE_USER {email} ({user.Role})" });
        return Results.Json(new { message = "Account created", id = user.Id }, statusCode: 201);
    });
}));

api.MapPut("/admin/users/{id:int}/toggle-active", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "admin", actor => db.Write(s =>
{
    var user = s.Users.FirstOrDefault(x => x.Id == id); if (user is null) return Api.Fail("User not found", 404);
    if (user.Id == actor.Id) return Api.Fail("You cannot deactivate your own account", 400);
    user.IsActive = !user.IsActive;
    s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"{(user.IsActive ? "ACTIVATE" : "DEACTIVATE")}_USER {user.Email}" });
    return Results.Json(new { message = user.IsActive ? "Account activated" : "Account deactivated", is_active = user.IsActive });
})));

api.MapPut("/admin/users/{id:int}/approve", (int id, HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "admin", actor => db.Write(s =>
{
    var user = s.Users.FirstOrDefault(x => x.Id == id); if (user is null) return Api.Fail("User not found", 404);
    user.IsApproved = true;
    var rec = s.Recruiters.FirstOrDefault(x => x.UserId == user.Id);
    if (rec?.CompanyId is int ci && s.Companies.FirstOrDefault(x => x.Id == ci) is { } co) co.IsVerified = true;
    s.AuditLogs.Add(new AuditLog { Id = AipmsStore.Next(s), UserId = actor.Id, Action = $"APPROVE_USER {user.Email}" });
    return Results.Json(new { message = "Account approved" });
})));

api.MapGet("/admin/audit-logs", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "admin", _ => db.Read(s => Results.Json(s.AuditLogs.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(200).Select(l => new { l.Id, email = l.UserId is null ? null : s.Users.FirstOrDefault(u => u.Id == l.UserId)?.Email, l.Action, created_at = l.CreatedAt }).ToList()))));

api.MapGet("/admin/system-stats", (HttpContext c, AipmsStore db, TokenService t) => Api.For(c, t, "admin", _ => db.Read(s => Results.Json(new { users = s.Users.Count, students = s.Students.Count, recruiters = s.Recruiters.Count, companies = s.Companies.Count, jobs = s.Jobs.Count, applications = s.Applications.Count, resumes = s.Resumes.Count }))));

// Unknown API paths must be a JSON 404, never the SPA's index.html (which the frontend would misread as data).
api.MapFallback(() => Api.Fail("API endpoint not found", 404));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }

static class Api
{
    public static IResult Fail(string error, int code) => Results.Json(new { error }, statusCode: code);
    public static UserSession? Authorize(HttpContext c, TokenService tokens, string role, out IResult? error)
    {
        var u = tokens.Validate(c.Request.Headers.Authorization);
        error = u is null ? Fail("Missing or invalid auth token", 401) : u.Role != role ? Fail("Forbidden: insufficient role", 403) : null;
        return error is null ? u : null;
    }
    public static IResult WithUser(HttpContext c, TokenService tokens, Func<UserSession, IResult> action) { var u = tokens.Validate(c.Request.Headers.Authorization); return u is null ? Fail("Missing or invalid auth token", 401) : action(u); }
    public static IResult For(HttpContext c, TokenService tokens, string role, Func<UserSession, IResult> action) => For(c, tokens, new[] { role }, action);
    public static IResult For(HttpContext c, TokenService tokens, string[] roles, Func<UserSession, IResult> action) { var u = tokens.Validate(c.Request.Headers.Authorization); return u is null ? Fail("Missing or invalid auth token", 401) : !roles.Contains(u.Role) ? Fail("Forbidden: insufficient role", 403) : action(u); }

    public static Student? Student(DatabaseState s, int userId) => s.Students.FirstOrDefault(x => x.UserId == userId);
    public static Recruiter? Recruiter(DatabaseState s, int userId) => s.Recruiters.FirstOrDefault(x => x.UserId == userId);
    public static bool IsEligible(Student st, Job j) => !st.Cgpa.HasValue || st.Cgpa >= j.MinCgpa;
    public static List<string> Skills(DatabaseState s, int studentId) => s.StudentSkills.Where(x => x.StudentId == studentId).Select(x => s.Skills.FirstOrDefault(k => k.Id == x.SkillId)?.Name).Where(x => x is not null).Select(x => x!).ToList();

    /// <summary>Adds a skill (creating it if needed) to a student, ignoring blanks and duplicates.</summary>
    public static void AddSkill(DatabaseState s, int studentId, string raw)
    {
        var name = (raw ?? "").Trim().ToLowerInvariant(); if (name.Length == 0) return;
        var skill = s.Skills.FirstOrDefault(x => x.Name == name);
        if (skill is null) { skill = new Skill { Id = AipmsStore.Next(s), Name = name }; s.Skills.Add(skill); }
        if (!s.StudentSkills.Any(x => x.StudentId == studentId && x.SkillId == skill.Id)) s.StudentSkills.Add(new StudentSkill { StudentId = studentId, SkillId = skill.Id });
    }

    /// <summary>Finds an application that belongs to one of the recruiter's jobs; a foreign or missing id is a 404.</summary>
    public static (Application? App, Job? Job, IResult? Error) OwnedApplication(DatabaseState s, int applicationId, int recruiterUserId)
    {
        var a = s.Applications.FirstOrDefault(x => x.Id == applicationId);
        var job = a is null ? null : s.Jobs.FirstOrDefault(x => x.Id == a.JobId);
        var rec = job is null ? null : s.Recruiters.FirstOrDefault(x => x.Id == job.RecruiterId);
        return a is null || job is null || rec is null || rec.UserId != recruiterUserId ? (null, null, Fail("Application not found", 404)) : (a, job, null);
    }

    public static void Notify(DatabaseState s, int uid, string message, string type, string? detail, int? refId) => s.Notifications.Add(new Notification { Id = AipmsStore.Next(s), UserId = uid, Message = message, Type = type, Detail = detail, ReferenceId = refId });
    public static object Notif(Notification n) => new { n.Id, user_id = n.UserId, n.Message, n.Type, n.Detail, reference_id = n.ReferenceId, is_read = n.IsRead, created_at = n.CreatedAt };

    public static object StudentView(DatabaseState s, Student st) => new
    {
        st.Id, user_id = st.UserId, full_name = st.FullName, enrollment_no = st.EnrollmentNo, department_id = st.DepartmentId, st.Cgpa, st.Phone, passing_year = st.PassingYear, st.Bio,
        department = st.DepartmentId is null ? null : s.Departments.FirstOrDefault(x => x.Id == st.DepartmentId)?.Name,
        skills = s.StudentSkills.Where(x => x.StudentId == st.Id).Select(x => s.Skills.FirstOrDefault(k => k.Id == x.SkillId)).Where(k => k is not null).ToList(),
        activeResume = s.Resumes.Where(x => x.StudentId == st.Id && x.IsActive).OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.Id).Select(x => new { x.Id, x.Filename, x.UploadedAt }).FirstOrDefault(),
    };

    public static Dictionary<string, object?> JobView(DatabaseState s, Job j)
    {
        var r = s.Recruiters.FirstOrDefault(x => x.Id == j.RecruiterId);
        return new()
        {
            ["id"] = j.Id, ["recruiter_id"] = j.RecruiterId, ["title"] = j.Title, ["description"] = j.Description, ["job_type"] = j.JobType, ["location"] = j.Location,
            ["salary_package"] = j.SalaryPackage, ["min_cgpa"] = j.MinCgpa, ["required_skills"] = string.Join(", ", j.RequiredSkills), ["deadline"] = j.Deadline, ["posted_at"] = j.PostedAt, ["is_active"] = j.IsActive,
            ["recruiterName"] = r?.FullName, ["companyName"] = r?.CompanyId is int ci ? s.Companies.FirstOrDefault(x => x.Id == ci)?.Name : null,
        };
    }
    public static Dictionary<string, object?> Recommendation(Dictionary<string, object?> job, int match, List<string> missing, bool eligible) { job["matchPct"] = match; job["missingSkills"] = missing; job["eligible"] = eligible; return job; }

    public static object ApplicationView(DatabaseState s, Application a)
    {
        var j = s.Jobs.FirstOrDefault(x => x.Id == a.JobId); var r = j is null ? null : s.Recruiters.FirstOrDefault(x => x.Id == j.RecruiterId);
        var i = s.Interviews.FirstOrDefault(x => x.ApplicationId == a.Id); var o = i is null ? null : s.OfferLetters.FirstOrDefault(x => x.InterviewId == i.Id);
        return new
        {
            a.Id, job_id = a.JobId, student_id = a.StudentId, a.Status, ats_score_at_apply = a.AtsScoreAtApply, applied_at = a.AppliedAt,
            Title = j?.Title ?? "(removed job)", job_type = j?.JobType, Location = j?.Location,
            companyName = r?.CompanyId is int ci ? s.Companies.FirstOrDefault(x => x.Id == ci)?.Name : null,
            scheduled_at = i?.ScheduledAt, interview_mode = i?.Mode, interview_status = i?.Status,
            offer_id = o?.Id, offer_package = o?.Package, joining_date = o?.JoiningDate, offer_response = o?.Response, offer_response_message = o?.ResponseMessage,
        };
    }

    public static dynamic ApplicantView(DatabaseState s, Application a, Job j, ResumeAnalyzer ai)
    {
        var st = s.Students.First(x => x.Id == a.StudentId); var gap = ai.SkillGap(Skills(s, st.Id), j.RequiredSkills);
        var i = s.Interviews.FirstOrDefault(x => x.ApplicationId == a.Id); var o = i is null ? null : s.OfferLetters.FirstOrDefault(x => x.InterviewId == i.Id);
        return new
        {
            a.Id, job_id = a.JobId, student_id = st.Id, a.Status, ats_score_at_apply = a.AtsScoreAtApply, applied_at = a.AppliedAt,
            full_name = st.FullName, enrollment_no = st.EnrollmentNo, st.Cgpa, department = st.DepartmentId is null ? null : s.Departments.FirstOrDefault(x => x.Id == st.DepartmentId)?.Name,
            studentSkills = Skills(s, st.Id), matchPct = gap.MatchPct, missingSkills = gap.Missing,
            offer_id = o?.Id, offer_package = o?.Package, joining_date = o?.JoiningDate, offer_response = o?.Response, offer_response_message = o?.ResponseMessage,
        };
    }
}