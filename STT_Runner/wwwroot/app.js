const elements = {
  theme: document.querySelector("#themeButton"),
  language: document.querySelector("#languageSelect"),
  translate: document.querySelector("#translateCheck"),
  record: document.querySelector("#recordButton"),
  recordLabel: document.querySelector("#recordLabel"),
  hint: document.querySelector("#recordHint"),
  status: document.querySelector("#statusText"),
  statusDot: document.querySelector("#statusDot"),
  model: document.querySelector("#modelText"),
  retry: document.querySelector("#retryButton"),
  sessions: document.querySelector("#sessionSelect"),
  copy: document.querySelector("#copyButton"),
  download: document.querySelector("#downloadButton"),
  clear: document.querySelector("#clearButton"),
  count: document.querySelector("#entryCount"),
  journal: document.querySelector("#journal"),
  entries: document.querySelector("#entries"),
  empty: document.querySelector("#emptyState")
};

const storageName = "mwandishi-journals";
let database;
let session;
let entries = [];
let socket;
let recorder;
let microphone;
let state = "idle";
let serverReady = false;
let completed = false;
let serverError = false;
let storageFailed = false;
let uploadQueue = Promise.resolve();
let persistQueue = Promise.resolve();
let finalizing = Promise.resolve();

function requestResult(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function transactionDone(transaction) {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = resolve;
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(storageName, 1);
    request.onupgradeneeded = () => {
      const db = request.result;
      db.createObjectStore("sessions", { keyPath: "id" });
      const records = db.createObjectStore("entries", { keyPath: ["sessionId", "index"] });
      records.createIndex("bySession", "sessionId");
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function saveSession(value) {
  const transaction = database.transaction("sessions", "readwrite");
  transaction.objectStore("sessions").put(value);
  await transactionDone(transaction);
}

async function saveEntry(value) {
  const transaction = database.transaction("entries", "readwrite");
  transaction.objectStore("entries").put(value);
  await transactionDone(transaction);
}

async function loadSessions(selectedId) {
  const transaction = database.transaction("sessions", "readonly");
  const all = await requestResult(transaction.objectStore("sessions").getAll());
  all.sort((a, b) => b.createdAt - a.createdAt);
  elements.sessions.replaceChildren();
  if (all.length === 0) elements.sessions.add(new Option("No recordings yet", ""));
  for (const item of all) elements.sessions.add(new Option(item.title, item.id));
  const chosen = all.find(item => item.id === selectedId) ?? all[0];
  elements.sessions.value = chosen?.id ?? "";
  await loadSession(chosen);
}

async function loadSession(value) {
  session = value;
  if (!value) {
    entries = [];
    renderJournal();
    return;
  }
  const transaction = database.transaction("entries", "readonly");
  entries = await requestResult(transaction.objectStore("entries")
    .index("bySession").getAll(IDBKeyRange.only(value.id)));
  renderJournal();
}

async function deleteSession() {
  if (!session || !window.confirm(`Delete “${session.title}” and its transcript?`)) return;
  const transaction = database.transaction(["sessions", "entries"], "readwrite");
  transaction.objectStore("sessions").delete(session.id);
  const index = transaction.objectStore("entries").index("bySession");
  const cursor = index.openKeyCursor(IDBKeyRange.only(session.id));
  cursor.onsuccess = () => {
    if (!cursor.result) return;
    transaction.objectStore("entries").delete(cursor.result.primaryKey);
    cursor.result.continue();
  };
  await transactionDone(transaction);
  await loadSessions();
}

function formatTime(seconds) {
  const value = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor(value / 60) % 60;
  const remaining = value % 60;
  return hours ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remaining).padStart(2, "0")}`
    : `${String(minutes).padStart(2, "0")}:${String(remaining).padStart(2, "0")}`;
}

function entryText() {
  return entries.map(item => `[${formatTime(item.start)}] ${item.text.trim()}`).join("\n");
}

function renderEntry(item) {
  const row = document.createElement("div");
  row.className = "entry";
  const stamp = document.createElement("time");
  stamp.textContent = formatTime(item.start);
  stamp.dateTime = `PT${Math.floor(item.start)}S`;
  const words = document.createElement("p");
  words.textContent = item.text.trim();
  row.append(stamp, words);
  elements.entries.append(row);
}

function updateActions() {
  elements.empty.hidden = entries.length > 0;
  elements.count.textContent = entries.length === 1 ? "1 entry" : `${entries.length} entries`;
  elements.copy.disabled = entries.length === 0;
  elements.download.disabled = entries.length === 0;
  elements.clear.disabled = !session || state !== "idle";
}

function renderJournal() {
  elements.entries.replaceChildren();
  for (const item of entries) renderEntry(item);
  updateActions();
}

function setStatus(message, kind = "ready", detail) {
  elements.status.textContent = message;
  elements.statusDot.className = `status-dot ${kind}`;
  if (detail !== undefined) elements.model.textContent = detail;
}

function updateControls() {
  const locked = state !== "idle";
  elements.language.disabled = locked;
  elements.translate.disabled = locked;
  elements.sessions.disabled = locked;
  elements.record.disabled = !serverReady || state === "connecting" || state === "finishing";
  elements.record.classList.toggle("is-recording", state === "recording");
  elements.recordLabel.textContent = state === "recording" ? "Stop recording"
    : state === "finishing" ? "Finishing..." : state === "connecting" ? "Connecting..." : "Start microphone";
  updateActions();
}

function setTheme(value) {
  document.documentElement.dataset.theme = value;
  elements.theme.textContent = value === "dark" ? "Light mode" : "Dark mode";
  elements.theme.setAttribute("aria-pressed", String(value === "dark"));
  localStorage.setItem("mwandishi-theme", value);
}

async function checkServer() {
  try {
    const [healthResponse, languageResponse] = await Promise.all([fetch("/health"), fetch("/v1/languages")]);
    if (!healthResponse.ok || !languageResponse.ok) throw new Error("The server is unavailable.");
    const health = await healthResponse.json();
    const languages = await languageResponse.json();
    const selected = elements.language.value || localStorage.getItem("mwandishi-language") || "auto";
    elements.language.replaceChildren();
    for (const item of languages.data) elements.language.add(new Option(item.name, item.code));
    elements.language.value = selected;
    if (!elements.language.value) elements.language.value = "auto";
    serverReady = true;
    elements.retry.hidden = true;
    if (state === "idle") setStatus("Ready to record", "ready",
      `Whisper ${health.model_family ?? "model"} · ${health.backend} · Ready`);
  } catch (error) {
    serverReady = false;
    elements.retry.hidden = false;
    setStatus("Server unavailable", "error", error.message);
  }
  updateControls();
}

function stopMicrophone() {
  for (const track of microphone?.getTracks() ?? []) track.stop();
  microphone = null;
}

function recordingFormat() {
  const formats = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4;codecs=mp4a.40.2", "audio/mp4"];
  return formats.find(format => MediaRecorder.isTypeSupported(format));
}

async function handleServerMessage(message) {
  if (message.type === "transcript.text.delta") {
    const item = { sessionId: session.id, index: message.index,
      start: message.start, end: message.end, text: message.delta };
    const nearBottom = elements.journal.scrollHeight - elements.journal.scrollTop
      - elements.journal.clientHeight < 96;
    entries.push(item);
    renderEntry(item);
    updateActions();
    if (nearBottom) elements.journal.scrollTop = elements.journal.scrollHeight;
    persistQueue = persistQueue.then(() => saveEntry(item)).catch(error => {
      storageFailed = true;
      setStatus("Journal storage failed", "error", `${error.message}. Download the transcript now.`);
    });
    return;
  }
  if (message.type === "transcript.text.done") {
    completed = true;
    await persistQueue;
    try {
      session = { ...session, duration: message.duration, language: message.language, finishedAt: Date.now() };
      await saveSession(session);
    } catch (error) {
      storageFailed = true;
    }
    if (storageFailed) {
      setStatus("Journal storage failed", "error", "Download the transcript before closing this page.");
    } else {
      setStatus("Recording saved", "ready", `${formatTime(message.duration)} processed · ${message.language}`);
      elements.hint.textContent = "The transcript is saved in this browser. Copy or download it whenever you like.";
    }
    return;
  }
  if (message.type === "error") {
    serverError = true;
    setStatus("Transcription stopped", "error", message.message);
    elements.hint.textContent = "The entries already received remain in your journal.";
  }
}

async function startRecording() {
  if (state !== "idle" || !serverReady) return;
  state = "connecting";
  completed = false;
  serverError = false;
  storageFailed = false;
  finalizing = Promise.resolve();
  updateControls();
  setStatus("Requesting microphone", "recording");
  try {
    if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder)
      throw new Error("This browser cannot record microphone audio on this page.");
    const format = recordingFormat();
    if (!format) throw new Error("No supported microphone recording format was found.");
    microphone = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
    recorder = new MediaRecorder(microphone, { mimeType: format });
    const scheme = location.protocol === "https:" ? "wss:" : "ws:";
    const url = new URL(`${scheme}//${location.host}/live`);
    url.searchParams.set("language", elements.language.value);
    url.searchParams.set("translate", String(elements.translate.checked));
    socket = new WebSocket(url);

    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("The server did not accept the session.")), 20000);
      socket.onmessage = event => {
        let message;
        try { message = JSON.parse(event.data); }
        catch { return; }
        if (message.type === "session.ready") {
          clearTimeout(timeout);
          resolve();
          return;
        }
        const pending = handleServerMessage(message);
        if (message.type === "transcript.text.done") finalizing = pending;
      };
      socket.onerror = () => { clearTimeout(timeout); reject(new Error("Could not open the live connection.")); };
      socket.onclose = async () => {
        clearTimeout(timeout);
        if (state === "connecting") reject(new Error("The live connection closed before recording."));
        if (state === "recording" || state === "finishing") {
          await finalizing;
          if (!completed && !serverError) setStatus("Connection lost", "error", "Your existing journal entries are still saved.");
          if (recorder?.state === "recording") recorder.stop();
          stopMicrophone();
          state = "idle";
          updateControls();
        }
      };
    });

    const createdAt = Date.now();
    session = { id: crypto.randomUUID(), createdAt,
      title: new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(createdAt) };
    await saveSession(session);
    await loadSessions(session.id);
    uploadQueue = Promise.resolve();
    recorder.ondataavailable = event => {
      if (!event.data.size) return;
      uploadQueue = uploadQueue.then(async () => {
        const bytes = await event.data.arrayBuffer();
        if (socket.readyState !== WebSocket.OPEN) return;
        socket.send(bytes);
        if (socket.bufferedAmount > 4 * 1024 * 1024) {
          setStatus("Server is falling behind", "error", "Stopping to avoid an unbounded audio queue.");
          stopRecording();
        }
      });
    };
    recorder.onstop = () => {
      uploadQueue.then(() => {
        if (socket.readyState === WebSocket.OPEN) socket.send("stop");
      }).catch(error => setStatus("Audio upload failed", "error", error.message));
    };
    recorder.onerror = () => stopRecording();
    recorder.start(500);
    state = "recording";
    updateControls();
    setStatus("Recording", "recording", `Microphone · ${format}`);
    elements.hint.textContent = elements.translate.checked
      ? "Listening and translating to English. Settings are locked until you stop."
      : "Listening. Settings are locked until you stop.";
  } catch (error) {
    if (recorder?.state === "recording") recorder.stop();
    stopMicrophone();
    socket?.close();
    state = "idle";
    updateControls();
    setStatus("Could not start", "error", error.message);
    elements.hint.textContent = "Check the microphone permission and try again.";
  }
}

