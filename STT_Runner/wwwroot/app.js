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
  clearAll: document.querySelector("#clearAllButton"),
  count: document.querySelector("#entryCount"),
  journal: document.querySelector("#journal"),
  entries: document.querySelector("#entries"),
  empty: document.querySelector("#emptyState")
};

const storageName = "mwandishi-journals";
let database;
let session;
let entries = [];
let sessionCount = 0;
let socket;
let audioContext;
let captureNode;
let captureSource;
let acknowledgeStop;
let rejectStop;
let microphone;
let state = "idle";
let serverReady = false;
let completed = false;
let serverError = false;
let storageFailed = false;
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
  sessionCount = all.length;
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

async function deleteAllSessions() {
  if (state !== "idle" || sessionCount === 0) return;
  if (!window.confirm(`Permanently delete all ${sessionCount} recordings and their transcripts?`)) return;
  const transaction = database.transaction(["sessions", "entries"], "readwrite");
  transaction.objectStore("sessions").clear();
  transaction.objectStore("entries").clear();
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
  elements.clearAll.disabled = sessionCount === 0 || state !== "idle";
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
    // Restore the remembered language before the initial HTML selection (auto).
    const selected = localStorage.getItem("mwandishi-language") || elements.language.value || "auto";
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

function releaseCapture() {
  captureSource?.disconnect();
  captureNode?.disconnect();
  captureNode?.port.close();
  captureSource = null;
  captureNode = null;
  if (audioContext) void audioContext.close().catch(() => {});
  audioContext = null;
  stopMicrophone();
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
    if (!navigator.mediaDevices?.getUserMedia || !window.AudioContext || !window.AudioWorkletNode)
      throw new Error("This browser cannot record microphone audio on this page.");
    audioContext = new AudioContext();
    await audioContext.resume();
    // Browser voice cleanup can suppress soft syllables before the server sees them.
    microphone = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
      video: false
    });
    await audioContext.audioWorklet.addModule("/pcm-worklet.js");
    captureNode = new AudioWorkletNode(audioContext, "pcm-capture");
    captureSource = audioContext.createMediaStreamSource(microphone);
    const format = `PCM float32 · ${audioContext.sampleRate} Hz`;

    const scheme = location.protocol === "https:" ? "wss:" : "ws:";
    const url = new URL(`${scheme}//${location.host}/live`);
    const language = elements.language.value || "auto";
    const translate = elements.translate.checked;
    url.searchParams.set("language", language);
    url.searchParams.set("translate", String(translate));
    url.searchParams.set("audio_format", "pcm_f32le");
    url.searchParams.set("sample_rate", String(audioContext.sampleRate));
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
        rejectStop?.(new Error("The connection closed before audio upload finished."));
        if (state === "connecting") reject(new Error("The live connection closed before recording."));
        if (state === "recording" || state === "finishing") {
          await finalizing;
          if (!completed && !serverError) setStatus("Connection lost", "error", "Your existing journal entries are still saved.");
          releaseCapture();
          state = "idle";
          updateControls();
        }
      };
    });

    const createdAt = Date.now();
    session = { id: crypto.randomUUID(), createdAt,
      title: new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(createdAt),
      sourceLanguage: language, translate };
    await saveSession(session);
    await loadSessions(session.id);
    if (socket.readyState !== WebSocket.OPEN) throw new Error("The live connection closed before recording.");
    captureNode.port.onmessage = event => {
      if (event.data === "stopped") {
        acknowledgeStop?.();
        return;
      }
      if (socket.readyState !== WebSocket.OPEN) return;
      socket.send(event.data);
      if (socket.bufferedAmount > 4 * 1024 * 1024 && state === "recording") {
        elements.hint.textContent = "Upload is falling behind; finishing this recording.";
        void stopRecording();
      }
    };
    captureNode.onprocessorerror = () => {
      serverError = true;
      setStatus("Audio capture failed", "error", "Start a new recording to retry.");
      socket.close();
      releaseCapture();
    };
    captureSource.connect(captureNode);
    captureNode.connect(audioContext.destination);
    state = "recording";
    updateControls();
    setStatus(translate ? "Recording · translating to English" : "Recording · transcribing",
      "recording", `Source: ${elements.language.selectedOptions[0]?.textContent ?? language} · ${format}`);
    elements.hint.textContent = translate
      ? "Translation is on: the journal will contain English text. Settings are locked until you stop."
      : "Transcription is on: the journal will use the spoken language. Settings are locked until you stop.";
  } catch (error) {
    releaseCapture();
    socket?.close();
    state = "idle";
    updateControls();
    setStatus("Could not start", "error", error.message);
    elements.hint.textContent = "Check the microphone permission and try again.";
  }
}

async function stopRecording() {
  if (state !== "recording") return;
  state = "finishing";
  updateControls();
  setStatus("Finishing the last phrases", "recording", "Keep this page open until the journal is saved.");
  try {
    // The port delivers the final partial PCM block before the stop acknowledgement.
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Audio capture did not finish.")), 3000);
      acknowledgeStop = () => { clearTimeout(timeout); resolve(); };
      rejectStop = error => { clearTimeout(timeout); reject(error); };
      captureNode.port.postMessage("stop");
    });
    if (socket.readyState === WebSocket.OPEN) socket.send("stop");
  } catch (error) {
    serverError = true;
    setStatus("Recording interrupted", "error", error.message);
    socket?.close();
  } finally {
    acknowledgeStop = null;
    rejectStop = null;
    releaseCapture();
  }
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
elements.record.addEventListener("click", () => state === "recording" ? stopRecording() : void startRecording());
elements.sessions.addEventListener("change", () => void loadSessionById(elements.sessions.value));
elements.copy.addEventListener("click", () => void copyJournal());
elements.download.addEventListener("click", downloadJournal);
elements.clear.addEventListener("click", () => void deleteSession());
elements.clearAll.addEventListener("click", () => void deleteAllSessions().catch(error =>
  setStatus("Could not delete recordings", "error", error.message)));
elements.retry.addEventListener("click", () => void checkServer());

async function loadSessionById(id) {
  const transaction = database.transaction("sessions", "readonly");
  await loadSession(await requestResult(transaction.objectStore("sessions").get(id)));
}

async function initialize() {
  setTheme(localStorage.getItem("mwandishi-theme") || "light");
  // A new session starts in transcription mode; translation requires an explicit choice.
  elements.translate.checked = false;
  localStorage.removeItem("mwandishi-translate");
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
