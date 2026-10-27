#!/usr/bin/env python3
"""
End-to-end API tests for AIPMS.Net.

Usage:  python3 tests/api_tests.py http://127.0.0.1:5180 [path/to/App_Data/aipms.db]

Run against a throw-away data folder (a fresh App_Data), NOT your real data: the tests create users,
jobs, applications, etc. Pass the SQLite path to also verify the persisted database state.
Uses only the Python standard library.
"""
import sys, json, io, zipfile, zlib, uuid, sqlite3, threading, urllib.request, urllib.error

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5180").rstrip("/") + "/api"
DB = sys.argv[2] if len(sys.argv) > 2 else None
results = []


def call(method, path, body=None, token=None, raw=None, ctype=None):
    headers = {}
    data = None
    if token: headers["Authorization"] = f"Bearer {token}"
    if raw is not None:
        data = raw; headers["Content-Type"] = ctype
    elif body is not None:
        data = json.dumps(body).encode(); headers["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            text = r.read().decode(); status = r.status; ct = r.headers.get("content-type", "")
    except urllib.error.HTTPError as e:
        text = e.read().decode(); status = e.code; ct = e.headers.get("content-type", "")
    try: js = json.loads(text) if text and "json" in ct else None
    except Exception: js = None
    return status, js, ct, text


def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"   <-- {detail}"))


def expect(name, resp, status, **fields):
    s, js, ct, text = resp
    ok = s == status and "json" in ct
    for k, v in fields.items():
        ok = ok and js is not None and js.get(k) == v
    check(name, ok, f"got HTTP {s} {text[:160]!r}")
    return js


def login(email, password, role):
    s, js, _, t = call("POST", "/auth/login", {"email": email, "password": password, "role": role})
    assert s == 200, f"login failed for {email}: {s} {t}"
    return js["token"]


def multipart(field, filename, content, ctype="application/octet-stream"):
    b = "----t" + uuid.uuid4().hex
    body = (f"--{b}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{filename}\"\r\nContent-Type: {ctype}\r\n\r\n").encode() + content + f"\r\n--{b}--\r\n".encode()
    return body, f"multipart/form-data; boundary={b}"


RESUME_TEXT = ("John Doe email john@example.com phone 9999999999 linkedin. Education B.Tech University college CGPA 8.5. "
               "Experience internship worked as developer. Projects: built a project using python java sql react javascript git docker. "
               "Skills technologies tools: python, java, sql, react, javascript, git, docker, aws, linux, rest api. Certification certified. "
               "Developed and designed services, improved performance by 30%, reduced cost by 20%, led a team of 5, optimized queries, automated deployment, "
               "implemented features, delivered 3+ releases and managed 12 projects. " + "More detail about the work done. " * 12)


def make_docx(text):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("word/document.xml", '<?xml version="1.0"?><w:document xmlns:w="x"><w:body><w:p><w:r><w:t>' + text + "</w:t></w:r></w:p></w:body></w:document>")
    return buf.getvalue()


