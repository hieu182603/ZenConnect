const state = {
  screenshots: [],
  messages: [],
  selectedId: null,
  objectUrls: new Map(),
  refreshTimer: null,
  roomJoined: false,
  roomCode: null,
};

const elements = {
  entryScreen: document.querySelector("#entry-screen"),
  roomScreen: document.querySelector("#room-screen"),
  connectionForm: document.querySelector("#connection-form"),
  accessKey: document.querySelector("#access-key"),
  toggleKey: document.querySelector("#toggle-key"),
  connectButton: document.querySelector("#connect-button"),
  connectButtonLabel: document.querySelector("#connect-button span:first-child"),
  entryStatus: document.querySelector("#entry-status"),
  roomSession: document.querySelector("#room-session"),
  leaveButton: document.querySelector("#leave-button"),
  status: document.querySelector("#status"),
  statusDot: document.querySelector("#status-dot"),
  chatContext: document.querySelector("#chat-context"),
  messageCount: document.querySelector("#message-count"),
  chatEmpty: document.querySelector("#chat-empty"),
  messageList: document.querySelector("#message-list"),
  messageForm: document.querySelector("#message-form"),
  composerTitle: document.querySelector("#composer-title"),
  characterCount: document.querySelector("#character-count"),
  message: document.querySelector("#message"),
  messageStatus: document.querySelector("#message-status"),
  sendButton: document.querySelector("#send-button"),
  sequence: document.querySelector("#sequence"),
  createdAt: document.querySelector("#created-at"),
  handled: document.querySelector("#handled"),
  reviewEmpty: document.querySelector("#review-empty"),
  selectedImage: document.querySelector("#selected-image"),
  previousButton: document.querySelector("#previous-button"),
  nextButton: document.querySelector("#next-button"),
  imageCount: document.querySelector("#image-count"),
  thumbnailList: document.querySelector("#thumbnail-list"),
};

elements.connectionForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  await joinRoom();
});

elements.toggleKey.addEventListener("click", () => {
  const showing = elements.accessKey.type === "text";
  elements.accessKey.type = showing ? "password" : "text";
  elements.toggleKey.textContent = showing ? "Hiện" : "Ẩn";
  elements.toggleKey.setAttribute("aria-label", showing ? "Hiện access key" : "Ẩn access key");
});

elements.leaveButton.addEventListener("click", leaveRoom);

elements.messageForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  await sendMessage();
});

elements.message.addEventListener("input", updateCharacterCount);
elements.previousButton.addEventListener("click", () => selectOffset(-1));
elements.nextButton.addEventListener("click", () => selectOffset(1));

elements.handled.addEventListener("change", async () => {
  const current = getSelected();
  if (!state.roomJoined || !current) return;

  try {
    await api(`/api/screenshots/${current.id}/handled`, {
      method: "PATCH",
      body: JSON.stringify({ handled: elements.handled.checked }),
    });
    current.handled = elements.handled.checked;
    renderFilmstrip();
  } catch (error) {
    elements.handled.checked = current.handled;
    setRoomConnection(error.message, true);
  }
});

document.addEventListener("keydown", (event) => {
  if (!state.roomJoined) return;
  const tagName = document.activeElement?.tagName;
  if (tagName === "INPUT" || tagName === "TEXTAREA") return;
  if (event.key === "ArrowLeft") selectOffset(-1);
  if (event.key === "ArrowRight") selectOffset(1);
});

async function joinRoom() {
  const accessKey = elements.accessKey.value.trim().toUpperCase();
  if (!accessKey) {
    setEntryStatus("Nhập key hiển thị trên máy cần xem.", true);
    return;
  }
  elements.accessKey.value = accessKey;

  setConnecting(true);
  setEntryStatus("Đang xác thực phòng...", false);
  try {
    const room = await api("/api/room");
    state.roomCode = room.session;
    await fetchWorkspaceData();
    enterRoom(state.roomCode);
  } catch (error) {
    setEntryStatus(error.message, true);
  } finally {
    setConnecting(false);
  }
}

async function fetchWorkspaceData() {
  const session = state.roomCode;
  if (!session) throw new Error("Server chưa cấu hình phòng.");
  const [screenshots, messages] = await Promise.all([
    api(`/api/sessions/${encodeURIComponent(session)}/screenshots`),
    api(`/api/sessions/${encodeURIComponent(session)}/messages?after=0`),
  ]);

  state.screenshots = screenshots;
  state.messages = messages;
  await loadImages(screenshots);

  if (!screenshots.some((item) => item.id === state.selectedId)) {
    state.selectedId = screenshots.at(-1)?.id ?? null;
  }

  renderAll();
}

function enterRoom(session) {
  state.roomJoined = true;
  elements.roomSession.textContent = `PHÒNG ${session.toUpperCase()}`;
  elements.entryScreen.hidden = true;
  elements.roomScreen.hidden = false;
  document.body.classList.add("room-active");
  setRoomConnection(`Đã kết nối · ${state.screenshots.length} ảnh`, false);
  startAutoRefresh();
}