function stopRecording() {
  if (state !== "recording") return;
  state = "finishing";
  updateControls();
  setStatus("Finishing the last phrases", "recording", "Keep this page open until the journal is saved.");
  if (recorder.state === "recording") recorder.stop();
  stopMicrophone();
}

async function copyJournal() {
  try {
    await navigator.clipboard.writeText(entryText());
    elements.copy.textContent = "Copied";
    setTimeout(() => { elements.copy.textContent = "Copy"; }, 1800);
  } catch {
    setStatus("Copy failed", "error", "Clipboard access was not granted. Download the transcript instead.");
  }
}

function downloadJournal() {
  if (!session) return;
  const blob = new Blob([entryText() + "\n"], { type: "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `mwandishi-${new Date(session.createdAt).toISOString().replaceAll(":", "-")}.txt`;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

elements.theme.addEventListener("click", () => setTheme(
  document.documentElement.dataset.theme === "dark" ? "light" : "dark"));
elements.language.addEventListener("change", () => localStorage.setItem("mwandishi-language", elements.language.value));
elements.translate.addEventListener("change", () => localStorage.setItem("mwandishi-translate", String(elements.translate.checked)));
elements.record.addEventListener("click", () => state === "recording" ? stopRecording() : void startRecording());
elements.sessions.addEventListener("change", () => void loadSessionById(elements.sessions.value));
elements.copy.addEventListener("click", () => void copyJournal());
elements.download.addEventListener("click", downloadJournal);
elements.clear.addEventListener("click", () => void deleteSession());
elements.retry.addEventListener("click", () => void checkServer());

async function loadSessionById(id) {
  const transaction = database.transaction("sessions", "readonly");
  await loadSession(await requestResult(transaction.objectStore("sessions").get(id)));
}

async function initialize() {
  setTheme(localStorage.getItem("mwandishi-theme") || "light");
  elements.translate.checked = localStorage.getItem("mwandishi-translate") === "true";
  try {
    database = await openDatabase();
    await loadSessions();
  } catch (error) {
    setStatus("Browser storage unavailable", "error", `${error.message}. The journal cannot be saved.`);
    elements.hint.textContent = "Enable browser storage before recording.";
    return;
  }
  await checkServer();
}

void initialize();