def make_pdf(text, compress=True):
    stream = f"BT /F1 12 Tf 72 720 Td ({text}) Tj ET".encode()
    flt = b""
    if compress: stream = zlib.compress(stream); flt = b" /Filter /FlateDecode"
    objs = [b"<< /Type /Catalog /Pages 2 0 R >>", b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            b"<< /Length %d%s >>\nstream\n" % (len(stream), flt) + stream + b"\nendstream",
            b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    out = b"%PDF-1.4\n"
    for i, o in enumerate(objs, 1): out += b"%d 0 obj\n" % i + o + b"\nendobj\n"
    return out + b"trailer << /Root 1 0 R /Size 6 >>\n%%EOF"


def db_scalar(sql):
    con = sqlite3.connect(DB); v = con.execute(sql).fetchone()[0]; con.close(); return v


# =============================================================== run
tag = uuid.uuid4().hex[:6]

# ---------------------------------------------------------------- platform / errors
s, js, ct, t = call("GET", "/health"); check("health endpoint returns 200 JSON", s == 200 and js and js["status"] == "ok")
s, js, ct, t = call("GET", "/does/not/exist"); check("unknown GET /api path -> JSON 404 (not index.html)", s == 404 and "json" in ct, f"{s} {ct}")
s, js, ct, t = call("POST", "/does/not/exist", {}); check("unknown POST /api path -> JSON 404", s == 404 and "json" in ct, f"{s} {ct}")
s, js, ct, t = call("GET", "/tpo/drives"); check("previously-unmapped /tpo/drives is now a real route (401 without token)", s == 401 and "json" in ct, f"{s} {ct}")
req = urllib.request.Request(BASE[:-4] + "/", method="GET")
with urllib.request.urlopen(req) as r: check("SPA index.html still served at /", r.status == 200 and b"AIPMS" in r.read())

# ---------------------------------------------------------------- authentication
tpo = login("tpo@aipms.local", "Tpo@1234", "tpo")
admin = login("admin@aipms.local", "Admin@123", "admin")
rec1 = login("hr@technova.com", "Recruit@123", "recruiter")
stu = login("aditi.rao@student.aipms.local", "Student@123", "student")
check("login works for all 4 seeded roles", all([tpo, admin, rec1, stu]))
expect("login wrong password -> 401", call("POST", "/auth/login", {"email": "tpo@aipms.local", "password": "bad", "role": "tpo"}), 401)
expect("login wrong role -> 401", call("POST", "/auth/login", {"email": "tpo@aipms.local", "password": "Tpo@1234", "role": "student"}), 401)
expect("login empty body -> 400", call("POST", "/auth/login", {}), 400)
expect("login null email/password JSON -> 400 (was NullReference 500)", call("POST", "/auth/login", {"email": None, "password": None}), 400)
expect("malformed bearer token -> 401 (was 500)", call("GET", "/students/me", token="a.b.c!!"), 401)
expect("garbage bearer token -> 401", call("GET", "/students/me", token="not-a-token"), 401)
expect("no token -> 401", call("GET", "/students/me"), 401)
expect("student calling TPO endpoint -> 403", call("GET", "/tpo/students", token=stu), 403)
expect("student calling admin endpoint -> 403", call("GET", "/admin/users", token=stu), 403)
expect("recruiter calling student endpoint -> 403", call("GET", "/students/me", token=rec1), 403)

# ---------------------------------------------------------------- registration (the original 500 scenario)
dept = call("GET", "/public/departments")[1][0]["id"]
email_a, enroll = f"a{tag}@t.com", f"EN{tag}"
expect("register student A -> 201", call("POST", "/auth/register", {"role": "student", "fullName": "A", "email": email_a, "password": "secret1", "enrollmentNo": enroll, "departmentId": dept}), 201)
expect("register duplicate EMAIL -> 409", call("POST", "/auth/register", {"role": "student", "email": email_a, "password": "secret1"}), 409)
expect("register duplicate ENROLLMENT no -> 409 (was 500 + poisoned DB)", call("POST", "/auth/register", {"role": "student", "fullName": "B", "email": f"b{tag}@t.com", "password": "secret1", "enrollmentNo": enroll.lower()}), 409)
check("rejected duplicate was NOT created (B cannot log in)", call("POST", "/auth/login", {"email": f"b{tag}@t.com", "password": "secret1"})[0] == 401)
expect("unrelated register after the rejected one -> 201 (app not poisoned)", call("POST", "/auth/register", {"role": "student", "fullName": "C", "email": f"c{tag}@t.com", "password": "secret1", "enrollmentNo": f"EC{tag}"}), 201)
expect("unrelated login after the rejected one -> 200", call("POST", "/auth/login", {"email": "tpo@aipms.local", "password": "Tpo@1234"}), 200)
expect("register invalid department -> 400", call("POST", "/auth/register", {"role": "student", "email": f"d{tag}@t.com", "password": "secret1", "departmentId": 99999}), 400)
expect("register short password -> 400", call("POST", "/auth/register", {"role": "student", "email": f"e{tag}@t.com", "password": "123"}), 400)
expect("register CGPA 11 -> 400", call("POST", "/auth/register", {"role": "student", "email": f"f{tag}@t.com", "password": "secret1", "cgpa": 11}), 400)
expect("register null email -> 400", call("POST", "/auth/register", {"role": "student", "email": None, "password": "secret1"}), 400)
expect("register bad role -> 400", call("POST", "/auth/register", {"role": "admin", "email": f"g{tag}@t.com", "password": "secret1"}), 400)
expect("register recruiter without company -> 400", call("POST", "/auth/register", {"role": "recruiter", "email": f"h{tag}@t.com", "password": "secret1"}), 400)
check("registered student can log in", call("POST", "/auth/login", {"email": email_a, "password": "secret1", "role": "student"})[0] == 200)

# ---------------------------------------------------------------- recruiter approval flow (TPO)
r_email = f"hr{tag}@acme.com"
expect("register recruiter (new company) -> 201", call("POST", "/auth/register", {"role": "recruiter", "fullName": "Rec", "email": r_email, "password": "secret1", "companyName": f"Acme {tag}"}), 201)
expect("pending recruiter cannot log in -> 403", call("POST", "/auth/login", {"email": r_email, "password": "secret1"}), 403)
pend = call("GET", "/tpo/pending-recruiters", token=tpo)[1]
mine = [p for p in pend if p["email"] == r_email]
check("TPO sees pending recruiter with company details", len(mine) == 1 and mine[0]["companyName"] == f"Acme {tag}")
expect("TPO approves recruiter", call("POST", f"/tpo/recruiters/{mine[0]['id']}/approve", token=tpo), 200)
check("approved recruiter can log in", call("POST", "/auth/login", {"email": r_email, "password": "secret1"})[0] == 200)
expect("approve nonexistent recruiter -> 404", call("POST", "/tpo/recruiters/999999/approve", token=tpo), 404)
r2 = f"rej{tag}@acme.com"
call("POST", "/auth/register", {"role": "recruiter", "email": r2, "password": "secret1", "companyName": f"RejectCo {tag}"})
rid = [p for p in call("GET", "/tpo/pending-recruiters", token=tpo)[1] if p["email"] == r2][0]["id"]
expect("TPO rejects pending recruiter (route was missing)", call("POST", f"/tpo/recruiters/{rid}/reject", token=tpo), 200)
check("rejected recruiter is gone from pending list", all(p["email"] != r2 for p in call("GET", "/tpo/pending-recruiters", token=tpo)[1]))
check("rejected recruiter cannot log in", call("POST", "/auth/login", {"email": r2, "password": "secret1"})[0] == 401)
expect("reject an approved recruiter -> 409", call("POST", f"/tpo/recruiters/{mine[0]['id']}/reject", token=tpo), 409)

# ---------------------------------------------------------------- student profile
me = expect("GET /students/me", call("GET", "/students/me", token=stu), 200)
check("profile hides resume raw text / file path", me and "rawText" not in json.dumps(me) and "filepath" not in json.dumps(me).lower())
expect("update profile + skills", call("PUT", "/students/me", {"fullName": "Aditi Rao", "phone": "123", "cgpa": 8.7, "skillNames": ["Python", " SQL ", "", "python"]}, token=stu), 200)
sk = [x["name"] for x in call("GET", "/students/me", token=stu)[1]["skills"]]
check("skills saved lower-cased, de-duplicated, blanks dropped", sorted(sk) == ["python", "sql"], str(sk))
expect("profile bad department -> 400 (was 500 FK failure)", call("PUT", "/students/me", {"departmentId": 99999}, token=stu), 400)
expect("profile CGPA 12 -> 400", call("PUT", "/students/me", {"cgpa": 12}, token=stu), 400)
expect("profile empty name -> 400", call("PUT", "/students/me", {"fullName": "  "}, token=stu), 400)
check("failed update did not change profile", call("GET", "/students/me", token=stu)[1]["cgpa"] == 8.7)

# ---------------------------------------------------------------- resume + AI analysis
expect("analysis before upload -> 404", call("GET", "/students/resume/analysis", token=login(email_a, "secret1", "student")), 404)
body, ct = multipart("resume", "cv.txt", b"hello", "text/plain"); expect("upload .txt -> 400", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 400)
body, ct = multipart("resume", "cv.pdf", b"not really a pdf"); expect("upload fake PDF -> 400 (was unhandled)", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 400)
body, ct = multipart("resume", "cv.docx", b"not a zip"); expect("upload corrupt DOCX -> 400 (was 500)", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 400)
expect("upload without multipart -> 400", call("POST", "/students/resume", {"x": 1}, token=stu), 400)
body, ct = multipart("resume", "big.pdf", b"%PDF-" + b"0" * (5 * 1024 * 1024 + 10)); expect("upload >5MB -> 400", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 400)
body, ct = multipart("resume", "cv.docx", make_docx(RESUME_TEXT)); js = expect("upload valid DOCX -> 201", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 201)
check("DOCX analysis finds skills and a real score", js and "python" in js["extractedSkills"] and js["atsScore"] >= 60, str(js and (js["atsScore"], js["extractedSkills"])))
body, ct = multipart("resume", "cv.pdf", make_pdf(RESUME_TEXT)); js = expect("upload valid COMPRESSED PDF -> 201", call("POST", "/students/resume", raw=body, ctype=ct, token=stu), 201)
check("compressed-PDF text is extracted (was silently empty)", js and "python" in js["extractedSkills"] and js["atsScore"] >= 60, str(js and (js["atsScore"], js["extractedSkills"])))
an = expect("GET analysis after upload", call("GET", "/students/resume/analysis", token=stu), 200)
check("only latest resume active; analysis matches upload", an and an["resume"]["filename"] == "cv.pdf")

# ---------------------------------------------------------------- jobs, search, recommendations
jobs = expect("list jobs", call("GET", "/students/jobs", token=stu), 200)
check("jobs list non-empty", jobs and len(jobs) >= 4)
check("search with blank filters (what the UI sends) returns jobs (was 0)", len(call("GET", "/students/jobs?q=&type=&location=", token=stu)[1]) == len(jobs))
check("search type=Internship filters", all(j["job_type"] == "Internship" for j in call("GET", "/students/jobs?type=Internship", token=stu)[1]))
check("search by keyword", any("Data" in j["title"] for j in call("GET", "/students/jobs?q=data", token=stu)[1]))
recs = expect("recommendations", call("GET", "/students/recommendations", token=stu), 200)
check("recommendations carry matchPct/missingSkills/eligible + job fields", recs and all(k in recs[0] for k in ("matchPct", "missingSkills", "eligible", "title", "companyName")))

# ---------------------------------------------------------------- applications
fs = [j for j in jobs if j["title"] == "Full Stack Developer"][0]   # min CGPA 7.5
data_job = [j for j in jobs if j["title"] == "Data Analyst"][0]
expect("apply with no resume -> 400", call("POST", f"/students/apply/{fs['id']}", token=login(email_a, "secret1", "student")), 400)
js = expect("apply -> 201", call("POST", f"/students/apply/{fs['id']}", token=stu), 201)
expect("apply again -> 409 (duplicate blocked)", call("POST", f"/students/apply/{fs['id']}", token=stu), 409)
expect("apply to nonexistent job -> 404", call("POST", "/students/apply/999999", token=stu), 404)
rahul = login("rahul.verma@student.aipms.local", "Student@123", "student")
body, ct = multipart("resume", "r.docx", make_docx(RESUME_TEXT)); call("POST", "/students/resume", raw=body, ctype=ct, token=rahul)
expect("apply below min CGPA (7.4 < 7.5) -> 400", call("POST", f"/students/apply/{fs['id']}", token=rahul), 400)
apps = call("GET", "/students/applications", token=stu)[1]
check("application appears immediately with job/company info", len(apps) == 1 and apps[0]["title"] == "Full Stack Developer" and apps[0]["companyName"] == "TechNova Solutions", str(apps))

# ---------------------------------------------------------------- recruiter job management
expect("post job without title -> 400", call("POST", "/recruiters/jobs", {"title": " "}, token=rec1), 400)
expect("post job bad CGPA -> 400", call("POST", "/recruiters/jobs", {"title": "x", "minCgpa": 99}, token=rec1), 400)
js = expect("post job -> 201", call("POST", "/recruiters/jobs", {"title": f"QA Engineer {tag}", "description": "d", "jobType": "Full-Time", "location": "Pune", "salaryPackage": 500000, "minCgpa": 6, "requiredSkills": ["python", "Python", "sql"], "deadline": "2099-01-31"}, token=rec1), 201)
newjob = js["jobId"]
mine = call("GET", "/recruiters/jobs", token=rec1)[1]
check("posted job listed immediately with one copy", sum(1 for j in mine if j["title"] == f"QA Engineer {tag}") == 1)
check("required skills de-duplicated", [j for j in mine if j["id"] == newjob][0]["required_skills"] == "python, sql")
expect("student applies to new job", call("POST", f"/students/apply/{newjob}", token=stu), 201)
ap = call("GET", f"/recruiters/jobs/{newjob}/applicants", token=rec1)[1]
check("recruiter sees applicant", len(ap) == 1 and ap[0]["full_name"] == "Aditi Rao")
check("applicant view has offer fields (keys present)", "offer_id" in ap[0] and "offer_response" in ap[0])
app_id = ap[0]["id"]
expect("applicants of someone else's/nonexistent job -> 404", call("GET", "/recruiters/jobs/999999/applicants", token=rec1), 404)
expect("recruiter B cannot read recruiter A's applicants -> 404", call("GET", f"/recruiters/jobs/{newjob}/applicants", token=login("hr@datasphere.com", "Recruit@123", "recruiter")), 404)
expect("set status Shortlisted", call("PUT", f"/recruiters/applications/{app_id}/status", {"status": "Shortlisted"}, token=rec1), 200)
expect("invalid status -> 400", call("PUT", f"/recruiters/applications/{app_id}/status", {"status": "Bogus"}, token=rec1), 400)
expect("status on nonexistent application -> 404", call("PUT", "/recruiters/applications/999999/status", {"status": "Applied"}, token=rec1), 404)
expect("recruiter B cannot change recruiter A's application -> 404", call("PUT", f"/recruiters/applications/{app_id}/status", {"status": "Rejected"}, token=login("hr@datasphere.com", "Recruit@123", "recruiter")), 404)
expect("offer before interview -> 400", call("POST", f"/recruiters/applications/{app_id}/offer", {"packageAmount": 100}, token=rec1), 400)
expect("interview without date -> 400", call("POST", f"/recruiters/applications/{app_id}/interview", {"mode": "Online"}, token=rec1), 400)
expect("interview bad mode -> 400", call("POST", f"/recruiters/applications/{app_id}/interview", {"scheduledAt": "2099-02-01T10:00", "mode": "Teleport"}, token=rec1), 400)
expect("schedule interview (datetime-local format)", call("POST", f"/recruiters/applications/{app_id}/interview", {"scheduledAt": "2099-02-01T10:00", "mode": "Online"}, token=rec1), 200)
expect("offer with missing package -> 400", call("POST", f"/recruiters/applications/{app_id}/offer", {"joiningDate": None}, token=rec1), 400)
expect("issue offer", call("POST", f"/recruiters/applications/{app_id}/offer", {"packageAmount": 800000, "joiningDate": "2099-07-01"}, token=rec1), 200)
sa = [a for a in call("GET", "/students/applications", token=stu)[1] if a["job_id"] == newjob][0]
check("student sees interview + offer immediately (no refresh)", sa["status"] == "Selected" and sa["offer_id"] and sa["offer_response"] == "Pending" and sa["scheduled_at"], str(sa))
notes = call("GET", "/notifications", token=stu)[1]
check("student got interview + offer + status notifications", {"interview", "offer", "status_update"} <= {n["type"] for n in notes})
offer_notif = [n for n in notes if n["type"] == "offer"][0]
check("offer notification references offer id", offer_notif["reference_id"] == sa["offer_id"])
expect("student marks notification read", call("POST", f"/notifications/{offer_notif['id']}/read", token=stu), 200)
expect("mark someone else's/nonexistent notification -> 404", call("POST", "/notifications/999999/read", token=stu), 404)
expect("student offer response invalid -> 400", call("POST", f"/students/offers/{sa['offer_id']}/respond", {"response": "Maybe"}, token=stu), 400)
expect("other student cannot respond to offer -> 404", call("POST", f"/students/offers/{sa['offer_id']}/respond", {"response": "Accepted"}, token=rahul), 404)
expect("student negotiates", call("POST", f"/students/offers/{sa['offer_id']}/respond", {"response": "Negotiating", "message": "more?"}, token=stu), 200)
ap = call("GET", f"/recruiters/jobs/{newjob}/applicants", token=rec1)[1][0]
check("recruiter sees offer + student's response message", ap["offer_response"] == "Negotiating" and ap["offer_response_message"] == "more?" and ap["offer_package"] == 800000, str(ap))
check("recruiter notified of offer response", any(n["type"] == "offer_response" for n in call("GET", "/notifications", token=rec1)[1]))
expect("student accepts", call("POST", f"/students/offers/{sa['offer_id']}/respond", {"response": "Accepted"}, token=stu), 200)
expect("respond after final answer -> 409", call("POST", f"/students/offers/{sa['offer_id']}/respond", {"response": "Rejected"}, token=stu), 409)
expect("re-issue accepted offer -> 409", call("POST", f"/recruiters/applications/{app_id}/offer", {"packageAmount": 1}, token=rec1), 409)
expect("close job", call("PUT", f"/recruiters/jobs/{newjob}/close", token=rec1), 200)
expect("close nonexistent job -> 404 (was fake 'Job closed')", call("PUT", "/recruiters/jobs/999999/close", token=rec1), 404)
expect("apply to closed job -> 404", call("POST", f"/students/apply/{newjob}", token=login("rahul.verma@student.aipms.local", "Student@123", "student")), 404)
past = call("POST", "/recruiters/jobs", {"title": f"Past {tag}", "deadline": "2001-01-01"}, token=rec1)[1]["jobId"]
expect("apply after deadline -> 400", call("POST", f"/students/apply/{past}", token=stu), 400)
check("recruiter dashboard data", call("GET", "/recruiters/me", token=rec1)[1]["company"]["name"] == "TechNova Solutions")

# ---------------------------------------------------------------- student queries <-> TPO
expect("empty query -> 400", call("POST", "/students/queries", {"message": " "}, token=stu), 400)
q = expect("submit query -> 201", call("POST", "/students/queries", {"subject": "Help", "message": "Need info"}, token=stu), 201)
sq = call("GET", "/students/queries", token=stu)[1]
check("student query list uses created_at (UI field) and shows once", len(sq) == 1 and sq[0]["created_at"] and sq[0]["status"] == "Open", str(sq))
tq = call("GET", "/tpo/queries", token=tpo)[1]
mine_q = [x for x in tq if x["id"] == q["id"]][0]
check("TPO sees query with student name/enrollment", mine_q["full_name"] == "Aditi Rao" and mine_q["enrollment_no"] == "22CS001")
check("TPO got new_query notification", any(n["type"] == "new_query" for n in call("GET", "/notifications", token=tpo)[1]))
expect("empty reply -> 400", call("POST", f"/tpo/queries/{q['id']}/reply", {"reply": ""}, token=tpo), 400)
expect("TPO replies", call("POST", f"/tpo/queries/{q['id']}/reply", {"reply": "Done"}, token=tpo), 200)
expect("second reply -> 409", call("POST", f"/tpo/queries/{q['id']}/reply", {"reply": "again"}, token=tpo), 409)
expect("reply to nonexistent query -> 404", call("POST", "/tpo/queries/999999/reply", {"reply": "x"}, token=tpo), 404)
sq = call("GET", "/students/queries", token=stu)[1][0]
check("student sees reply + Resolved immediately", sq["reply"] == "Done" and sq["status"] == "Resolved" and sq["replied_at"])
check("student got query_reply notification", any(n["type"] == "query_reply" for n in call("GET", "/notifications", token=stu)[1]))

# ---------------------------------------------------------------- TPO modules
rep = expect("TPO reports", call("GET", "/tpo/reports", token=tpo), 200)
check("reports include byDepartment/topCompanies/avgAtsScore (dashboard crashed without them)", rep and isinstance(rep["byDepartment"], list) and isinstance(rep["topCompanies"], list) and rep["avgAtsScore"] is not None)
check("reports reflect the hire", rep["placed"] >= 1 and any(c["name"] == "TechNova Solutions" for c in rep["topCompanies"]))
comps = expect("TPO companies", call("GET", "/tpo/companies", token=tpo), 200)
tn = [c for c in comps if c["name"] == "TechNova Solutions"][0]
check("companies include jobs with applicant/offer counts", any(j["title"] == f"QA Engineer {tag}" and j["applicantCount"] == 1 and j["offersIssued"] == 1 for j in tn["jobs"]))
expect("drive without title -> 400", call("POST", "/tpo/drives", {"title": ""}, token=tpo), 400)
expect("drive bad company -> 400", call("POST", "/tpo/drives", {"title": "x", "companyId": 99999}, token=tpo), 400)
d = expect("create drive -> 201", call("POST", "/tpo/drives", {"title": f"Drive {tag}", "companyId": tn["id"], "driveDate": "2099-03-01", "description": "d"}, token=tpo), 201)
dl = call("GET", "/tpo/drives", token=tpo)[1]
check("drive listed once immediately with company name", sum(1 for x in dl if x["title"] == f"Drive {tag}") == 1 and [x for x in dl if x["id"] == d["id"]][0]["companyName"] == "TechNova Solutions")
expect("drive status update", call("PUT", f"/tpo/drives/{d['id']}/status", {"status": "Ongoing"}, token=tpo), 200)
expect("drive invalid status -> 400", call("PUT", f"/tpo/drives/{d['id']}/status", {"status": "Nope"}, token=tpo), 400)
expect("drive status nonexistent -> 404", call("PUT", "/tpo/drives/999999/status", {"status": "Ongoing"}, token=tpo), 404)
check("drive status persisted", [x for x in call("GET", "/tpo/drives", token=tpo)[1] if x["id"] == d["id"]][0]["status"] == "Ongoing")
dep = expect("create department with courses -> 201", call("POST", "/tpo/departments", {"name": f"Dept {tag}", "courses": ["B.Tech X", "b.tech x", "M.Tech X"]}, token=tpo), 201)
expect("duplicate department -> 409", call("POST", "/tpo/departments", {"name": f"dept {tag}"}, token=tpo), 409)
expect("empty department -> 400", call("POST", "/tpo/departments", {"name": ""}, token=tpo), 400)
dl = [x for x in call("GET", "/tpo/departments", token=tpo)[1] if x["id"] == dep["id"]][0]
check("department has 2 de-duplicated courses", len(dl["courses"]) == 2, str(dl))
c = expect("add course -> 201", call("POST", f"/tpo/departments/{dep['id']}/courses", {"name": "Diploma X"}, token=tpo), 201)
expect("duplicate course -> 409", call("POST", f"/tpo/departments/{dep['id']}/courses", {"name": "diploma x"}, token=tpo), 409)
expect("course on missing department -> 404", call("POST", "/tpo/departments/999999/courses", {"name": "x"}, token=tpo), 404)
expect("delete course", call("DELETE", f"/tpo/courses/{c['id']}", token=tpo), 200)
expect("delete missing course -> 404", call("DELETE", "/tpo/courses/999999", token=tpo), 404)
check("new department visible to public list (registration dropdown)", any(x["name"] == f"Dept {tag}" for x in call("GET", "/public/departments")[1]))
expect("announcement empty -> 400", call("POST", "/tpo/announcements", {"message": ""}, token=tpo), 400)
n_before = len(call("GET", "/notifications", token=stu)[1])
js = expect("announcement", call("POST", "/tpo/announcements", {"message": "Resumes due Friday"}, token=tpo), 200)
check("announcement reports recipient count and students receive it", js["recipients"] >= 3 and len(call("GET", "/notifications", token=stu)[1]) == n_before + 1)
studs = call("GET", "/tpo/students", token=tpo)[1]
aid = [x for x in studs if x["full_name"] == "Aditi Rao"][0]["id"]
expect("message with no recipients -> 400", call("POST", "/tpo/messages", {"studentIds": [], "message": "hi"}, token=tpo), 400)
expect("message to nonexistent student -> 400", call("POST", "/tpo/messages", {"studentIds": [999999], "message": "hi"}, token=tpo), 400)
js = expect("message to student -> 201", call("POST", "/tpo/messages", {"studentIds": [aid], "subject": "Hello", "message": "Interview tips"}, token=tpo), 201)
sent = call("GET", "/tpo/messages", token=tpo)[1]
check("sent message listed with recipient names", sent[0]["recipients"] == ["Aditi Rao"] and sent[0]["subject"] == "Hello")
check("student received personal notification", any(n["type"] == "personal" for n in call("GET", "/notifications", token=stu)[1]))
rahul_id = [x for x in studs if x["full_name"] == "Rahul Verma"][0]["id"]
expect("TPO deactivates student", call("PUT", f"/tpo/students/{rahul_id}/toggle-active", token=tpo), 200)
expect("deactivated student's EXISTING token stops working -> 401", call("GET", "/students/me", token=rahul), 401)
expect("deactivated student cannot log in -> 403", call("POST", "/auth/login", {"email": "rahul.verma@student.aipms.local", "password": "Student@123"}), 403)
expect("TPO reactivates student", call("PUT", f"/tpo/students/{rahul_id}/toggle-active", token=tpo), 200)
check("reactivated student can log in", call("POST", "/auth/login", {"email": "rahul.verma@student.aipms.local", "password": "Student@123"})[0] == 200)
expect("toggle nonexistent student -> 404", call("PUT", "/tpo/students/999999/toggle-active", token=tpo), 404)

# ---------------------------------------------------------------- admin
expect("admin creates TPO -> 201 (route was missing)", call("POST", "/admin/users", {"email": f"tpo{tag}@x.com", "password": "secret1", "role": "tpo"}, token=admin), 201)
expect("admin duplicate email -> 409", call("POST", "/admin/users", {"email": f"tpo{tag}@x.com", "password": "secret1", "role": "tpo"}, token=admin), 409)
expect("admin bad role -> 400", call("POST", "/admin/users", {"email": f"z{tag}@x.com", "password": "secret1", "role": "student"}, token=admin), 400)
expect("admin short password -> 400", call("POST", "/admin/users", {"email": f"y{tag}@x.com", "password": "1", "role": "tpo"}, token=admin), 400)
check("new TPO can log in", call("POST", "/auth/login", {"email": f"tpo{tag}@x.com", "password": "secret1", "role": "tpo"})[0] == 200)
users = call("GET", "/admin/users", token=admin)[1]
uid = [u for u in users if u["email"] == f"tpo{tag}@x.com"][0]["id"]
admin_id = [u for u in users if u["email"] == "admin@aipms.local"][0]["id"]
expect("admin deactivates user", call("PUT", f"/admin/users/{uid}/toggle-active", token=admin), 200)
check("deactivated TPO is blocked", call("POST", "/auth/login", {"email": f"tpo{tag}@x.com", "password": "secret1"})[0] == 403)
expect("admin cannot deactivate self -> 400", call("PUT", f"/admin/users/{admin_id}/toggle-active", token=admin), 400)
expect("admin approve user", call("PUT", f"/admin/users/{uid}/approve", token=admin), 200)
expect("admin toggle nonexistent -> 404", call("PUT", "/admin/users/999999/toggle-active", token=admin), 404)
logs = expect("admin audit logs (route was missing)", call("GET", "/admin/audit-logs", token=admin), 200)
check("audit logs include LOGIN/REGISTER/CREATE_USER with emails", logs and {"LOGIN", "REGISTER"} <= {l["action"] for l in logs} and any(l["action"].startswith("CREATE_USER") for l in logs) and any(l["email"] for l in logs))
st = expect("admin system stats", call("GET", "/admin/system-stats", token=admin), 200)
check("stats counts sensible", st["users"] >= 10 and st["jobs"] >= 6)
check("TPO may use shared tpo endpoints but admin may too", call("GET", "/tpo/students", token=admin)[0] == 200)

# ---------------------------------------------------------------- reliability: concurrent duplicate submits
fs2 = [j for j in jobs if j["title"] == "Frontend Developer Intern"][0]
statuses = []
def apply_once(): statuses.append(call("POST", f"/students/apply/{fs2['id']}", token=stu)[0])
ts = [threading.Thread(target=apply_once) for _ in range(20)]; [t.start() for t in ts]; [t.join() for t in ts]
check("20 simultaneous identical applies -> exactly one 201, rest 409, no 500", statuses.count(201) == 1 and statuses.count(409) == 19, str(sorted(set(statuses))) + str(statuses))
statuses = []; e = f"dup{tag}@t.com"
def reg_once(): statuses.append(call("POST", "/auth/register", {"role": "student", "email": e, "password": "secret1"})[0])
ts = [threading.Thread(target=reg_once) for _ in range(10)]; [t.start() for t in ts]; [t.join() for t in ts]
check("10 simultaneous identical registrations -> exactly one 201, no 500", statuses.count(201) == 1 and statuses.count(409) == 9, str(statuses))
statuses = []
def reg_unique(i): statuses.append(call("POST", "/auth/register", {"role": "student", "email": f"par{i}{tag}@t.com", "password": "secret1", "enrollmentNo": f"PAR{i}{tag}"})[0])
ts = [threading.Thread(target=reg_unique, args=(i,)) for i in range(10)]; [t.start() for t in ts]; [t.join() for t in ts]
check("10 simultaneous different registrations all succeed", statuses.count(201) == 10, str(statuses))
statuses = []
def post_job(): statuses.append(call("POST", "/recruiters/jobs", {"title": f"Dup {tag}"}, token=rec1)[0])
ts = [threading.Thread(target=post_job) for _ in range(5)]; [t.start() for t in ts]; [t.join() for t in ts]
check("concurrent writes by different requests never 500", 500 not in statuses and all(s == 201 for s in statuses), str(statuses))

# ---------------------------------------------------------------- database consistency
if DB:
    con = sqlite3.connect(DB)
    state = json.loads(con.execute("select json from app_state where id=1").fetchone()[0])
    pairs = [("users", "Users"), ("students", "Students"), ("recruiters", "Recruiters"), ("companies", "Companies"), ("jobs", "Jobs"), ("applications", "Applications"),
             ("resumes", "Resumes"), ("resume_analysis", "ResumeAnalyses"), ("interviews", "Interviews"), ("offer_letters", "OfferLetters"), ("notifications", "Notifications"),
             ("student_queries", "StudentQueries"), ("placement_drives", "PlacementDrives"), ("departments", "Departments"), ("courses", "Courses"),
             ("personal_messages", "PersonalMessages"), ("audit_logs", "AuditLogs"), ("skills", "Skills")]
    bad = [(t, con.execute(f"select count(*) from {t}").fetchone()[0], len(state[k[0].lower() + k[1:]])) for t, k in pairs if con.execute(f"select count(*) from {t}").fetchone()[0] != len(state[k[0].lower() + k[1:]])]
    check("relational tables match the JSON snapshot for every table (no partial writes)", not bad, str(bad))
    check("exactly one application per (job,student) in DB", con.execute("select count(*) from (select job_id,student_id from applications group by 1,2 having count(*)>1)").fetchone()[0] == 0)
    check("exactly 1 'Aditi' application for Frontend Intern in DB", con.execute("select count(*) from applications a join students s on s.id=a.student_id where s.full_name='Aditi Rao' and a.job_id=?", (fs2["id"],)).fetchone()[0] == 1)
    check("exactly 1 user per email for the 10x duplicate registration", con.execute("select count(*) from users where email=?", (e,)).fetchone()[0] == 1)
    check("duplicate-enrollment student B was never persisted", con.execute("select count(*) from users where email=?", (f"b{tag}@t.com",)).fetchone()[0] == 0)
    check("PRAGMA integrity_check ok", con.execute("pragma integrity_check").fetchone()[0] == "ok")
    check("PRAGMA foreign_key_check clean", con.execute("pragma foreign_key_check").fetchall() == [])
    con.close()

failed = [r for r in results if not r[1]]
print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
sys.exit(1 if failed else 0)