function leaveRoom() {
  state.roomJoined = false;
  window.clearInterval(state.refreshTimer);
  state.refreshTimer = null;
  resetWorkspace();

  elements.roomScreen.hidden = true;
  elements.entryScreen.hidden = false;
  document.body.classList.remove("room-active");
  elements.connectionForm.reset();
  elements.accessKey.type = "password";
  elements.toggleKey.textContent = "Hiện";
  elements.message.value = "";
  updateCharacterCount();
  setEntryStatus("Key được tạo tự động khi ZenConnect chạy lần đầu.", false);
  elements.accessKey.focus();
}

async function refreshRoom() {
  if (!state.roomJoined) return;
  try {
    await fetchWorkspaceData();
    setRoomConnection(`Đã kết nối · ${state.screenshots.length} ảnh`, false);
  } catch (error) {
    setRoomConnection(error.message, true);
  }
}

function resetWorkspace() {
  for (const url of state.objectUrls.values()) URL.revokeObjectURL(url);
  state.objectUrls.clear();
  state.screenshots = [];
  state.messages = [];
  state.selectedId = null;
  state.roomCode = null;
  renderAll();
}

async function loadImages(items) {
  const activeIds = new Set(items.map((item) => item.id));
  for (const [id, url] of state.objectUrls) {
    if (!activeIds.has(id)) {
      URL.revokeObjectURL(url);
      state.objectUrls.delete(id);
    }
  }

  await Promise.all(items.map(async (item) => {
    if (state.objectUrls.has(item.id)) return;
    const response = await fetch(item.imageUrl, { headers: authHeaders() });
    if (!response.ok) throw new Error(await responseError(response));
    state.objectUrls.set(item.id, URL.createObjectURL(await response.blob()));
  }));
}

function renderAll() {
  renderViewer();
  renderFilmstrip();
  renderChat();
}

function renderViewer() {
  const current = getSelected();
  const index = getSelectedIndex();
  const total = state.screenshots.length;
  const hasCurrent = Boolean(current);

  elements.reviewEmpty.hidden = hasCurrent;
  elements.selectedImage.hidden = !hasCurrent;
  elements.handled.disabled = !hasCurrent;
  elements.message.disabled = !hasCurrent;
  elements.sendButton.disabled = !hasCurrent;
  elements.previousButton.disabled = index <= 0;
  elements.nextButton.disabled = index < 0 || index >= total - 1;
  elements.previousButton.hidden = !hasCurrent;
  elements.nextButton.hidden = !hasCurrent;
  elements.imageCount.textContent = hasCurrent ? `${index + 1} / ${total}` : `0 / ${total}`;

  if (!current) {
    elements.sequence.textContent = "Chưa có ảnh";
    elements.createdAt.textContent = "";
    elements.chatContext.textContent = "Chưa chọn ảnh";
    elements.composerTitle.textContent = "Phản hồi ảnh";
    elements.message.placeholder = "Chưa có ảnh để phản hồi...";
    elements.selectedImage.removeAttribute("src");
    elements.handled.checked = false;
    return;
  }

  elements.sequence.textContent = `Ảnh ${padNumber(current.sequence)} / ${padNumber(total)}`;
  elements.createdAt.textContent = formatDateTime(current.createdAt);
  elements.chatContext.textContent = `Đang xem ảnh ${current.sequence}`;
  elements.composerTitle.textContent = `Phản hồi ảnh ${padNumber(current.sequence)}`;
  elements.message.placeholder = "Nhập phản hồi của bạn...";
  elements.selectedImage.src = state.objectUrls.get(current.id) ?? "";
  elements.handled.checked = current.handled;
}

function renderFilmstrip() {
  elements.thumbnailList.replaceChildren();

  for (const item of state.screenshots) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `thumbnail-button${item.id === state.selectedId ? " active" : ""}`;
    button.title = `Mở ảnh ${item.sequence}`;
    button.setAttribute("aria-label", `Mở ảnh ${item.sequence}`);
    button.addEventListener("click", () => selectScreenshot(item.id));

    const image = document.createElement("img");
    image.src = state.objectUrls.get(item.id) ?? "";
    image.alt = "";

    const label = document.createElement("span");
    label.className = "thumbnail-label";
    const sequence = document.createElement("strong");
    sequence.textContent = padNumber(item.sequence);
    const time = document.createElement("span");
    time.textContent = formatTime(item.createdAt);
    label.append(sequence, time);
    button.append(image, label);
    elements.thumbnailList.append(button);
  }

  requestAnimationFrame(() => {
    elements.thumbnailList.querySelector(".thumbnail-button.active")
      ?.scrollIntoView({ behavior: "smooth", block: "nearest", inline: "nearest" });
  });
}

