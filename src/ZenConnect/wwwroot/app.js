const state = {
  screenshots: [],
  messages: [],
  selectedId: null,
  objectUrls: new Map(),
  refreshTimer: null,
  roomJoined: false,
  roomCode: null,
  refreshing: false,
  deletingScreenshot: false,
  deletingAll: false,
  exporting: false,
  sendingMessage: false,
  updatingHandled: false,
  deletingMessageIds: new Set(),
  generation: 0,
};

const STORAGE_KEY = "zenconnect_room_key";
const SESSION_SELECTED_KEY = "zenconnect_selected_id";

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
  refreshButton: document.querySelector("#refresh-button"),
  status: document.querySelector("#status"),
  statusDot: document.querySelector("#status-dot"),
  chatContext: document.querySelector("#chat-context"),
  messageCount: document.querySelector("#message-count"),
  chatEmpty: document.querySelector("#chat-empty"),
  messageList: document.querySelector("#message-list"),
  messageForm: document.querySelector("#message-form"),
  messageMode: document.querySelector("#message-mode"),
  composerTitle: document.querySelector("#composer-title"),
  characterCount: document.querySelector("#character-count"),
  message: document.querySelector("#message"),
  messageStatus: document.querySelector("#message-status"),
  sendButton: document.querySelector("#send-button"),
  sequence: document.querySelector("#sequence"),
  createdAt: document.querySelector("#created-at"),
  handled: document.querySelector("#handled"),
  deleteButton: document.querySelector("#delete-button"),
  deleteLabel: document.querySelector("#delete-label"),
  deleteAllButton: document.querySelector("#delete-all-button"),
  exportButton: document.querySelector("#export-button"),
  imageStage: document.querySelector("#image-stage"),
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
  setKeyVisibility(elements.accessKey.type !== "text");
});

function setKeyVisibility(showing) {
  elements.accessKey.type = showing ? "text" : "password";
  elements.toggleKey.textContent = showing ? "Ẩn" : "Hiện";
  elements.toggleKey.setAttribute("aria-pressed", String(showing));
  const label = showing ? "Ẩn key phòng" : "Hiện key phòng";
  elements.toggleKey.setAttribute("aria-label", label);
  elements.toggleKey.title = label;
}

elements.leaveButton.addEventListener("click", leaveRoom);
elements.refreshButton.addEventListener("click", refreshRoom);

elements.messageForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  await sendMessage();
});

elements.message.addEventListener("input", updateCharacterCount);
elements.messageMode.addEventListener("change", () => {
  elements.messageStatus.textContent = "";
  renderComposer();
});

elements.message.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    if (event.ctrlKey || event.metaKey) {
      event.preventDefault();
      elements.messageForm.requestSubmit();
    } else if (!event.shiftKey && elements.messageMode.value === "text") {
      event.preventDefault();
      elements.messageForm.requestSubmit();
    }
  }
});
elements.accessKey.addEventListener("input", formatAccessKey);
elements.previousButton.addEventListener("click", () => selectOffset(-1));
elements.nextButton.addEventListener("click", () => selectOffset(1));
elements.deleteButton.addEventListener("click", deleteSelectedScreenshot);
elements.deleteAllButton.addEventListener("click", deleteAllScreenshots);
elements.exportButton.addEventListener("click", exportScreenshots);

elements.handled.addEventListener("change", async () => {
  const current = getSelected();
  if (!state.roomJoined || !current || isBusy()) {
    renderViewer();
    return;
  }

  const handled = elements.handled.checked;
  state.updatingHandled = true;
  renderAll();
  try {
    await api(`/api/screenshots/${current.id}/handled`, {
      method: "PATCH",
      body: JSON.stringify({ handled }),
    });
    current.handled = handled;
    renderFilmstrip();
  } catch (error) {
    elements.handled.checked = current.handled;
    setRoomConnection(error.message, true);
  } finally {
    state.updatingHandled = false;
    renderAll();
  }
});

document.addEventListener("keydown", (event) => {
  if (!state.roomJoined) return;
  const tagName = document.activeElement?.tagName;
  if (["INPUT", "TEXTAREA", "SELECT", "BUTTON"].includes(tagName)) return;
  if (event.key === "ArrowLeft") selectOffset(-1);
  if (event.key === "ArrowRight") selectOffset(1);
});

