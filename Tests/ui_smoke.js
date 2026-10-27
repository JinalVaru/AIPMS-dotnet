// Headless frontend smoke test (jsdom). Loads the real SPA from a running server, logs in as each role,
// visits every route and checks: no error toast, no uncaught JS error, page body rendered.
// Then drives key UI actions and checks the page updates WITHOUT a manual reload.
//
// Setup:  cd tests && npm install jsdom       Run:  node tests/ui_smoke.js http://127.0.0.1:5180
// Use a throw-away data folder; this test creates records.
const { JSDOM, VirtualConsole } = require("jsdom");
const BASE = (process.argv[2] || "http://127.0.0.1:5180").replace(/\/$/, "");
const results = [];
const check = (name, ok, detail = "") => { results.push(ok); console.log(`${ok ? "PASS" : "FAIL"} ${name}${ok ? "" : "   <-- " + detail}`); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function openApp(session) {
  const errors = [];
  const vc = new VirtualConsole();
  vc.on("jsdomError", (e) => errors.push("jsdomError: " + (e.detail?.message || e.message)));
  vc.on("error", (...a) => errors.push("console.error: " + a.join(" ")));
  const dom = await JSDOM.fromURL(BASE + "/", {
    runScripts: "dangerously", resources: "usable", pretendToBeVisual: true, virtualConsole: vc,
    beforeParse(window) {
      window.fetch = (u, o) => fetch(new URL(u, BASE + "/"), o);
      window.addEventListener("error", (e) => errors.push("window error: " + e.message));
      window.addEventListener("unhandledrejection", (e) => errors.push("unhandled rejection: " + (e.reason?.message || e.reason)));
      if (session) { window.localStorage.setItem("aipms_token", session.token); window.localStorage.setItem("aipms_user", JSON.stringify(session.user)); }
    },
  });
  await sleep(600);
  return { dom, window: dom.window, doc: dom.window.document, errors };
}

async function login(email, password, role) {
  const res = await fetch(BASE + "/api/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password, role }) });
  return res.json();
}

async function visit(ctx, hash, settle = 700) {
  ctx.window.location.hash = hash;
  await sleep(settle);
}
const toasts = (ctx) => [...ctx.doc.querySelectorAll(".toast.error")].map((t) => t.textContent);
const bodyText = (ctx) => (ctx.doc.getElementById("pageBody")?.textContent || "").replace(/\s+/g, " ").trim();

async function smokeRoutes(label, session, hashes) {
  const ctx = await openApp(session);
  for (const h of hashes) {
    ctx.errors.length = 0; ctx.doc.querySelectorAll(".toast").forEach((t) => t.remove());
    await visit(ctx, h);
    const bad = [...ctx.errors, ...toasts(ctx)];
    const text = bodyText(ctx);
    check(`${label} ${h} renders cleanly`, bad.length === 0 && text.length > 0 && !/^Loading…$/.test(text), bad.join(" | ") || `body='${text.slice(0, 60)}'`);
  }
  return ctx;
}

(async () => {
  const student = await login("aditi.rao@student.aipms.local", "Student@123", "student");
  const recruiter = await login("hr@technova.com", "Recruit@123", "recruiter");
  const tpo = await login("tpo@aipms.local", "Tpo@1234", "tpo");
  const admin = await login("admin@aipms.local", "Admin@123", "admin");

  // ---------- every route for every role
  const sctx = await smokeRoutes("student", student, ["#/student/dashboard", "#/student/profile", "#/student/resume", "#/student/jobs", "#/student/recommendations", "#/student/applications", "#/student/queries", "#/student/notifications"]);
  const rctx = await smokeRoutes("recruiter", recruiter, ["#/recruiter/dashboard", "#/recruiter/post-job", "#/recruiter/jobs", "#/recruiter/notifications"]);
  const tctx = await smokeRoutes("tpo", tpo, ["#/tpo/dashboard", "#/tpo/recruiters", "#/tpo/companies", "#/tpo/students", "#/tpo/drives", "#/tpo/departments", "#/tpo/announcements", "#/tpo/messages", "#/tpo/queries", "#/tpo/notifications"]);
  const actx = await smokeRoutes("admin", admin, ["#/admin/users", "#/admin/audit-logs", "#/admin/stats"]);

  // ---------- UI actions: the page must update itself, with no reload
  // TPO: create a drive through the form -> row appears immediately, exactly once
  await visit(tctx, "#/tpo/drives");
  const title = "UI Drive " + Date.now();
  let f = tctx.doc.getElementById("driveForm");
  f.querySelector("[name=title]").value = title; f.querySelector("[name=driveDate]").value = "2099-05-01";
  f.dispatchEvent(new tctx.window.Event("submit", { cancelable: true, bubbles: true }));
  f.dispatchEvent(new tctx.window.Event("submit", { cancelable: true, bubbles: true })); // simulated double-submit
  await sleep(900);
  const rows = [...tctx.doc.querySelectorAll("table.ledger tbody tr")].filter((tr) => tr.textContent.includes(title));
  check("TPO drive form: new drive visible immediately, exactly once, despite double-submit", rows.length === 1, `rows=${rows.length}`);
  check("TPO drive form: success toast shown, no error toast", [...tctx.doc.querySelectorAll(".toast.success")].some((t) => /Drive created/.test(t.textContent)) && toasts(tctx).length === 0, toasts(tctx).join("|"));

  // TPO: add a department, then a course -> appear without reload
  await visit(tctx, "#/tpo/departments");
  const dep = "UI Dept " + Date.now();
  f = tctx.doc.getElementById("deptForm"); f.querySelector("[name=name]").value = dep; f.querySelector("[name=courses]").value = "Course A, Course B";
  f.dispatchEvent(new tctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(900);
  check("TPO departments: new department + its 2 courses render immediately", tctx.doc.getElementById("pageBody").textContent.includes(dep) && tctx.doc.getElementById("pageBody").textContent.includes("Course B"));
  const dupAttempt = () => { f = tctx.doc.getElementById("deptForm"); f.querySelector("[name=name]").value = dep; f.dispatchEvent(new tctx.window.Event("submit", { cancelable: true, bubbles: true })); };
  dupAttempt(); await sleep(700);
  check("TPO departments: duplicate department shows a real error message (not success)", toasts(tctx).some((t) => /already exists/.test(t)), toasts(tctx).join("|"));
  const x = [...tctx.doc.querySelectorAll(".remove-course")].find((a) => a.parentElement.textContent.includes("Course A"));
  x.click(); await sleep(900);
  check("TPO departments: removing a course updates the list immediately", !tctx.doc.getElementById("pageBody").textContent.includes("Course A") && tctx.doc.getElementById("pageBody").textContent.includes("Course B"));

  // Student: job search with the form (blank type) returns jobs; apply updates button state
  await visit(sctx, "#/student/jobs");
  const cards = () => sctx.doc.querySelectorAll(".job-card").length;
  const initial = cards();
  f = sctx.doc.getElementById("jobFilters"); f.dispatchEvent(new sctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(800);
  check("Student job search with blank filters returns the same jobs (was empty)", initial > 0 && cards() === initial, `initial=${initial} after=${cards()}`);
  const btn = [...sctx.doc.querySelectorAll(".apply-btn")].find((b) => b.textContent === "Apply");
  if (btn) { btn.click(); btn.click(); await sleep(900); }
  check("Student apply: button flips to 'Applied ✓' with no reload", !btn || btn.textContent === "Applied ✓", btn?.textContent);
  await visit(sctx, "#/student/applications");
  check("Student applications page lists applications", sctx.doc.querySelectorAll("table.ledger tbody tr").length >= 1);

  // Student: ask TPO a question -> appears in list right away
  await visit(sctx, "#/student/queries");
  const subj = "UI question " + Date.now();
  f = sctx.doc.getElementById("queryForm"); f.querySelector("[name=subject]").value = subj; f.querySelector("[name=message]").value = "hello";
  f.dispatchEvent(new sctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(900);
  check("Student query: appears immediately in 'Your Queries' with a real date", sctx.doc.getElementById("pageBody").textContent.includes(subj) && !/Invalid Date/.test(bodyText(sctx)));

  // TPO replies -> UI updates
  await visit(tctx, "#/tpo/queries");
  const rf = [...tctx.doc.querySelectorAll(".reply-form")].find((x) => x.closest(".card").textContent.includes(subj));
  rf.querySelector("textarea").value = "Answered in UI"; rf.dispatchEvent(new tctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(900);
  check("TPO reply: card flips to the reply view immediately", tctx.doc.getElementById("pageBody").textContent.includes("Answered in UI") && !tctx.doc.querySelector(`.reply-form`)?.closest(".card")?.textContent.includes(subj));

  // Recruiter: post a job through the form -> lands on postings list with exactly one row
  await visit(rctx, "#/recruiter/post-job");
  const jt = "UI Job " + Date.now();
  f = rctx.doc.getElementById("jobForm"); f.querySelector("[name=title]").value = jt; f.querySelector("[name=requiredSkills]").value = "python, sql"; f.querySelector("[name=deadline]").value = "2099-12-31";
  f.dispatchEvent(new rctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(1200);
  const jobRows = [...rctx.doc.querySelectorAll("table.ledger tbody tr")].filter((tr) => tr.textContent.includes(jt));
  check("Recruiter post-job: redirected to postings, job listed exactly once", rctx.window.location.hash === "#/recruiter/jobs" && jobRows.length === 1, `hash=${rctx.window.location.hash} rows=${jobRows.length}`);
  jobRows[0]?.querySelector(".close-btn")?.click(); await sleep(900);
  check("Recruiter close job: row flips to Closed immediately", [...rctx.doc.querySelectorAll("table.ledger tbody tr")].find((tr) => tr.textContent.includes(jt))?.textContent.includes("Closed"));

  // Admin: create a TPO -> listed immediately
  await visit(actx, "#/admin/users");
  const em = `ui${Date.now()}@x.com`;
  f = actx.doc.getElementById("createUserForm"); f.querySelector("[name=email]").value = em; f.querySelector("[name=password]").value = "secret1";
  f.dispatchEvent(new actx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(1500);
  check("Admin create user: account listed immediately", actx.doc.getElementById("pageBody").textContent.includes(em));

  // Real failure still shows a real error (not swallowed / not fake success)
  await visit(actx, "#/admin/users");
  f = actx.doc.getElementById("createUserForm"); f.querySelector("[name=email]").value = em; f.querySelector("[name=password]").value = "secret1";
  actx.doc.querySelectorAll(".toast").forEach((t) => t.remove());
  f.dispatchEvent(new actx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(900);
  check("Admin duplicate user: shows the server's real error, no success toast", toasts(actx).some((t) => /already registered/.test(t)) && ![...actx.doc.querySelectorAll(".toast.success")].some((t) => /created/i.test(t.textContent)), toasts(actx).join("|"));

  // Login screen: wrong password shows alert; right one redirects
  const lctx = await openApp(null);
  f = lctx.doc.getElementById("loginForm");
  f.querySelector("[name=role]").value = "tpo"; f.querySelector("[name=email]").value = "tpo@aipms.local"; f.querySelector("[name=password]").value = "wrong";
  f.dispatchEvent(new lctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(900);
  check("Login wrong password: inline error shown", /Invalid credentials/.test(lctx.doc.getElementById("authAlert").textContent));
  f.querySelector("[name=password]").value = "Tpo@1234";
  f.dispatchEvent(new lctx.window.Event("submit", { cancelable: true, bubbles: true })); await sleep(1500);
  check("Login success: redirected to TPO dashboard", lctx.window.location.hash === "#/tpo/dashboard", lctx.window.location.hash);

  const failed = results.filter((r) => !r).length;
  console.log(`\n${results.length - failed}/${results.length} UI checks passed`);
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error("UI test crashed:", e); process.exit(2); });