function renderChat() {
  const wasNearBottom = elements.messageList.scrollHeight
    - elements.messageList.scrollTop
    - elements.messageList.clientHeight < 70;

  elements.messageList.replaceChildren();
  elements.chatEmpty.hidden = state.messages.length > 0;
  elements.messageCount.textContent = String(state.messages.length);

  for (const item of [...state.messages].sort((left, right) => left.id - right.id)) {
    const wrapper = document.createElement("article");
    wrapper.className = `message-item${item.screenshotId === state.selectedId ? " active" : ""}`;
    wrapper.tabIndex = 0;
    wrapper.addEventListener("click", () => selectScreenshot(item.screenshotId));
    wrapper.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") selectScreenshot(item.screenshotId);
    });

    const badge = document.createElement("span");
    badge.className = "message-badge";
    badge.textContent = padNumber(item.sequence);

    const copy = document.createElement("div");
    copy.className = "message-copy";
    const meta = document.createElement("div");
    meta.className = "message-meta";
    const author = document.createElement("span");
    author.className = "message-author";
    author.textContent = `Phản hồi · Ảnh ${item.sequence}`;
    const time = document.createElement("time");
    time.textContent = formatTime(item.createdAt);
    meta.append(author, time);

    const bubble = document.createElement("div");
    bubble.className = "message-bubble";
    bubble.textContent = item.text;
    copy.append(meta, bubble);
    wrapper.append(badge, copy);
    elements.messageList.append(wrapper);
  }

  if (wasNearBottom || state.messages.length <= 1) {
    elements.messageList.scrollTop = elements.messageList.scrollHeight;
  }
}

async function sendMessage() {
  const current = getSelected();
  const text = elements.message.value.trim();
  if (!state.roomJoined || !current || !text) return;

  setMessageLoading(true);
  try {
    const created = await api(`/api/screenshots/${current.id}/message`, {
      method: "POST",
      body: JSON.stringify({ text }),
    });
    state.messages.push(created);
    current.handled = true;
    elements.message.value = "";
    elements.handled.checked = true;
    elements.messageStatus.textContent = "Đã gửi";
    updateCharacterCount();
    renderChat();
    renderFilmstrip();
  } catch (error) {
    elements.messageStatus.textContent = error.message;
  } finally {
    setMessageLoading(false);
  }
}

function selectScreenshot(id) {
  if (!state.screenshots.some((item) => item.id === id)) return;
  state.selectedId = id;
  elements.messageStatus.textContent = "";
  renderAll();
}

function selectOffset(offset) {
  const index = getSelectedIndex();
  const target = state.screenshots[index + offset];
  if (target) selectScreenshot(target.id);
}

function getSelected() {
  return state.screenshots.find((item) => item.id === state.selectedId) ?? null;
}

function getSelectedIndex() {
  return state.screenshots.findIndex((item) => item.id === state.selectedId);
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      ...authHeaders(),
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(options.headers ?? {}),
    },
  });
  if (!response.ok) throw new Error(await responseError(response));
  if (response.status === 204) return null;
  return response.json();
}

function authHeaders() {
  return { "X-Screen-Review-Key": elements.accessKey.value.trim() };
}

async function responseError(response) {
  if (response.status === 401) return "Key không hợp lệ hoặc máy chưa kết nối server.";
  try {
    const payload = await response.json();
    return payload.error ?? `HTTP ${response.status}`;
  } catch {
    return `HTTP ${response.status}`;
  }
}

function startAutoRefresh() {
  window.clearInterval(state.refreshTimer);
  state.refreshTimer = window.setInterval(refreshRoom, 5000);
}

function setEntryStatus(text, error) {
  elements.entryStatus.textContent = text;
  elements.entryStatus.classList.toggle("error", error);
}

function setRoomConnection(text, error) {
  elements.status.textContent = text;
  elements.statusDot.className = `status-dot${error ? " error" : " connected"}`;
}

function setConnecting(connecting) {
  elements.connectButton.disabled = connecting;
  elements.accessKey.disabled = connecting;
  elements.toggleKey.disabled = connecting;
  elements.connectButtonLabel.textContent = connecting ? "Đang xác thực" : "Vào phòng";
}

function setMessageLoading(loading) {
  elements.sendButton.disabled = loading || !getSelected();
}

function updateCharacterCount() {
  elements.characterCount.textContent = `${elements.message.value.length} / 2000`;
}

function padNumber(value) {
  return String(value).padStart(2, "0");
}

function formatTime(value) {
  return new Intl.DateTimeFormat("vi-VN", {
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function formatDateTime(value) {
  return new Intl.DateTimeFormat("vi-VN", {
    dateStyle: "short",
    timeStyle: "medium",
  }).format(new Date(value));
}

window.addEventListener("beforeunload", () => {
  window.clearInterval(state.refreshTimer);
  for (const url of state.objectUrls.values()) URL.revokeObjectURL(url);
});

updateCharacterCount();
elements.accessKey.focus();