async function joinRoom() {
  const accessKey = elements.accessKey.value.trim().toUpperCase();
  if (!accessKey) {
    setEntryStatus("Nhập key hiển thị trên máy cần xem.", true);
    return;
  }
  if (!/^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){4}$/.test(accessKey)) {
    setEntryStatus("Room key chưa đúng định dạng XXXX-XXXX-XXXX-XXXX-XXXX.", true);
    return;
  }
  elements.accessKey.value = accessKey;

  setConnecting(true);
  setEntryStatus("Đang xác thực phòng...", false);
  const generation = ++state.generation;
  try {
    const room = await api("/api/room");
    if (generation !== state.generation) return;
    state.roomCode = room.session;
    await fetchWorkspaceData(generation);
    if (generation !== state.generation) return;
    enterRoom(state.roomCode);
  } catch (error) {
    try {
      localStorage.removeItem(STORAGE_KEY);
    } catch {}
    state.roomCode = null;
    setRoomLoading(false);
    setEntryStatus(error.message, true);
  } finally {
    setConnecting(false);
  }
}

async function fetchWorkspaceData(generation = state.generation) {
  const session = state.roomCode;
  if (!session) throw new Error("Server chưa cấu hình phòng.");
  setRoomLoading(true);
  const [screenshots, messages] = await Promise.all([
    api("/api/screenshots"),
    api("/api/messages?after=0"),
  ]);

  if (generation !== state.generation || session !== state.roomCode) return;
  state.screenshots = screenshots;
  state.messages = messages;
  await loadImages(screenshots, generation);
  if (generation !== state.generation || session !== state.roomCode) return;

  if (!screenshots.some((item) => item.id === state.selectedId)) {
    let restoredId = null;
    try {
      restoredId = sessionStorage.getItem(SESSION_SELECTED_KEY);
    } catch {}
    if (restoredId && screenshots.some((item) => item.id === restoredId)) {
      state.selectedId = restoredId;
    } else {
      state.selectedId = screenshots.at(-1)?.id ?? null;
    }
  }

  renderAll();
  setRoomLoading(false);
}

function enterRoom(session) {
  state.roomJoined = true;
  try {
    localStorage.setItem(STORAGE_KEY, elements.accessKey.value.trim().toUpperCase());
  } catch {}
  const lastGroup = session.toUpperCase().split("-").at(-1);
  elements.roomSession.textContent = `PHÒNG ••••-${lastGroup}`;
  elements.entryScreen.hidden = true;
  elements.roomScreen.hidden = false;
  document.body.classList.add("room-active");
  renderAll();
  setRoomConnection(`Đã kết nối · ${state.screenshots.length} ảnh`, false);
  startAutoRefresh();
}

function leaveRoom() {
  if (hasPendingOperation()) return;
  try {
    localStorage.removeItem(STORAGE_KEY);
    sessionStorage.removeItem(SESSION_SELECTED_KEY);
  } catch {}
  state.roomJoined = false;
  state.generation += 1;
  window.clearInterval(state.refreshTimer);
  state.refreshTimer = null;
  setRoomLoading(false);
  resetWorkspace();

  elements.roomScreen.hidden = true;
  elements.entryScreen.hidden = false;
  document.body.classList.remove("room-active");
  elements.connectionForm.reset();
  setKeyVisibility(false);
  elements.messageMode.value = "text";
  elements.message.value = "";
  updateCharacterCount();
  setEntryStatus("Key được tạo tự động khi ZenConnect chạy lần đầu.", false);
  elements.accessKey.focus({ preventScroll: true });
}

async function refreshRoom() {
  if (!state.roomJoined || isBusy()) return;
  const generation = state.generation;
  state.refreshing = true;
  try {
    await fetchWorkspaceData(state.generation);
    if (generation === state.generation) setRoomConnection(`Đã kết nối · ${state.screenshots.length} ảnh`, false);
  } catch (error) {
    if (generation === state.generation) {
      setRoomConnection(error.message, true);
      setRoomLoading(false);
    }
  } finally {
    state.refreshing = false;
    elements.refreshButton.disabled = false;
    renderAll();
  }
}

