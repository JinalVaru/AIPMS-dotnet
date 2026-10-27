// api.js — thin fetch wrapper around the AIPMS REST API.

const API_BASE = "/api";

const Auth = {
  getToken() { return localStorage.getItem("aipms_token"); },
  getUser() {
    const raw = localStorage.getItem("aipms_user");
    return raw ? JSON.parse(raw) : null;
  },
  setSession(token, user) {
    localStorage.setItem("aipms_token", token);
    localStorage.setItem("aipms_user", JSON.stringify(user));
  },
  clear() {
    localStorage.removeItem("aipms_token");
    localStorage.removeItem("aipms_user");
  },
  isLoggedIn() { return !!Auth.getToken(); },
};

// Identical mutating requests (same method, path and body) that are already in flight share one network call.
// This stops double-clicks / double-submits from creating duplicate records, without ever retrying a request.
const inFlight = new Map();

function apiRequest(method, path, body, isForm = false) {
  const mutating = method !== "GET";
  if (!mutating || isForm) return sendRequest(method, path, body, isForm);
  const key = `${method} ${path} ${body === undefined ? "" : JSON.stringify(body)}`;
  if (inFlight.has(key)) return inFlight.get(key);
  const pending = sendRequest(method, path, body, isForm).finally(() => inFlight.delete(key));
  inFlight.set(key, pending);
  return pending;
}

function apiError(message, status) {
  const err = new Error(message);
  err.status = status;
  return err;
}

async function sendRequest(method, path, body, isForm) {
  const headers = {};
  const token = Auth.getToken();
  if (token) headers["Authorization"] = `Bearer ${token}`;
  let payload;
  if (isForm) {
    payload = body;
  } else if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    payload = JSON.stringify(body);
  }

  let res;
  try {
    res = await fetch(`${API_BASE}${path}`, { method, headers, body: payload });
  } catch {
    throw apiError("Cannot reach the server. Check your connection and try again.", 0);
  }

  const text = await res.text();
  const isJson = (res.headers.get("content-type") || "").includes("json");
  let data = null;
  if (text && isJson) {
    try { data = JSON.parse(text); } catch { throw apiError("The server sent an unreadable response.", res.status); }
  }

  if (!res.ok) {
    if (res.status === 401 && token) {
      Auth.clear();
      window.location.hash = "#/login";
    }
    const message = (data && data.error) || (res.status >= 500 ? "Something went wrong on the server. Please try again." : `Request failed (${res.status})`);
    throw apiError(message, res.status);
  }
  // A 2xx that is not JSON (for example the SPA's index.html served for a wrong URL) is a bug, not data.
  if (text && !isJson) throw apiError(`Unexpected response from ${path}`, res.status);
  return data;
}

const api = {
  get: (path) => apiRequest("GET", path),
  post: (path, body) => apiRequest("POST", path, body),
  put: (path, body) => apiRequest("PUT", path, body),
  del: (path) => apiRequest("DELETE", path),
  postForm: (path, formData) => apiRequest("POST", path, formData, true),
};