function resetWorkspace() {
  for (const url of state.objectUrls.values()) URL.revokeObjectURL(url);
  state.objectUrls.clear();
  state.screenshots = [];
  state.messages = [];
  state.selectedId = null;
  state.roomCode = null;
  state.deletingScreenshot = false;
  state.deletingMessageIds.clear();
  renderAll();
}

async function loadImages(items, generation) {
  const activeIds = new Set(items.map((item) => item.id));
  for (const [id, url] of state.objectUrls) {
    if (!activeIds.has(id)) {
      URL.revokeObjectURL(url);
      state.objectUrls.delete(id);
    }
  }

  await Promise.all(items.map(async (item) => {
    if (state.objectUrls.has(item.id)) return;
    try {
      const response = await fetch(item.imageUrl, { headers: authHeaders() });
      if (!response.ok) {
        console.warn(`Failed to load image ${item.imageUrl}: HTTP ${response.status}`);
        return;
      }
      const objectUrl = URL.createObjectURL(await response.blob());
      if (generation !== state.generation) {
        URL.revokeObjectURL(objectUrl);
        return;
      }
      state.objectUrls.set(item.id, objectUrl);
    } catch (err) {
      console.warn(`Network error loading ${item.imageUrl}:`, err);
    }
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
  const deleting = isBusy();

  elements.reviewEmpty.hidden = hasCurrent;
  elements.selectedImage.hidden = !hasCurrent;
  elements.handled.disabled = !hasCurrent || state.refreshing || deleting;
  elements.message.disabled = !hasCurrent || state.refreshing || deleting;
  elements.sendButton.disabled = !hasCurrent || state.refreshing || deleting;
  elements.deleteButton.disabled = !hasCurrent || state.refreshing || deleting;
  elements.deleteButton.classList.toggle("is-loading", state.deletingScreenshot);
  elements.deleteButton.setAttribute("aria-busy", String(state.deletingScreenshot));
  elements.deleteLabel.textContent = state.deletingScreenshot ? "Đang xóa" : "Xóa ảnh";
  elements.deleteAllButton.disabled = !state.roomJoined || total === 0 || deleting;
  elements.exportButton.disabled = !state.roomJoined || total === 0 || deleting;
  elements.deleteAllButton.textContent = state.deletingAll ? "Đang xóa…" : "Xóa toàn bộ ảnh";
  elements.exportButton.textContent = state.exporting ? "Đang xuất…" : "Xuất đề · ZIP";
  elements.deleteAllButton.setAttribute("aria-busy", String(state.deletingAll));
  elements.exportButton.setAttribute("aria-busy", String(state.exporting));
  elements.leaveButton.disabled = hasPendingOperation();
  elements.refreshButton.disabled = deleting;
  renderComposer();
  elements.previousButton.disabled = deleting || index <= 0;
  elements.nextButton.disabled = deleting || index < 0 || index >= total - 1;
  elements.previousButton.hidden = !hasCurrent;
  elements.nextButton.hidden = !hasCurrent;
  elements.imageCount.textContent = hasCurrent ? `${index + 1} / ${total}` : `0 / ${total}`;

  if (!current) {
    elements.sequence.textContent = "Chưa có ảnh";
    elements.createdAt.textContent = "";
    elements.chatContext.textContent = "Chưa chọn ảnh";
    elements.selectedImage.removeAttribute("src");
    elements.handled.checked = false;
    return;
  }

  elements.sequence.textContent = `Ảnh ${padNumber(current.sequence)} / ${padNumber(total)}`;
  elements.createdAt.textContent = formatDateTime(current.createdAt);
  elements.chatContext.textContent = `Đang xem ảnh ${current.sequence}`;
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
    button.disabled = hasPendingOperation();
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
    const roomLevel = !item.screenshotId;
    const wrapper = document.createElement("article");
    wrapper.className = `message-item${roomLevel ? " answer-message" : item.screenshotId === state.selectedId ? " active" : ""}`;
    if (!roomLevel) {
      wrapper.tabIndex = 0;
      wrapper.addEventListener("click", () => selectScreenshot(item.screenshotId));
      wrapper.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          selectScreenshot(item.screenshotId);
        }
      });
    }

    const badge = document.createElement("span");
    badge.className = "message-badge";
    badge.textContent = roomLevel ? "ĐA" : padNumber(item.sequence);

    const copy = document.createElement("div");
    copy.className = "message-copy";
    const meta = document.createElement("div");
    meta.className = "message-meta";
    const author = document.createElement("span");
    author.className = "message-author";
    author.textContent = roomLevel ? "Đáp án chung" : `Phản hồi · Ảnh ${item.sequence}`;
    const time = document.createElement("time");
    time.textContent = formatTime(item.createdAt);
    const metaActions = document.createElement("div");
    metaActions.className = "message-meta-actions";
    const deleteButton = document.createElement("button");
    deleteButton.type = "button";
    deleteButton.className = "message-delete-button";
    deleteButton.textContent = state.deletingMessageIds.has(item.id) ? "Đang xóa" : "Xóa";
    deleteButton.title = roomLevel ? "Xóa danh sách đáp án" : `Xóa phản hồi ảnh ${item.sequence}`;
    deleteButton.setAttribute("aria-label", deleteButton.title);
    deleteButton.disabled = isBusy();
    deleteButton.addEventListener("click", (event) => {
      event.stopPropagation();
      deleteMessage(item);
    });
    deleteButton.addEventListener("keydown", (event) => event.stopPropagation());
    metaActions.append(time, deleteButton);
    meta.append(author, metaActions);

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
  if (!state.roomJoined || isBusy()) return;
  const composed = composedMessage();
  const text = composed.text;
  if (!text) return;
  if (elements.messageMode.value === "choice" && !composed.roomLevel) {
    elements.messageStatus.textContent = "Nhập đáp án theo mẫu: 1 A | 2 B | 3 C";
    return;
  }
  if (!composed.roomLevel && !current) {
    elements.messageStatus.textContent = "Chưa có ảnh để gửi phản hồi.";
    return;
  }
  if (text.length > 2000) {
    elements.messageStatus.textContent = "Nội dung gồm đáp án và ghi chú không được quá 2000 ký tự.";
    return;
  }

  state.sendingMessage = true;
  setMessageLoading(true);
  renderAll();
  try {
    const endpoint = composed.roomLevel ? "/api/messages" : `/api/screenshots/${current.id}/message`;
    const created = await api(endpoint, {
      method: "POST",
      body: JSON.stringify({ text }),
    });
    state.messages.push(created);
    if (!composed.roomLevel) current.handled = true;
    elements.message.value = "";
    if (!composed.roomLevel) elements.handled.checked = true;
    elements.messageStatus.textContent = "Đã gửi";
    updateCharacterCount();
    renderChat();
    renderFilmstrip();
  } catch (error) {
    elements.messageStatus.textContent = error.message;
  } finally {
    state.sendingMessage = false;
    setMessageLoading(false);
    renderAll();
  }
}

async function deleteSelectedScreenshot() {
  const current = getSelected();
  if (!state.roomJoined || !current || isBusy()) return;

  const relatedMessageCount = state.messages.filter((item) => item.screenshotId === current.id).length;
  const relatedText = relatedMessageCount > 0
    ? ` và ${relatedMessageCount} phản hồi liên quan`
    : "";
  if (!window.confirm(`Xóa vĩnh viễn ảnh ${current.sequence}${relatedText}? Hành động này không thể hoàn tác.`)) return;

  const deletedIndex = getSelectedIndex();
  state.deletingScreenshot = true;
  renderAll();
  try {
    await api(`/api/screenshots/${current.id}`, { method: "DELETE" });

    const objectUrl = state.objectUrls.get(current.id);
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    state.objectUrls.delete(current.id);
    state.screenshots = state.screenshots.filter((item) => item.id !== current.id);
    state.messages = state.messages.filter((item) => item.screenshotId !== current.id);
    state.selectedId = state.screenshots[Math.min(deletedIndex, state.screenshots.length - 1)]?.id ?? null;
    elements.message.value = "";
    elements.messageStatus.textContent = "";
    updateCharacterCount();
    setRoomConnection(`Đã xóa ảnh ${current.sequence} · ${state.screenshots.length} ảnh còn lại`, false);
  } catch (error) {
    setRoomConnection(error.message, true);
  } finally {
    state.deletingScreenshot = false;
    renderAll();
  }
}

async function deleteMessage(item) {
  if (!state.roomJoined || isBusy()) return;
  const label = item.screenshotId ? `phản hồi của ảnh ${item.sequence}` : "danh sách đáp án này";
  if (!window.confirm(`Xóa ${label}? Hành động này không thể hoàn tác.`)) return;

  state.deletingMessageIds.add(item.id);
  renderAll();
  try {
    await api(`/api/messages/${item.id}`, { method: "DELETE" });
    state.messages = state.messages.filter((message) => message.id !== item.id);
    elements.messageStatus.textContent = `Đã xóa phản hồi ảnh ${item.sequence}`;
  } catch (error) {
    setRoomConnection(error.message, true);
  } finally {
    state.deletingMessageIds.delete(item.id);
    renderAll();
  }
}

function selectScreenshot(id) {
  if (hasPendingOperation()) return;
  if (!state.screenshots.some((item) => item.id === id)) return;
  state.selectedId = id;
  try {
    sessionStorage.setItem(SESSION_SELECTED_KEY, id);
  } catch {}
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

function hasPendingDelete() {
  return state.deletingScreenshot || state.deletingAll || state.deletingMessageIds.size > 0;
}

function hasPendingOperation() {
  return hasPendingDelete() || state.sendingMessage || state.exporting || state.updatingHandled;
}

function isBusy() { return state.refreshing || hasPendingOperation(); }

async function deleteAllScreenshots() {
  if (!state.roomJoined || !state.screenshots.length || isBusy()) return;
  if (!window.confirm(`Xóa vĩnh viễn TOÀN BỘ ảnh và phản hồi gắn với ảnh trong phòng này? Danh sách đáp án chung vẫn được giữ. Hiện có ${state.screenshots.length} ảnh. Hành động không thể hoàn tác.`)) return;
  state.deletingAll = true;
  renderAll();
  try {
    const result = await api("/api/screenshots", { method: "DELETE" });
    for (const url of state.objectUrls.values()) URL.revokeObjectURL(url);
    state.objectUrls.clear();
    state.screenshots = [];
    state.messages = state.messages.filter((item) => !item.screenshotId);
    state.selectedId = null;
    elements.message.value = "";
    elements.messageStatus.textContent = "";
    updateCharacterCount();
    const warning = result.failedImageCount > 0
      ? ` Có ${result.failedImageCount} file trên server chưa xóa được.` : "";
    setRoomConnection(`Đã xóa ${result.deletedScreenshotCount} ảnh và ${result.deletedMessageCount} phản hồi.${warning}`, result.failedImageCount > 0);
  } catch (error) {
    setRoomConnection(error.message, true);
  } finally {
    state.deletingAll = false;
    renderAll();
  }
}

async function exportScreenshots() {
  if (!state.roomJoined || !state.screenshots.length || isBusy()) return;
  state.exporting = true;
  renderAll();
  setRoomConnection("Đang gom ảnh và chuẩn bị file ZIP…", false);
  try {
    const response = await fetch("/api/screenshots/export", { headers: authHeaders() });
    if (!response.ok) throw new Error(await responseError(response));
    const count = Number(response.headers.get("X-Screenshot-Count"));
    const blob = await response.blob();
    if (!Number.isInteger(count) || count < 1 || blob.size === 0 || !blob.type.includes("application/zip")) {
      throw new Error("Không nhận được file ZIP chứa ảnh. Hãy thử lại.");
    }
    const fileName = response.headers.get("Content-Disposition")?.match(/filename="?(de-zenconnect-[\d-]+\.zip)"?/i)?.[1]
      ?? "de-zenconnect.zip";
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 60000);
    setRoomConnection(`Đã tạo ZIP gồm ${count} ảnh và bắt đầu tải về.`, false);
  } catch (error) {
    setRoomConnection(error.message, true);
  } finally {
    state.exporting = false;
    renderAll();
  }
}

function composedMessage() {
  const value = elements.message.value.trim();
  const parsed = parseAnswerSet(value);
  const forced = elements.messageMode.value === "choice";
  if (parsed && (forced || parsed.answers.length >= 2)) {
    return {
      text: `Đáp án trắc nghiệm\n${parsed.answers.map((item) => `${item.number}. ${item.answer}`).join(" | ")}`,
      roomLevel: true,
    };
  }
  return { text: value, roomLevel: false };
}

function parseAnswerSet(value) {
  const source = value.replace(/^\s*(?:đáp\s*án(?:\s*trắc\s*nghiệm)?\s*[:\-]?\s*)/iu, "");
  const pattern = /(\d{1,3})\s*[.):-]?\s*([A-D](?:\s*[,/+]+\s*[A-D])*)/giu;
  const answers = [];
  const seen = new Set();
  let remainder = source;
  for (const match of source.matchAll(pattern)) {
    const number = Number(match[1]);
    if (seen.has(number)) continue;
    seen.add(number);
    const choices = [...new Set(match[2].toUpperCase().match(/[A-D]/g) ?? [])];
    answers.push({ number, answer: choices.join(", ") });
    remainder = remainder.replace(match[0], " ");
  }
  remainder = remainder.replace(/[\s|;,.…\-–—]+/gu, "");
  return answers.length && !remainder ? { answers } : null;
}

function renderComposer() {
  const current = getSelected();
  const choice = elements.messageMode.value === "choice";
  const disabled = !state.roomJoined || isBusy();
  elements.messageMode.disabled = disabled;
  elements.message.disabled = disabled;
  elements.message.required = false;
  elements.sendButton.disabled = disabled;

  elements.messageForm.setAttribute("aria-busy", String(state.sendingMessage));
  if (elements.composerTitle) {
    elements.composerTitle.textContent = choice ? "Danh sách đáp án"
      : current ? `Phản hồi ảnh ${padNumber(current.sequence)}` : "Phản hồi ảnh";
  }
  elements.message.placeholder = choice
    ? "Ví dụ: 1 A | 2 B | 3 C | 4 D"
    : !current ? "Dán đáp án hoặc chờ ảnh để phản hồi..." : "Nhập phản hồi...";
  updateCharacterCount();
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
  if (!response.ok) {
    const errStr = await responseError(response);
    throw new Error(errStr);
  }
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

function setRoomLoading(loading) {
  elements.roomScreen.classList.toggle("is-loading", loading);
  elements.messageList.setAttribute("aria-busy", String(loading));
  elements.imageStage.setAttribute("aria-busy", String(loading));
  elements.refreshButton.disabled = loading;
  elements.deleteAllButton.disabled = loading || !state.screenshots.length || isBusy();
  elements.exportButton.disabled = loading || !state.screenshots.length || isBusy();
  elements.deleteButton.disabled = loading || !getSelected() || hasPendingDelete();
  for (const button of elements.messageList.querySelectorAll(".message-delete-button")) {
    button.disabled = loading || hasPendingDelete();
  }
  if (state.screenshots.length === 0) {
    const title = elements.reviewEmpty.querySelector("strong");
    const description = elements.reviewEmpty.querySelector("span");
    title.textContent = loading ? "Đang tải phòng" : "Chưa có ảnh";
    description.textContent = loading
      ? "ZenConnect đang đồng bộ ảnh và phản hồi."
      : "Ảnh mới từ máy ZenConnect sẽ xuất hiện tại đây.";
  }
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
  elements.sendButton.disabled = loading || !state.roomJoined;
}

function updateCharacterCount() {
  elements.characterCount.textContent = `${composedMessage().text.length} / 2000`;
}

function formatAccessKey() {
  const compact = elements.accessKey.value
    .toUpperCase()
    .replace(/[^A-HJ-NP-Z2-9]/g, "")
    .slice(0, 20);
  elements.accessKey.value = compact.match(/.{1,4}/g)?.join("-") ?? "";
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
elements.accessKey.focus({ preventScroll: true });

function autoRestoreSession() {
  let savedKey = null;
  try {
    const urlParams = new URLSearchParams(window.location.search);
    savedKey = urlParams.get("key") || localStorage.getItem(STORAGE_KEY);
  } catch {}

  if (savedKey && /^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){4}$/i.test(savedKey.trim())) {
    elements.accessKey.value = savedKey.trim().toUpperCase();
    setEntryStatus("Đang khôi phục phiên phòng...", false);
    joinRoom().catch(() => {
      try {
        localStorage.removeItem(STORAGE_KEY);
      } catch {}
    });
  }
}

autoRestoreSession();
